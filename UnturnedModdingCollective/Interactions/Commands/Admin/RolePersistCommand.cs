using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.WebSocket;
using UnturnedModdingCollective.API;
using UnturnedModdingCollective.Models;
using UnturnedModdingCollective.Util;

namespace UnturnedModdingCollective.Interactions.Commands.Admin;

[Discord.Interactions.Group("role-persist", "Allows adding and removing roles that persist after leaving.")]
[CommandContextType(InteractionContextType.Guild)]
public class RolePersistCommand : InteractionModuleBase<SocketInteractionContext<SocketSlashCommand>>
{
    private readonly IPersistingRoleService _persistingRoles;
    private readonly TimeProvider _timeProvider;
    public RolePersistCommand(IPersistingRoleService persistingRoles, TimeProvider timeProvider)
    {
        _persistingRoles = persistingRoles;
        _timeProvider = timeProvider;
    }
    [SlashCommand("add", "Add a new role that will persist after leaving. Time string is in the format '1d 3hr 5min etc' can also be 'permanent'.")]
    public async Task AddRolePersist(IRole role, IUser user, [Name("expire-in")] string expireInTimeString = "permanent")
    {
        IGuildUser caller = (IGuildUser)Context.User;

        if (caller.Id != Context.Guild.OwnerId && !caller.GuildPermissions.Has(GuildPermission.ManageRoles))
        {
            await Context.Interaction.RespondAsync("No permissions.", ephemeral: true);
            return;
        }

        TimeSpan? expireIn = TimeUtility.ParseTimespan(expireInTimeString);
        if (expireIn == Timeout.InfiniteTimeSpan)
            expireIn = null;

        DateTime? activeUntil = expireIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.Add(expireIn.Value) : null;
        IReadOnlyList<PersistingRole> existingRoles = await _persistingRoles.GetPersistingRoles(user.Id, role.Id);

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
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                    .WithColor(Color.Red)
                    .WithTitle("Duplicate Role Persist")
                    .WithDescription($"There's already a role persist for this role on {user.Mention} for at least the time requested.")
                    .Build(),
                ephemeral: true
            );
            return;
        }

    }
}
