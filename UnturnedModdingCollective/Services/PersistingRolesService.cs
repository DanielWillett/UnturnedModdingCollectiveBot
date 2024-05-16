using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UnturnedModdingCollective.API;
using UnturnedModdingCollective.Models;
using UnturnedModdingCollective.Util;

namespace UnturnedModdingCollective.Services;
public class PersistingRolesService : IPersistingRoleService, IHostedService, IDisposable
{
    private readonly ILogger<PersistingRolesService> _logger;
    private readonly IServiceScope _dbContextScope;
    private readonly BotDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly DiscordSocketClient _discordClient;
    private readonly SemaphoreSlim _dbContextGate = new SemaphoreSlim(1, 1);
    public PersistingRolesService(IServiceProvider serviceProvider)
    {
        _logger = serviceProvider.GetRequiredService<ILogger<PersistingRolesService>>();
        _dbContextScope = serviceProvider.CreateScope();
        _dbContext = _dbContextScope.ServiceProvider.GetRequiredService<BotDbContext>();
        _dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

        _timeProvider = serviceProvider.GetRequiredService<TimeProvider>();
        _discordClient = serviceProvider.GetRequiredService<DiscordSocketClient>();
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        _discordClient.UserJoined += OnUserJoined;
        _discordClient.GuildMemberUpdated += OnGuildMemberUpdated;
        _discordClient.Ready += OnReady;
        return Task.CompletedTask;
    }
    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        _discordClient.UserJoined -= OnUserJoined;
        _discordClient.GuildMemberUpdated -= OnGuildMemberUpdated;
        _discordClient.Ready -= OnReady;
        return _dbContextGate.WaitAsync(cancellationToken);
    }

    void IDisposable.Dispose()
    {
        _dbContextScope.Dispose();
    }

    public async Task CheckMemberRoles(IGuildUser user, CancellationToken token = default)
    {
        await _dbContextGate.WaitAsync(token);
        try
        {
            List<PersistingRole> allRoles = await _dbContext.PersistingRoles
                .Where(role => role.GuildId == user.GuildId && role.UserId == user.Id)
                .ToListAsync(token);

            await ApplyPersistingRoles(user, allRoles, token);
        }
        finally
        {
            _dbContextGate.Release();
        }
    }
    private async Task OnReady()
    {
        int rolesChecked = 0;
        await _dbContextGate.WaitAsync();
        try
        {
            // check for players missing roles since the last startup
            List<PersistingRole> allRoles = await _dbContext.PersistingRoles
                .Where(x => _discordClient.Guilds.Select(x => x.Id).Contains(x.GuildId))
                .OrderBy(x => x.GuildId)
                .ThenBy(x => x.UserId)
                .ToListAsync();

            IGuild? guild = null;
            IGuildUser? user = null;
            for (int i = 0; i < allRoles.Count; ++i)
            {
                PersistingRole thisRole = allRoles[i];
                if (guild == null || guild.Id != thisRole.GuildId)
                {
                    guild = _discordClient.Guilds.FirstOrDefault(x => x.Id == thisRole.GuildId);
                    if (guild == null)
                    {
                        // skip to next guild
                        do ++i;
                        while (i < allRoles.Count && thisRole.GuildId == allRoles[i].GuildId);
                        --i;
                        continue;
                    }

                    // cache as many users as possible
                    _ = await guild.GetUsersAsync();
                }

                if (user == null || user.Id != thisRole.GuildId)
                {
                    user = await guild.GetUserAsync(thisRole.UserId);
                    if (user == null)
                    {
                        // skip to next user
                        do ++i;
                        while (i < allRoles.Count && thisRole.UserId == allRoles[i].UserId);
                        --i;
                        continue;
                    }
                }

                int nextUserIndex = allRoles.FindIndex(i + 1, x => x.GuildId != thisRole.GuildId || x.UserId != thisRole.UserId);
                if (nextUserIndex == -1)
                    nextUserIndex = allRoles.Count;

                rolesChecked += nextUserIndex - i;
                await ApplyPersistingRoles(user, allRoles.Skip(i).Take(nextUserIndex - i));
            }
        }
        finally
        {
            _dbContextGate.Release();
        }

        _logger.LogInformation("Checked {0} persisting roles for updates.", rolesChecked);
    }
    private async Task ApplyPersistingRoles(IGuildUser user, IEnumerable<PersistingRole> persistingRoles, CancellationToken token = default)
    {
        IReadOnlyCollection<ulong> roles = user.RoleIds;

        List<ulong>? toRemove = null, toAdd = null;

        foreach (PersistingRole role in persistingRoles)
        {
            if (role.IsExpired(_timeProvider))
            {
                if (roles.Contains(role.RoleId))
                    (toRemove ??= []).Add(role.RoleId);

                continue;
            }

            if (!roles.Contains(role.RoleId))
            {
                (toAdd ??= []).Add(role.RoleId);
            }
        }

        if (toRemove != null)
            await user.RemoveRolesAsync(toRemove, token == default ? null : new RequestOptions { CancelToken = token });

        if (toAdd != null)
            await user.AddRolesAsync(toAdd, token == default ? null : new RequestOptions { CancelToken = token });
    }
    public async Task CheckMemberRoles(ulong userId, ulong guildId, CancellationToken token = default)
    {
        IGuild? guild = _discordClient.GetGuild(guildId);
        IGuildUser? user = guild == null ? null : await guild.GetUserAsync(userId, CacheMode.AllowDownload, new RequestOptions { CancelToken = token });

        if (user != null)
            await CheckMemberRoles(user, token);
    }
    private void InvokeUserUpdated(IGuildUser user)
    {
        Task.Run(async () =>
        {
            try
            {
                await CheckMemberRoles(user);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update member roles.");
            }
        });
    }
    private Task OnGuildMemberUpdated(Cacheable<SocketGuildUser, ulong> arg1, SocketGuildUser arg2)
    {
        InvokeUserUpdated(arg2);
        return Task.CompletedTask;
    }
    private Task OnUserJoined(SocketGuildUser arg)
    {
        InvokeUserUpdated(arg);
        return Task.CompletedTask;
    }
    public async Task<IReadOnlyList<PersistingRole>> GetPersistingRoles(ulong user, ulong roleId, CancellationToken token = default)
    {
        await _dbContextGate.WaitAsync(token);
        try
        {
            return await _dbContext.PersistingRoles
                .Where(role => role.UserId == user && role.RoleId == roleId)
                .ToListAsync(token);
        }
        finally
        {
            _dbContextGate.Release();
        }
    }

    public async Task<IReadOnlyList<PersistingRole>> GetPersistingRoles(ulong user, CancellationToken token = default)
    {
        await _dbContextGate.WaitAsync(token);
        try
        {
            return await _dbContext.PersistingRoles
                .Where(role => role.UserId == user)
                .ToListAsync(token);
        }
        finally
        {
            _dbContextGate.Release();
        }
    }

    public Task AddPersistingRole(ulong userId, ulong guildId, ulong roleId, TimeSpan? activeTime, ulong addedByUserId, CancellationToken token = default)
        => AddPersistingRole(userId, guildId, roleId, activeTime.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.Add(activeTime.Value) : null, addedByUserId, token);
    public async Task AddPersistingRole(ulong userId, ulong guildId, ulong roleId, DateTime? activeUntil, ulong addedByUserId, CancellationToken token = default)
    {
        await _dbContextGate.WaitAsync(token);
        try
        {
            PersistingRole role = new PersistingRole
            {
                UserId = userId,
                GuildId = guildId,
                UserAddedBy = addedByUserId,
                RoleId = roleId,
                UtcRemoveAt = activeUntil
            };

            _dbContext.PersistingRoles.Add(role);

            await _dbContext.SaveChangesAsync(token);
        }
        finally
        {
            _dbContextGate.Release();
        }

        await CheckMemberRoles(userId, guildId, token);
    }

    public async Task AddPersistingRoles(IEnumerable<PersistingRole> roles, CancellationToken token = default)
    {
        List<PersistingRole> rolesList = [ ..roles ];
        await _dbContextGate.WaitAsync(token);
        try
        {
            _dbContext.PersistingRoles.AddRange(rolesList);

            await _dbContext.SaveChangesAsync(token);
        }
        finally
        {
            _dbContextGate.Release();
        }

        foreach (PersistingRole role in rolesList.DistinctBy(x => (x.UserId, x.GuildId)))
        {
            await CheckMemberRoles(role.UserId, role.GuildId, token);
        }
    }

    public async Task<int> RemovePersistingRoles(ulong userId, ulong roleId, ulong guildId, CancellationToken token = default)
    {
        int rowsUpdated;
        await _dbContextGate.WaitAsync(token);
        try
        {
            foreach (EntityEntry<PersistingRole> entityEntry in _dbContext.ChangeTracker
                         .Entries<PersistingRole>()
                         .Where(r => r.Entity.UserId == userId && r.Entity.RoleId == roleId && r.Entity.GuildId == guildId)
                     )
            {
                entityEntry.State = EntityState.Detached;
            }

            rowsUpdated = await _dbContext.RemoveWhere<PersistingRole>(role => role.UserId == userId && role.RoleId == roleId && role.GuildId == guildId, token);
        }
        finally
        {
            _dbContextGate.Release();
        }

        await CheckMemberRoles(userId, guildId, token);
        return rowsUpdated;
    }

    public async Task<bool> RemovePersistingRole(PersistingRole role, CancellationToken token = default)
    {
        bool updated;
        await _dbContextGate.WaitAsync(token);
        try
        {
            _dbContext.Remove(role);
            updated = await _dbContext.SaveChangesAsync(token) > 0;
        }
        finally
        {
            _dbContextGate.Release();
        }

        await CheckMemberRoles(role.UserId, role.GuildId, token);
        return updated;
    }
}
