using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Subsonic.Response;

/// <summary>Subsonic API protocol version advertised to clients.</summary>
public static class SubsonicConstants
{
    public const string Version = "1.16.1";
    public static string ServerVersion
    {
        get
        {
            var v = SubsonicPlugin.Instance?.Version;
            if (v == null) return "0.0.0";
            // Parts 1-2 (Major/Minor) encode the Jellyfin target (e.g. 12.1).
            // Parts 3-4 (Build/Revision) are the plugin version exposed to Subsonic clients.
            return $"{v.Build}.{v.Revision}.0";
        }
    }
    public const string ServerType = "subfin-ng";

    /// <summary>OpenSubsonic extensions actually implemented, as returned by getOpenSubsonicExtensions.</summary>
    public static readonly (string Name, int[] Versions)[] Extensions =
    [
        ("formPost", [1]),
        ("transcodeOffset", [1]),
        ("songLyrics", [1]),
    ];
}

/// <summary>Standard Subsonic error codes.</summary>
public static class ErrorCode
{
    public const int Generic = 0;
    public const int RequiredParameterMissing = 10;
    public const int ClientUpgrade = 20;
    public const int ServerDown = 30;
    public const int WrongCredentials = 40;
    public const int TokenAuthNotSupported = 41;
    public const int AuthMechanismNotSupported = 42;
    public const int ConflictingAuthMechanisms = 43;
    public const int InvalidApiKey = 44;
    public const int NotLicensed = 50;
    public const int NotAuthorized = 50;  // "The user is not authorized for the given operation"
    public const int TrialExpired = 60;
    public const int NotFound = 70;
}

/// <summary>Builds JSON-serializable Subsonic envelope objects.</summary>
public static class SubsonicEnvelope
{
    /// <summary>
    /// Returns a <see cref="JsonObject"/> whose keys are ordered: metadata first, payload last.
    /// JsonObject preserves insertion order, which is critical for clients (e.g. Navic/subsonic-kotlin)
    /// that use .entries.last() to locate the payload key.
    /// </summary>
    public static JsonObject Ok(Dictionary<string, object>? payload = null)
    {
        var inner = new JsonObject
        {
            ["status"] = "ok",
            ["version"] = SubsonicConstants.Version,
            ["type"] = SubsonicConstants.ServerType,
            ["serverVersion"] = SubsonicConstants.ServerVersion,
            ["openSubsonic"] = true,
        };
        if (payload != null)
            foreach (var kv in payload)
                inner[kv.Key] = kv.Value == null ? null : JsonSerializer.SerializeToNode(kv.Value);

        return new JsonObject { ["subsonic-response"] = inner };
    }

    /// <param name="helpUrl">Where users can fix the problem (e.g. manage API keys); omitted when null.</param>
    public static JsonObject Error(int code, string message, string? helpUrl = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (helpUrl != null) error["helpUrl"] = helpUrl;
        return new JsonObject
        {
            ["subsonic-response"] = new JsonObject
            {
                ["status"] = "failed",
                ["version"] = SubsonicConstants.Version,
                // required in every OpenSubsonic response, errors included
                ["type"] = SubsonicConstants.ServerType,
                ["serverVersion"] = SubsonicConstants.ServerVersion,
                ["openSubsonic"] = true,
                ["error"] = error,
            },
        };
    }
}
