using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.Subsonic.Auth;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Subsonic;

/// <summary>Registers plugin services with Jellyfin's DI container.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SubsonicAuth>();
        serviceCollection.AddScoped<IEventConsumer<UserPasswordChangedEventArgs>, LoginCacheInvalidator>();
        serviceCollection.AddScoped<IEventConsumer<UserUpdatedEventArgs>, LoginCacheInvalidator>();
        serviceCollection.AddScoped<IEventConsumer<UserDeletedEventArgs>, LoginCacheInvalidator>();
    }
}
