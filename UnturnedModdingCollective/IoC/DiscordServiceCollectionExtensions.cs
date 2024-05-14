using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;

namespace UnturnedModdingCollective.IoC;

public static class DiscordServiceCollectionExtensions
{
    /// <summary>
    /// Add a discord bot using the WebSockets API.
    /// </summary>
    public static IDiscordBuilder<DiscordSocketClient, DiscordSocketConfig> AddDiscordWebSockets(this IServiceCollection serviceCollection)
    {
        return new WebSocketsDiscordBuilder(serviceCollection);
    }

    /// <summary>
    /// Add an interaction module to the service collection.
    /// </summary>
    public static IServiceCollection AddDiscordInteractionModule<TInteractionModule>(this IServiceCollection serviceCollection)
        where TInteractionModule : IInteractionModuleBase
    {
        serviceCollection.Add(new ServiceDescriptor(typeof(IInteractionModuleBase), typeof(TInteractionModule), ServiceLifetime.Transient));
        serviceCollection.Add(new ServiceDescriptor(typeof(TInteractionModule), typeof(TInteractionModule), ServiceLifetime.Transient));
        return serviceCollection;
    }

    /// <summary>
    /// Add an interaction module to the service collection.
    /// </summary>
    public static IServiceCollection AddDiscordInteractionModule<TInteractionModule, TImplementationModuleType>(this IServiceCollection serviceCollection)
        where TInteractionModule : IInteractionModuleBase where TImplementationModuleType : IInteractionModuleBase
    {
        serviceCollection.Add(new ServiceDescriptor(typeof(IInteractionModuleBase), typeof(TImplementationModuleType), ServiceLifetime.Transient));
        serviceCollection.Add(new ServiceDescriptor(typeof(TInteractionModule), typeof(TImplementationModuleType), ServiceLifetime.Transient));
        return serviceCollection;
    }

    /// <summary>
    /// Set up interaction modules.
    /// </summary>
    public static IServiceProvider RegisterInteractionModules(this IServiceProvider serviceProvider)
    {
        InteractionService intxService = serviceProvider.GetRequiredService<InteractionService>();
        foreach (IInteractionModuleBase moduleBase in serviceProvider.GetServices<IInteractionModuleBase>())
        {
            intxService.AddModuleAsync(moduleBase.GetType(), serviceProvider);
        }

        return serviceProvider;
    }
    private class WebSocketsDiscordBuilder(IServiceCollection collection) : IDiscordBuilder<DiscordSocketClient, DiscordSocketConfig>
    {
        public IDiscordBuilder<DiscordSocketClient, DiscordSocketConfig> ConfigureClient(Action<DiscordSocketConfig>? config = null)
        {
            Type[] toRemove = [typeof(DiscordConfig), typeof(IDiscordClient)];
            foreach (Type type in toRemove)
            {
                ServiceDescriptor? existing = collection.FirstOrDefault(x => type.IsAssignableFrom(x.ImplementationType));
                if (existing != null)
                    collection.Remove(existing);
            }

            DiscordSocketConfig socketConfig = new DiscordSocketConfig();
            config?.Invoke(socketConfig);

            collection.AddSingleton<DiscordConfig>(socketConfig);
            collection.AddSingleton<DiscordRestConfig>(socketConfig);
            collection.AddSingleton(socketConfig);
            collection.AddSingleton<DiscordSocketClient>();
            collection.AddSingleton<IDiscordClient>(serviceProvider => serviceProvider.GetRequiredService<DiscordSocketClient>());
            return this;
        }
        public IDiscordBuilder<DiscordSocketClient, DiscordSocketConfig> WithInteractions(Action<InteractionServiceConfig>? config = null)
        {
            InteractionServiceConfig commandConfig = new InteractionServiceConfig();
            config?.Invoke(commandConfig);

            collection.AddSingleton(commandConfig);
            collection.AddSingleton<InteractionService>();
            collection.AddSingleton<IRestClientProvider>(serviceProvider => serviceProvider.GetRequiredService<DiscordSocketClient>());
            return this;
        }
    }
}

public interface IDiscordBuilder<TDiscordClient, out TDiscordConfig> where TDiscordClient : IDiscordClient where TDiscordConfig : DiscordConfig
{
    IDiscordBuilder<TDiscordClient, TDiscordConfig> ConfigureClient(Action<TDiscordConfig> config);
    IDiscordBuilder<TDiscordClient, TDiscordConfig> WithInteractions(Action<InteractionServiceConfig> config);
}