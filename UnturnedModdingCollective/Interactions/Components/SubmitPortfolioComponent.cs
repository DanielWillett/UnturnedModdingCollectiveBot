﻿using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using UnturnedModdingCollective.Models;
using UnturnedModdingCollective.Services;

namespace UnturnedModdingCollective.Interactions.Components;
public class SubmitPortfolioComponent : InteractionModuleBase<SocketInteractionContext<SocketMessageComponent>>
{
    private readonly BotDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly PollFactory _pollFactory;
    private readonly VoteLifetimeManager _scheduler;
    public SubmitPortfolioComponent(BotDbContext dbContext, TimeProvider timeProvider, PollFactory pollFactory, VoteLifetimeManager scheduler)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _pollFactory = pollFactory;
        _scheduler = scheduler;
    }

    internal const string SubmitPortfolioButtonPrefix = "submit_portfolio_btn_";
    internal const string CancelPortfolioButtonPrefix = "cancel_portfolio_btn_";

    [ComponentInteraction($"{CancelPortfolioButtonPrefix}*")]
    public async Task ReceiveCancelButtonPress()
    {
        ReviewRequest? request = await ValidateButtonPress(true);
        if (request == null)
            return;

        request.UtcTimeCancelled = _timeProvider.GetUtcNow().UtcDateTime;

        _dbContext.Update(request);
        await _dbContext.SaveChangesAsync();

        IThreadChannel thread = (IThreadChannel)Context.Interaction.Message.Channel;
        Task deleteThreadTask = thread.DeleteAsync();

        IMessageChannel? channel = await Context.Client.GetChannelAsync(request.MessageChannelId) as IMessageChannel;
        IMessage? message = channel == null ? null : await channel.GetMessageAsync(request.MessageId);
        if (message != null)
        {
            await message.DeleteAsync();
        }

        await deleteThreadTask;
    }

    [ComponentInteraction($"{SubmitPortfolioButtonPrefix}*")]
    public async Task ReceiveSubmitButtonPress()
    {
        ReviewRequest? request = await ValidateButtonPress(true);
        if (request == null)
            return;

        IThreadChannel thread = (IThreadChannel)Context.Interaction.Message.Channel;

        Task deferTask = Context.Interaction.DeferLoadingAsync();

        // lock thread, remove the user's ability to view the thread
        Task modifyThread = thread!.ModifyAsync(properties =>
        {
            properties.Locked = true;
            properties.PermissionOverwrites = new Optional<IEnumerable<Overwrite>>(
            [
                new Overwrite(Context.User.Id, PermissionTarget.User, new OverwritePermissions(viewChannel: PermValue.Deny)),
                new Overwrite(Context.Client.CurrentUser.Id, PermissionTarget.User, new OverwritePermissions(
                    sendMessages  : PermValue.Allow,
                    viewChannel   : PermValue.Allow,
                    manageChannel : PermValue.Allow)
                )
            ]);
        });

        // todo proper config
        TimeSpan voteTime = TimeSpan.FromHours(1d);

        // update database
        request.UtcTimeSubmitted = _timeProvider.GetUtcNow().UtcDateTime;
        request.UtcTimeVoteExpires = request.UtcTimeSubmitted.Value.Add(voteTime);
        request.UserName = Context.User.Username;
        request.GlobalName = Context.User.GlobalName ?? string.Empty;

        try
        {
            await modifyThread;

            // send the vote polls
            await Parallel.ForEachAsync(request.RequestedRoles, async (link, _) =>
            {
                IRole? discordRole = Context.Guild.Roles.FirstOrDefault(r => r.Id == link.RoleId);

                string question = $"Should {Context.User.GlobalName ?? Context.User.Username} receive the role {discordRole?.Name ?? link.RoleId.ToString(CultureInfo.InvariantCulture)}?";
                IMessage poll = await thread.SendMessageAsync(poll: _pollFactory.CreateYesNoPoll(question, voteTime));

                link.PollMessageId = poll.Id;
            });

            foreach (ReviewRequestRole link in request.RequestedRoles)
                _dbContext.Update(link);

            // update request so the vote expires just after the poll ends instead of just before
            request.UtcTimeVoteExpires = _timeProvider.GetUtcNow().UtcDateTime.Add(voteTime);

            await deferTask;
            await Context.Interaction.DeleteOriginalResponseAsync();
        }
        finally
        {
            _dbContext.Update(request);
            Task saveTask = _dbContext.SaveChangesAsync();
            _scheduler.StartVoteTimer(request);
            await saveTask;
        }
    }

    private async Task<ReviewRequest?> ValidateButtonPress(bool isCancel)
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

        ReviewRequest? request = null;
        if (thread != null)
        {
            request = await _dbContext.ReviewRequests
                .Include(x => x.RequestedRoles)
                .FirstOrDefaultAsync(req => req.UserId == userId && req.ThreadId == thread.Id);
        }

        if (request != null)
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
