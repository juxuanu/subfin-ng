# Subfin — OpenSubsonic API for Jellyfin

Exposes an [OpenSubsonic](https://opensubsonic.netlify.app/)-compatible API directly from Jellyfin, so Subsonic and Navidrome clients can use your Jellyfin music library. There's no separate server or proxy, and no separate accounts: clients sign in with your Jellyfin username and password.

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

| Setting | Value |
| --- | --- |
| Server URL | `https://<your-jellyfin>/opensubsonic`, including Jellyfin's base URL if it has one (e.g. `https://example.com/jellyfin/opensubsonic`) |
| Username / password | your Jellyfin username and password |
| Authentication | plain password, often called **legacy authentication** or **plain-text password** |

Token authentication, the default in many clients, can't work: it needs the server to know your password, and Jellyfin only stores a hash of it. A client that uses it gets error 41, which asks it to switch. API keys aren't offered (error 42). Because the password travels with every request, reach Jellyfin over HTTPS.

Jellyfin's rules apply to Subsonic logins as they do to its own:

- **Login checks.** Login providers (such as LDAP), disabled accounts, remote access, access schedules and the lockout after failed attempts all apply.
- **Content.** Library access and parental ratings apply, and downloads need the download permission.
- **Account changes.** Changing a password, disabling an account or changing its policy takes effect on the next request.
- **Devices.** Each client appears in Jellyfin's dashboard as a device of its own, named after the client. Its plays count towards the user's history and reach Jellyfin's scrobbler plugins.

Clients may send parameters in the query string or as a form `POST` (the `formPost` extension).

## Settings

In **Dashboard → Plugins → Subfin**:

- **Last.fm API key** – enables artist biographies and images (`getArtistInfo`, `getArtistInfo2`, `getAlbumInfo`).
- **Enable sharing** – see below. When it's off, share links stop working and clients can't create shares.
- **Log API requests** – logs each Subsonic call's method and format.

## Sharing

Clients create shares with `createShare`. The link, `https://<your-jellyfin>/opensubsonic/share/<id>?secret=…`, opens a page that plays the shared songs, with an M3U playlist and, if the sharer may download, a ZIP download. No Jellyfin login is needed.

A share link can only play the songs it shares, and only while the sharer could: it stops working when it expires, when it's deleted, and while the sharer's account is disabled or outside its schedule. Users list, change and delete their shares from their client (`getShares`, `updateShare`, `deleteShare`).

## Upgrading from device logins

Earlier versions had their own logins: app passwords per device, managed at `/subfin/`, with the API at `/rest`. When this version first starts, it does the following:

- **Shares and play queues** move from each device to its Jellyfin user. Links keep their secret but move from `/subfin/share/…` to `/opensubsonic/share/…`.
- **Devices** are removed, along with their stored app passwords.
- **Per-device library selections** are dropped. Use Jellyfin's library access for each user instead.

Each client then needs the new server URL and the user's Jellyfin password.

## Development

- `dotnet test` runs the unit tests.
- [`tests/conformance`](tests/conformance/README.md) checks a build end to end against a throwaway Jellyfin and the OpenSubsonic spec.
