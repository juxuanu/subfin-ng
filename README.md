# Subfin — OpenSubsonic API for Jellyfin

Subfin lets Subsonic-compatible music apps play your Jellyfin music library. It adds an [OpenSubsonic](https://opensubsonic.netlify.app/) API to Jellyfin itself, so there's no second server to run and no second set of accounts: apps sign in with Jellyfin usernames, and Jellyfin's library access, parental ratings and account rules apply. Everything apps see comes from Jellyfin, including artist biographies, album notes, images and similar artists.

Requires Jellyfin **12.1** or later.

## Install

1. In Jellyfin, open **Dashboard → Plugins → Manage Repositories** and add this repository:
   ```
   https://raw.githubusercontent.com/juxuanu/subfin-plugin/main/jellyfin-plugin-subfin-manifest.json
   ```
2. Back in **Plugins**, choose **Available**, open **Subfin** and install it.
3. Restart Jellyfin.

Jellyfin then offers new versions as updates, like for any other plugin.

<details>
<summary>Build it yourself instead</summary>

1. Build the plugin (needs the .NET 10 SDK):
   ```sh
   dotnet publish -c Release Jellyfin.Plugin.Subsonic
   ```
2. Copy `Jellyfin.Plugin.Subsonic/bin/Release/net10.0/publish/Jellyfin.Plugin.Subsonic.dll` and `meta.json` into a new folder `plugins/Subfin_<version>/` in Jellyfin's data directory. That's `/var/lib/jellyfin/plugins/` for the Linux packages and `/config/plugins/` in the Docker image.
3. Restart Jellyfin.

</details>

## Set up

1. In Jellyfin, open **Dashboard → Plugins → Subfin**.
2. Click **Generate** next to each user who'll use a music app. Copy the password: it's shown only once.
3. In the app, add a server:

   | | |
   | --- | --- |
   | Server | your Jellyfin address followed by `/opensubsonic`, e.g. `https://jellyfin.example.com/opensubsonic` (the settings page shows the exact path) |
   | Username | the Jellyfin username |
   | Password | the OpenSubsonic password from step 2 |

That's all. Use HTTPS if the app connects from outside your home network.

## Passwords

The OpenSubsonic password works with token login, the way Navidrome prefers apps to sign in and the default in most apps, and with plain password login. Token login needs the server to know the password, so Subfin stores it encrypted.

Apps that offer plain password (“legacy”) login can use the Jellyfin password instead. It can't be used for token login, because Jellyfin only stores a hash of it.

**Regenerate** or **Remove** on the settings page signs out the apps using the old password straight away. Changes in Jellyfin, such as disabling a user or changing their permissions, also take effect on the next request. Changing a user's Jellyfin password doesn't change their OpenSubsonic password.

## Sharing

Apps can create share links for songs, albums and playlists. A link opens a page that plays the shared songs, with an M3U playlist and a ZIP download if the sharing user may download. It doesn't need a Jellyfin login, and it stops working when it expires or is deleted. Sharing can be turned off on the settings page.

## Settings

In **Dashboard → Plugins → Subfin**:

- **OpenSubsonic passwords**: one per user, see above.
- **Enable sharing**: when off, apps can't create share links and existing links stop working.
- **Log API requests**: logs each request's method, for troubleshooting.

## Updating

Install updates from **Plugins**, then restart the Jellyfin service, for example with `systemctl restart jellyfin` or by restarting the container. The Restart button in Jellyfin's dashboard can keep the old plugin code loaded.

## Upgrading from earlier versions

Versions with their own device logins (the `/subfin/` page, API at `/rest`) are upgraded on first start. Shares and play queues move to their Jellyfin user, and the device logins and their passwords are deleted. Share links move from `/subfin/share/…` to `/opensubsonic/share/…`. Apps need the new server path and a new password.

## Development

- `dotnet test` runs the unit tests.
- `scripts/release.sh <version>` (on `main`) bumps the version, tags it and pushes. GitHub Actions then builds and tests it, publishes the release and adds it to the repository manifest.
- `tests/conformance/run.sh` checks a build against a throwaway Jellyfin and the OpenSubsonic spec; `--ui` also tests the settings page in a browser. See [its README](tests/conformance/README.md).
