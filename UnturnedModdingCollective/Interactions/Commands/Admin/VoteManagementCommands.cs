using System.Globalization;
using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using UnturnedModdingCollective.Models;
using UnturnedModdingCollective.Services;

namespace UnturnedModdingCollective.Interactions.Commands.Admin;

[Group("vote", "Manage the current vote.")]
[CommandContextType(InteractionContextType.Guild)]
public class VoteManagementCommands : InteractionModuleBase<SocketInteractionContext<SocketSlashCommand>>
{
    private readonly BotDbContext _dbContext;
    private readonly VoteLifetimeManager _votes;
    private readonly TimeProvider _timeProvider;
    private readonly EmbedFactory _embedFactory;
    public VoteManagementCommands(BotDbContext dbContext, VoteLifetimeManager votes, TimeProvider timeProvider, EmbedFactory embedFactory)
    {
        _dbContext = dbContext;
        _votes = votes;
        _timeProvider = timeProvider;
        _embedFactory = embedFactory;
    }

    [SlashCommand("view", "View vote history on a user.")]
    public async Task ViewVoteHistory(IUser lookupUser)
    {
        IGuildUser user = (IGuildUser)Context.User;

        if (user.Id != Context.Guild.OwnerId && !user.GuildPermissions.Has(GuildPermission.Administrator))
        {
            await Context.Interaction.RespondAsync(embed: _embedFactory.NoPermissionsEmbed(GuildPermission.Administrator).Build(), ephemeral: true);
            return;
        }

        List<ReviewRequest> requests = await _dbContext.ReviewRequests
                                                .Include(x => x.RequestedRoles)
                                                .ThenInclude(x => x.Votes)
                                                .Where(x => x.UserId == lookupUser.Id)
                                                .OrderByDescending(x => x.UtcTimeStarted)
                                                .ToListAsync();

        if (requests.Count == 0)
        {
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                    .WithColor(Color.Red)
                    .WithTitle("No History")
                    .WithDescription($"{lookupUser.Mention} doesn't have any review requests.")
                    .Build(),
                ephemeral: true
            );
            return;
        }

        StringBuilder descBuilder = new StringBuilder();

        foreach (ReviewRequest request in requests)
        {
            if (descBuilder.Length != 0)
                descBuilder.AppendLine();

            descBuilder.Append("Request at ").Append(TimestampTag.FormatFromDateTime(DateTime.SpecifyKind(request.UtcTimeStarted, DateTimeKind.Utc), TimestampTagStyles.LongDateTime));
            if (request.UtcTimeCancelled.HasValue)
            {
                descBuilder.AppendLine(" cancelled.");
                continue;
            }
            
            if (request.ClosedUnderError)
            {
                descBuilder.AppendLine(" was closed with an error.");
                continue;
            }

            descBuilder.Append('.').AppendLine();

            descBuilder.AppendLine("**Roles**");
            foreach (ReviewRequestRole role in request.RequestedRoles)
            {
                IRole? guildRole = Context.Guild.Roles.FirstOrDefault(x => x.Id == role.RoleId);

                descBuilder.Append("__").Append(guildRole != null
                    ? guildRole.Mention
                    : role.RoleId.ToString(CultureInfo.InvariantCulture)
                ).AppendLine("__");

                foreach (ReviewRequestVote vote in role.Votes.OrderByDescending(x => x.Vote))
                {
                    descBuilder.Append(vote.Vote ? ":white_check_mark: " : ":x: ");

                    IGuildUser? votingUser = await ((IGuild)Context.Guild).GetUserAsync(vote.UserId);

                    if (votingUser != null)
                        descBuilder.Append(votingUser.Mention);
                    else
                        descBuilder.Append(vote.GlobalName).Append(" (").Append(vote.UserName).Append(')');

                    descBuilder.AppendLine();
                }
            }
        }

        await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                .WithColor(Color.Green)
                .WithTitle("Review Request History")
                .WithDescription(descBuilder.ToString())
                .Build(),
            ephemeral: true
        );
    }

    [SlashCommand("end-early", "End the vote now.")]
    public async Task EndVoteEarly()
    {
        IGuildUser user = (IGuildUser)Context.User;

        if (user.Id != Context.Guild.OwnerId && !user.GuildPermissions.Has(GuildPermission.Administrator))
        {
            await Context.Interaction.RespondAsync(embed: _embedFactory.NoPermissionsEmbed(GuildPermission.Administrator).Build(), ephemeral: true);
            return;
        }

        IThreadChannel? thread = Context.Channel as IThreadChannel;

        ReviewRequest? request = null;
        if (thread != null)
        {
            request = await _dbContext.ReviewRequests
                .Where(req => !req.ClosedUnderError && req.UtcTimeSubmitted.HasValue && !req.UtcTimeClosed.HasValue)
                .OrderByDescending(req => req.UtcTimeStarted)
                .FirstOrDefaultAsync(req => req.ThreadId == thread.Id);
        }

        if (request == null)
        {
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                    .WithColor(Color.Red)
                    .WithTitle("Unknown Request")
                    .WithDescription("This command must be ran in a request thread that hasn't been completed yet.")
                    .Build(),
                ephemeral: true
            );
            return;
        }

        await Context.Interaction.DeferAsync();

        await _votes.FinalizePoll(request.Id);

        string endTime = TimestampTag.FormatFromDateTime(
            DateTime.SpecifyKind(request.UtcTimeVoteExpires.GetValueOrDefault(), DateTimeKind.Utc),
            TimestampTagStyles.Relative
        );

        await Context.Interaction.FollowupAsync(embed: new EmbedBuilder()
            .WithColor(Color.Green)
            .WithAuthor(Context.User.GlobalName ?? Context.User.Username, Context.User.GetAvatarUrl())
            .WithTitle("Ended Poll Early")
            .WithDescription($"This poll was supposed to end {endTime /* in x units */}.")
            .WithTimestamp(_timeProvider.GetUtcNow())
            .Build());
    }
}
