using Discord;
using UnturnedModdingCollective.Models;

namespace UnturnedModdingCollective.API;
public interface IPersistingRoleService
{
    Task CheckMemberRoles(IGuildUser user, CancellationToken token = default);
    Task CheckMemberRoles(ulong userId, ulong guildId, CancellationToken token = default);
    Task<IReadOnlyList<PersistingRole>> GetPersistingRoles(ulong user, ulong roleId, CancellationToken token = default);
    Task<IReadOnlyList<PersistingRole>> GetPersistingRoles(ulong user, CancellationToken token = default);
    Task AddPersistingRole(ulong userId, ulong guildId, ulong roleId, TimeSpan? activeTime, ulong addedByUserId, CancellationToken token = default);
    Task AddPersistingRole(ulong userId, ulong guildId, ulong roleId, DateTime? activeUntil, ulong addedByUserId, CancellationToken token = default);
    Task AddPersistingRoles(IEnumerable<PersistingRole> roles, CancellationToken token = default);
    Task<int> RemovePersistingRoles(ulong userId, ulong roleId, ulong guildId, CancellationToken token = default);
    Task<bool> RemovePersistingRole(PersistingRole role, CancellationToken token = default);
}