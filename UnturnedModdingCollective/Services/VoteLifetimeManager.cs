using DanielWillett.ReflectionTools;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using UnturnedModdingCollective.API;
using UnturnedModdingCollective.Models;
using UnturnedModdingCollective.Models.Config;

namespace UnturnedModdingCollective.Services;
public class VoteLifetimeManager : IHostedService, IDisposable
{
    private static readonly Func<IMessageChannel, BaseDiscordClient, ulong, RequestOptions?, Task<RestMessage>>? InvokeIntlGetMessage =
        Accessor.GenerateStaticCaller<Func<IMessageChannel, BaseDiscordClient, ulong, RequestOptions?, Task<RestMessage>>>(
                Type.GetType("Discord.Rest.ChannelHelper, Discord.Net.Rest")
                    ?.GetMethod("GetMessageAsync", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!,
                throwOnError: false, allowUnsafeTypeBinding: true
            );

    private readonly ConcurrentDictionary<RequestRoleId, Timer> _timers = new ConcurrentDictionary<RequestRoleId, Timer>();
    private readonly SemaphoreSlim _pollFinalizeGate = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource _tknSrc = new CancellationTokenSource();
    private readonly IServiceScope _dbContextScope;

    private readonly DiscordClientLifetime _lifetime;
    private readonly BotDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VoteLifetimeManager> _logger;
    private readonly DiscordSocketClient _discordClient;
    private readonly IPersistingRoleService _persistingRoles;
    private readonly ILiveConfiguration<LiveConfiguration> _liveConfig;
    public VoteLifetimeManager(IServiceProvider serviceProvider)
    {
        _lifetime = serviceProvider.GetRequiredService<DiscordClientLifetime>();

        _dbContextScope = serviceProvider.CreateScope();
        _dbContext = _dbContextScope.ServiceProvider.GetRequiredService<BotDbContext>();

        _discordClient = serviceProvider.GetRequiredService<DiscordSocketClient>();
        _timeProvider = serviceProvider.GetRequiredService<TimeProvider>();
        _logger = serviceProvider.GetRequiredService<ILogger<VoteLifetimeManager>>();
        _persistingRoles = serviceProvider.GetRequiredService<IPersistingRoleService>();
        _liveConfig = serviceProvider.GetRequiredService<ILiveConfiguration<LiveConfiguration>>();
    }

    Task IHostedService.StartAsync(CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _lifetime.WaitUntilReady();

                List<ReviewRequestRole> activeReviewPolls = await _dbContext.Set<ReviewRequestRole>()
                    .Include(x => x.Request)
                    .Where(x => x.UtcTimeSubmitted.HasValue && x.UtcTimeVoteExpires.HasValue && !x.UtcTimeClosed.HasValue)
                    .ToListAsync(token);

                foreach (ReviewRequestRole poll in activeReviewPolls)
                {
                    StartVoteTimer(poll);
                    _logger.LogInformation("Restarted timer for poll {0} - {1} (for user {2}).", poll.Request!.Id, poll.RoleId, poll.Request!.GlobalName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start vote timers on startup.");
            }
        }, token);

        return Task.CompletedTask;
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

    private async Task ErrorPoll(ReviewRequestRole request, CancellationToken token = default)
    {
        request.ClosedUnderError = true;
        _dbContext.Update(request);
        await _dbContext.SaveChangesAsync(token);
    }

    public async Task FinalizePoll(int requestId, ulong roleId, CancellationToken token = default)
    {
        RemoveTimer(requestId, roleId);
        await _pollFinalizeGate.WaitAsync(token);
        try
        {
            ReviewRequestRole? request = await _dbContext.Set<ReviewRequestRole>()
                .Include(req => req.Request)
                .FirstOrDefaultAsync(req => req.RequestId == requestId && req.RoleId == roleId, token);

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
    private async Task ApplyRoleIntl(IGuild guild, ReviewRequestRole request, CancellationToken token = default)
    {
        IGuildUser? user = await guild.GetUserAsync(request.Request!.UserId);
        if (user == null)
        {
            _logger.LogWarning("User left before the poll was completed.");
            await ErrorPoll(request, token);
            return;
        }

        bool accepted = request.Accepted.GetValueOrDefault();
        if (accepted)
        {
            await _persistingRoles.AddPersistingRole(request.Request!.UserId, guild.Id, request.RoleId, default(DateTime?), 0ul, token);
        }

        try
        {
            IRole? discordRole = guild.Roles.FirstOrDefault(r => r.Id == request.RoleId);

            await user.SendMessageAsync(embed: new EmbedBuilder()
                .WithTitle($"{discordRole?.Name ?? request.RoleId.ToString(CultureInfo.InvariantCulture)} Application Results")
                .WithDescription(accepted
                    ? "You were accepted to be a member of Unturned Modding Collective."
                    : "Unfortunately, you did not meet the criteria to join Unturned Modding Collective. Please try again at a later date.")
                .WithColor(accepted ? Color.Green : Color.Gold)
                .Build());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error sending notification DM to user.");
        }

        if (!accepted)
            return;
        
        // add the user to all existing threads for their role
        List<ReviewRequestRole> allNewRoles = await _dbContext.Set<ReviewRequestRole>()
                                                      .Where(x =>
                                                          x.RoleId == request.RoleId
                                                          && x.Request!.UserId != request.Request!.UserId
                                                          && !x.ClosedUnderError
                                                          && !x.UtcTimeCancelled.HasValue
                                                          && !x.UtcTimeClosed.HasValue)
                                                      .ToListAsync(token);

        RequestOptions reqOpt = new RequestOptions { CancelToken = token };
        await Parallel.ForEachAsync(allNewRoles, token, async (role, _) =>
        {
            IThreadChannel? tc = await _discordClient.GetChannelAsync(role.ThreadId) as IThreadChannel;
            if (tc == null)
                return;

            try
            {
                await tc.AddUserAsync(user, reqOpt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to retroactively add {user.Username} (<@{user.Id}>) to thread {tc.Name} ({tc.Id}).");
            }
        });
    }
    private async Task<IUserMessage?> GetMessageFresh(IThreadChannel channel, ulong messageId, RequestOptions? reqOpt)
    {
        if (InvokeIntlGetMessage != null)
        {
            // force redownload the message using reflection, since Discord.NET caches and the poll update doesn't seem to arrive on time
            return await InvokeIntlGetMessage(channel, _discordClient, messageId, reqOpt) as IUserMessage;
        }
        else
        {
            _logger.LogWarning("Unable to redownload poll message without using cache, it could be out of date.");
            return await channel.GetMessageAsync(messageId, options: reqOpt) as IUserMessage;
        }
    }
    private async Task FinalizePollIntl(ReviewRequestRole request, CancellationToken token = default)
    {
        RequestOptions reqOpt = new RequestOptions { CancelToken = token };

        IThreadChannel? thread = await _discordClient.GetChannelAsync(request.ThreadId, reqOpt) as IThreadChannel;
        if (thread == null)
        {
            _logger.LogWarning("Unable to finalize poll, unable to find thread channel.");
            await ErrorPoll(request, token);
            return;
        }

        try
        {
            if (!request.PollMessageId.HasValue)
            {
                _logger.LogWarning("Unable to finalize poll for role {0}, poll message is not set.", request.RoleId);
                request.ClosedUnderError = true;
                return;
            }

            ApplicableRole? roleRecord = await _dbContext.ApplicableRoles.FirstOrDefaultAsync(x => x.RoleId == request.RoleId, token);
            if (roleRecord == null)
            {
                _logger.LogWarning("Unable to finalize poll for role {0}, role is no longer able to be applied for.", request.RoleId);
                request.ClosedUnderError = true;
                return;
            }

            IUserMessage? pollMessage = await GetMessageFresh(thread, request.PollMessageId.Value, reqOpt);
            if (pollMessage is not { Poll: not null })
            {
                _logger.LogWarning("Unable to finalize poll for role {0}, unable to find poll message.", request.RoleId);
                request.ClosedUnderError = true;
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
                    pollMessage = await GetMessageFresh(thread, request.PollMessageId.Value, reqOpt);
                }
            }

            // sanity check
            if (pollMessage is not { Poll.Results: not null })
            {
                _logger.LogWarning("Unable to finalize poll, poll results were not available after finalizing it.");
                request.ClosedUnderError = true;
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

            _logger.LogDebug($"{request.RoleId} - ({votesForYes.Count}-{votesForNo.Count})");

            bool accepted = votesForYes.Count - votesForNo.Count >= roleRecord.NetVotesRequired || votesForYes.Count > 0 && votesForNo.Count == 0;

            // increment request RolesAccepted count
            if (accepted)
                request.Request!.RolesAccepted = request.Request!.RolesAccepted.GetValueOrDefault() + 1;
            else if (!request.Request!.RolesAccepted.HasValue)
                request.Request.RolesAccepted = 0;

            request.Accepted = accepted;
            request.YesVotes = votesForYes.Count;
            request.NoVotes = votesForNo.Count;
            request.Votes.Clear();
            for (int i = 0; i < votesForYes.Count + votesForNo.Count; i++)
            {
                bool vote = i < votesForYes.Count;
                IUser user = vote ? votesForYes[i] : votesForNo[i - votesForYes.Count];
                ReviewRequestVote reqVote = new ReviewRequestVote
                {
                    Vote = vote,

                    Request = request.Request,
                    RequestId = request.RequestId,
                    Role = request,
                    RoleId = request.RoleId,
                    VoteIndex = i,

                    UserId = user.Id,
                    UserName = user.Username,
                    GlobalName = user.GlobalName ?? user.Username,
                };

                request.Votes.Add(reqVote);
            }

            foreach (ReviewRequestVote vote in request.Votes)
                _dbContext.Add(vote);
            _dbContext.Update(request);
            _dbContext.Update(request.Request);

        }
        finally
        {
            await _dbContext.SaveChangesAsync(CancellationToken.None);
        }

        await ApplyRoleIntl(thread.Guild, request, token);
    }
    public bool RemoveTimer(int requestId, ulong roleId)
    {
        if (!_timers.TryRemove(new RequestRoleId(requestId, roleId), out Timer? timer))
            return false;

        timer.Change(Timeout.Infinite, Timeout.Infinite);
        timer.Dispose();
        return true;
    }
    public void StartVoteTimer(ReviewRequestRole request)
    {
        if (!request.UtcTimeVoteExpires.HasValue)
            throw new ArgumentException("Request has not started voting.");

        DateTime finishAt = DateTime.SpecifyKind(request.UtcTimeVoteExpires.Value, DateTimeKind.Utc);

        StartVoteTimer(request, finishAt);
    }
    private void StartVoteTimer(ReviewRequestRole request, DateTime finishAt)
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
                RunTriggerCompleted(request.RequestId, request.RoleId);
                return;
            }

            RunPingChannel(request.RequestId, request.RoleId);
            secondTimeSpan = timeToWait;

            isPingTimer = false;
        }

        TimerRequestState state = new TimerRequestState
        {
            RequestId = request.RequestId,
            RoleId = request.RoleId,
            IsPingTimer = isPingTimer,
            TimeForSecondSection = secondTimeSpan
        };

        _logger.LogInformation("Queued request {0} - {1} for {2} at {3:G} (in {4:G}) (ping: {isPingTimer}).", request.RequestId, request.RoleId, request.Request!.UserName, newFinishAt.ToLocalTime(), timeToWait, isPingTimer);
        Timer timer = new Timer(TimerCompleted, state, timeToWait, Timeout.InfiniteTimeSpan);
        state.Timer = timer;

        Timer? old = null;

        _timers.AddOrUpdate(new RequestRoleId(request.RequestId, request.RoleId), timer, (_, t) =>
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
        _timers.TryRemove(new KeyValuePair<RequestRoleId, Timer>(new RequestRoleId(state.RequestId, state.RoleId), state.Timer));
        if (!state.IsPingTimer)
        {
            RunTriggerCompleted(state.RequestId, state.RoleId);
            return;
        }

        state = new TimerRequestState
        {
            RequestId = state.RequestId,
            RoleId = state.RoleId,
            TimeForSecondSection = state.TimeForSecondSection,
            IsPingTimer = false
        };

        _logger.LogInformation("Queued request {0} in {3:G} (ping: {isPingTimer}).", state.RequestId, state.TimeForSecondSection, false);
        Timer timer = new Timer(TimerCompleted, state, state.TimeForSecondSection, Timeout.InfiniteTimeSpan);
        state.Timer = timer;

        Timer? old = null;

        _timers.AddOrUpdate(new RequestRoleId(state.RequestId, state.RoleId), timer, (_, t) =>
        {
            old = t;
            return timer;
        });

        if (!ReferenceEquals(old, timer) && old != null)
        {
            old.Change(Timeout.Infinite, Timeout.Infinite);
            old.Dispose();
        }

        RunPingChannel(state.RequestId, state.RoleId);
    }
    private void RunPingChannel(int requestId, ulong roleId)
    {
        Task.Run(async () =>
        {
            await _pollFinalizeGate.WaitAsync();
            try
            {
                ReviewRequestRole? request = _dbContext.Set<ReviewRequestRole>()
                    .FirstOrDefault(req => req.RequestId == requestId && req.RoleId == roleId);

                if (request == null)
                    return;

                IThreadChannel? threadChannel = await _discordClient.GetChannelAsync(request.ThreadId) as IThreadChannel;
                if (threadChannel == null)
                    return;

                IRole? role = threadChannel.Guild.Roles.FirstOrDefault(x => x.Id == roleId);

                DateTime closeTime = DateTime.SpecifyKind(request.UtcTimeVoteExpires ?? (_timeProvider.GetUtcNow().UtcDateTime - _liveConfig.Configuraiton.PingTimeBeforeVoteClose), DateTimeKind.Utc);

                string mention = role?.Mention ?? "@here";
                
                await threadChannel.SendMessageAsync($"{mention} This vote will close {TimestampTag.FormatFromDateTime(closeTime, TimestampTagStyles.Relative)}.");
            }
            finally
            {
                _pollFinalizeGate.Release();
            }
        });
    }
    private void RunTriggerCompleted(int requestId, ulong roleId)
    {
        Task.Run(async () =>
        {
            try
            {
                await FinalizePoll(requestId, roleId, _tknSrc.Token);
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
        public ulong RoleId;
        
        /// <summary>
        /// Timers have a stop point in between when they start and when the vote ends. This is true if the timer's on the first section.
        /// </summary>
        public bool IsPingTimer;

        public TimeSpan TimeForSecondSection;
    }
    private readonly struct RequestRoleId : IEquatable<RequestRoleId>
    {
        public readonly int RequestId;
        public readonly ulong RoleId;
        public RequestRoleId(int requestId, ulong roleId)
        {
            RequestId = requestId;
            RoleId = roleId;
        }

        public bool Equals(RequestRoleId other) => other.RequestId == RequestId && other.RoleId == RoleId;
        public override bool Equals(object obj) => obj is RequestRoleId r && r.RequestId == RequestId && r.RoleId == RoleId;
        public override int GetHashCode() => HashCode.Combine(RequestId, RoleId);
    }
#nullable restore
}
