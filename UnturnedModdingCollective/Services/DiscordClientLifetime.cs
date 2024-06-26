﻿using Discord;
using Discord.Interactions;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Reflection;
using UnturnedModdingCollective.API;

namespace UnturnedModdingCollective.Services;
public class DiscordClientLifetime : IHostedService
{
    private readonly ConcurrentDictionary<string, ILogger> _discordLoggers = new ConcurrentDictionary<string, ILogger>();

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DiscordSocketClient> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IConfiguration _configuration;
    private readonly ISecretProvider _secretProvider;

    private readonly DiscordSocketClient _discordClient;
    private readonly InteractionService _interactionService;

    private string _interactionVersionFilePath = null!;
    private Version? _lastUpdatedInteractionVersion;
    private ulong _lastUpdatedInteractionGuildId;

    private readonly TaskCompletionSource _readyTask = new TaskCompletionSource();
    private bool _isReady;
    public DiscordClientLifetime(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;

        _discordClient = serviceProvider.GetRequiredService<DiscordSocketClient>();
        _interactionService = serviceProvider.GetRequiredService<InteractionService>();

        _loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _logger = _loggerFactory.CreateLogger<DiscordSocketClient>();
        _configuration = serviceProvider.GetRequiredService<IConfiguration>();
        _secretProvider = serviceProvider.GetRequiredService<ISecretProvider>();

        ReadInteractionRegistrationVersion();
    }

    public Task WaitUntilReady()
    {
        return _isReady ? Task.CompletedTask : _readyTask.Task;
    }

    async Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        string? token = _configuration["Token"];

        if (string.IsNullOrWhiteSpace(token))
            throw new FormatException("Invalid or missing bot token.");

        token = await _secretProvider.GetSecret(token);

        if (string.IsNullOrWhiteSpace(token))
            throw new FormatException("Bot token missing from secret provider.");

        _discordClient.Log += HandleLog;
        _discordClient.Ready += HandleReady;

        _discordClient.InteractionCreated += HandleInteractionReceived;

