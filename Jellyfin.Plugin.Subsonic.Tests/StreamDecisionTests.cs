using Jellyfin.Plugin.Subsonic.Controllers;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

/// <summary>stream sends the file itself unless the format or bitrate asked for needs a transcode.</summary>
public class StreamDecisionTests
{
    [Theory]
    [InlineData(null, 0, "mp3", 320, true)]      // nothing asked for
    [InlineData("raw", 64, "flac", 900, true)]   // "raw" is the file, whatever the limit
    [InlineData("mp3", 0, "mp3", 320, true)]     // the file's own format
    [InlineData("MP3", 0, "mp3", 320, true)]
    [InlineData("mp3", 320, "mp3", 256, true)]   // within the limit
    [InlineData(null, 320, "mp3", 320, true)]
    [InlineData("mp3", 128, "mp3", 256, false)]  // over the limit
    [InlineData(null, 128, "flac", 900, false)]
    [InlineData("mp3", 0, "flac", 900, false)]   // another format
    [InlineData("aac", 0, "m4a", 256, false)]    // AAC in .m4a isn't the raw AAC asked for
    [InlineData(null, 320, "mp3", 0, false)]     // unknown bitrate: can't tell it fits
    public void SendsTheFile_OnlyWhenItFits(string? format, int maxBitRate, string suffix, int fileBitRate, bool sendsFile) =>
        Assert.Equal(sendsFile, SubsonicController.CanSendFile(format, maxBitRate, suffix, fileBitRate));
}
