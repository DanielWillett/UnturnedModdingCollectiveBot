using DanielWillett.ReflectionTools.IoC;
using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using UnturnedModdingCollective.API;
using UnturnedModdingCollective.Interactions.Commands;
using UnturnedModdingCollective.Interactions.Commands.Admin;
using UnturnedModdingCollective.Interactions.Components;
using UnturnedModdingCollective.IoC;
using UnturnedModdingCollective.Services;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

#if DEBUG
builder.Environment.EnvironmentName = "Development";
#else
builder.Environment.EnvironmentName = "Production";
#endif

// add appsettings.Environment.json file to configuration
builder.Configuration.AddJsonFile(Path.Combine(Environment.CurrentDirectory, $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true);


/* LOGGING */
builder.Services.AddSerilog((_, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .MinimumLevel.Debug()
    );

/* DISCORD SETUP */
builder.Services.AddDiscordWebSockets()
    .ConfigureClient(config =>
    {
        config.GatewayIntents = GatewayIntents.Guilds;
    })
    .WithInteractions(config =>
    {
        config.DefaultRunMode = RunMode.Sync;
        config.UseCompiledLambda = true;
        config.AutoServiceScopes = true;
    });


/* SERVICES */
builder.Services.AddDbContext<BotDbContext>();
builder.Services.AddReflectionTools();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTransient<ISecretProvider, IdentitySecretProvider>();

builder.Services.AddSingleton<DiscordClientLifetime>();
builder.Services.AddSingleton<VoteLifetimeManager>();
builder.Services.AddTransient<PollFactory>();
builder.Services.AddTransient<EmbedFactory>();

builder.Services.AddSingleton<IHostedService, DiscordClientLifetime>(x => x.GetRequiredService<DiscordClientLifetime>());
builder.Services.AddSingleton<IHostedService, VoteLifetimeManager>(x => x.GetRequiredService<VoteLifetimeManager>());

/* COMMANDS */
builder.Services.AddDiscordInteractionModule<SetupRoleSelectCommand>();
builder.Services.AddDiscordInteractionModule<ApplicableRolesCommands>();

/* COMPONENTS */
builder.Services.AddDiscordInteractionModule<SubmitPortfolioComponent>();
builder.Services.AddDiscordInteractionModule<StartPortfolioComponent>();

try
{
    IHost host = builder.Build();

    host.Services.RegisterInteractionModules();

    /* Migrate database */
    BotDbContext dbContext = host.Services.GetRequiredService<BotDbContext>();
    await dbContext.Database.MigrateAsync();

    await host.RunAsync();
}
catch (HostAbortedException)
{
    // ignored, this is thrown by EF during migration commands
}
catch (Exception ex)
{
    string crashlogTxtPath = Path.Combine(Path.Combine(Environment.CurrentDirectory), "crashlog.txt");
    File.WriteAllText(crashlogTxtPath, ex.ToString());

    Console.WriteLine(ex);
}