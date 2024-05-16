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
    private readonly ILogger<EmbedFactory> _logger;
    private readonly BotDbContext _dbContext;
    private readonly DiscordSocketClient _discordClient;
    public EmbedFactory(ILogger<EmbedFactory> logger, BotDbContext dbContext, DiscordSocketClient discordClient)
    {
        _logger = logger;
        _dbContext = dbContext;
        _discordClient = discordClient;
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
}
