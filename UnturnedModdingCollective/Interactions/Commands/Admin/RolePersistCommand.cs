using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;
using UnturnedModdingCollective.API;
using UnturnedModdingCollective.Models;
using UnturnedModdingCollective.Services;
using UnturnedModdingCollective.Util;

namespace UnturnedModdingCollective.Interactions.Commands.Admin;

[Discord.Interactions.Group("role-persist", "Allows adding and removing roles that persist after leaving.")]
[CommandContextType(InteractionContextType.Guild)]
public class RolePersistCommand : InteractionModuleBase<SocketInteractionContext<SocketSlashCommand>>
{
    private readonly IPersistingRoleService _persistingRoles;
    private readonly TimeProvider _timeProvider;
    private readonly BotDbContext _dbContext;
    private readonly EmbedFactory _embedFactory;
    public RolePersistCommand(IPersistingRoleService persistingRoles, TimeProvider timeProvider, BotDbContext dbContext, EmbedFactory embedFactory)
    {
        _persistingRoles = persistingRoles;
        _timeProvider = timeProvider;
        _dbContext = dbContext;
        _embedFactory = embedFactory;
    }

    [SlashCommand("add", "Add a new role that will persist after leaving. Expire in format '1d 3hr 5min etc' or 'permanent'.")]
    public async Task AddRolePersist(IUser user, IRole role, [Name("expire-in")] string expireInTimeString = "permanent")
    {
        IGuildUser caller = (IGuildUser)Context.User;

        IGuildUser myUser = Context.Guild.GetUser(Context.Client.CurrentUser.Id);

        if (!myUser.GuildPermissions.Has(GuildPermission.ManageRoles))
        {
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                    .WithColor(Color.Red)
                    .WithTitle("No Manage Roles Permission")
                    .WithDescription("The bot must have the `manage roles` or `administrator` permission.")
                    .Build(),
                ephemeral: true
            );
            return;
        }

        if (caller.Id != Context.Guild.OwnerId && !caller.GuildPermissions.Has(GuildPermission.ManageRoles))
        {
            await Context.Interaction.RespondAsync(embed: _embedFactory.NoPermissionsEmbed(GuildPermission.ManageRoles).Build(), ephemeral: true);
            return;
        }

        await Context.Interaction.DeferAsync();

        TimeSpan? expireIn = TimeUtility.ParseTimespan(expireInTimeString);
        if (expireIn == Timeout.InfiniteTimeSpan)
            expireIn = null;

        DateTime? activeUntil = expireIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.Add(expireIn.Value) : null;
        IReadOnlyList<PersistingRole> existingRoles = await _persistingRoles.GetPersistingRoles(user.Id, role.Guild.Id, role.Id);

        bool alreadyActive;
        if (!activeUntil.HasValue)
        {
            alreadyActive = existingRoles.Any(x => !x.UtcRemoveAt.HasValue);
        }
        else
        {
            alreadyActive = existingRoles.Any(x => !x.UtcRemoveAt.HasValue || activeUntil.Value < x.UtcRemoveAt.Value);
        }

        if (alreadyActive)
        {
            await Context.Interaction.FollowupAsync(embed: new EmbedBuilder()
                    .WithColor(Color.Red)
                    .WithTitle("Duplicate Role Persist")
                    .WithDescription($"There's already a role persist for this role on {user.Mention} for at least the time requested.")
                    .Build(),
                ephemeral: true
            );
            return;
        }

        await _persistingRoles.AddPersistingRole(user.Id, Context.Guild.Id, role.Id, activeUntil, caller.Id);

        EmbedBuilder embed = new EmbedBuilder()
            .WithColor(Color.Green)
            .WithAuthor(Context.User.GlobalName ?? Context.User.Username, Context.User.GetAvatarUrl())
            .WithTitle("Added Persisting Role");

