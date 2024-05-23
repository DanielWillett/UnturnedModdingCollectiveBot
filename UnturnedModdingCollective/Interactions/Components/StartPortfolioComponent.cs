using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using UnturnedModdingCollective.API;
using UnturnedModdingCollective.Models;
using UnturnedModdingCollective.Models.Config;
using UnturnedModdingCollective.Services;
// ReSharper disable InconsistentlySynchronizedField

namespace UnturnedModdingCollective.Interactions.Components;
public class StartPortfolioComponent : InteractionModuleBase<SocketInteractionContext<SocketMessageComponent>>
{
    internal const string StartPortfolioMenu = "start_portfolio_sel";

    private readonly BotDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILiveConfiguration<LiveConfiguration> _liveConfig;
    public StartPortfolioComponent(BotDbContext dbContext, TimeProvider timeProvider, ILiveConfiguration<LiveConfiguration> liveConfig)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _liveConfig = liveConfig;
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

        TimeSpan allowedGapTime = _liveConfig.Configuraiton.TimeBetweenApplications;

        double seconds = allowedGapTime.TotalSeconds;
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        // remove all roles the user has applied for in the past
        List<ReviewRequestRole> pastApplications = await _dbContext.Set<ReviewRequestRole>()
            .Where(x => x.Request!.UserId == user.Id
                        && !x.ResubmitApprover.HasValue
                        && (x.UtcTimeCancelled.HasValue
                            || seconds > 0 && EF.Functions.DateDiffSecond(x.UtcTimeSubmitted, now) < seconds)
                        && !x.ClosedUnderError)
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

        // create database entry
        ReviewRequest request = new ReviewRequest
        {
            UserId = user.Id,
            Steam64 = 0,
            GlobalName = user.GlobalName ?? user.Username,
            UserName = user.Username,
            UtcTimeStarted = _timeProvider.GetUtcNow().UtcDateTime,
            RolesAppliedFor = allowedRoles.Count
        };

        await Parallel.ForEachAsync(allowedRoles, async (role, _) =>
        {
            ITextChannel channel = (ITextChannel)await Context.Interaction.GetChannelAsync();

            IRole? discordRole = Context.Guild.Roles.FirstOrDefault(r => r.Id == role.RoleId);
            string roleName = discordRole?.Name ?? role.RoleId.ToString(CultureInfo.InvariantCulture);

            string threadChannelName = $"{Context.User.GlobalName} {roleName} Portfolio";

            IThreadChannel thread = await channel.CreateThreadAsync(threadChannelName, ThreadType.PrivateThread, ThreadArchiveDuration.OneWeek, invitable: false);

            Task addUserTask = thread.AddUserAsync(user);

            EmbedBuilder postEmbed = new EmbedBuilder()
                .WithTitle("Member Review - " + roleName)
                .WithDescription($"Please post your portfolio in {thread.Mention} and click **Submit** when you're done.")
                .WithColor(Color.DarkerGrey);

            await Context.Interaction.FollowupAsync(embed: postEmbed.Build(), ephemeral: true);

            postEmbed.Description = "Please post your portfolio for the following role and click **Submit** when you're done.";
            postEmbed.Color = Color.Green;
            postEmbed.AddField(
                name: roleName,
                value: role.Description
            );

            // show all the roles they couldn't apply for.
            foreach (ApplicableRole applyRole in requestedRoles.Where(x => !allowedRoles.Contains(x)).Take(25 /* max fields */))
            {
                IRole? applyDiscordRole = Context.Guild.Roles.FirstOrDefault(r => r.Id == applyRole.RoleId);

                postEmbed.AddField(
                    name: applyDiscordRole?.Name ?? applyRole.RoleId.ToString(CultureInfo.InvariantCulture),
                    value: ":x: You can't apply for this role right now."
                );
            }

            lock (request.RequestedRoles)
            {
                ReviewRequestRole roleInfo = new ReviewRequestRole
                {
                    Request = request,
                    RoleId = role.RoleId,
                    ThreadId = thread.Id
                };
                request.RequestedRoles.Add(roleInfo);
                _dbContext.Add(roleInfo);
            }

            await addUserTask;

            string userIdStr = user.Id.ToString(CultureInfo.InvariantCulture);

            ComponentBuilder components = new ComponentBuilder()
                .WithButton(ButtonBuilder.CreatePrimaryButton("Submit", SubmitPortfolioComponent.SubmitPortfolioButtonPrefix + userIdStr))
                .WithButton(ButtonBuilder.CreateDangerButton("Cancel", SubmitPortfolioComponent.CancelPortfolioButtonPrefix + userIdStr));

            await thread.SendMessageAsync(embed: postEmbed.Build(), components: components.Build());
        });

        _dbContext.ReviewRequests.Add(request);
        await _dbContext.SaveChangesAsync();

    }
}
