# Subfin — OpenSubsonic API for Jellyfin

Exposes an [OpenSubsonic](https://opensubsonic.netlify.app/)-compatible API directly from Jellyfin, so Subsonic and Navidrome clients can use your Jellyfin music library. There's no separate server or proxy, and no separate accounts: apps sign in with Jellyfin usernames.

## Requirements

- Jellyfin **12.1** or later (built against 12.1.0)
- .NET 10 runtime (included in Jellyfin 12.x)

## Installation

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and add:
   ```
   https://raw.githubusercontent.com/williamkray/subfin-plugin/main/jellyfin-plugin-subfin-manifest.json
   ```
2. Go to **Catalog**, find **Subfin**, and install it.
3. Restart Jellyfin.

To install a build of your own, run `dotnet publish -c Release Jellyfin.Plugin.Subsonic`. Copy `Jellyfin.Plugin.Subsonic.dll` and `meta.json` into `<jellyfin config>/plugins/Subfin_<version>/`, then restart Jellyfin.

## Connecting a client

An administrator first generates an **OpenSubsonic password** for each user who'll use a Subsonic app: in **Dashboard → Plugins → Subfin**, click **Generate** next to the user. The page shows the password once, together with the server path and username to enter in the app.

| Setting | Value |
| --- | --- |
| Server URL | `https://<your-jellyfin>/opensubsonic`, including Jellyfin's base URL if it has one (e.g. `https://example.com/jellyfin/opensubsonic`) |
| Username | the Jellyfin username |
| Password | the user's OpenSubsonic password |

The OpenSubsonic password works with both ways Subsonic apps sign in: token login, the way Navidrome prefers apps to sign in and the default in most apps, and plain password login. Token login sends `md5(password + salt)`, so the server has to know the password itself. The plugin therefore stores it encrypted instead of hashed.

Apps that offer plain password login (often called **legacy authentication**) can use the Jellyfin password instead. Jellyfin checks it, including login providers such as LDAP and the lockout after failed attempts. Jellyfin only stores a hash of that password, so it can't be used for token login: that gets error 41. API keys aren't offered (error 42). Reach Jellyfin over HTTPS, since plain password login sends the password with every request.

Jellyfin's rules apply to Subsonic logins as they do to its own:

- **Account checks.** Disabled accounts, remote access and access schedules are checked on every request.
- **Content.** Library access and parental ratings apply, and downloads need the download permission.
- **Changes.** Changing the Jellyfin password, disabling an account or changing its policy takes effect on the next request. So do regenerating and removing an OpenSubsonic password, which signs out the apps using it. Deleting a user deletes their OpenSubsonic password.
- **Devices.** Each app appears in Jellyfin's dashboard as a device of its own, named after the app. Its plays count towards the user's history and reach Jellyfin's scrobbler plugins.

Apps may send parameters in the query string or as a form `POST` (the `formPost` extension).

## Settings

In **Dashboard → Plugins → Subfin**:

- **OpenSubsonic passwords** – generate, regenerate or remove each user's password (see above).
- **Last.fm API key** – enables artist biographies and images (`getArtistInfo`, `getArtistInfo2`, `getAlbumInfo`).
- **Enable sharing** – see below. When it's off, share links stop working and apps can't create shares.
- **Log API requests** – logs each Subsonic call's method and format.

## Sharing

Apps create shares with `createShare`. The link, `https://<your-jellyfin>/opensubsonic/share/<id>?secret=…`, opens a page that plays the shared songs, with an M3U playlist and, if the sharer may download, a ZIP download. No Jellyfin login is needed.

A share link can only play the songs it shares, and only while the sharer could: it stops working when it expires, when it's deleted, and while the sharer's account is disabled or outside its schedule. Users list, change and delete their shares from their app (`getShares`, `updateShare`, `deleteShare`).

## Upgrading from device logins

Earlier versions had their own logins: app passwords per device, managed at `/subfin/`, with the API at `/rest`. When this version first starts, it does the following:

- **Shares and play queues** move from each device to its Jellyfin user. Links keep their secret but move from `/subfin/share/…` to `/opensubsonic/share/…`.
- **Devices** are removed, along with their stored app passwords.
- **Per-device library selections** are dropped. Use Jellyfin's library access for each user instead.

Each app then needs the new server URL and a new password: an OpenSubsonic password generated on the plugin page, or the Jellyfin password in apps with plain password login.

## Development

- `dotnet test` runs the unit tests.
- [`tests/conformance`](tests/conformance/README.md) checks a build end to end against a throwaway Jellyfin and the OpenSubsonic spec.
