using Microsoft.Extensions.Hosting;
using Serilog;

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);

hostBuilder.Services.AddSerilog((_, configuration) => configuration
        .ReadFrom.Configuration(hostBuilder.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console()
    );

IHost host = hostBuilder.Build();

await host.RunAsync();