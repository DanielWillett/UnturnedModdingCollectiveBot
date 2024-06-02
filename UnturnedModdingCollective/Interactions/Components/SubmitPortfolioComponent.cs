using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using UnturnedModdingCollective.API;
using UnturnedModdingCollective.Models;
using UnturnedModdingCollective.Models.Config;
using UnturnedModdingCollective.Services;

namespace UnturnedModdingCollective.Interactions.Components;
public class SubmitPortfolioComponent : InteractionModuleBase<SocketInteractionContext<SocketMessageComponent>>
{
    private readonly BotDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly PollFactory _pollFactory;
    private readonly VoteLifetimeManager _scheduler;
    private readonly ILiveConfiguration<LiveConfiguration> _liveConfiguration;
    public SubmitPortfolioComponent(
        BotDbContext dbContext,
        TimeProvider timeProvider,
        PollFactory pollFactory,
        VoteLifetimeManager scheduler,
        ILiveConfiguration<LiveConfiguration> liveConfiguration
        )
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _pollFactory = pollFactory;
        _scheduler = scheduler;
        _liveConfiguration = liveConfiguration;
    }

    internal const string SubmitPortfolioButtonPrefix = "submit_portfolio_btn_";
    internal const string CancelPortfolioButtonPrefix = "cancel_portfolio_btn_";

    [ComponentInteraction($"{CancelPortfolioButtonPrefix}*")]
    public async Task ReceiveCancelButtonPress()
    {
        ReviewRequestRole? request = await ValidateButtonPress(true);
        if (request == null)
            return;

        IThreadChannel thread = (IThreadChannel)Context.Interaction.Message.Channel;
        Task deleteThreadTask = thread.DeleteAsync();

        request.UtcTimeCancelled = _timeProvider.GetUtcNow().UtcDateTime;

        _dbContext.Update(request);
        await _dbContext.SaveChangesAsync();

        await deleteThreadTask;
    }

    [ComponentInteraction($"{SubmitPortfolioButtonPrefix}*")]
    public async Task ReceiveSubmitButtonPress()
    {
        ReviewRequestRole? request = await ValidateButtonPress(true);
        if (request == null)
            return;

        IThreadChannel thread = (IThreadChannel)Context.Interaction.Message.Channel;


        TimeSpan voteTime = TimeSpan.FromHours(Math.Round(_liveConfiguration.Configuraiton.VoteTime.TotalHours));
        if (voteTime.TotalDays > 7d || voteTime.TotalHours < 0.5f)
            throw new InvalidOperationException("Configured value for \"VoteTime\" is not valid. It must be less than 7 days, non-zero, and non-negative.");

        // update database
        request.UtcTimeSubmitted = _timeProvider.GetUtcNow().UtcDateTime;
        request.UtcTimeVoteExpires = request.UtcTimeSubmitted.Value.Add(voteTime);
        request.Request!.UserName = Context.User.Username;
        request.Request!.GlobalName = Context.User.GlobalName ?? string.Empty;

        try
        {
            bool removeApplicant = _liveConfiguration.Configuraiton.RemoveApplicantFromThread;
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                .WithColor(Color.Green)
                .WithTitle("Submitted")
                .WithDescription($"Your portfolio has been submitted, you should receive a DM {
                                    TimestampTag.FormatFromDateTime(request.UtcTimeVoteExpires.Value, TimestampTagStyles.Relative)
                                 } when the vote closes and the decision is made.{(removeApplicant
                                     ? " You've been locked out of this thread and can no longer see new messages, " +
                                       "although Discord may keep you in the channel until you click out."
                                     : string.Empty)}")
                .Build(),
                ephemeral: true);

            // lock thread, remove the user's ability to view the thread
            Task modifyThread = thread.ModifyAsync(properties =>
            {
                // set auto-archive duration to be higher than the vote time
                if (voteTime <= TimeSpan.FromHours(1d))
                    properties.AutoArchiveDuration = ThreadArchiveDuration.OneHour;
                else if (voteTime <= TimeSpan.FromDays(1d))
                    properties.AutoArchiveDuration = ThreadArchiveDuration.OneDay;
                else if (voteTime <= TimeSpan.FromDays(3d))
                    properties.AutoArchiveDuration = ThreadArchiveDuration.ThreeDays;
                else
                    properties.AutoArchiveDuration = ThreadArchiveDuration.OneWeek;
            });

            if (removeApplicant)
            {
                await thread.RemoveUserAsync((IGuildUser)Context.User);
            }
            await modifyThread;

            // send the vote poll
            IRole? discordRole = Context.Guild.Roles.FirstOrDefault(r => r.Id == request.RoleId);

            string question = $"Should {Context.User.GlobalName ?? Context.User.Username} receive the role {discordRole?.Name ?? request.RoleId.ToString(CultureInfo.InvariantCulture)}?";
            IMessage poll = await thread.SendMessageAsync(discordRole?.Mention, poll: _pollFactory.CreateYesNoPoll(question, voteTime));

            request.PollMessageId = poll.Id;

            // update request so the vote expires just after the poll ends instead of just before
            request.UtcTimeVoteExpires = _timeProvider.GetUtcNow().UtcDateTime.Add(voteTime);
        }
        finally
        {
            _dbContext.Update(request.Request);
            _dbContext.Update(request);
            Task saveTask = _dbContext.SaveChangesAsync();
            _scheduler.StartVoteTimer(request);
            await saveTask;
        }
    }

    private async Task<ReviewRequestRole?> ValidateButtonPress(bool isCancel)
    {
        // parse user id from button custom id

        string prefix = isCancel ? CancelPortfolioButtonPrefix : SubmitPortfolioButtonPrefix;

        string userIdStr = Context.Interaction.Data.CustomId[prefix.Length..];
        ulong userId = ulong.Parse(userIdStr, CultureInfo.InvariantCulture);

        if (userId != Context.User.Id)
        {
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                    .WithColor(Color.Red)
                    .WithTitle("No Permissions")
                    .WithDescription($"Only the original poster can {(isCancel ? "cancel" : "submit")} their request.")
                    .Build(),
                ephemeral: true
            );
            return null;
        }

        IThreadChannel? thread = Context.Interaction.Message.Channel as IThreadChannel;

        ReviewRequestRole? request = null;
        if (thread != null)
        {
            request = await _dbContext.Set<ReviewRequestRole>()
                .Include(x => x.Request)
                .FirstOrDefaultAsync(req => req.Request!.UserId == userId && req.ThreadId == thread.Id);
        }

        if (request?.Request != null)
            return request;

        await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                .WithColor(Color.Red)
                .WithTitle("Invalid Setup")
                .WithDescription("This button must belong to a valid review request.")
                .Build(),
            ephemeral: true
        );

        return null;
    }
}
