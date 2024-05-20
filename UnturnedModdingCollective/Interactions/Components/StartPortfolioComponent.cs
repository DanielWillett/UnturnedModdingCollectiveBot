using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using UnturnedModdingCollective.Models;
using UnturnedModdingCollective.Services;

namespace UnturnedModdingCollective.Interactions.Components;
public class StartPortfolioComponent : InteractionModuleBase<SocketInteractionContext<SocketMessageComponent>>
{
    internal const string StartPortfolioMenu = "start_portfolio_sel";

    private readonly BotDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    public StartPortfolioComponent(BotDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    [ComponentInteraction(StartPortfolioMenu)]
    public async Task ReceiveStartPortfolio()
    {
        await Context.Interaction.DeferAsync();

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
        List<ReviewRequestRole> pastApplications = await _dbContext.Set<ReviewRequestRole>()
            .Where(x => x.Request!.UserId == user.Id && !x.Request!.ResubmitApprover.HasValue && !x.Request.UtcTimeCancelled.HasValue && !x.Request.ClosedUnderError)
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

            await Context.Interaction.FollowupAsync(ephemeral: true, embed: eb.Build());
            return;
        }

        ITextChannel channel = (ITextChannel)await Context.Interaction.GetChannelAsync();

        string threadChannelName = Context.User.GlobalName + " Portfolio";

        IThreadChannel thread = await channel.CreateThreadAsync(threadChannelName, ThreadType.PrivateThread, ThreadArchiveDuration.OneWeek, invitable: false);

        Task addUserTask = thread.AddUserAsync(user);

        EmbedBuilder postEmbed = new EmbedBuilder()
            .WithTitle("Member Review")
            .WithDescription($"Please post your portfolio in {thread.Mention} and click **Submit** when you're done.")
            .WithColor(Color.DarkerGrey);

        await Context.Interaction.FollowupAsync(embed: postEmbed.Build(), ephemeral: true);

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
            GlobalName = user.GlobalName ?? user.Username,
            UserName = user.Username,
            ThreadId = thread.Id,
            UtcTimeStarted = _timeProvider.GetUtcNow().UtcDateTime,
            RolesAppliedFor = allowedRoles.Count
        };

        foreach (ApplicableRole role in allowedRoles)
        {
            request.RequestedRoles.Add(new ReviewRequestRole
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
    }
}
