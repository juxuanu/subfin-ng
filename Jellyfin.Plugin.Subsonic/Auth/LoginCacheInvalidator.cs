using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;

namespace Jellyfin.Plugin.Subsonic.Auth;

/// <summary>
/// Forgets remembered logins when a user's password or name changes or the user is deleted, so the old
/// credentials stop working at once instead of when the login cache expires.
/// </summary>
public class LoginCacheInvalidator :
    IEventConsumer<UserPasswordChangedEventArgs>,
    IEventConsumer<UserUpdatedEventArgs>,
    IEventConsumer<UserDeletedEventArgs>
{
    private readonly SubsonicAuth _auth;

    public LoginCacheInvalidator(SubsonicAuth auth) => _auth = auth;

    public Task OnEvent(UserPasswordChangedEventArgs eventArgs) => Forget(eventArgs.Argument.Id);

    public Task OnEvent(UserUpdatedEventArgs eventArgs) => Forget(eventArgs.Argument.Id);

    public Task OnEvent(UserDeletedEventArgs eventArgs) => Forget(eventArgs.Argument.Id);

    private Task Forget(System.Guid userId)
    {
        _auth.Forget(userId);
        return Task.CompletedTask;
    }
}
