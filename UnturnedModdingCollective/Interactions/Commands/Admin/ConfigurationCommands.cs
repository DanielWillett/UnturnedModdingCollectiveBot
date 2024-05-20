using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using System.Globalization;
using UnturnedModdingCollective.API;
using UnturnedModdingCollective.Models.Config;
using UnturnedModdingCollective.Services;
using UnturnedModdingCollective.Util;

namespace UnturnedModdingCollective.Interactions.Commands.Admin;

[Discord.Commands.Group("configuration")]
public class ConfigurationCommands : InteractionModuleBase<SocketInteractionContext<SocketSlashCommand>>
{
    public readonly ILiveConfiguration<LiveConfiguration> _liveConfig;
    private readonly EmbedFactory _embedFactory;
    public ConfigurationCommands(ILiveConfiguration<LiveConfiguration> liveConfig, EmbedFactory embedFactory)
    {
        _liveConfig = liveConfig;
        _embedFactory = embedFactory;
    }

    [SlashCommand("set", "Set the value of a configuration entry")]
    public async Task SetConfiguration(
        [Choice("Council Role", "council-role")]
        [Choice("Vote Time (fmt: 1d[ 4h 30m])", "vote-time")]
        [Choice("Time Before Vote End for Ping (fmt: 1d[ 4h 30m])", "vote-ping-time")]
        string setting,
        string value
        )
    {
        if (setting.Equals("council-role", StringComparison.Ordinal))
        {
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                .WithColor(Color.Red)
                .WithTitle("Incorrect Command")
                .WithDescription("Use `/configuration set-role <role>` instead.")
                .Build()
            );
            return;
        }

        LiveConfiguration liveConfig = _liveConfig.Configuraiton;
        string oldValue, newValue, settingName;
        if (setting.Equals("vote-time", StringComparison.Ordinal))
        {
            TimeSpan ts = TimeUtility.ParseTimespan(value);

            oldValue = liveConfig.VoteTime.ToString("G", CultureInfo.InvariantCulture);
            newValue = ts.ToString("G", CultureInfo.InvariantCulture);
            settingName = "Vote Time";

            liveConfig.VoteTime = ts;
        }
        else if (setting.Equals("vote-ping-time", StringComparison.Ordinal))
        {
            TimeSpan ts = TimeUtility.ParseTimespan(value);

            oldValue = liveConfig.PingTimeBeforeVoteClose.ToString("G", CultureInfo.InvariantCulture);
            newValue = ts.ToString("G", CultureInfo.InvariantCulture);
            settingName = "Time Before Vote End for Ping";

            liveConfig.PingTimeBeforeVoteClose = ts;
        }
        else
        {
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                .WithColor(Color.Red)
                .WithTitle("No Setting Found")
                .WithDescription($"There's isn't a setting called `{setting}`.")
                .Build()
            );
            return;
        }

        _liveConfig.Save();
        await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
            .WithAuthor(Context.User.GlobalName ?? Context.User.Username, Context.User.GetAvatarUrl())
            .WithColor(Color.Green)
            .WithTitle("Set Configuration Value")
            .WithDescription($"Set the value of {settingName}:{Environment.NewLine}**{oldValue} -> {newValue}**")
            .Build()
        );
    }

    [SlashCommand("set-role", "Set the value of a role configuration entry.")]
    [RequireContext(ContextType.Guild)]
    public async Task SetRoleConfiguration(
        [Choice("Council Role", "council-role")]
        string setting,
        IRole role)
    {
        if (!await ValidateCommand())
            return;

        LiveConfiguration liveConfig = _liveConfig.Configuraiton;
        string oldValue, newValue, settingName;
        if (setting.Equals("council-role", StringComparison.Ordinal))
        {
            IRole? oldRole = Context.Guild.Roles.FirstOrDefault(x => x.Id == liveConfig.CouncilRole);

            oldValue = oldRole?.Mention ?? "`" + liveConfig.CouncilRole.ToString(CultureInfo.InvariantCulture) + "`";
            newValue = role.Mention;
            settingName = "Council Role";

            liveConfig.CouncilRole = role.Id;
        }
        else
        {
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                .WithColor(Color.Red)
                .WithTitle("No Setting Found")
                .WithDescription($"There's isn't a role setting called `{setting}`.")
                .Build()
            );
            return;
        }

        _liveConfig.Save();
        await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
            .WithAuthor(Context.User.GlobalName ?? Context.User.Username, Context.User.GetAvatarUrl())
            .WithColor(Color.Green)
            .WithTitle("Set Configuration Value")
            .WithDescription($"Set the value of {settingName}:{Environment.NewLine}**{oldValue} -> {newValue}**")
            .Build()
        );
    }
    private async Task<bool> ValidateCommand()
    {
        IGuildUser user = (IGuildUser)Context.User;

        if (user.Id == Context.Guild.OwnerId || user.GuildPermissions.Has(GuildPermission.Administrator))
            return true;

        await Context.Interaction.RespondAsync(embed: _embedFactory.NoPermissionsEmbed(GuildPermission.AddReactions).Build(), ephemeral: true);
        return false;

    }
}
