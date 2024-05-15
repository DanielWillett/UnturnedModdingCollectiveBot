using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UnturnedModdingCollective.Models;

namespace UnturnedModdingCollective.Services;
public class VoteLifetimeManager : IHostedService
{
    private readonly ConcurrentBag<Timer> _timers = new ConcurrentBag<Timer>();
    private readonly SemaphoreSlim _pollFinalizeGate = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource _tknSrc = new CancellationTokenSource();

    private readonly BotDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VoteLifetimeManager> _logger;
    private readonly DiscordSocketClient _discordClient;
    public VoteLifetimeManager(
        DiscordClientLifetime lifetime,
        BotDbContext dbContext,
        TimeProvider timeProvider,
        DiscordSocketClient discordClient,
        ILogger<VoteLifetimeManager> logger
    )
    {
        _discordClient = discordClient;
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _logger = logger;
        // force this to run after discord is initialized
        _ = lifetime;
    }

    async Task IHostedService.StartAsync(CancellationToken token)
    {
        List<ReviewRequest> activeReviewPolls = await _dbContext.ReviewRequests
            .Where(x => x.UtcTimeSubmitted.HasValue && x.UtcTimeVoteExpires.HasValue && !x.UtcTimeClosed.HasValue)
            .ToListAsync(token);

        activeReviewPolls.ForEach(StartVoteTimer);
    }

    async Task IHostedService.StopAsync(CancellationToken token)
    {
        _tknSrc.Cancel();

        while (_timers.TryTake(out Timer? timer))
            timer.Dispose();
    }

    public async Task FinalizePoll(ReviewRequest request, CancellationToken token = default)
    {
        RequestOptions reqOpt = new RequestOptions { CancelToken = token };

        IThreadChannel? thread = await _discordClient.GetChannelAsync(request.ThreadId, reqOpt) as IThreadChannel;

        IUserMessage? pollMessage = thread == null || !request.PollMessageId.HasValue
            ? null
            : await thread.GetMessageAsync(request.PollMessageId.Value, options: reqOpt) as IUserMessage;

        if (pollMessage is not { Poll: not null })
        {
            _logger.LogWarning("Unable to finalize poll, unable to find poll message or thread channel.");

            request.ClosedUnderError = true;
            _dbContext.Update(request);
            await _dbContext.SaveChangesAsync(token);

            return;
        }

        Poll poll = pollMessage.Poll.Value;

        // check to make sure the poll has ended, if not, end it.
        if (poll.Results is not { IsFinalized: true })
        {
            await pollMessage.EndPollAsync(reqOpt);
            pollMessage = await thread!.GetMessageAsync(request.PollMessageId!.Value, options: reqOpt) as IUserMessage;
        }

        // sanity check
        if (pollMessage == null || poll.Results is not { IsFinalized: true })
        {
            _logger.LogWarning("Unable to finalize poll, poll results were not available after finalizing it.");

            request.ClosedUnderError = true;
            _dbContext.Update(request);
            await _dbContext.SaveChangesAsync(token);

            return;
        }

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

        request.Accepted = votesForYes.Count > votesForNo.Count;
        // todo
    }
    public void StartVoteTimer(ReviewRequest request)
    {
        if (!request.UtcTimeVoteExpires.HasValue)
            throw new ArgumentException("Request has not started voting.");

        DateTime finishAt = DateTime.SpecifyKind(request.UtcTimeVoteExpires.Value, DateTimeKind.Utc);

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

        _logger.LogInformation($"Queued request {request.Id} for {request.UserName} at {finishAt.ToLocalTime():G} (in {timeToWait:G}).");
        Timer timer = new Timer(TimerCompleted, state, timeToWait, Timeout.InfiniteTimeSpan);
        state.Timer = timer;

        _timers.Add(timer);
    }

    private void TimerCompleted(object? stateBox)
    {
        if (stateBox is not TimerRequestState state)
            throw new InvalidOperationException("Invalid state passed to timer.");

        state.Timer.Dispose();
        RunTriggerCompleted(state.RequestId);
    }

    private void RunTriggerCompleted(int requestId)
    {
        Task.Run(async () =>
        {
            try
            {
                await TriggerCompleted(requestId, _tknSrc.Token);
            }
            catch (OperationCanceledException) when (_tknSrc.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error triggering completion of poll.");
            }
        }, _tknSrc.Token);
    }
    private async Task TriggerCompleted(int requestId, CancellationToken token = default)
    {
        ReviewRequest? request;
        await _pollFinalizeGate.WaitAsync(token);
        try
        {
            request = await _dbContext.ReviewRequests.FirstOrDefaultAsync(req => req.Id == requestId, token);

            if (request is not { UtcTimeClosed: null })
                return;
            
            request.UtcTimeClosed = _timeProvider.GetUtcNow().UtcDateTime;

            _dbContext.Update(request);
            await _dbContext.SaveChangesAsync(token);
        }
        finally
        {
            _pollFinalizeGate.Release();
        }

        await FinalizePoll(request, token);
    }

    private class TimerRequestState
    {
        public Timer Timer;
        public int RequestId;
    }
}
