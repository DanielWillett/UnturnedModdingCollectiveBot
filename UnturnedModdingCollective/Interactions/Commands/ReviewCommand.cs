using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using UnturnedModdingCollective.Interactions.Components;
using UnturnedModdingCollective.Models;
using UnturnedModdingCollective.Services;

namespace UnturnedModdingCollective.Interactions.Commands;
public class ReviewCommand : InteractionModuleBase<SocketInteractionContext<SocketSlashCommand>>
{
    private readonly BotDbContext _dbContext;
    public ReviewCommand(BotDbContext dbContext)
    {
        _dbContext = dbContext;
    }
    [SlashCommand("review", description: "Submit a portfolio to be reviewed for entry.")]
    [CommandContextType(InteractionContextType.Guild)]
    public async Task SendTestPoll()
    {
        IGuildUser user = (IGuildUser)Context.User;

        ITextChannel channel = (ITextChannel)await Context.Interaction.GetChannelAsync();

        // check if they've already requested a review
        bool anyExist = await _dbContext.ReviewRequests.AnyAsync(request => request.UserId == user.Id && !request.ResubmitApprover.HasValue);

        if (anyExist)
        {
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                .WithColor(Color.Red)
                .WithTitle("Already Reviewed")
                .WithDescription("You've already submitted a review in the past. If you would like to get reviewed again request a re-review from a staff member.")
                .Build()
            );
            return;
        }

        EmbedBuilder postEmbed = new EmbedBuilder()
            .WithDescription("Please post your portfolio in the created thread and click **Submit** when you're done.")
            .WithColor(Color.Blue)
            .WithTitle("Member Review");

        ComponentBuilder components = new ComponentBuilder()
            .WithButton(ButtonBuilder.CreatePrimaryButton("Submit", SubmitPortfolioComponent.SubmitPortfolioButtonPrefix + user.Id.ToString(CultureInfo.InvariantCulture)));

        await Context.Interaction.RespondAsync(embed: postEmbed.Build(), components: components.Build());
        IUserMessage message = await Context.Interaction.GetOriginalResponseAsync();
        message = (IUserMessage)await channel.GetMessageAsync(message.Id);

        string threadChannelName = Context.User.GlobalName + " Portfolio";

        IThreadChannel thread = await channel.CreateThreadAsync(threadChannelName, ThreadType.PrivateThread, ThreadArchiveDuration.OneWeek, message);

        Task addUserTask = thread.AddUserAsync(user);

        // edit the thread mention into the original message
        postEmbed.Description = $"Please post your portfolio in {thread.Mention} and click **Submit** when you're done.";
        Task modifyMessageTask = message.ModifyAsync(msg => msg.Embeds = new Embed[] { postEmbed.Build() });

        // create database entry
        ReviewRequest request = new ReviewRequest
        {
            UserId = user.Id,
            Steam64 = 0,
            GlobalName = user.GlobalName,
            UserName = user.Username,
            MessageId = message.Id,
            ThreadId = thread.Id,
            UtcTimeStarted = DateTime.UtcNow
        };

        _dbContext.ReviewRequests.Add(request);
        await _dbContext.SaveChangesAsync();

        await addUserTask;
        await modifyMessageTask;
    }
}
