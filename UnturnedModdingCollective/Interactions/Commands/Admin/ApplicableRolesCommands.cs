using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using UnturnedModdingCollective.Models;
using UnturnedModdingCollective.Services;

namespace UnturnedModdingCollective.Interactions.Commands.Admin;

[Discord.Interactions.Group("applicable-role", "Manage which roles can be applied for.")]
[CommandContextType(InteractionContextType.Guild)]
public class ApplicableRolesCommands : InteractionModuleBase<SocketInteractionContext<SocketSlashCommand>>
{
    private readonly BotDbContext _dbContext;
    private readonly EmbedFactory _embedFactory;
    public ApplicableRolesCommands(BotDbContext dbContext, EmbedFactory embedFactory)
    {
        _dbContext = dbContext;
        _embedFactory = embedFactory;
    }

    [SlashCommand("add", "Add a role to be able to be applied for.")]
    public async Task AddApplicableRole(IRole role, string emoji, string description, [Name("net-votes-required")] string netVotesRequired = "1")
    {
        IGuildUser user = (IGuildUser)Context.User;

        if (user.Id != Context.Guild.OwnerId && !user.GuildPermissions.Has(GuildPermission.Administrator))
        {
            await Context.Interaction.RespondAsync(embed: _embedFactory.NoPermissionsEmbed(GuildPermission.AddReactions).Build(), ephemeral: true);
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

        if (!int.TryParse(netVotesRequired, NumberStyles.Number, CultureInfo.InvariantCulture, out int netVotesRequiredVal) || netVotesRequiredVal < 0)
        {
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                    .WithColor(Color.Red)
                    .WithTitle("Error")
                    .WithDescription($"Out of range net votes: `{netVotesRequired}`. Must be an integer greater than or equal to 0.")
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
            UserAddedBy = user.Id,
            NetVotesRequired = netVotesRequiredVal
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

    [SlashCommand("edit", "Edit a role that is able to be applied for.")]
    public async Task EditApplicableRole(IRole role, string emoji = "", string description = "", [Name("net-votes-required")] string netVotesRequired = "")
    {
        IGuildUser user = (IGuildUser)Context.User;

        if (user.Id != Context.Guild.OwnerId && !user.GuildPermissions.Has(GuildPermission.Administrator))
        {
            await Context.Interaction.RespondAsync(embed: _embedFactory.NoPermissionsEmbed(GuildPermission.AddReactions).Build(), ephemeral: true);
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

        bool update = false;

        if (!string.IsNullOrEmpty(emoji) && TryParseEmoji(emoji, out string emojiSave))
        {
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                    .WithColor(Color.Red)
                    .WithTitle("Error")
                    .WithDescription($"Unknown/invalid emoji: `{emoji}`. If using a custom emote it must be from this server.")
                    .Build(),
                ephemeral: true
            );

            applicableRole.Emoji = emojiSave;
            update = true;
        }

        if (!string.IsNullOrEmpty(netVotesRequired) && int.TryParse(netVotesRequired, NumberStyles.Number, CultureInfo.InvariantCulture, out int netVotesRequiredVal) && netVotesRequiredVal >= 0)
        {
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                    .WithColor(Color.Red)
                    .WithTitle("Error")
                    .WithDescription($"Out of range net votes: `{netVotesRequired}`. Must be an integer greater than or equal to 0.")
                    .Build(),
                ephemeral: true
            );

            applicableRole.NetVotesRequired = netVotesRequiredVal;
            update = true;
        }

        if (!string.IsNullOrEmpty(description))
        {
            applicableRole.Description = description;
            update = true;
        }

        if (!update)
        {
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                    .WithColor(Color.Red)
                    .WithTitle("No Changes Made")
                    .WithDescription("You did not specify any optional arguments.")
                    .Build(),
                ephemeral: true
            );
            return;
        }

        _dbContext.Update(applicableRole);
        await _dbContext.SaveChangesAsync();

        await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                .WithColor(Color.Green)
                .WithTitle("Updated Applicable Role")
                .WithDescription($"{role.Mention} has been modified.")
                .Build(),
            ephemeral: true
        );
    }

    [SlashCommand("remove", "Remove a role that's able to be applied for.")]
    public async Task RemoveApplicableRole(IRole role)
    {
        IGuildUser user = (IGuildUser)Context.User;

        if (user.Id != Context.Guild.OwnerId && !user.GuildPermissions.Has(GuildPermission.Administrator))
        {
            await Context.Interaction.RespondAsync(embed: _embedFactory.NoPermissionsEmbed(GuildPermission.AddReactions).Build(), ephemeral: true);
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
