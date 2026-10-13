# Setup

## 1. Install

Dashboard → Plugins → **Manage repositories** → **+**, URL:

```
https://raw.githubusercontent.com/SurfHost/jellyfin-plugin-radio/main/manifest.json
```

Then install **Radio** from the plugin list and restart Jellyfin. Dashboard → Plugins should list **Radio** as Active.
Versions 0.0.x are test builds (GitHub pre-releases) for the checks in [docs/SPIKES.md](docs/SPIKES.md).

### By hand (without the repository)

Download `radio_<version>.zip` from the GitHub release, or build it (needs the .NET 10 SDK):

```
dotnet publish Jellyfin.Plugin.Radio/Jellyfin.Plugin.Radio.csproj -c Release -o publish
```

Copy `Jellyfin.Plugin.Radio.dll`, `meta.json` and `logo.png` into a new folder `Radio_<version>` inside Jellyfin's **plugins**
folder and restart Jellyfin. The plugins folder sits in Jellyfin's data folder:

| Install | Plugins folder |
|---|---|
| Docker / TrueNAS app | `/config/plugins` inside the container (TrueNAS: the jellyfin app's config storage, see Apps → jellyfin → Edit → Storage) |
| Debian / Ubuntu package | `/var/lib/jellyfin/plugins` |
| Windows | `%ProgramData%\Jellyfin\Server\plugins` |

## 2. First start

About 30 seconds after Jellyfin starts, the plugin loads the built-in Dutch stations and creates the **Radio** channel. Open the plugin
page (Dashboard → Radio in the sidebar, or Plugins → Radio → Settings) and press **Test**:

- **Channel: registered** and **N of N published stations ready to play**. If not, press **Sync now** and test again.
- Users listed in red cannot use the channel; fix what the line says.

## 3. Give users access

Dashboard → Users → *user*:

- **Access** tab, under **Channels**: tick **Radio**, or tick "Enable access to all channels".
- **Profile** tab, under **Playback**: "Force transcoding of remote media sources" must be **off**. Live radio cannot be transcoded.
- **Parental control** tab: "Block items with no or unrecognized rating" must not include **Channels**; stations carry no age rating.

## 4. Listen

- **Web, Desktop, Android, iOS:** Home → **My Media** → **Radio**, pick a station. Mark stations as favourite (heart) to find them quickly.
  The apps list the stations in the order of the plugin page.
- **Android TV:** the Android TV app only plays local audio files, so the channel does not work there. Hide it from that account with
  **Hide the channel from** on the plugin page. Do not tick an administrator you use in the dashboard: Radio would then disappear from
  every user's Access tab, and saving a user there would remove their access to it.
- **Casting:** cast from Chrome on `https://<your Jellyfin address>` or from the Jellyfin Android app (Play Store version). The Chromecast must
  be able to reach that address from your LAN (split DNS or hairpin NAT).

## 5. Your own stations

Plugin page → **Add a station**: name, stream URL, optional group and logo, then **Test and add** and **Save**.

- Use the stream itself, e.g. `https://icecast.omroep.nl/radio2-bb-mp3`. Many station sites offer a "listen" link to an .m3u or .pls
  playlist: open that file and use the URL inside it.
- Only MP3. Most stations offer MP3 next to AAC.
- For StreamTheWorld stations (Talpa, Mediahuis, BNR) store
  `https://playerservices.streamtheworld.com/api/livestream-redirect/<MOUNT>.mp3`. The plugin rewrites a pasted node URL (`12345.live.streamtheworld.com`) to that.
- After changing a stream URL, press **Test** again; the page will not save an untested URL.
- **Disabling or removing a station deletes its item** when you save. Its playlist entries are lost for good; favourites come back if the
  same station (same id) returns within 90 days.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| No Radio under My Media | The user has no channel access (step 3), or the channel is hidden from that user on the plugin page. |
| Radio is there but empty | The user blocks unrated channel content (step 3, Parental control). |
| Playback fails at once | "Force transcoding of remote media sources" is on for that user (step 3), or the station is down (plugin page → Test on that station). |
| A renamed station keeps its old name | Press **Sync now**; renames reach existing items on the next sync. |
| One station silent, others fine | The broadcaster changed its stream. Plugin page → **Test** on that station shows the error; replace the URL. |
