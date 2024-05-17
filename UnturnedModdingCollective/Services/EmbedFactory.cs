using System.Globalization;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UnturnedModdingCollective.Interactions.Components;
using UnturnedModdingCollective.Models;

namespace UnturnedModdingCollective.Services;
public class EmbedFactory
{
    private static readonly IReadOnlyDictionary<GuildPermission, string> PermissionNames = new Dictionary<GuildPermission, string>
    {
        { GuildPermission.CreateInstantInvite, "Create Invite" },
        { GuildPermission.KickMembers, "Kick Members" },
        { GuildPermission.BanMembers, "Ban Members" },
        { GuildPermission.Administrator, "Administrator" },
        { GuildPermission.ManageChannels, "Manage Channels" },
        { GuildPermission.ManageGuild, "Manage Guild" },
        { GuildPermission.ViewGuildInsights, "View Server Insights" },
        { GuildPermission.AddReactions, "Add Reactions" },
        { GuildPermission.ViewAuditLog, "View Audit Log" },
        { GuildPermission.ViewChannel, "View Channels" },
        { GuildPermission.SendMessages, "Send Messages" },
        { GuildPermission.SendTTSMessages, "Send Text-To-Speech Messages" },
        { GuildPermission.ManageMessages, "Manage Messages" },
        { GuildPermission.EmbedLinks, "Embed Links" },
        { GuildPermission.AttachFiles, "Attach Files" },
        { GuildPermission.ReadMessageHistory, "Read Message History" },
        { GuildPermission.MentionEveryone, "Mention @everyone, @here, and All Roles" },
        { GuildPermission.UseExternalEmojis, "Use External Emoji" },
        { GuildPermission.Connect, "Connect" },
        { GuildPermission.Speak, "Speak" },
        { GuildPermission.MuteMembers, "Mute Members" },
        { GuildPermission.DeafenMembers, "Deafen Members" },
        { GuildPermission.MoveMembers, "Move Members" },
        { GuildPermission.UseVAD, "Use Voice Activity" },
        { GuildPermission.PrioritySpeaker, "Priority Speaker" },
        { GuildPermission.Stream, "Video" },
        { GuildPermission.ChangeNickname, "Change Nickname" },
        { GuildPermission.ManageNicknames, "Manage Nicknames" },
        { GuildPermission.ManageRoles, "Manage Roles" },
        { GuildPermission.ManageWebhooks, "Manage Webhooks" },
        { GuildPermission.ManageEmojisAndStickers, "Manage Expressions" },
        { GuildPermission.UseApplicationCommands, "Use Application Commands" },
        { GuildPermission.RequestToSpeak, "Request to Speak" },
        { GuildPermission.ManageEvents, "Manage Events" },
        { (GuildPermission)(1ul << 44), "Create Events" },
        { GuildPermission.ManageThreads, "Manage Threads" },
        { GuildPermission.CreatePublicThreads, "Create Public Threads" },
        { GuildPermission.CreatePrivateThreads, "Create Private Threads" },
        { GuildPermission.UseExternalStickers, "Use External Stickers" },
        { GuildPermission.SendMessagesInThreads, "Send Messages in Threads" },
        { GuildPermission.StartEmbeddedActivities, "Use Activities" },
        { GuildPermission.ModerateMembers, "Timeout Members" },
        { GuildPermission.ViewMonetizationAnalytics, "View Server Subscription Insights" },
        { GuildPermission.UseSoundboard, "Use Soundboard" },
        { GuildPermission.CreateGuildExpressions, "Create Expressions" },
        { GuildPermission.SendVoiceMessages, "Send Voice Messages" },
        { GuildPermission.UseClydeAI, "Use Clyde AI" }, // not available
        { GuildPermission.SetVoiceChannelStatus, "Set Voice Channel Status" },
        { GuildPermission.SendPolls, "Create Polls" }
    };

    private readonly ILogger<EmbedFactory> _logger;
    private readonly BotDbContext _dbContext;
    private readonly DiscordSocketClient _discordClient;
    public EmbedFactory(ILogger<EmbedFactory> logger, BotDbContext dbContext, DiscordSocketClient discordClient)
    {
        _logger = logger;
        _dbContext = dbContext;
        _discordClient = discordClient;
    }

    public EmbedBuilder NoPermissionsEmbed(GuildPermission requiredPermission)
    {
        return new EmbedBuilder()
            .WithColor(Color.Red)
            .WithTitle("Missing Permissions")
            .WithDescription($"You must have the **{GetPermissionName(requiredPermission)}** permission to use this feature.");
    }

    public async Task BuildMembershipApplicationMessage(IGuild guild, EmbedBuilder embedBuilder, ComponentBuilder componentBuilder)
    {
        IUser currentUser = _discordClient.CurrentUser;
        embedBuilder
            .WithColor(new Color(99, 123, 101))
            .WithAuthor(currentUser.GlobalName ?? currentUser.Username, currentUser.GetAvatarUrl())
            .WithTitle("Apply for Membership")
            .WithDescription("Choose which roles you'd like to apply to, submit some basic information about yourself, " +
                             "then present your portfolio in your personal application thread.");

        IList<ApplicableRole> roles = await _dbContext.ApplicableRoles
            .Where(role => role.GuildId == guild.Id)
            .ToListAsync();

        List<SelectMenuOptionBuilder> selectOptions = new List<SelectMenuOptionBuilder>(roles.Count);

        bool hadToRemoveAny = false;
        foreach (ApplicableRole role in roles)
        {
            IRole? discordRole = guild.Roles.FirstOrDefault(r => r.Id == role.RoleId);
            if (discordRole == null)
            {
                _logger.LogWarning("Applicable role missing: {0}, {1}. Removing from database.", role.RoleId, role.Description);
                _dbContext.ApplicableRoles.Remove(role);
                hadToRemoveAny = true;
                continue;
            }

            SelectMenuOptionBuilder builder = new SelectMenuOptionBuilder()
                .WithLabel(discordRole.Name)
                .WithDescription(role.Description)
                .WithValue(role.Id.ToString(CultureInfo.InvariantCulture));

            selectOptions.Add(builder);

            if (string.IsNullOrWhiteSpace(role.Emoji))
                continue;

            if (ulong.TryParse(role.Emoji, NumberStyles.Number, CultureInfo.InvariantCulture, out ulong customEmojiId))
            {
                Emote? emote = guild.Emotes.FirstOrDefault(e => e.Id == customEmojiId);
                if (emote != null)
                {
                    builder.Emote = emote;
                    continue;
                }
            }

            builder.Emote = new Emoji(role.Emoji);
        }

        if (hadToRemoveAny)
        {
            await _dbContext.SaveChangesAsync();
        }

        componentBuilder.WithSelectMenu(new SelectMenuBuilder()
            .WithOptions(selectOptions)
            .WithCustomId(StartPortfolioComponent.StartPortfolioMenu)
            .WithMaxValues(selectOptions.Count)
            .WithMinValues(1)
        );
    }

    public string GetPermissionName(GuildPermission permission) => PermissionNames.TryGetValue(permission, out string? v) ? v : permission.ToString();
}