        embed.Description = 
            $"{user.Mention} will have {role.Mention} until {(
                !activeUntil.HasValue ? "it's removed" : TimestampTag.FormatFromDateTime(activeUntil.Value, TimestampTagStyles.ShortDateTime)
            )}.";

        await Context.Interaction.FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("remove", "Remove the given role from the user and keep it from persisting.")]
    public async Task RemoveRolePersist(IUser user, IRole role)
    {
        IGuildUser caller = (IGuildUser)Context.User;

        IGuildUser myUser = Context.Guild.GetUser(Context.Client.CurrentUser.Id);

        if (caller.Id != Context.Guild.OwnerId && !caller.GuildPermissions.Has(GuildPermission.ManageRoles))
        {
            await Context.Interaction.RespondAsync(embed: _embedFactory.NoPermissionsEmbed(GuildPermission.ManageRoles).Build(), ephemeral: true);
            return;
        }

        if (!myUser.GuildPermissions.Has(GuildPermission.ManageRoles))
        {
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                    .WithColor(Color.Red)
                    .WithTitle("No Manage Roles Permission")
                    .WithDescription("The bot must have the `manage roles` or `administrator` permission.")
                    .Build(),
                ephemeral: true
            );
            return;
        }

        await Context.Interaction.DeferAsync();

        int rolesRemoved = await _persistingRoles.RemovePersistingRoles(user.Id, role.Id, Context.Guild.Id);
        
        if (rolesRemoved == 0)
        {
            await Context.Interaction.FollowupAsync(embed: new EmbedBuilder()
                    .WithColor(Color.Red)
                    .WithTitle("No Persisting Role")
                    .WithDescription($"There's isn't a persisting {role.Mention} on {user.Mention}.")
                    .Build(),
                ephemeral: true
            );
            return;
        }

        EmbedBuilder embed = new EmbedBuilder()
            .WithColor(Color.Green)
            .WithAuthor(Context.User.GlobalName ?? Context.User.Username, Context.User.GetAvatarUrl())
            .WithTitle($"Removed Persisting Role{(rolesRemoved == 1 ? string.Empty : "s")}")
            .WithDescription(rolesRemoved == 1
                ? $"Removed persisting {role.Mention} from {user.Mention}."
                : $"Removed {rolesRemoved} persisting {role.Mention}'s from {user.Mention}");

        await Context.Interaction.FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("check", "See what roles are persisting on a user.")]
    public async Task CheckRolePersist(IUser user)
    {
        IReadOnlyList<PersistingRole> roles = await _persistingRoles.GetPersistingRoles(user.Id, Context.Guild.Id);

        if (roles.Count == 0)
        {
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                    .WithColor(Color.Green)
                    .WithTitle("No Persisting Roles")
                    .WithDescription($"There aren't any persisting roles on {user.Mention}.")
                    .Build(),
                ephemeral: true
            );
            return;
        }

        await Context.Interaction.DeferAsync();

        List<ReviewRequestRole>? roleRequests = roles.All(x => x.UserAddedBy != 0ul)
            ? null
            : await _dbContext.Set<ReviewRequestRole>()
                .Include(x => x.Request)
                .Include(x => x.Votes)
                .Where(x => x.Request!.UserId == user.Id && x.UtcTimeClosed.HasValue)
                .OrderByDescending(x => x.UtcTimeClosed)
                .ToListAsync();

        StringBuilder descBuilder = new StringBuilder();
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        descBuilder.Append("**")
                   .Append(roles.Count(role => !role.UtcRemoveAt.HasValue || now < role.UtcRemoveAt.Value).ToString(CultureInfo.InvariantCulture))
                   .Append(" Active Role")
                   .Append(roles.Count == 1 ? string.Empty : "s")
                   .Append("**");

        int lastStartIndex = -1;
        foreach (PersistingRole role in roles)
        {
            if (role.UtcRemoveAt.HasValue && now >= role.UtcRemoveAt.Value)
                continue;

            string emoji = GetClockProgressEmoji(now, role);

            descBuilder.Append(Environment.NewLine)
                       .Append(Environment.NewLine)
                       .Append(emoji)
                       .Append(' ');

            IRole? discordRole = Context.Guild.Roles.FirstOrDefault(x => x.Id == role.RoleId);

            if (discordRole != null)
                descBuilder.Append(discordRole.Mention);
            else
                descBuilder.Append($"Unknown role - id: `{role.RoleId}`");

            if (role.UtcRemoveAt.HasValue)
            {
                descBuilder.Append(" | Expires ")
                           .Append(TimestampTag.FormatFromDateTime(DateTime.SpecifyKind(role.UtcRemoveAt.Value, DateTimeKind.Utc), TimestampTagStyles.Relative));
            }

            if (role.UserAddedBy != 0ul)
            {
                descBuilder.Append(Environment.NewLine)
                           .Append("Added by <@")
                           .Append(role.UserAddedBy.ToString(CultureInfo.InvariantCulture))
                           .Append('>');
            }
            else
            {
                descBuilder.Append(Environment.NewLine)
                           .Append("Added by vote");

                ReviewRequestRole? request = roleRequests!.FirstOrDefault(x => x.RoleId == role.RoleId);

                if (request != null && request.YesVotes + request.NoVotes > 0)
                {
                    descBuilder.Append(" `")
                        .Append(request.YesVotes.ToString(CultureInfo.InvariantCulture))
                        .Append('-')
                        .Append(request.NoVotes.ToString(CultureInfo.InvariantCulture))
                        .Append('`');
                }
            }

            descBuilder.Append(" at ")
                       .Append(TimestampTag.FormatFromDateTime(DateTime.SpecifyKind(role.UtcTimestamp, DateTimeKind.Utc), TimestampTagStyles.ShortDateTime));

            if (descBuilder.Length > 4096)
                break;

            lastStartIndex = descBuilder.Length;
        }

        // cut off the last row and add '...' in the rare case the description is too long
        if (descBuilder.Length > 4096)
        {
            descBuilder.Remove(lastStartIndex, descBuilder.Length - lastStartIndex);

            if (lastStartIndex <= 4093)
                descBuilder.Append('.', 3);
        }

        await Context.Interaction.FollowupAsync(embed: new EmbedBuilder()
            .WithColor(Color.Green)
            .WithTitle("Persisting Roles")
            .WithDescription(descBuilder.ToString())
            .Build(),
            ephemeral: true
        );
    }
    private static string GetClockProgressEmoji(DateTime now, PersistingRole role)
    {
        if (!role.UtcRemoveAt.HasValue)
            return ":infinity:";

        // gets the emoji of a clock on the hour going from 12 o'clock to 11 o'clock
        // based on how close the role is to being over
        TimeSpan timeUntilDone = role.UtcRemoveAt.Value - now;
        TimeSpan totalTime = role.UtcRemoveAt.Value - role.UtcTimestamp;

        double progress = 1 - timeUntilDone.Ticks / (double)totalTime.Ticks;

        // clock face range one o'clock to twelve o'clock
        int clockFace = (int)Math.Round(11 * progress);

        clockFace = (clockFace + 11) % 12;

        return $":clock{(clockFace + 1).ToString(CultureInfo.InvariantCulture)}:";
    }
}
