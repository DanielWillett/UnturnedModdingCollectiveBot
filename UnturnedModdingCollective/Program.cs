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
using UnturnedModdingCollective.Models.Config;
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
    );

/* DISCORD SETUP */
builder.Services.AddDiscordWebSockets()
    .ConfigureClient(config =>
    {
        config.GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMembers;
    })
    .WithInteractions(config =>
    {
        config.DefaultRunMode = RunMode.Sync;
        config.UseCompiledLambda = true;
        config.AutoServiceScopes = true;
    });

/* SERVICES */
builder.Services.AddReflectionTools();
builder.Services.AddDbContext<BotDbContext>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTransient<ISecretProvider, IdentitySecretProvider>();
builder.Services.AddTransient<ILiveConfiguration<LiveConfiguration>, JsonLiveConfiguration<LiveConfiguration>>();

builder.Services.AddTransient<PollFactory>();
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddTransient<EmbedFactory>();
builder.Services.AddTransient<EmoteService>();
builder.Services.AddSingleton<VoteLifetimeManager>();
builder.Services.AddSingleton<DiscordClientLifetime>();
builder.Services.AddSingleton<IPersistingRoleService>(x => x.GetRequiredService<PersistingRolesService>());

builder.Services.AddSingleton<IHostedService, VoteLifetimeManager>(x => x.GetRequiredService<VoteLifetimeManager>());
builder.Services.AddSingleton<IHostedService, DiscordClientLifetime>(x => x.GetRequiredService<DiscordClientLifetime>());
builder.Services.AddTransient<IHostedService, PersistingRolesService>(x => x.GetRequiredService<PersistingRolesService>());

builder.Services.AddSingleton<PersistingRolesService>();

/* COMMANDS */
builder.Services.AddDiscordInteractionModule<RolePersistCommand>();
builder.Services.AddDiscordInteractionModule<SetupRoleSelectCommand>();
builder.Services.AddDiscordInteractionModule<VoteManagementCommands>();
builder.Services.AddDiscordInteractionModule<ApplicableRolesCommands>();
builder.Services.AddDiscordInteractionModule<UnityVersionCommand>();

/* COMPONENTS */
builder.Services.AddDiscordInteractionModule<StartPortfolioComponent>();
builder.Services.AddDiscordInteractionModule<SubmitPortfolioComponent>();

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