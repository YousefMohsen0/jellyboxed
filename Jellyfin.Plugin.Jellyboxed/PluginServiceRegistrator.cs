using Jellyfin.Plugin.Jellyboxed.Letterboxd;
using Jellyfin.Plugin.Jellyboxed.Services;
using Jellyfin.Plugin.Jellyboxed.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Jellyboxed;

/// <summary>Registers the plugin's services with Jellyfin's dependency injection container.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<RequestGate>();
        serviceCollection.AddSingleton<AccountStore>();
        serviceCollection.AddSingleton<AccountStateStore>();
        serviceCollection.AddSingleton<SlugResolver>();
        serviceCollection.AddSingleton<ClientPool>();
        serviceCollection.AddSingleton<JellyfinBridge>();
        serviceCollection.AddSingleton<SyncService>();
        serviceCollection.AddSingleton<FilmService>();
        serviceCollection.AddSingleton<PlaybackWatcher>();

        serviceCollection.AddHostedService(sp => sp.GetRequiredService<SyncService>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<PlaybackWatcher>());

        // Adds the Jellyboxed script to Jellyfin's web client without needing any other plugin.
        serviceCollection.AddSingleton<IStartupFilter, ScriptInjectionStartupFilter>();
    }
}
