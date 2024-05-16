using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using UnturnedModdingCollective.API;
using UnturnedModdingCollective.Models;

namespace UnturnedModdingCollective.Services;
public class VoteLifetimeManager : IHostedService, IDisposable
{
    // todo timer needs to be removed from this list, need to pick a different type probably
    private readonly ConcurrentDictionary<int, Timer> _timers = new ConcurrentDictionary<int, Timer>();
    private readonly SemaphoreSlim _pollFinalizeGate = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource _tknSrc = new CancellationTokenSource();
    private readonly IServiceScope _dbContextScope;

    private readonly BotDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VoteLifetimeManager> _logger;
    private readonly DiscordSocketClient _discordClient;
    private readonly IPersistingRoleService _persistingRoles;
    public VoteLifetimeManager(IServiceProvider serviceProvider)
    {
        _dbContextScope = serviceProvider.CreateScope();
        _dbContext = _dbContextScope.ServiceProvider.GetRequiredService<BotDbContext>();

        _discordClient = serviceProvider.GetRequiredService<DiscordSocketClient>();
        _timeProvider = serviceProvider.GetRequiredService<TimeProvider>();
        _logger = serviceProvider.GetRequiredService<ILogger<VoteLifetimeManager>>();
        _persistingRoles = serviceProvider.GetRequiredService<IPersistingRoleService>();
    }

    async Task IHostedService.StartAsync(CancellationToken token)
    {
        List<ReviewRequest> activeReviewPolls = await _dbContext.ReviewRequests
            .Where(x => x.UtcTimeSubmitted.HasValue && x.UtcTimeVoteExpires.HasValue && !x.UtcTimeClosed.HasValue)
            .ToListAsync(token);

        foreach (ReviewRequest poll in activeReviewPolls)
        {
            StartVoteTimer(poll);
            _logger.LogInformation("Restarted timer for poll {0} (for user {1}).", poll.Id, poll.GlobalName);
        }
    }

    async Task IHostedService.StopAsync(CancellationToken token)
    {
        _tknSrc.Cancel();

        await _pollFinalizeGate.WaitAsync(token);
        foreach (Timer timer in _timers.Values)
        {
            timer.Dispose();
        }
    }
    void IDisposable.Dispose()
    {
        _dbContextScope.Dispose();
    }

    private async Task ErrorPoll(ReviewRequest request, CancellationToken token = default)
    {
        request.ClosedUnderError = true;
        _dbContext.Update(request);
        await _dbContext.SaveChangesAsync(token);
    }

