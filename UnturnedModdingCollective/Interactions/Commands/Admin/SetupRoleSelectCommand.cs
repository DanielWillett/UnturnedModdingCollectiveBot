using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using UnturnedModdingCollective.Services;

namespace UnturnedModdingCollective.Interactions.Commands.Admin;
public class SetupRoleSelectCommand : InteractionModuleBase<SocketInteractionContext<SocketSlashCommand>>
{
    private readonly EmbedFactory _embedFactory;
    public SetupRoleSelectCommand(EmbedFactory embedFactory)
    {
        _embedFactory = embedFactory;
    }

    [SlashCommand("setup-role-select", "Sets up the role selection message.")]
    public async Task SetupRoleSelect()
    {
        IGuildUser user = (IGuildUser)Context.User;

        if (user.Id != Context.Guild.OwnerId && !user.GuildPermissions.Has(GuildPermission.Administrator))
        {
            await Context.Interaction.RespondAsync("No permissions.", ephemeral: true);
            return;
        }


        Task deferTask = Context.Interaction.DeferAsync(ephemeral: true);

        EmbedBuilder embed = new EmbedBuilder();
        ComponentBuilder components = new ComponentBuilder();

        await _embedFactory.BuildMembershipApplicationMessage(Context.Guild, embed, components);

        await Context.Channel.SendMessageAsync(embed: embed.Build(), components: components.Build());

        await deferTask;
        await Context.Interaction.DeleteOriginalResponseAsync();
    }
}
