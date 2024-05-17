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
    private Timer? _currentTimer;
    private DateTime _currentTimerComplete = DateTime.MaxValue;
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

    public Task CheckMemberRoles(IGuildUser user, CancellationToken token = default)
        => CheckMemberRoles(user, 0, token);
    private async Task CheckMemberRoles(IGuildUser user, ulong roleIdToRemove, CancellationToken token = default)
    {
        await _dbContextGate.WaitAsync(token);
        try
        {
            List<PersistingRole> allRoles = await _dbContext.PersistingRoles
                .Where(role => (!role.ExpiryProcessed || !role.UtcRemoveAt.HasValue) && role.GuildId == user.GuildId && role.UserId == user.Id)
                .ToListAsync(token);

            if (await ApplyPersistingRoles(user, allRoles, roleIdToRemove, token))
                await _dbContext.SaveChangesAsync(token);
        }
        finally
        {
            _dbContextGate.Release();
        }
    }
    private async Task StartNextTimer(CancellationToken token = default)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        
        List<PersistingRole> allRolesToExpire = await _dbContext.PersistingRoles
            .Where(x => !x.ExpiryProcessed && x.UtcRemoveAt.HasValue)
            .OrderBy(x => x.UtcRemoveAt)
            .ToListAsync(token);

        if (allRolesToExpire.Count == 0)
            return;

        PersistingRole? toStartTimer = allRolesToExpire.SkipWhile(x => now > x.UtcRemoveAt!.Value).FirstOrDefault();
        if (toStartTimer != null)
        {
            TimeSpan timeUntilStart = toStartTimer.UtcRemoveAt!.Value - now;

            TimerState state = new TimerState
            {
                RoleId = toStartTimer.Id
            };

            _currentTimerComplete = _timeProvider.GetUtcNow().UtcDateTime.Add(timeUntilStart);
            Timer timer = new Timer(TimerFinished, state, timeUntilStart, Timeout.InfiniteTimeSpan);
            _logger.LogInformation("Started persisting role timer for {0} ending at {1}.", toStartTimer.Id, _currentTimerComplete.ToLocalTime());
            state.Timer = timer;

            Timer? oldTimer = Interlocked.Exchange(ref _currentTimer, timer);
            if (oldTimer != null)
            {
                oldTimer.Change(Timeout.Infinite, Timeout.Infinite);
                oldTimer.Dispose();
            }
        }

        bool anySave = false;
        foreach (PersistingRole roleToRemove in allRolesToExpire.Where(x => now > x.UtcRemoveAt!.Value))
        {
            roleToRemove.ExpiryProcessed = true;
            anySave = true;
            IGuild? guild = _discordClient.GetGuild(roleToRemove.GuildId);
            if (guild == null)
                continue;
            
            IGuildUser? user = await guild.GetUserAsync(roleToRemove.UserId);
            if (user == null)
                continue;

            if (!user.RoleIds.Contains(roleToRemove.RoleId))
                continue;

            try
            {
                await user.RemoveRoleAsync(roleToRemove.RoleId);
            }
            catch (Exception ex)
            {
                roleToRemove.ExpiryProcessed = false;
                _logger.LogError(ex, "Failed to remove role {0} from {1} ({2}).", roleToRemove.RoleId, user.Id, user.Username);
            }
        }

        if (anySave)
            await _dbContext.SaveChangesAsync(token);
    }
    private void TimerFinished(object? stateBox)
    {
        if (stateBox is not TimerState state)
            return;

        Interlocked.CompareExchange(ref _currentTimer, null, state.Timer);
        Task.Run(async () =>
        {
            await state.Timer.DisposeAsync();
            await _dbContextGate.WaitAsync();
            PersistingRole? role = null;
            try
            {
                if (_currentTimer == null)
                    _currentTimerComplete = DateTime.MaxValue;

                role = await _dbContext.PersistingRoles.FirstOrDefaultAsync(x => x.Id == state.RoleId);
                
                if (role is not { ExpiryProcessed: false } || !role.IsExpired(_timeProvider))
                    return;

                role.ExpiryProcessed = true;
                _dbContext.Update(role);
                await _dbContext.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to finish timer - failed to update expiry.");
            }
            finally
            {
                try
                {
                    await StartNextTimer();
                }
                finally
                {
                    _dbContextGate.Release();
                }
            }

            try
            {
                if (role != null)
                    await CheckMemberRoles(role.UserId, role.GuildId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to finish timer - failed to check member roles.");
            }
        });
    }
    private async Task OnReady()
    {
        int rolesChecked = 0;
        await _dbContextGate.WaitAsync();
        try
        {
            // check for players missing roles since the last startup
            List<PersistingRole> allRoles = await _dbContext.PersistingRoles
                .Where(x => _discordClient.Guilds.Select(x => x.Id).Contains(x.GuildId) && (!x.ExpiryProcessed || !x.UtcRemoveAt.HasValue))
                .OrderBy(x => x.GuildId)
                .ThenBy(x => x.UserId)
                .ToListAsync();

            IGuild? guild = null;
            IGuildUser? user = null;
            bool needsSave = false;
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
                needsSave |= await ApplyPersistingRoles(user, allRoles.Skip(i).Take(nextUserIndex - i), 0ul);
            }

            if (needsSave)
                await _dbContext.SaveChangesAsync();

            await StartNextTimer();
        }
        finally
        {
            _dbContextGate.Release();
        }

        _logger.LogInformation("Checked {0} persisting roles for updates.", rolesChecked);
    }
    private async Task<bool> ApplyPersistingRoles(IGuildUser user, IEnumerable<PersistingRole> persistingRoles, ulong roleIdToRemove, CancellationToken token = default)
    {
        IReadOnlyCollection<ulong> roles = user.RoleIds;

        List<ulong>? toRemove = null, toAdd = null;

        bool needsSave = false;
        foreach (PersistingRole role in persistingRoles)
        {
            if (role.IsExpired(_timeProvider))
            {
                if (roles.Contains(role.RoleId))
                {
                    toRemove ??= [ ];
                    if (!toRemove.Contains(role.RoleId))
                        toRemove.Add(role.RoleId);
                    role.ExpiryProcessed = true;
                    _dbContext.Update(role);
                    needsSave = true;
                    _logger.LogDebug("Removing present role {0} to {1} ({2}).", role.RoleId, user.Id, user.Username);
                }

                continue;
            }

            if (roles.Contains(role.RoleId))
                continue;

            toAdd ??= [ ];
            if (!toAdd.Contains(role.RoleId))
                toAdd.Add(role.RoleId);
            _logger.LogDebug("Adding missing role {0} to {1} ({2}).", role.RoleId, user.Id, user.Username);
        }

        if (roleIdToRemove != 0ul && roles.Contains(roleIdToRemove) && (toRemove == null || !toRemove.Contains(roleIdToRemove)))
            (toRemove ??= [ ]).Add(roleIdToRemove);


        if (toRemove != null)
            await user.RemoveRolesAsync(toRemove, token == default ? null : new RequestOptions { CancelToken = token });

        if (toAdd != null)
            await user.AddRolesAsync(toAdd, token == default ? null : new RequestOptions { CancelToken = token });

        return needsSave;
    }
    public Task CheckMemberRoles(ulong userId, ulong guildId, CancellationToken token = default)
        => CheckMemberRoles(userId, guildId, 0ul, token);
    private async Task CheckMemberRoles(ulong userId, ulong guildId, ulong roleIdToRemove, CancellationToken token = default)
    {
        IGuild? guild = _discordClient.GetGuild(guildId);
        IGuildUser? user = guild == null ? null : await guild.GetUserAsync(userId, CacheMode.AllowDownload, new RequestOptions { CancelToken = token });

        if (user != null)
            await CheckMemberRoles(user, roleIdToRemove, token);
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
    public async Task<IReadOnlyList<PersistingRole>> GetPersistingRoles(ulong user, ulong guildId, ulong roleId, CancellationToken token = default)
    {
        await _dbContextGate.WaitAsync(token);
        try
        {
            return await _dbContext.PersistingRoles
                .OrderByDescending(role => role.UtcTimestamp)
                .Where(role => role.UserId == user && role.RoleId == roleId && role.GuildId == guildId)
                .ToListAsync(token);
        }
        finally
        {
            _dbContextGate.Release();
        }
    }

    public async Task<IReadOnlyList<PersistingRole>> GetPersistingRoles(ulong user, ulong guildId, CancellationToken token = default)
    {
        await _dbContextGate.WaitAsync(token);
        try
        {
            return await _dbContext.PersistingRoles
                .OrderByDescending(role => role.UtcTimestamp)
                .Where(role => role.UserId == user && role.GuildId == guildId)
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
                UtcRemoveAt = activeUntil,
                UtcTimestamp = _timeProvider.GetUtcNow().UtcDateTime
            };

            _dbContext.PersistingRoles.Add(role);

            await _dbContext.SaveChangesAsync(token);

            if (activeUntil.HasValue && _currentTimerComplete > activeUntil.Value)
            {
                await StartNextTimer(token);
            }
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

            rowsUpdated = await _dbContext.RemoveWhere<PersistingRole>(role => role.UserId == userId && role.RoleId == roleId && role.GuildId == guildId, token, userId, roleId, guildId);
        }
        finally
        {
            _dbContextGate.Release();
        }

        await CheckMemberRoles(userId, guildId, roleId, token);
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

        await CheckMemberRoles(role.UserId, role.GuildId, role.RoleId, token);
        return updated;
    }
#nullable disable
    private class TimerState
    {
        public int RoleId;
        public Timer Timer;
    }
#nullable restore
}
