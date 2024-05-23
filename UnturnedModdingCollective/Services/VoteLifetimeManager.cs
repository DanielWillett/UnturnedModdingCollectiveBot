using DanielWillett.ReflectionTools;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Reflection;
using UnturnedModdingCollective.API;
using UnturnedModdingCollective.Models;
using UnturnedModdingCollective.Models.Config;

namespace UnturnedModdingCollective.Services;
public class VoteLifetimeManager : IHostedService, IDisposable
{
    private static readonly Func<IMessageChannel, BaseDiscordClient, ulong, RequestOptions, Task<RestMessage>>? InvokeIntlGetMessage =
        Accessor.GenerateStaticCaller<Func<IMessageChannel, BaseDiscordClient, ulong, RequestOptions, Task<RestMessage>>>(
                Type.GetType("Discord.Rest.ChannelHelper, Discord.Net.Rest")
                    ?.GetMethod("GetMessageAsync", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!,
                throwOnError: false, allowUnsafeTypeBinding: true
            );

    private readonly ConcurrentDictionary<int, Timer> _timers = new ConcurrentDictionary<int, Timer>();
    private readonly SemaphoreSlim _pollFinalizeGate = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource _tknSrc = new CancellationTokenSource();
    private readonly IServiceScope _dbContextScope;

    private readonly BotDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VoteLifetimeManager> _logger;
    private readonly DiscordSocketClient _discordClient;
    private readonly IPersistingRoleService _persistingRoles;
    private readonly ILiveConfiguration<LiveConfiguration> _liveConfig;
    public VoteLifetimeManager(IServiceProvider serviceProvider)
    {
        _dbContextScope = serviceProvider.CreateScope();
        _dbContext = _dbContextScope.ServiceProvider.GetRequiredService<BotDbContext>();

        _discordClient = serviceProvider.GetRequiredService<DiscordSocketClient>();
        _timeProvider = serviceProvider.GetRequiredService<TimeProvider>();
        _logger = serviceProvider.GetRequiredService<ILogger<VoteLifetimeManager>>();
        _persistingRoles = serviceProvider.GetRequiredService<IPersistingRoleService>();
        _liveConfig = serviceProvider.GetRequiredService<ILiveConfiguration<LiveConfiguration>>();
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
        _timers.Clear();
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

        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (ReviewRequestRole role in request.RequestedRoles)
        {
            newPersistRoles.Add(new PersistingRole
            {
                GuildId = guild.Id,
                RoleId = role.RoleId,
                UserAddedBy = 0ul,
                UserId = request.UserId,
                UtcTimestamp = now
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
                pollMessage ??= await thread.GetMessageAsync(request.ThreadId, options: reqOpt) as IUserMessage;

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
                    try
                    {
                        await pollMessage.EndPollAsync(reqOpt);
                    }
                    catch (Discord.Net.HttpException ex) when (ex.Message.Contains("520001" /* Poll expired */, StringComparison.Ordinal))
                    {
                        _logger.LogDebug("Failed to finalize poll, it's already expired.");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to finalize poll.");
                    }

                    // refetch poll results if needed
                    if (pollMessage is not { Poll.Results.IsFinalized: true })
                    {
                        if (InvokeIntlGetMessage != null)
                        {
                            // force redownload the message using reflection, since Discord.NET caches and the poll update doesn't seem to arrive on time
                            pollMessage = await InvokeIntlGetMessage(thread, _discordClient, pollMessage.Id, reqOpt) as IUserMessage;
                        }
                        else
                        {
                            pollMessage = await thread.GetMessageAsync(pollMessage.Id, options: reqOpt) as IUserMessage;
                            _logger.LogWarning("Unable to redownload poll message without using cache, it could be out of date.");
                        }
                    }
                }

                // sanity check
                if (pollMessage is not { Poll.Results: not null })
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

                _logger.LogDebug($"{role.RoleId} - ({votesForYes.Count}-{votesForNo.Count})");

                bool accepted = votesForYes.Count - votesForNo.Count >= roleRecord.NetVotesRequired || votesForYes.Count > 0 && votesForNo.Count == 0;
                if (accepted)
                    ++ctAccepted;

                role.Accepted = accepted;
                role.YesVotes = votesForYes.Count;
                role.NoVotes = votesForNo.Count;
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

        DateTime newFinishAt = finishAt.Subtract(_liveConfig.Configuraiton.PingTimeBeforeVoteClose);
        TimeSpan secondTimeSpan = _liveConfig.Configuraiton.PingTimeBeforeVoteClose;

        TimeSpan timeToWait = newFinishAt - _timeProvider.GetUtcNow();
        bool isPingTimer = true;
        if (timeToWait <= TimeSpan.Zero)
        {
            timeToWait = finishAt - _timeProvider.GetUtcNow();
            if (timeToWait <= TimeSpan.Zero)
            {
                RunTriggerCompleted(request.Id);
                return;
            }

            RunPingChannel(request.Id);
            secondTimeSpan = timeToWait;

            isPingTimer = false;
        }

        TimerRequestState state = new TimerRequestState
        {
            RequestId = request.Id,
            IsPingTimer = isPingTimer,
            TimeForSecondSection = secondTimeSpan
        };

        _logger.LogInformation("Queued request {0} for {1} at {2:G} (in {3:G}) (ping: {isPingTimer}).", request.Id, request.UserName, newFinishAt.ToLocalTime(), timeToWait, isPingTimer);
        Timer timer = new Timer(TimerCompleted, state, timeToWait, Timeout.InfiniteTimeSpan);
        state.Timer = timer;

        Timer? old = null;

        _timers.AddOrUpdate(request.Id, timer, (_, t) =>
        {
            old = t;
            return timer;
        });

        if (ReferenceEquals(old, timer) || old == null)
            return;

        old.Change(Timeout.Infinite, Timeout.Infinite);
        old.Dispose();
    }

    private void TimerCompleted(object? stateBox)
    {
        if (stateBox is not TimerRequestState state)
            throw new InvalidOperationException("Invalid state passed to timer.");

        state.Timer.Dispose();
        _timers.TryRemove(new KeyValuePair<int, Timer>(state.RequestId, state.Timer));
        if (!state.IsPingTimer)
        {
            RunTriggerCompleted(state.RequestId);
            return;
        }

        state = new TimerRequestState
        {
            RequestId = state.RequestId,
            TimeForSecondSection = state.TimeForSecondSection,
            IsPingTimer = false
        };

        _logger.LogInformation("Queued request {0} in {3:G} (ping: {isPingTimer}).", state.RequestId, state.TimeForSecondSection, false);
        Timer timer = new Timer(TimerCompleted, state, state.TimeForSecondSection, Timeout.InfiniteTimeSpan);
        state.Timer = timer;

        Timer? old = null;

        _timers.AddOrUpdate(state.RequestId, timer, (_, t) =>
        {
            old = t;
            return timer;
        });

        if (!ReferenceEquals(old, timer) && old != null)
        {
            old.Change(Timeout.Infinite, Timeout.Infinite);
            old.Dispose();
        }

        RunPingChannel(state.RequestId);
    }
    private void RunPingChannel(int requestId)
    {
        Task.Run(async () =>
        {
            await _pollFinalizeGate.WaitAsync();
            try
            {
                ulong councilRoleId = _liveConfig.Configuraiton.CouncilRole;

                ReviewRequest? request = _dbContext.ReviewRequests.FirstOrDefault(req => req.Id == requestId);
                if (request == null)
                    return;

                IThreadChannel? threadChannel = await _discordClient.GetChannelAsync(request.ThreadId) as IThreadChannel;
                if (threadChannel == null)
                    return;

                IRole? councilRole = threadChannel.Guild.GetRole(councilRoleId);

                DateTime closeTime = DateTime.SpecifyKind(request.UtcTimeVoteExpires ?? (_timeProvider.GetUtcNow().UtcDateTime - _liveConfig.Configuraiton.PingTimeBeforeVoteClose), DateTimeKind.Utc);

                string mention = councilRole?.Mention ?? "@here";
                
                await threadChannel.SendMessageAsync($"{mention} This vote will close {TimestampTag.FormatFromDateTime(closeTime, TimestampTagStyles.Relative)}.");
            }
            finally
            {
                _pollFinalizeGate.Release();
            }
        });
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
#nullable disable
    private class TimerRequestState
    {
        public Timer Timer;
        public int RequestId;
        
        /// <summary>
        /// Timers have a stop point in between when they start and when the vote ends. This is true if the timer's on the first section.
        /// </summary>
        public bool IsPingTimer;

        public TimeSpan TimeForSecondSection;
    }
#nullable restore
}
