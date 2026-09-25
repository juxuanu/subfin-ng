using System.Security.Cryptography;
using Jellyfin.Plugin.Subsonic.Configuration;
using Jellyfin.Plugin.Subsonic.Store;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Subsonic;

/// <summary>Jellyfin plugin entry point.</summary>
public sealed class SubsonicPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public SubsonicPlugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<SubsonicPlugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Auto-generate salt on first run.
        if (string.IsNullOrEmpty(Configuration.Salt))
        {
            Configuration.Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            SaveConfiguration();
            logger.LogInformation("[Subfin-NG] Generated new encryption salt");
        }

        // Initialize SQLite store — migrate data dir from SubsonicPlugin → SubfinPlugin if needed.
        // Still named after Subfin, whose installs keep their data when they switch to Subfin-NG.
        var oldDataDir = Path.Combine(applicationPaths.DataPath, "SubsonicPlugin");
        var dataDir = Path.Combine(applicationPaths.DataPath, "SubfinPlugin");
        if (Directory.Exists(oldDataDir) && !Directory.Exists(dataDir))
        {
            Directory.Move(oldDataDir, dataDir);
            logger.LogInformation("[Subfin-NG] Migrated data dir SubsonicPlugin → SubfinPlugin");
        }
        Directory.CreateDirectory(dataDir);
        SubsonicStore.Initialize(Path.Combine(dataDir, "subsonic.db"), Configuration.Salt);

        logger.LogInformation("[Subfin-NG] Plugin loaded, DB at {DataDir}", dataDir);
    }

    public static SubsonicPlugin? Instance { get; private set; }

    public override string Name => "Subfin-NG";

    // Not the id of Subfin, which Subfin-NG started as a fork of: Jellyfin tells plugins apart by id
    public override Guid Id => Guid.Parse("a5d299f5-eb55-4574-85cc-84e1a2f86972");

    public override string Description =>
        "OpenSubsonic API at /opensubsonic: use Subsonic/Navidrome clients with Jellyfin, signing in with Jellyfin accounts.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "Subfin-NG",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.Views.config.html",
            },
        ];
    }
}
