using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Globalization;
using UnturnedModdingCollective.Models;
using UnturnedModdingCollective.Services;

namespace UnturnedModdingCollective.Interactions.Components;
public class StartPortfolioComponent : InteractionModuleBase<SocketInteractionContext<SocketMessageComponent>>
{
    internal const string StartPortfolioMenu = "start_portfolio_sel";

    private readonly BotDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly EmbedFactory _embedFactory;
    public StartPortfolioComponent(BotDbContext dbContext, TimeProvider timeProvider, EmbedFactory embedFactory)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _embedFactory = embedFactory;
    }

    [ComponentInteraction(StartPortfolioMenu)]
    public async Task ReceiveStartPortfolio()
    {
        IGuildUser user = (IGuildUser)Context.User;

        IReadOnlyCollection<string> selectedRoleIds = Context.Interaction.Data.Values;

        int[] ids = new int[selectedRoleIds.Count];
        int index = -1;
        foreach (string roleId in selectedRoleIds)
        {
            int.TryParse(roleId, NumberStyles.Number, CultureInfo.InvariantCulture, out int id);
            ids[++index] = id;
        }

        List<ApplicableRole> requestedRoles = await _dbContext.ApplicableRoles.Where(role => ids.Contains(role.Id)).ToListAsync();

        List<ApplicableRole> allowedRoles = [.. requestedRoles];

        // remove all roles the user already has
        allowedRoles.RemoveAll(role => user.RoleIds.Contains(role.RoleId));

        // remove all roles the user has applied for in the past
        List<ReviewRequestRoleLink> pastApplications = await _dbContext.Set<ReviewRequestRoleLink>()
            .Where(x => x.Request!.UserId == user.Id && !x.Request!.ResubmitApprover.HasValue && !x.Request.UtcTimeCancelled.HasValue)
            .ToListAsync();

        allowedRoles.RemoveAll(role => pastApplications.Any(pastApplication => pastApplication.RoleId == role.RoleId));

        // no roles left to add
        if (allowedRoles.Count == 0)
        {
            EmbedBuilder eb = new EmbedBuilder()
                .WithColor(Color.Red)
                .WithTitle("No Roles Selected")
                .WithDescription("You must apply for at least one role.");

            foreach (ApplicableRole role in requestedRoles.Take(25 /* max fields */))
            {
                IRole? discordRole = Context.Guild.Roles.FirstOrDefault(r => r.Id == role.RoleId);

                bool skippedBecauseAlreadyPossessed = user.RoleIds.Contains(role.RoleId);

                eb.AddField(
                    name: discordRole?.Name ?? role.RoleId.ToString(CultureInfo.InvariantCulture),
                    value: skippedBecauseAlreadyPossessed ? "You already have this role" : "You've already applied for this role",
                    inline: true
                );
            }

            await Context.Interaction.RespondAsync(ephemeral: true, embed: eb.Build());
            return;
        }

        ITextChannel channel = (ITextChannel)await Context.Interaction.GetChannelAsync();

        EmbedBuilder postEmbed = new EmbedBuilder()
            .WithDescription("Please post your portfolio in the created thread and click **Submit** when you're done.")
            .WithColor(Color.DarkerGrey)
            .WithTitle("Member Review");

        await Context.Interaction.RespondAsync(embed: postEmbed.Build());
        IUserMessage message = await Context.Interaction.GetOriginalResponseAsync();

        string threadChannelName = Context.User.GlobalName + " Portfolio";

        IThreadChannel thread = await channel.CreateThreadAsync(threadChannelName, ThreadType.PrivateThread, ThreadArchiveDuration.OneWeek, message);

        Task addUserTask = thread.AddUserAsync(user);

        // edit the thread mention into the original message

        postEmbed.Description = $"Please post your portfolio in {thread.Mention} and click **Submit** when you're done.";
        Task modifyMessageTask = Context.Interaction.ModifyOriginalResponseAsync(msg => msg.Embeds = new Embed[] { postEmbed.Build() });

        postEmbed.Description = "Please post your portfolio for the following roles and click **Submit** when you're done.";
        postEmbed.Color = Color.Green;
        foreach (ApplicableRole role in requestedRoles.Take(25 /* max fields */))
        {
            IRole? discordRole = Context.Guild.Roles.FirstOrDefault(r => r.Id == role.RoleId);

            postEmbed.AddField(
                name: discordRole?.Name ?? role.RoleId.ToString(CultureInfo.InvariantCulture),
                value: allowedRoles.Contains(role) ? role.Description : ":x: You can't apply for this role right now."
            );
        }

        // create database entry
        ReviewRequest request = new ReviewRequest
        {
            UserId = user.Id,
            Steam64 = 0,
            GlobalName = user.GlobalName,
            UserName = user.Username,
            ThreadId = thread.Id,
            MessageId = message.Id,
            MessageChannelId = message.Channel.Id,
            UtcTimeStarted = _timeProvider.GetUtcNow().UtcDateTime
        };

        foreach (ApplicableRole role in allowedRoles)
        {
            request.RequestedRoles.Add(new ReviewRequestRoleLink
            {
                Request = request,
                RoleId = role.RoleId
            });
        }

        _dbContext.ReviewRequests.Add(request);
        await _dbContext.SaveChangesAsync();

        await addUserTask;

        string userIdStr = user.Id.ToString(CultureInfo.InvariantCulture);
        ComponentBuilder components = new ComponentBuilder()
            .WithButton(ButtonBuilder.CreatePrimaryButton("Submit", SubmitPortfolioComponent.SubmitPortfolioButtonPrefix + userIdStr))
            .WithButton(ButtonBuilder.CreateDangerButton("Cancel", SubmitPortfolioComponent.CancelPortfolioButtonPrefix + userIdStr));

        await thread.SendMessageAsync(embed: postEmbed.Build(), components: components.Build());

        await modifyMessageTask;

        // move setup message back to the bottom of the channel

        // don't block gateway
        EmbedBuilder newMsgEmbed = new EmbedBuilder();
        ComponentBuilder newMsgComponents = new ComponentBuilder();

        IMessage srcMessage = Context.Interaction.Message;

        // need to run this before task.run, otherwise the lifetime will end before this returns
        await _embedFactory.BuildMembershipApplicationMessage(Context.Guild, newMsgEmbed, newMsgComponents);

        _ = Task.Run(async () =>
        {
            try
            {
                Task sendNewMessageTask = srcMessage.Channel.SendMessageAsync(
                    "_ _" /* leave a space */,
                    embed: newMsgEmbed.Build(),
                    components: newMsgComponents.Build()
                );
                await srcMessage.DeleteAsync();
                await sendNewMessageTask;
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Error moving source message.");
            }
        });
    }
}
