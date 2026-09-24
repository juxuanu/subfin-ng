using Jellyfin.Plugin.Subsonic.Controllers;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

/// <summary>
/// Times SQLite stores with datetime('now') are UTC without saying so, whatever the server's time zone
/// (CI runs these on a non-UTC zone, as a UTC one can't tell the difference).
/// </summary>
public class StoredTimeTests
{
    [Theory]
    [InlineData("2026-09-24 16:30:00", "2026-09-24T16:30:00Z")]                // SQLite's datetime('now')
    [InlineData("2026-09-24T18:30:00.0000000+02:00", "2026-09-24T16:30:00Z")]  // DateTimeOffset.ToString("o")
    public void StoredTimes_AreSentAsIsoUtc(string stored, string sent) =>
        Assert.Equal(sent, SubsonicController.ToIsoDateTime(stored));
}