    public async Task FinalizePoll(int requestId, CancellationToken token = default)
    {
        RemoveTimer(requestId);
        await _pollFinalizeGate.WaitAsync(token);
        try
        {
            ReviewRequest? request = await _dbContext.ReviewRequests
                .Include(req => req.RequestedRoles)
                .FirstOrDefaultAsync(req => req.Id == requestId, token);

            if (request is not { UtcTimeClosed: null })
                return;

            request.UtcTimeClosed = _timeProvider.GetUtcNow().UtcDateTime;

            _dbContext.Update(request);
            await _dbContext.SaveChangesAsync(token);

            await FinalizePollIntl(request, token);
        }
        finally
        {
            _pollFinalizeGate.Release();
        }
    }
    private async Task ApplyRolesIntl(IGuild guild, ReviewRequest request, CancellationToken token = default)
    {
        IGuildUser? user = await guild.GetUserAsync(request.UserId);
        if (user == null)
        {
            _logger.LogWarning("User left before the poll was completed.");
            await ErrorPoll(request, token);
            return;
        }

        List<PersistingRole> newPersistRoles = new List<PersistingRole>(request.RequestedRoles.Count);
            
        foreach (ReviewRequestRole role in request.RequestedRoles)
        {
            newPersistRoles.Add(new PersistingRole
            {
                GuildId = guild.Id,
                RoleId = role.RoleId,
                UserAddedBy = 0ul,
                UserId = request.UserId
            });
        }

        await _persistingRoles.AddPersistingRoles(newPersistRoles, token);
    }
    private async Task FinalizePollIntl(ReviewRequest request, CancellationToken token = default)
    {
        RequestOptions reqOpt = new RequestOptions { CancelToken = token };

        IThreadChannel? thread = await _discordClient.GetChannelAsync(request.ThreadId, reqOpt) as IThreadChannel;
        if (thread == null)
        {
            _logger.LogWarning("Unable to finalize poll, unable to find thread channel.");
            await ErrorPoll(request, token);
            return;
        }

        // get the largest (latest) message sent
        ulong lastMessage = request.RequestedRoles
            .Where(x => x.PollMessageId.HasValue && x.PollMessageId.Value != 0)
            .MaxBy(x => x.PollMessageId!.Value)
            ?.PollMessageId ?? 0;

        List<IUserMessage> receivedMessages = await thread.GetMessagesAsync(lastMessage, Direction.Around, request.RequestedRoles.Count * 2, options: reqOpt).Flatten().OfType<IUserMessage>().ToListAsync(token);

        List<ApplicableRole> roleRecords = await _dbContext.ApplicableRoles
            .Where(r => request.RequestedRoles.Select(x => x.RoleId).Contains(r.RoleId))
            .ToListAsync(token);

        try
        {
            int ctAccepted = 0;
            await Parallel.ForEachAsync(request.RequestedRoles, token, async (role, token) =>
            {
                if (!role.PollMessageId.HasValue)
                {
                    _logger.LogWarning("Unable to finalize poll for role {0}, poll message is not set.", role.RoleId);
                    role.ClosedUnderError = true;
                    return;
                }

                ApplicableRole? roleRecord = roleRecords.Find(r => r.RoleId == role.RoleId);
                if (roleRecord == null)
                {
                    _logger.LogWarning("Unable to finalize poll for role {0}, role is no longer able to be applied for.", role.RoleId);
                    role.ClosedUnderError = true;
                    return;
                }

                IUserMessage? pollMessage = receivedMessages.Find(msg => msg.Id == role.PollMessageId);
                pollMessage ??= await thread.GetMessageAsync(request.MessageChannelId, options: reqOpt) as IUserMessage;

                if (pollMessage is not { Poll: not null })
                {
                    _logger.LogWarning("Unable to finalize poll for role {0}, unable to find poll message.", role.RoleId);
                    role.ClosedUnderError = true;
                    return;
                }

                Poll poll = pollMessage.Poll.Value;

                // check to make sure the poll has ended, if not, end it.
                if (poll.Results is not { IsFinalized: true })
                {
                    await pollMessage.EndPollAsync(reqOpt);

                    // refetch poll results if needed
                    if (pollMessage is not { Poll.Results.IsFinalized: true })
                        pollMessage = await thread.GetMessageAsync(pollMessage.Id, options: reqOpt) as IUserMessage;
                }

                // sanity check
                if (pollMessage is not { Poll.Results.IsFinalized: true })
                {
                    _logger.LogWarning("Unable to finalize poll, poll results were not available after finalizing it.");
                    role.ClosedUnderError = true;
                    return;
                }

                poll = pollMessage.Poll.Value;

                uint yesId = 1, noId = 2;

                // api docs states to not rely on the order of the answers to get the answer IDs, even if that's how it's implemented right now.
                // this will double-check the IDs by name
                foreach (PollAnswer answer in poll.Answers)
                {
                    string pollMediaText = answer.PollMedia.Text;
                    if (pollMediaText.Equals(PollFactory.PollYesText, StringComparison.Ordinal))
                    {
                        yesId = answer.AnswerId;
                    }
                    else if (pollMediaText.Equals(PollFactory.PollNoText, StringComparison.Ordinal))
                    {
                        noId = answer.AnswerId;
                    }
                }

                // get users that voted for each answer
                ValueTask<List<IUser>> getYesesTask = pollMessage
                    .GetPollAnswerVotersAsync(yesId, options: reqOpt)
                    .Flatten()
                    .ToListAsync(token);

                List<IUser> votesForNo = await pollMessage
                    .GetPollAnswerVotersAsync(noId, options: reqOpt)
                    .Flatten()
                    .ToListAsync(token);

                List<IUser> votesForYes = await getYesesTask;

                bool accepted = votesForYes.Count - votesForNo.Count >= roleRecord.NetVotesRequired || votesForYes.Count > 0 && votesForNo.Count == 0;
                if (accepted)
                    ++ctAccepted;

                role.Accepted = accepted;
                role.Votes.Clear();
                for (int i = 0; i < votesForYes.Count + votesForNo.Count; i++)
                {
                    bool vote = i < votesForYes.Count;
                    IUser user = vote ? votesForYes[i] : votesForNo[i - votesForYes.Count];
                    ReviewRequestVote reqVote = new ReviewRequestVote
                    {
                        Vote = vote,

                        Request = role.Request,
                        RequestId = role.RequestId,
                        Role = role,
                        RoleId = role.RoleId,
                        VoteIndex = i,

                        UserId = user.Id,
                        UserName = user.Username,
                        GlobalName = user.GlobalName ?? user.Username,
                    };
                    role.Votes.Add(reqVote);
                }
            });

            request.RolesAccepted = ctAccepted;
            _dbContext.Update(request);
            foreach (ReviewRequestRole role in request.RequestedRoles)
            {
                foreach (ReviewRequestVote vote in role.Votes)
                    _dbContext.Add(vote);

                _dbContext.Update(role);
            }
        }
        finally
        {
            await _dbContext.SaveChangesAsync(CancellationToken.None);
        }

        await ApplyRolesIntl(thread.Guild, request, token);
    }
    public bool RemoveTimer(int requestId)
    {
        if (!_timers.TryRemove(requestId, out Timer? timer))
            return false;

        timer.Change(Timeout.Infinite, Timeout.Infinite);
        timer.Dispose();
        return true;
    }
    public void StartVoteTimer(ReviewRequest request)
    {
        if (!request.UtcTimeVoteExpires.HasValue)
            throw new ArgumentException("Request has not started voting.");

        DateTime finishAt = DateTime.SpecifyKind(request.UtcTimeVoteExpires.Value, DateTimeKind.Utc);

        StartVoteTimer(request, finishAt);
    }
    private void StartVoteTimer(ReviewRequest request, DateTime finishAt)
    {
        if (!request.UtcTimeVoteExpires.HasValue)
            throw new ArgumentException("Request has not started voting.");

        TimeSpan timeToWait = finishAt - _timeProvider.GetUtcNow();
        if (timeToWait <= TimeSpan.Zero)
        {
            RunTriggerCompleted(request.Id);
            return;
        }

        TimerRequestState state = new TimerRequestState
        {
            RequestId = request.Id
        };

        _logger.LogInformation("Queued request {0} for {1} at {2:G} (in {3:G}).", request.Id, request.UserName, finishAt.ToLocalTime(), timeToWait);
        Timer timer = new Timer(TimerCompleted, state, timeToWait, Timeout.InfiniteTimeSpan);
        state.Timer = timer;

        Timer existing = _timers.GetOrAdd(request.Id, timer);
        if (!ReferenceEquals(existing, timer))
        {
            existing.Change(timeToWait, Timeout.InfiniteTimeSpan);
            timer.Change(Timeout.Infinite, Timeout.Infinite);
            timer.Dispose();
        }
    }

    private void TimerCompleted(object? stateBox)
    {
        if (stateBox is not TimerRequestState state)
            throw new InvalidOperationException("Invalid state passed to timer.");

        state.Timer.Dispose();
        _timers.TryRemove(new KeyValuePair<int, Timer>(state.RequestId, state.Timer));
        RunTriggerCompleted(state.RequestId);
    }

    private void RunTriggerCompleted(int requestId)
    {
        Task.Run(async () =>
        {
            try
            {
                await FinalizePoll(requestId, _tknSrc.Token);
            }
            catch (OperationCanceledException) when (_tknSrc.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error triggering completion of poll.");
            }
        }, _tknSrc.Token);
    }
    private class TimerRequestState
    {
        public Timer Timer;
        public int RequestId;
    }
}
