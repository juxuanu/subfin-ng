using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Subsonic.Auth;
using Jellyfin.Plugin.Subsonic.Store;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

[Collection("StaticState")]
public sealed class SubsonicAuthTests : IDisposable
{
    private const string Password = "correct horse";
    private static readonly IPAddress Client = IPAddress.Parse("192.168.1.20");

    private readonly string _dir = Directory.CreateTempSubdirectory("subfin-test").FullName;
    private readonly IUserManager _users = Substitute.For<IUserManager>();
    private readonly User _alice = new("alice", "default", "default");
    private readonly SubsonicAuth _auth;

    public SubsonicAuthTests()
    {
        SubsonicStore.Initialize(Path.Combine(_dir, "subsonic.db"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        // What Jellyfin does: null for a known user with the wrong password, AuthenticationException for an
        // unknown user, SecurityException when a correct login isn't allowed.
        _users.AuthenticateUser(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>()).Returns((User?)null);
        _users.AuthenticateUser("alice", Password, Arg.Any<string>(), Arg.Any<bool>()).Returns(_alice);
        _users.AuthenticateUser("nobody", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .ThrowsAsync(new AuthenticationException("Invalid username or password entered."));
        _users.AuthenticateUser("locked", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .ThrowsAsync(new SecurityException("The locked account is currently disabled."));
        _auth = new SubsonicAuth(_users, NullLogger<SubsonicAuth>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private Task<object> Resolve(params (string Key, string Value)[] query)
    {
        var dict = new Dictionary<string, StringValues>();
        foreach (var (k, v) in query) dict[k] = v;
        return _auth.ResolveAsync(new QueryCollection(dict), Client);
    }

    private static int ErrorCodeOf(object result) => Assert.IsType<SubsonicAuth.AuthError>(result).Code;

    private Task JellyfinWasAsked(int times) =>
        _users.Received(times).AuthenticateUser(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());

    // ── Jellyfin logins ───────────────────────────────────────────────────────

    [Fact]
    public async Task Password_SignsInAsTheJellyfinUser()
    {
        var result = Assert.IsType<AuthResult>(await Resolve(("u", "alice"), ("p", Password)));
        Assert.Equal(_alice.Id, result.UserId);
        Assert.Null(result.ShareId);
        // Checked by Jellyfin with the client's address (remote-access rules), not as a new session
        await _users.Received(1).AuthenticateUser("alice", Password, "192.168.1.20", false);
    }

    [Fact]
    public async Task HexEncodedPassword_SignsIn()
    {
        var hex = "enc:" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(Password));
        Assert.IsType<AuthResult>(await Resolve(("u", "alice"), ("p", hex)));
    }

    [Theory]
    [InlineData("alice", "wrong")]
    [InlineData("nobody", "anything")]
    [InlineData("alice", "enc:ZZ")]
    public async Task WrongCredentials_AreError40(string user, string password)
    {
        Assert.Equal(40, ErrorCodeOf(await Resolve(("u", user), ("p", password))));
    }

    [Fact]
    public async Task LoginJellyfinRefuses_IsError50()
    {
        var error = Assert.IsType<SubsonicAuth.AuthError>(await Resolve(("u", "locked"), ("p", "pw")));
        Assert.Equal(50, error.Code);
        Assert.Contains("disabled", error.Message);
    }

    [Fact]
    public async Task TokenAuthentication_IsError41()
    {
        // Jellyfin only stores a password hash, so md5(password + salt) can't be checked
        Assert.Equal(41, ErrorCodeOf(await Resolve(("u", "alice"), ("t", "26719a1196d2a940705a59634eb18eab"), ("s", "c19b2d"))));
        await JellyfinWasAsked(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApiKeys_AreError42(bool withPassword)
    {
        var result = withPassword
            ? await Resolve(("apiKey", "whatever"), ("u", "alice"), ("p", Password))
            : await Resolve(("apiKey", "whatever"));
        Assert.Equal(42, ErrorCodeOf(result));
    }

    [Fact]
    public async Task PasswordAndToken_Together_IsError43()
    {
        Assert.Equal(43, ErrorCodeOf(await Resolve(("u", "alice"), ("p", Password), ("t", "0123"), ("s", "ab"))));
    }

    [Theory]
    [InlineData("u")]
    [InlineData("p")]
    public async Task MissingUsernameOrPassword_IsError10(string missing)
    {
        var query = new List<(string, string)> { ("u", "alice"), ("p", Password) };
        query.RemoveAll(kv => kv.Item1 == missing);
        Assert.Equal(10, ErrorCodeOf(await Resolve(query.ToArray())));
    }

    // ── Remembered logins ─────────────────────────────────────────────────────

    [Fact]
    public async Task SuccessfulLogin_IsRemembered()
    {
        await Resolve(("u", "alice"), ("p", Password));
        var again = Assert.IsType<AuthResult>(await Resolve(("u", "alice"), ("p", Password)));
        Assert.Equal(_alice.Id, again.UserId);
        await JellyfinWasAsked(1);
    }

    [Fact]
    public async Task FailedLogins_AlwaysReachJellyfin()
    {
        // so Jellyfin's lockout counts every attempt
        await Resolve(("u", "alice"), ("p", "wrong"));
        await Resolve(("u", "alice"), ("p", "wrong"));
        await JellyfinWasAsked(2);
    }

    [Fact]
    public async Task RememberedLogin_DoesNotAcceptOtherPasswords()
    {
        await Resolve(("u", "alice"), ("p", Password));
        Assert.Equal(40, ErrorCodeOf(await Resolve(("u", "alice"), ("p", Password + "!"))));
    }

    [Fact]
    public async Task ForgottenLogin_IsCheckedAgain()
    {
        await Resolve(("u", "alice"), ("p", Password));
        _auth.Forget(_alice.Id);
        await Resolve(("u", "alice"), ("p", Password));
        await JellyfinWasAsked(2);
    }

    // ── Share links ───────────────────────────────────────────────────────────

    private string Share(DateTimeOffset? expires, string secret) =>
        SubsonicStore.InsertShare(_alice.Id.ToString("N"), ["al-1"], ["song1", "song2"], null, expires?.ToString("o"), secret);

    [Fact]
    public async Task ShareLink_ActsAsItsOwner_LimitedToTheSharedSongs()
    {
        var uid = Share(DateTimeOffset.UtcNow.AddDays(1), "s3cret");
        var result = Assert.IsType<AuthResult>(await Resolve(("u", $"share_{uid}"), ("p", "s3cret")));
        Assert.Equal(_alice.Id, result.UserId);
        Assert.Equal(uid, result.ShareId);
        Assert.Equal(new HashSet<string> { "song1", "song2" }, result.ShareAllowedIds);
        await JellyfinWasAsked(0);
    }

    [Fact]
    public async Task ShareLink_WithWrongSecret_IsError40()
    {
        var uid = Share(null, "s3cret");
        Assert.Equal(40, ErrorCodeOf(await Resolve(("u", $"share_{uid}"), ("p", "guess"))));
        Assert.Equal(40, ErrorCodeOf(await Resolve(("u", "share_nope"), ("p", "s3cret"))));
    }

    [Fact]
    public async Task ExpiredShareLink_IsError40()
    {
        var uid = Share(DateTimeOffset.UtcNow.AddDays(-1), "s3cret");
        Assert.Equal(40, ErrorCodeOf(await Resolve(("u", $"share_{uid}"), ("p", "s3cret"))));
    }

    [Fact]
    public void CheckShare_ReportsExpiry_OnlyWithTheRightSecret()
    {
        var uid = Share(DateTimeOffset.UtcNow.AddDays(-1), "s3cret");
        Assert.Equal(SubsonicAuth.ShareStatus.Expired, SubsonicAuth.CheckShare(uid, "s3cret").Status);
        Assert.Equal(SubsonicAuth.ShareStatus.Invalid, SubsonicAuth.CheckShare(uid, "guess").Status);
    }

    // ── DecodePassword ────────────────────────────────────────────────────────

    [Fact]
    public void DecodePassword_PlainText()
    {
        Assert.Equal("mypassword", SubsonicAuth.DecodePassword("mypassword"));
    }

    [Fact]
    public void DecodePassword_EncHex()
    {
        // "abc" in UTF-8 hex is 61 62 63
        Assert.Equal("abc", SubsonicAuth.DecodePassword("enc:616263"));
    }

    [Fact]
    public void DecodePassword_InvalidHex()
    {
        Assert.Null(SubsonicAuth.DecodePassword("enc:ZZZZ"));
    }

    [Fact]
    public void DecodePassword_Empty()
    {
        Assert.Null(SubsonicAuth.DecodePassword(""));
    }
}
