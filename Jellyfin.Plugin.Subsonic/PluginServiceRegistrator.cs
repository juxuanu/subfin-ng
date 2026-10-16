using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.Subsonic.Auth;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Subsonic;

/// <summary>Registers plugin services with Jellyfin's DI container.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>Longest request line Jellyfin accepts with the plugin: 1 MB, Navidrome's limit too.</summary>
    public const int MaxRequestLineSize = 1024 * 1024;

    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SubsonicAuth>();
        serviceCollection.AddScoped<IEventConsumer<UserPasswordChangedEventArgs>, UserEventConsumer>();
        serviceCollection.AddScoped<IEventConsumer<UserUpdatedEventArgs>, UserEventConsumer>();
        serviceCollection.AddScoped<IEventConsumer<UserDeletedEventArgs>, UserEventConsumer>();

        // Subsonic clients send song lists in the query string (createPlaylist, updatePlaylist,
        // savePlayQueue), and Kestrel answers 414 to request lines over 8 KB, about 200 songs, before
        // the request reaches the plugin. Kestrel refuses to start if the limit is over its request
        // buffer, so it stays within that.
        serviceCollection.PostConfigure<KestrelServerOptions>(options =>
        {
            var limits = options.Limits;
            var allowed = Math.Min(MaxRequestLineSize, limits.MaxRequestBufferSize ?? long.MaxValue);
            limits.MaxRequestLineSize = (int)Math.Max(limits.MaxRequestLineSize, allowed);
        });
    }
}