        await _discordClient.LoginAsync(TokenType.Bot, token);
        await _discordClient.StartAsync();
    }

    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        await _discordClient.LogoutAsync();
        await _discordClient.StopAsync();

        _discordClient.Log -= HandleLog;
        _discordClient.Ready -= HandleReady;

        _discordClient.InteractionCreated -= HandleInteractionReceived;
    }
    private async Task HandleInteractionReceived(SocketInteraction interaction)
    {
        Type contextType = typeof(SocketInteractionContext<>).MakeGenericType(interaction.GetType());
        IInteractionContext ctx = (IInteractionContext?)Activator.CreateInstance(contextType, [_discordClient, interaction])!;
        try
        {
            IResult result = await _interactionService.ExecuteCommandAsync(ctx, _serviceProvider);
            if (result.IsSuccess)
                return;

            string err = $"Failure handling interaction {ctx.Interaction.Id} of type {ctx.Interaction.Type}: ";

            if (result.Error.HasValue)
                err += $"[{result.Error.Value}] \"{result.ErrorReason}\".";
            else
                err += $"\"{result.ErrorReason}\"";

            Exception? ex = result is ExecuteResult er ? er.Exception : null;

            _logger.LogError(ex, err);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception handling slash command interaction.");
        }
    }

    private Task HandleLog(LogMessage arg)
    {
        ILogger logger = _discordLoggers.GetOrAdd("Discord " + arg.Source, _loggerFactory.CreateLogger);
        switch (arg.Severity)
        {
            case LogSeverity.Debug:
            case LogSeverity.Verbose:
                logger.LogDebug(arg.Exception, arg.Message);
                break;
            
            case LogSeverity.Error:
            case LogSeverity.Critical:
                logger.LogError(arg.Exception, arg.Message);
                break;

            case LogSeverity.Warning:
                if (string.Equals(arg.Source, "Discord Gateway", StringComparison.Ordinal) && arg.Exception is GatewayReconnectException or TaskCanceledException)
                    logger.LogInformation(arg.Exception.Message);
                else
                    logger.LogWarning(arg.Exception, arg.Message);
                break;
            
            default:
                logger.LogInformation(arg.Exception, arg.Message);
                break;
        }

        return Task.CompletedTask;
    }

    private async Task HandleReady()
    {
        if (_discordClient.Guilds.Count == 0)
            throw new InvalidOperationException("The bot must be in at least one guild.");

        await TrySyncInteractions();

        IUser currentUser = _discordClient.CurrentUser;
        _logger.LogDebug("Discord bot ready: {0}#{1}.", currentUser.Username, currentUser.DiscriminatorValue);
        _readyTask.SetResult();
        _isReady = true;
    }
    
    private async Task TrySyncInteractions()
    {
        ulong discordGuildId = _discordClient.Guilds.First().Id;

        Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version!;

        if (_lastUpdatedInteractionVersion == currentVersion && _lastUpdatedInteractionGuildId == discordGuildId)
        {
            _logger.LogInformation("Skipping interaction syncing, already up to date.");
            return;
        }

        IReadOnlyCollection<RestGuildCommand> commandsRegistered = await _interactionService.RegisterCommandsToGuildAsync(discordGuildId);
        if (commandsRegistered.Count == 0)
        {
            _logger.LogDebug("Found no slash commands to register.");
        }
        else foreach (RestGuildCommand command in commandsRegistered)
        {
            _logger.LogDebug("Registered slash command: {0} ({1}).", command.Name, command.Type);
        }

        _lastUpdatedInteractionVersion = currentVersion;
        _lastUpdatedInteractionGuildId = discordGuildId;

        UpdateInteractionFile(_lastUpdatedInteractionVersion, _lastUpdatedInteractionGuildId);
    }

    /// <summary>
    /// Reads the last 'version' of the set of interactions so we only update interactions when they actually change.
    /// </summary>
    private void ReadInteractionRegistrationVersion()
    {
        _interactionVersionFilePath = Path.Combine(Environment.CurrentDirectory, "data", "dsIntxVsn.bin");

        if (!File.Exists(_interactionVersionFilePath))
        {
            _lastUpdatedInteractionVersion = new Version(0, 0, 0, 0);
            return;
        }

        try
        {
            byte[] data = File.ReadAllBytes(_interactionVersionFilePath);
            if (data.Length < sizeof(int) * 4 + sizeof(ulong))
            {
                _lastUpdatedInteractionVersion = new Version(0, 0, 0, 0);
                _logger.LogWarning("Corrupted interaction version file at \"{0}\".", _interactionVersionFilePath);
                return;
            }

            Version v = new Version(
                BitConverter.ToInt32(data, 0),
                BitConverter.ToInt32(data, 4),
                BitConverter.ToInt32(data, 8),
                BitConverter.ToInt32(data, 12)
            );

            _lastUpdatedInteractionVersion = v;
            _lastUpdatedInteractionGuildId = BitConverter.ToUInt64(data, 16);
        }
        catch (Exception ex)
        {
            _lastUpdatedInteractionVersion = new Version(0, 0, 0, 0);
            _logger.LogWarning(ex, "Unable to read last interaction version.");
        }
    }

    /// <summary>
    /// Writes the last interaction version and guild Id to a file so we can only update interactions when they actually change.
    /// </summary>
    private void UpdateInteractionFile(Version version, ulong guildId)
    {
        try
        {
            byte[] newData = new byte[sizeof(int) * 4 + sizeof(ulong)];

            BitConverter.TryWriteBytes(newData, version.Major);
            BitConverter.TryWriteBytes(newData.AsSpan(4), version.Minor);
            BitConverter.TryWriteBytes(newData.AsSpan(8), version.Build);
            BitConverter.TryWriteBytes(newData.AsSpan(12), version.Revision);

            BitConverter.TryWriteBytes(newData.AsSpan(16), guildId);

            string? dir = Path.GetDirectoryName(_interactionVersionFilePath);

            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(_interactionVersionFilePath, newData);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to update interaction version file.");
        }
    }
}