using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

/// <summary>
/// Clients like UAPP send a whole playlist's song ids in the query string, so the plugin raises
/// Kestrel's 8 KB request line limit.
/// </summary>
public class RequestLimitTests
{
    private static KestrelServerLimits Limits(Action<KestrelServerOptions>? jellyfin = null)
    {
        var services = new ServiceCollection();
        if (jellyfin != null) services.Configure(jellyfin);
        new PluginServiceRegistrator().RegisterServices(services, null!);
        return services.BuildServiceProvider().GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;
    }

    [Fact]
    public void RequestLine_IsRaisedTo1MB()
    {
        Assert.Equal(PluginServiceRegistrator.MaxRequestLineSize, Limits().MaxRequestLineSize);
    }

    [Fact]
    public void RequestLine_StaysWithinTheRequestBuffer()
    {
        // Kestrel won't start with a request line limit over its request buffer
        var limits = Limits(o => o.Limits.MaxRequestBufferSize = 64 * 1024);
        Assert.Equal(64 * 1024, limits.MaxRequestLineSize);
    }

    [Fact]
    public void RequestLine_IsNeverLowered()
    {
        var limits = Limits(o => { o.Limits.MaxRequestBufferSize = null; o.Limits.MaxRequestLineSize = 4 * 1024 * 1024; });
        Assert.Equal(4 * 1024 * 1024, limits.MaxRequestLineSize);
    }
}
