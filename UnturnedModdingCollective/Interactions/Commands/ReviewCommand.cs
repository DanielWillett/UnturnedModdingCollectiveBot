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
    private readonly TimeProvider _timeProvider;
    public ReviewCommand(BotDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    [SlashCommand("review", description: "Submit a portfolio to be reviewed for entry.")]
    [CommandContextType(InteractionContextType.Guild)]
    public async Task SendTestPoll()
    {
    }
}
