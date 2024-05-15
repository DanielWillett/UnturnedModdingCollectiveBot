﻿using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using UnturnedModdingCollective.Models;
using UnturnedModdingCollective.Services;

namespace UnturnedModdingCollective.Interactions.Commands.Admin;
public class ApplicableRolesCommands : InteractionModuleBase<SocketInteractionContext<SocketSlashCommand>>
{
    private readonly BotDbContext _dbContext;
    public ApplicableRolesCommands(BotDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [SlashCommand("add-applicable-role", "Add a role to be able to be applied for.")]
    public async Task AddApplicableRole(IRole role, string emoji, string description)
    {
        IGuildUser user = (IGuildUser)Context.User;

        if (user.Id != Context.Guild.OwnerId && !user.GuildPermissions.Has(GuildPermission.Administrator))
        {
            await Context.Interaction.RespondAsync("No permissions.", ephemeral: true);
            return;
        }

        bool alreadyAdded = await _dbContext.ApplicableRoles.AnyAsync(x => x.RoleId == role.Id);
        if (alreadyAdded)
        {
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                .WithColor(Color.Red)
                .WithTitle("Already Added")
                .WithDescription($"{role.Mention} was already added as a role that can be applied for.")
                .Build(),
                ephemeral: true
            );
            return;
        }

        if (!TryParseEmoji(emoji, out string emojiSave))
        {
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                    .WithColor(Color.Red)
                    .WithTitle("Error")
                    .WithDescription($"Unknown/invalid emoji: `{emoji}`. If using a custom emote it must be from this server.")
                    .Build(),
                ephemeral: true
            );
            return;
        }

        ApplicableRole applicableRole = new ApplicableRole
        {
            RoleId = role.Id,
            Description = description,
            GuildId = role.Guild.Id,
            Emoji = emojiSave,
            UserAddedBy = user.Id
        };

        _dbContext.ApplicableRoles.Add(applicableRole);
        await _dbContext.SaveChangesAsync();

        await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                .WithColor(Color.Green)
                .WithTitle("Added Applicable Role")
                .WithDescription($"{role.Mention} has been added as a role that can be applied for.")
                .Build(),
            ephemeral: true
        );
    }

    [SlashCommand("remove-applicable-role", "Remove a role that's able to be applied for.")]
    public async Task RemoveApplicableRole(IRole role)
    {
        IGuildUser user = (IGuildUser)Context.User;

        if (user.Id != Context.Guild.OwnerId && !user.GuildPermissions.Has(GuildPermission.Administrator))
        {
            await Context.Interaction.RespondAsync("No permissions.", ephemeral: true);
            return;
        }

        ApplicableRole? applicableRole = await _dbContext.ApplicableRoles.FirstOrDefaultAsync(x => x.RoleId == role.Id);
        if (applicableRole == null)
        {
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                .WithColor(Color.Red)
                .WithTitle("Not Added")
                .WithDescription($"{role.Mention} is not a role that can be applied for.")
                .Build(),
                ephemeral: true
            );
            return;
        }

        _dbContext.ApplicableRoles.Remove(applicableRole);
        await _dbContext.SaveChangesAsync();

        await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                .WithColor(Color.Green)
                .WithTitle("Removed Applicable Role")
                .WithDescription($"{role.Mention} is no longer a role that can be applied for.")
                .Build(),
            ephemeral: true
        );
    }

    private bool TryParseEmoji(string emoji, out string emojiSave)
    {
        if (string.IsNullOrEmpty(emoji))
        {
            emojiSave = string.Empty;
            return true;
        }

        if (ulong.TryParse(emoji, NumberStyles.Number, CultureInfo.InvariantCulture, out ulong emojiId))
        {
            emojiSave = emojiId.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (Emoji.TryParse(emoji, out Emoji parsedEmoji))
        {
            emojiSave = parsedEmoji.Name;
            return true;
        }

        if (emoji.StartsWith(':') && emoji.EndsWith(':') && emoji.Length > 2)
            emoji = emoji.Substring(1, emoji.Length - 2);

        Emote? guildEmote = Context.Guild.Emotes.FirstOrDefault(x => x.Name.Equals(emoji, StringComparison.OrdinalIgnoreCase));

        if (guildEmote == null)
        {
            emojiSave = null!;
            return false;
        }

        emojiSave = guildEmote.Id.ToString(CultureInfo.InvariantCulture);
        return true;
    }
}
