using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using System.Globalization;

namespace UnturnedModdingCollective.Interactions.Components;
public class SubmitPortfolioComponent : InteractionModuleBase<SocketInteractionContext<SocketMessageComponent>>
{
    internal const string SubmitPortfolioButtonPrefix = "submit_portfolio_btn_";

    [ComponentInteraction($"{SubmitPortfolioButtonPrefix}*")]
    public async Task ReceiveSubmitButtonPress()
    {
        SocketMessageComponent intx = Context.Interaction;

        // parse user id from button custom id
        string userIdStr = intx.Data.CustomId[SubmitPortfolioButtonPrefix.Length..];
        ulong userId = ulong.Parse(userIdStr, CultureInfo.InvariantCulture);

        IUser? user = await Context.Client.GetUserAsync(userId);
        if (user == null)
        {
            await Context.Interaction.RespondAsync(ephemeral: true, embed: new EmbedBuilder().WithColor(Color.Red).WithTitle("Error").WithDescription("User not found.").Build() /* todo */);
            return;
        }
        
        IThreadChannel? thread = intx.Message.Thread;
        if (thread == null)
        {
            await Context.Interaction.RespondAsync(ephemeral: true, embed: new EmbedBuilder().WithColor(Color.Red).WithTitle("Error").WithDescription("Thread gone.").Build() /* todo */);
            return;
        }

        Task deferTask = intx.DeferLoadingAsync();

        // lock thread, remove the user's ability to view the thread
        await thread.ModifyAsync(properties =>
        {
            properties.Locked = true;
            properties.PermissionOverwrites = new Optional<IEnumerable<Overwrite>>(
            [
                new Overwrite(userId, PermissionTarget.User, new OverwritePermissions(viewChannel: PermValue.Deny))
            ]);
        });

        // send the vote poll
        await thread.SendMessageAsync("<@ping role> This portfolio is ready to be reviewed.",
            poll: new PollProperties
            {
                Duration = (uint)Math.Round(TimeSpan.FromDays(7d).TotalHours),
                LayoutType = PollLayout.Default,
                Question = new PollMediaProperties
                {
                    Text = $"Should {user.GlobalName} become a member?"
                },
                Answers =
                [
                    new PollMediaProperties
                    {
                        Emoji = new Emoji("\U00002705"),
                        Text = "Yes"
                    },
                    new PollMediaProperties
                    {
                        Emoji = new Emoji("\U0000274C"),
                        Text = "No"
                    }
                ]
            });

        await deferTask;

        await Context.Interaction.DeferAsync();
    }
}
