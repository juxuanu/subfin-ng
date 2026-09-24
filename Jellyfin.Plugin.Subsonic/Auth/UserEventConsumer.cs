using System;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.Subsonic.Store;
using MediaBrowser.Controller.Events;

namespace Jellyfin.Plugin.Subsonic.Auth;

/// <summary>
/// Keeps logins in step with Jellyfin's users: remembered logins are forgotten when a user's password,
/// name or policy changes, so the old credentials stop working at once instead of when the login
/// cache expires, and a deleted user's OpenSubsonic password goes with them.
/// </summary>
public class UserEventConsumer :
    IEventConsumer<UserPasswordChangedEventArgs>,
    IEventConsumer<UserUpdatedEventArgs>,
    IEventConsumer<UserDeletedEventArgs>
{
    private readonly SubsonicAuth _auth;

    public UserEventConsumer(SubsonicAuth auth) => _auth = auth;

    public Task OnEvent(UserPasswordChangedEventArgs eventArgs) => Forget(eventArgs.Argument.Id);

    public Task OnEvent(UserUpdatedEventArgs eventArgs) => Forget(eventArgs.Argument.Id);

    public Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        SubsonicStore.DeleteSubsonicPassword(eventArgs.Argument.Id.ToString("N"));
        return Forget(eventArgs.Argument.Id);
    }

    private Task Forget(Guid userId)
    {
        _auth.Forget(userId);
        return Task.CompletedTask;
    }
}
