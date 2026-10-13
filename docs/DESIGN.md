# Design

This is the design the plugin is built from (2026-10-13). It was written by a panel of three independent designs and a judge, each claim
checked against Jellyfin 12.2's source (tag v12.2) and live tests. File and line references point at that tag.

Phases 2 and later are built only when the matching check in [SPIKES.md](SPIKES.md) passes. Where the 0.0.1 code deliberately
differs (for example, no Artists line until spike S9), the code comments say why.

## Corrections the judge made to the draft designs

- coverage-first and risk-first: both assume that bumping DateModified carries station edits through. ChannelManager v12.2 (src/Jellyfin.LiveTv/Channels/ChannelManager.cs@v12.2) sets Name, Overview, Genres and Tags only when isNew (L1027-1043), and calls SetImagePath for ImageUrl only when !item.HasImage(Primary) (L1133-1138). A DateModified bump re-saves Path, Artists and MediaSources (L1075-1080, L1175-1184), but renames and logo changes never reach existing items. ux-first's ItemSync patch through ILibraryManager is needed.
- coverage-first: PlaylistSync orders the Android Auto playlist with SortOrder and MoveItemAsync. jellyfin-android v2.7.3 PlaylistLibraryPage.kt L13-15 requests the playlist children with sortBy=SORT_NAME. The server applies that sort through Folder.PostFilterAndSort -> UserViewBuilder.SortAndPage (MediaBrowser.Controller/Entities/Folder.cs@v12.2 L1163-1178); Playlist.IsPreSorted is declared but has no effect there. Only SortName/ForcedSortName (or the names themselves) set the grid order. MoveItemAsync only changes the jellyfin-web playlist view.
- risk-first: it removes the Live TV tuner through the LiveTvOptions 'livetv' configuration and queues RefreshGuide through ITaskManager. ITunerHostManager.DeleteTunerHost(string? id) exists in 12.2 (MediaBrowser.Controller/LiveTv/ITunerHostManager.cs@v12.2 L44), and IListingsManager.DeleteListingsProvider exists too (L29). SaveTunerHost already calls _taskManager.CancelIfRunningAndQueue<RefreshGuideScheduledTask>() when dataSourceChanged=true (src/Jellyfin.LiveTv/TunerHosts/TunerHostManager.cs@v12.2). Note for all three designs: SaveTunerHost assigns a new GUID whenever info.Id is empty or unknown, so the stored id must be passed back on every save to stay idempotent.
- coverage-first: the S5 risk and fallback endpoint for when a local M3U file path is rejected. M3uParser v12.2 opens any Url that does not start with 'http' with AsyncFile.OpenRead (src/Jellyfin.LiveTv/TunerHosts/M3uParser.cs@v12.2 L52-54), so a DataPath file is accepted. Channel URLs inside the file are still limited to http/https/rtsp/rtp/udp (IsValidChannelUrl L257-264).
- risk-first and ux-first: their relay contingency puts an expiring HMAC (exp/TTL hours) in MediaSource.Path. Static channel sources are persisted in channelmediasourceinfos.json and rewritten only when the item is new or force-updated (ChannelManager.cs@v12.2 L1175-1184). DLNA reads only static sources, and the Cast receiver and Android Auto (after the 302) use Path as-is, so an expiring signature goes stale. Use a non-expiring per-station signature (slug|revision) revoked by rotating the secret, as coverage-first does, or bump DateModified on every re-sign.
- coverage-first: 'Item ids are MD5(externalId + channel name + type)'. The exact formula is _libraryManager.GetNewItemId(externalId + channelName + "16", typeof(T)) (ChannelManager.cs@v12.2 L941-951). ux-first states this correctly.
- coverage-first: 'all 37 verified Dutch streams are progressive HTTPS MP3'. The verified facts give MP3 explicitly for NPO and the .mp3 STW/Qmusic/JOE/100%NL/SLAM mounts, but not for Arrow (stream.player.arrow.nl/arrow) or the 12 regional CloudFront Icecast streams. Their codec must be probed before they ship enabled (risk-first's ship-disabled rule).
- risk-first: the client matrix marks Jellyfin Android (phone) as 'verified'. Channel-audio playback through universal is a verified path, but background playback, lock screen and AVRCP for an endless channel stream in the WebView/RemotePlayerService are only code-read. The android-auto research lists them as spike 7, so the row should read 'likely'.
- All designs (diagnostics): Admin/Test must call IMediaSourceManager.GetPlaybackMediaSources with allowMediaProbe=false. With true, a source lacking an Audio MediaStream triggers item.RefreshMetadata(FullRefresh, EnableRemoteContentProbe=true) (Emby.Server.Implementations/Library/MediaSourceManager.cs@v12.2 L183-195), which is the endless-stream ffprobe the design is meant to avoid.
- ux-first (confirmation, not a contradiction): removing or disabling a station deletes its item. GetChannelItemsInternal deletes every existing id no longer returned by the channel (ChannelManager.cs@v12.2 L757-775). It only runs when the listing is not served from cache. IChannel.IsEnabledFor is honoured for user views (ChannelManager L222-231; UserViewManager.cs@v12.2 L120), so coverage-first's HideChannelForUserIds and ux-first's ExcludedUserIds both work. They hide the channel view only; channel items reached through playlists are still governed by the channel permission.
- Cross-check of PR #18037 (merged 2026-09-14, 'Enforce channel mediasource id'). NormalizeMediaSources now fills an empty source Id with the item id, or MD5(itemId+path) for later sources. That is GUID-shaped but equal to the item id, so it is kept. A custom non-GUID Id such as 'radio-<slug>' passes the user-scoped filter in GetStaticMediaSources (MediaSourceManager.cs@v12.2 L398-403). All three designs' Id choices are valid; GUID- or 32-hex-shaped custom ids are not.


## Final design: "Radio" for Jellyfin 12.2 (SurfHost/jellyfin-plugin-radio, project Jellyfin.Plugin.Radio)

**Base and grafts.** The base is the **risk-first** design: spike build first, a small verified MVP, and every later phase gated by a go/no-go. Grafted onto it:
- **From ux-first:** RadioSync item maintenance for names, logos and ForcedSortName order; the "Mijn zenders" favourites playlist; a health probe that writes back the format fields; the URL normaliser; the artist-pollution spike; the warning that removing a station deletes it.
- **From coverage-first:** the Live TV copy with DeleteTunerHost and an optional XMLTV guide; IsEnabledFor exclusion; the full per-user permission audit; the MP3 frame-header probe; a non-expiring relay signature; the DisplayMessage toast.

**Constraint.** All listening goes through official Jellyfin apps. Third-party clients are a one-line fallback at the end.

---

### 1. Architecture

**One station list and one playback object.** Each station is a live **channel Audio item** of a single `IChannel` called "Radio", with one complete static MP3 `MediaSourceInfo`. Jellyfin's existing playback paths take that one item to every official app. In v1 the plugin never sits in the audio path. Two opt-in projections and one overlay are layered on top:
- **(B) Playlists for Android Auto.** Android Auto only browses Music, Books and Playlists.
- **(C) A TV-typed M3U Live TV copy for Android TV.** Android TV cannot play channel audio.
- **(D) Song-level now-playing.** Shown in the apps built on jellyfin-web, via the MeTube-style index.html injection.

### 1.1 Channel contract (verified against v12.2 source)
- **Name.** `IChannel.Name` is the frozen constant "Radio". Item id = `GetNewItemId(externalId + "Radio" + "16", typeof(Audio))` (src/Jellyfin.LiveTv/Channels/ChannelManager.cs@v12.2 L941-951). Renaming the channel or a slug, or switching ContentType to Podcast (which makes the item an AudioBook), changes every id and breaks favourites and playlists.
- **Cache.** `IHasCacheKey.GetCacheKey(userId)` and `DataVersion` both return the catalog hash. Both are part of the listing-cache path (L913-919), so a new station list gets a new cache file. That alone is not enough: going back to an earlier list within 3 hours hits the earlier, still-fresh file (review finding, 2026-10-13), so RadioSync also deletes the channel's listing cache folder before every pass.
- **Per-user hiding.** `IsEnabledFor(userId)` returns false for `ExcludedUserIds`. That list is meant for the living-room Android TV user (the channel folder is unplayable there) and for GuestPass guests. It hides the channel view, because `GetChannelsInternalAsync` filters on it (L222-231) and UserViewManager uses that call (Emby.Server.Implementations/Library/UserViewManager.cs@v12.2 L120). It does not block items reached through playlists; the built-in channel permission does that.
- **Not implemented:** `ISupportsMediaProbe` (it would ffprobe an endless stream), `IRequiresMediaInfoCallback` (DLNA reads only static sources), `ISupportsLatestMedia`, `IDisableMediaSourceDisplay`.
- **Root listing.** A flat root list in v0.1. "Regionale zenders" Container folders are optional later. Each station must be listed exactly once: the same item id under two parents flips ParentId and forces updates (L1086-1092).
- **ChannelItemInfo fields:**
  - Id = immutable slug (`npo-radio-2`), Type Media, MediaType Audio, ContentType **Song**, IsLiveStream true.
  - Name, Overview, ImageUrl, Artists = [Group, e.g. "NPO"] (spike S9 checks artist-list pollution).
  - DateModified = per-station `modifiedUtc` from state.json, bumped whenever the station's URL or format hash changes. This forces the re-save of `channelmediasourceinfos.json`, Path and Artists (L1075-1080, L1175-1184).
- **The source**, built by the pure `MediaSourceFactory`:
```
Id = "radio-<slug>"  (must never parse as a Guid: user-scoped static lists drop GUID ids, MediaSourceManager.cs@v12.2 L398-403)
Path = canonical URL (icecast.omroep.nl/...-bb-mp3, playerservices.streamtheworld.com/api/livestream-redirect/<MOUNT>.mp3,
       stream.qmusic.nl/qmusic/mp3, stream.joe.nl/joe/mp3, ...) - never a rotating STW node
Protocol=Http, Type=Default, Container="mp3", IsRemote = !RelayThroughServer (default true), IsInfiniteStream=true, RunTimeTicks=null,
SupportsDirectPlay=true, SupportsDirectStream=true, SupportsTranscoding=false (not authoritative: server overwrites it from the user's permission),
SupportsProbing=false, RequiresOpening=false, RequiredHttpHeaders empty (keeps Cast direct play), Bitrate set, DefaultAudioStreamIndex=0,
MediaStreams=[{Type=Audio, Index=0, Codec="mp3", BitRate, SampleRate, Channels=2, IsDefault=true}]
```
  - The audio stream avoids the `StreamBuilder` ThrowIfNull and the forced remote probe (MediaSourceManager.cs L183-195).
  - SampleRate, Channels and BitRate avoid the Sonos FLAC transcode.
  - MP3 only, because the Cast receiver has no ADTS direct-play profile and live audio cannot be HLS-transcoded.

### 1.2 Item maintenance (RadioSync, ux-first graft)
ChannelManager v12.2 sets Name, Overview, Genres and Tags **only when an item is new** (L1027-1043). It sets ImageUrl **only when no Primary image exists** (L1133-1138). A DateModified bump alone therefore never applies a rename or a new logo.

`RadioSync` is a hosted `BackgroundService` modelled on MeTube's DownloadWatcher: a 30 s startup delay, a `Nudge()` triggered by `Plugin.ConfigurationChanged`, and exception guards. Each pass:
1. Materialises items with `IChannelManager.GetChannelItemsInternal(new InternalItemsQuery{ChannelIds=[channelId]}, ...)`.
2. Maps slugs to item ids (computed id, confirmed by lookup).
3. Patches existing items through `ILibraryManager` (Name, Overview, Primary image, and in Phase 2 ForcedSortName), then `UpdateToRepositoryAsync`.

**Removing or disabling a station deletes its item** (dead-id sweep, L757-775). Favourites and playlist entries go with it. The config page warns about this, and PlaylistSync removes the dangling entries.

### 1.3 How each official client reaches the audio (no plugin code)
- **Web, Android phone and iOS app:** htmlAudioPlayer calls `/Audio/{id}/universal` without RemoteAudio. The result is DirectPlay, and AudioHelper serves a "Static remote stream": the server follows the STW/Qmusic 302s and never requests ICY.
- **Desktop / JMP:** mpvAudioPlayer uses PlaybackInfo, and mpv plays Path directly.
- **Cast receiver:** loads Path directly as `audio/mp3`, BUFFERED.
- **DLNA Play To:** reads the static sources and gets `/dlna/audio/{id}/stream.mp3?Static=true`, proxied by the server.
- **Android Auto:** universal with `enableRemoteMedia=true` gets a 302 to the https station (IsRemote=true). With RelayThroughServer the server relays instead.

### 1.4 Persistence
- `PluginConfiguration.Stations`: admin-only XML, the house pattern, edited with `updatePluginConfiguration`.
- `DataPath/radio/state.json`, using the MeTube JobStore pattern (SemaphoreSlim, atomic .tmp then Move, `.corrupt` fallback). It holds the per-station hash, modifiedUtc and itemId, plus playlistId, tunerHostId and listingsProviderId.

### 1.5 Live TV copy for Android TV (opt-in)
- The plugin writes `DataPath/radio/livetv.m3u`: `#EXTINF:-1 tvg-id="radio-<slug>" tvg-chno="901.." tvg-logo group-title="Radio",Name` followed by the canonical URL. A local file path is accepted, because M3uParser opens any non-http Url with `AsyncFile.OpenRead` (src/Jellyfin.LiveTv/TunerHosts/M3uParser.cs@v12.2 L52-54).
- Registration: `ITunerHostManager.SaveTunerHost(new TunerHostInfo{Id=<stored id or empty>, Type="m3u", Url=file, FriendlyName="Radio (Android TV)", TunerCount=0, EnableStreamLooping=false})`.
  - Pass the stored Id; otherwise SaveTunerHost mints a new GUID and duplicates the tuner.
  - SaveTunerHost already queues `RefreshGuideScheduledTask` (TunerHostManager.cs@v12.2).
  - When disabled: `DeleteTunerHost(id)` (ITunerHostManager.cs@v12.2 L44).
- Optional XMLTV guide (NPO `/api/broadcasts` programme names, otherwise an all-day "Live radio" block) via `IListingsManager.SaveListingProvider` and `DeleteListingsProvider`.
- Channels are always TV-typed, never `ChannelType.Radio`. Radio-typed channels fail silently on Android TV and throw in DLNA.

### 1.6 Now-playing (Phase 5)
- **NowPlayingService:** a per-station provider (Triton NP XML, NPO `/api/tracks` + `/api/broadcasts` with a browser User-Agent and Europe/Amsterdam times, Qmusic/JOE plays with played_at+duration staleness, KINK JSON, ICY one-shot fallback). It caches until endsAt, polls on demand only, clamped to 15-60 s.
- **Injection:** MeTube's composable `WebInjectionStartupFilter`, registered with `Insert(0)` as a singleton, adds `/Radio/ClientScript`. The path has no `/native/` segment, which the Android app would intercept.
- **radio.js:** patches the now-playing bar and page, and sets `navigator.mediaSession`. The Android `NativeShell.updateMediaSession` synthetic-itemId hack is off by default.
- **Opt-in DisplayMessage toast** for Android TV Live TV sessions.

### 1.7 Diagnostics
`Admin/Test` reports:
- whether the channel is registered, the item count, and the playlist and tuner ids;
- for each station, `IMediaSourceManager.GetPlaybackMediaSources(item, sampleNonAdminUser, allowMediaProbe: false, ...)`. The flag must be **false**; true triggers a FullRefresh with remote probing of an endless stream (MediaSourceManager.cs L183-195);
- per-user flags: no channel access (EnableAllChannels/EnabledChannels), ForceRemoteSourceTranscoding, EnableAudioPlaybackTranscoding, no EnableSharedDeviceControl (DLNA), EnableLiveTvAccess (copy);
- injection ServedCount, and whether the DLNA plugin is installed.

---

### 2. Components and files
All paths are under `C:\Users\Hans\Claude\GitHub\jellyfin-plugin-radio\`. The build files are copied from jellyfin-plugin-metube.

| File | Phase | Responsibility |
|---|---|---|
| `Jellyfin.Plugin.Radio/Jellyfin.Plugin.Radio.csproj` | 0 | net10.0; Jellyfin.Controller 12.2.0 with ExcludeAssets=runtime; CopyLocalLockFileAssemblies=false; Version, AssemblyVersion and FileVersion all equal; EmbeddedResource Web/**/*.html, *.js, Stations/*.json, Images/channel.png; InternalsVisibleTo the tests |
| `meta.json`, `manifest.json`, `global.json` (10.0.400), `NuGet.config`, `.gitignore` | 0 | House copies; new GUID; targetAbi 12.2.0.0; category General |
| `Plugin.cs` | 0 | BasePlugin + IHasWebPages; PluginId const; safe static Config; **GetPluginInfo 3-part override**; config page (admin-only); ConfigurationChanged wakes RadioSync |
| `PluginServiceRegistrator.cs` | 0 | `AddSingleton<IChannel, RadioChannel>()` (without it the channel never appears); singletons for every service; prefixed HttpClients `Radio.Probe` and `Radio.NowPlaying`; Phase 5: `Insert(0, Singleton<IStartupFilter, WebInjectionStartupFilter>)` |
| `Configuration/PluginConfiguration.cs`, `StationConfig.cs` | 1 | Stations; RelayThroughServer=false; ExcludedUserIds; PlaylistEnabled, PlaylistName, PlaylistOwnerUserId; FavoritesPlaylistEnabled (P6); LiveTvCopyEnabled=false, LiveTvFirstNumber=901, LiveTvGuideEnabled; NowPlayingEnabled, InjectionEnabled=true, AndroidMediaSessionHack=false; SongToastUserIds |
| `Stations/DefaultStations.json` + `DefaultStations.cs` | 1 | The 37 stations verified on 2026-10-13, canonical URLs, NowPlaying keys. NPO Soul & Jazz omitted. Stations without a confirmed `audio/mpeg` codec (Arrow, regional, until probed) ship disabled. No logo files committed: LogoUrl points at publicly hosted logos or stays empty |
| `Stations/StationCatalog.cs`, `StationValidator.cs`, `StreamUrlNormalizer.cs` | 1 | Validation: http/https only; reject .m3u/.m3u8/.pls/.asx/.mpd; rewrite `\d+.live.streamtheworld.com/<MOUNT>` to livestream-redirect; unique, immutable slugs; mp3 only; catalog hash |
| `Stations/StationStateStore.cs` | 1 | state.json (hash, modifiedUtc, itemId, playlistId, tunerId, listingsId); test constructor taking a directory |
| `Stations/StreamProbe.cs`, `Mp3FrameHeader.cs` | 1 | Admin probe: redirect chain, final scheme and host, Content-Type, icy-* headers. Never sends Icy-MetaData. Parses two consecutive MP3 frame headers and pre-fills Bitrate, SampleRate and Channels. Flags https-to-http redirects (Android Auto), AAC (Cast), and "ICY 200"/HLS (needs a relay) |
| `Channel/MediaSourceFactory.cs`, `ChannelItemFactory.cs`, `RadioChannel.cs` | 0-1 | The contracts in section 1.1 |
| `Library/RadioSync.cs` | 1 | Materialises items; patches Name, Overview and Primary image on existing items; P2 adds ForcedSortName and the playlist step |
| `Api/RadioController.cs`, `ApiModels.cs`, `UserAccess.cs` | 1 | `[ApiController][Route("Radio")][Authorize]` + NoStore + ErrorDto. Admin endpoints use `Policies.RequiresElevation`: Admin/Test, Admin/Probe, Admin/Defaults, Admin/Resync. UserAccess is MeTube's plus CanUseChannel and the permission audit |
| `Web/configPage.html` | 1 | Station table (slug read-only after save), Load Dutch defaults, Test (probe), Diagnostics, deployment checklist. House rules: no `${`, style inside data-role=page, contains the GUID |
| `Playlists/PlaylistSync.cs`, `PlaylistDiff.cs` | 2 | Public "Radio" playlist (CreatePlaylist Public=true, MediaType Audio, admin owner; then AddItemToPlaylistAsync/RemoveItemFromPlaylistAsync). Matched by stored id, never by name. Order comes from **ForcedSortName**, not MoveItemAsync |
| `Tasks/RadioSyncTask.cs`, `Tasks/CheckStationsTask.cs` | 2-3 | IScheduledTask: sync every 6 h; probe health weekly (writes back the format fields and bumps modifiedUtc) |
| `LiveTv/LiveTvCopyManager.cs`, `M3uWriter.cs`, `XmlTvWriter.cs` | 4 | Section 1.5 |
| `NowPlaying/*` (service, 5 providers, IcyMetadataReader, TitleCleaner), `Sessions/ListenerTracker.cs`, `SongToastNotifier.cs` | 5 | Section 1.6 |
| `Web/WebInjectionStartupFilter.cs`, `WebInjectionStatus.cs`, `Web/radio.js` | 5 | MeTube copy; ES5 IIFE with `var VERSION` checked by the release gate |
| `Playlists/FavoritesPlaylistSync.cs` | 6 | Per-user private "Mijn zenders" playlist, driven by IUserDataManager.UserDataSaved for radio items (5 s debounce) |
| `Relay/*`, `PlayTo/*` | 7 | Contingency only (see Phase 7) |
| `.github/workflows/build.yml`, `release.yml`, `CHANGELOG.md`, `README.md`, `SETUP.md`, `logo.svg/png` | 0-1 | House CI. Version gate (csproj x3, meta.json, radio.js VERSION once the file exists, GUIDs, tag is an ancestor of main). Zip allowlist exactly `Jellyfin.Plugin.Radio.dll logo.png meta.json` (SubtitleCat's expected-vs-actual print). SubtitleCat's awk for notes. Manifest sync as SurfHost `[skip ci]`. Logo in house style with tagline "Nederlandse radio / in elke Jellyfin-app" |

---

### 3. Client matrix (official apps only)

| Client | Route | Confidence | Limits and workaround |
|---|---|---|---|
| Jellyfin Web | Channel "Radio" (My Media), or the "Radio" playlist. universal, then the server's static-remote proxy | **verified** (playback); now-playing overlay unverified (S10) | Casting only from Chrome, Edge or Opera on **https://jellyfin.surfhost.eu** |
| Jellyfin Desktop / JMP | Same UI; mpv plays Path directly | likely | OS media controls show the station name only. **No Google Cast** (#490); use DLNA Play To or another sender |
| Jellyfin Android (phone/tablet) | WebView HTML audio via universal; RemotePlayerService keeps it in the background | likely (playback path verified in code; background and lock screen are spike S2) | Lock screen shows station + Group; song titles only with the opt-in hack (S10). The F-Droid build cannot cast; use the Play Store build |
| Jellyfin for iOS | WKWebView, same as Web | likely; background playback unverified (S2) | **No Google Cast** from iOS; use DLNA Play To or AirPlay via Control Center |
| Swiftfin | **Not supported** | verified (no audio player, #1686/#2137) | Use the official "Jellyfin" iOS app for radio |
| Jellyfin Android TV | **Channel items cannot play**: `JellyfinMediaStreamResolver` keeps only `FILE && !isRemote`. Route: the opt-in **TV-typed Live TV copy** (channels 901+), which plays in the legacy video player with a **black screen** | channel route verified impossible; Live TV copy likely (S7) | Hide the channel folder from the living-room user (ExcludedUserIds). The Live TV copy can only be limited per user (Live TV permission). Song changes only as an opt-in toast. Real fix is an upstream PR to the resolver |
| Android Auto (official Android app 2.7.x) | Root > Playlists > "Radio" (and later "Mijn zenders"), a grid sorted by SortName (ForcedSortName order). universal with enableRemoteMedia, then a 302 to the https station | **unverified** (S3/S4) | App must use the **https** server URL (cross-protocol redirect fails), or switch on RelayThroughServer. Station name and logo only (Media3 MediaItem title wins over ICY). No voice search for Audio items. Plays are not reported. Channels and Live TV are never browsable; an upstream PR adding a Channels route is the real fix |
| Google Cast (Chromecast / Google TV) | Receiver v1.3.1 direct-plays Path as audio/mp3 | likely (S5) | The Chromecast must resolve jellyfin.surfhost.eu on the LAN (hairpin NAT or split DNS). MP3 only. Static title and logo |
| Google Cast, audio-only Nest/Google Home and speaker groups | Same audio branch; their profile has no video, so only Audio items can work (never the Live TV copy) | **unverified** (S5) | If they don't appear or don't play: contingency plugin-owned Cast session controller (Default Media Receiver, LIVE) |
| Sonos via DLNA plugin v14 "Play To" | Static sources, then `/dlna/audio/{id}/stream.mp3`, musicTrack, audio/mpeg, proxied | **unverified** (S6) | Requires host networking on TrueNAS and EnableSharedDeviceControl. If Sonos rejects an endless http track: contingency x-rincon-mp3radio session controller |
| AirPlay from the iOS app | Control Center output picker; the phone stays the source | unverified (S8) | The in-app AirPlay button is probably inert (allowsAirPlayForMediaPlayback is false) |

---

### 4. Spikes (Phase 0, run first, go/no-go)
Spike build v0.0.x is installed by hand and never published in the manifest. It has three hard-coded stations: NPO Radio 2 (icecast direct), Radio 538 (STW 302) and Qmusic (302).

- **S1 Server contract (curl, as a non-admin user).**
  - `PlaybackInfo` must return exactly 1 source: Id `radio-*`, DirectPlay, mp3, an Audio stream with sample rate, channels and bitrate.
  - `/Audio/{id}/universal` with browser parameters must give 200 audio/mpeg.
  - With the exact Android Auto parameters (`container=opus,mp3|mp3,aac,m4a,m4b|aac,flac,webma,webm,wav,ogg&transcodingProtocol=hls&transcodingContainer=ts&audioCodec=aac&enableRemoteMedia=true&maxStreamingBitrate=140000000&ApiKey=...`) it must give a 302 to the station URL.
  - The server log must show no ffprobe and no master.m3u8.
  - No-go means fixing the factory; the channel approach itself stays.
- **S2 Official apps, 60 min each:** Web (Chrome and Firefox), Desktop 2.x (the server log should show no /Audio request), Android 2.7.3 with the screen off (notification, lock screen, Bluetooth AVRCP), and the iOS app locked for 30 min.
- **S3 Android Auto playlist visibility.** Use a playlist created by hand (public, admin-owned) and a second non-admin user:
  - /UserViews must include playlists, and `/Items?parentId=<playlist>&SortBy=SortName` must return the stations.
  - Both must hold after a restart and after the 3 h cache expiry.
  - No-go: Android Auto is unsupported, and an upstream PR is the path.
- **S4 Android Auto device.** Desktop Head Unit, then the car, app on the https URL:
  - grid with logos; tap to audio within 5 s; next/previous switches station;
  - 10 min pause and resume; 60 s tunnel; 1-2 h wireless session;
  - negative test with the LAN http URL, then the RelayThroughServer fix.
- **S5 Cast.**
  - Senders: Chrome over https, and the Android Play Store build.
  - Targets: a Chromecast with a screen, Nest Mini/Audio, a speaker group.
  - Stations: the STW and Qmusic 302s. Also an AAC station, expected to fail.
  - Checks: 60 min with no false MEDIA_FINISHED; the server log shows the device fetching directly.
- **S6 DLNA v14 to Sonos (host networking).** Capture SetAVTransportURI and the DIDL. There must be no FLAC transcode and at least 60 min of playback on S2 (and S1 if available).
- **S7 Android TV Live TV.**
  - By hand: a dashboard M3U tuner with a **file path** and TunerCount 0. The guide shows the channels, they play in the video player, zapping works.
  - Then the same programmatically: SaveTunerHost with the stored Id, a restart with no duplicate tuner, and DeleteTunerHost.
- **S8 AirPlay.** iOS Control Center to a HomePod or AirPlay-2 Sonos for 30 min; record what the in-app button does.
- **S9 Metadata propagation.**
  - The RadioSync patch (Name, Primary image, ForcedSortName) must survive the listing refresh, the following QueueRefresh, a restart and the 3 h cache expiry, with the item id unchanged and favourites kept.
  - Check whether Artists=[Group] creates global MusicArtist entries (/Artists with no parentId). If so, switch to an AlbumArtists- or Album-based second line.
- **S10 Injection.**
  - A stub script logs ApiClient, NativeShell and navigator.mediaSession in Web, Desktop 2.x, Android 2.7.3 and iOS 1.8 (ServedCount > 0).
  - On Android, a synthetic itemId must update the Android 13+ lock screen and car AVRCP without breaking the transport controls.

`docs/SPIKES.md` records pass/fail with date and app versions. Hans decides which of Phases 2-7 get built.

---

### 5. Phases (each tagged release is shippable on its own)
- **Phase 0, spike build (not released).** Skeleton, three-station channel, S1-S10. One session.
- **Phase 1, v0.1.0 "Radio channel" (shippable on its own).** Covers Web, Desktop, Android phone and the iOS app. Cast and DLNA work as far as S5/S6 passed, with the README matrix stating the results.
  - Deliverables: configuration and seed (unconfirmed codecs disabled); validator and normaliser; state store; source and item factories; RadioChannel (IHasCacheKey, frozen name, ExcludedUserIds); RadioSync (materialise, patch Name/Overview/image); Admin Test, Probe, Defaults and Resync; config page; tests; build.yml and release.yml; README; SETUP.md (https URL, channel permission per user, Play Store build for Cast, DLNA v14 plus host networking with a port and proxy re-check, hairpin DNS for Chromecasts); logo.
  - Acceptance: V1-V5 and V10-V11.
- **Phase 2, v0.2.0 Android Auto (only if S3/S4 = go).**
  - Deliverables: PlaylistSync for the public "Radio" playlist (stored id, pure diff, removes dangling entries); ForcedSortName ordering `<group><order> <name>` in RadioSync; RadioSyncTask every 6 h plus the startup pass; RelayThroughServer toggle; playlist status in Admin/Test; SETUP.md Android Auto section (https, 4-tab root, no voice search).
  - Acceptance: V6, V10.
- **Phase 3, v0.3.0 speaker hardening (only if S5 and/or S6 = go).**
  - Deliverables: per-station "speaker readiness" in Admin/Test (mp3, no headers, SampleRate, Channels and BitRate present, non-GUID id); weekly CheckStationsTask that writes back measured fields and bumps modifiedUtc; documentation.
  - Acceptance: V7, V8.
- **Phase 4, v0.4.0 Android TV Live TV copy (opt-in, only if S7 = go).**
  - Deliverables: M3uWriter; LiveTvCopyManager (SaveTunerHost with the stored Id, DeleteTunerHost); optional XMLTV with SaveListingProvider and DeleteListingsProvider; SETUP.md "woonkamer" user (Live TV access, channel excluded); warning that a URL change creates a new Live TV channel.
  - Acceptance: V9.
- **Phase 5, v0.5.0 now-playing (only if S10 = go, per client).**
  - Deliverables: NowPlayingService and the five providers; ListenerTracker; `Radio/ClientScript`, `Radio/ClientConfig`, `Radio/NowPlaying/{itemId}`; injection filter with Insert(0) and the kill switch; radio.js; Android hack off by default; opt-in SongToastNotifier.
  - Acceptance: V12.
- **Phase 6, v0.6.0 UX extras (optional, ux-first).**
  - Per-user "Mijn zenders" favourites playlist for Android Auto.
  - Optional Container folders "Regionale zenders" / "Meer zenders".
  - radio-browser.info import (MP3 only, normalised, probed on import).
  - Logo upload store.
  - Not adopted: one-tap play from the injected home row through `POST /Sessions/{own}/Playing`. It needs its own spike first; otherwise the tile links to the details page.
- **Phase 7, contingency (only with Hans's explicit go per failed spike).**
  - (a) S6 failed: a Sonos `ISessionController` (LogSessionActivity plus AddController, the DLNA-plugin pattern) sending `x-rincon-mp3radio://` with radio DIDL.
  - (b) S5 failed for Nest speakers or groups, or Cast is needed from iOS or Desktop: a Cast session controller (Default Media Receiver, streamType LIVE). Needs a CASTV2 library and licence review.
  - (c) A wanted station is AAC- or HLS-only: an anonymous MP3 relay `/Radio/Relay/{slug}.mp3?sig=HMAC(secret, slug|revision)`. The signature is **non-expiring**, because static sources store Path; it is revoked by rotating the secret. The relay serves catalog stations only, ffmpeg via `IMediaEncoder.EncoderPath`, one upstream per station with fan-out, 200 audio/mpeg, no Content-Length, Accept-Ranges none, no icy-metaint.
  - (d) Upstream PRs: a jellyfin-androidtv resolver that accepts remote/live sources, and a jellyfin-android Android Auto Channels route plus ICY title for live items.

---

### 6. Tests (xunit, MeTube layout; Jellyfin.Controller without ExcludeAssets)
- **PluginPlumbing:** GUID the same in Plugin, meta.json and manifest.json; version the same in meta.json, csproj x3 and radio.js; net10.0; targetAbi starts with 12.; configPage rules; radio.js is ES5; script path has no `/native/`.
- **PluginVersion theory:** 0.1.0.0 gives 0.1.0; 1.0.0.0 gives 1.0.0; 0.1.0.5 is unchanged.
- **Registration:** exactly one IChannel (RadioChannel, singleton); every registration is a singleton; HttpClient names are prefixed. Phase 5: InjectionOrderTests (first IStartupFilter; composes with a MeTube-style patcher and a GuestPass-0.2.x-style short-circuit injector; /web/main.js untouched; idempotent).
- **MediaSourceFactory:**
  - every field in section 1.1;
  - `Guid.TryParse(Id)` is false;
  - exactly one Audio stream with non-null SampleRate, Channels and BitRate;
  - IsRemote follows RelayThroughServer;
  - no RequiredHttpHeaders;
  - Path equals the canonical URL.
- **ChannelItemFactory:** Media/Audio/**Song** (assert not Podcast), IsLiveStream, Id equals the slug, DateModified comes from the state, one source.
- **RadioChannel:**
  - Name == "Radio", with a pinned test explaining the id hash;
  - computed id equals `GetNewItemId(slug+"Radio"+"16", typeof(Audio))`;
  - cache key and DataVersion change with the catalog hash;
  - ExcludedUserIds handled by IsEnabledFor;
  - every station listed exactly once.
- **Validator / normaliser / DefaultStations:** schemes; playlist extensions; STW node rewrite; unique, immutable slugs; mp3 only; 37 seed entries; no Soul & Jazz; no node hosts.
- **StationStateStore:** first sight; unchanged hash means no bump; changed URL bumps; `.corrupt` fallback; no leftover .tmp.
- **StreamProbe / Mp3FrameHeader:** synthetic frames (MPEG-1 L3 at 192k/48k, 128k/44.1k), ID3 skip, false sync; 302 chain; text/html flagged; never sends Icy-MetaData.
- **PlaylistDiff:** minimal adds and removes; idempotent; dangling ids removed.
- **M3uWriter / XmlTvWriter:** exact EXTINF lines, numbers from 901, escaping, URLs unchanged; XMLTV ids match tvg-id; UTC conversion.
- **NowPlaying fixtures** (IPs scrubbed, listed in Fixtures/README.md): Triton CDATA trim and a KINK jingle with no title; NPO local times; Qmusic staleness; KINK JSON; ICY at metaint 1024/16000/64000 with L=0 blocks, UTF-8 then Windows-1252, TitleFirst vs ArtistFirst, marker blacklist, NPO programme-format titles treated as a show; poll interval clamped to 15-60 s.
- **RelaySigner (P7):** tamper detection, constant-time compare, unknown slug gives 404, secret rotation.
- **Headless Chrome page tests** (`Tests/Web/run_page_test.py`): config page against a mocked API (defaults, add, edit, test, save, diagnostics); radio.js against a mocked now-playing bar (patch, re-apply after mutation, ignores non-radio items).
- **CI:** dotnet format verify; vulnerable-package grep; zip allowlist.

---

### 7. Live verification steps
- **V1:** S1 repeated on the release build for all enabled stations, through Admin/Test with allowMediaProbe=false.
- **V2:** Web, Chrome and Firefox, LAN http and public https: three stations, 60 min each, pause and resume.
- **V3:** Desktop 2.x: mpv fetches the station directly; record whether DLNA targets are listed.
- **V4:** Android 2.7.3 (Play Store build): 60 min with the screen off; notification, lock screen and car AVRCP lines.
- **V5:** iOS app: 30 min locked; Control Center AirPlay; in-app button result.
- **V6:** Android Auto, Desktop Head Unit and car (Phase 2): playlist grid in curated order, zapping, tunnel, LAN-http negative test.
- **V7:** Cast to a Chromecast, Nest and a speaker group (60 min, direct fetch).
- **V8:** Sonos Play To (no FLAC, 60 min, what the Sonos app displays).
- **V9:** Android TV living-room user: guide 901+ with logos, playback in the video player, channel folder hidden, no duplicate tuner after a restart, removed when disabled.
- **V10:** stability: restart plus more than 3 h. Item ids, playlist entries and favourites unchanged. A URL edit reaches PlaybackInfo after one sync. A rename and a logo change appear after RadioSync.
- **V11:** permissions: a new non-admin user is flagged; works after channel access is enabled; ForceRemoteSourceTranscoding is flagged.
- **V12 (P5):** song titles follow within one poll on Web, Desktop, Android and iOS for a Triton, an NPO and a Qmusic station; news and ads show the station or show; the kill switch removes the script.
- **Release:** version gate; zip contents; GitHub Release; manifest sync; install from the repository URL; the logo survives a restart; injection coexists with MeTube and GuestPass.

---

### 8. Out of scope and honest limits
- **Out of scope:**
  - `ChannelType.Radio` tuner, custom ITunerHost/ILiveStream (TS MIME hardcoded, ffprobe), .strm in music libraries;
  - a custom Cast receiver fork for live titles;
  - Android Automotive OS (built into the car);
  - per-device visibility (Jellyfin only offers per-user);
  - song titles on Cast, DLNA, Android Auto and Android TV (static station metadata only);
  - voice search through the AudioBook/Podcast hack (it changes ids; experimental at most).
- **Unsupported official clients and workarounds:**
  - Swiftfin: use the Jellyfin iOS app.
  - Channel radio on Android TV: Live TV copy with a black screen, or an upstream PR.
  - Google Cast from iOS, Desktop, F-Droid Android, Firefox and Safari: use DLNA Play To, a Chrome or Play Store sender, or the Phase 7b controller.
  - In-app AirPlay button: use Control Center.
- **One-line fallback outside the rule:** Symfonium (Live TV radio import, ICY), Home Assistant play_media, or the speaker's own radio app.
## Implementation notes for 0.0.1 (where the code differs from the text above)

- **The test build already contains the Phase 1 code.** Phase 1 uses only code paths verified in the 12.2 source, so the test build is the
  full channel (configuration, built-in list, validator, normalizer, state store, RadioSync, admin page) instead of three hard-coded stations.
- **Built-in list: 44 stations, 33 enabled.** On 2026-10-13 every stream was fetched and its MP3 frame headers read, including Arrow and the
  12 regional broadcasters (all MP3), so those ship enabled. The FunX city/theme variants and NPO 3FM Alternative ship disabled. Omroep
  Flevoland offers only AAC and is left out. Stations with only a `.ico` favicon have no logo.
- **No Artists line yet.** `ChannelItemInfo.Artists = [Group]` is left out until spike S9 shows whether it adds entries to the global artist
  list. The group is shown in the overview instead ("NPO · Live · MP3 192 kbps").
- **RadioSync locks Name and Overview** (`LockedFields`) on every station item, so a later metadata refresh cannot undo a rename.
- **Logo changes are tracked in state.json** (`AppliedLogoUrl`): Jellyfin replaces a remote image path with a local copy the first time the
  image is shown, so the item's image path cannot tell which logo it holds.
- **Channel order:** RadioSync already sets ForcedSortName (`001 NPO Radio 1`, ...) from the page's order. jellyfin-web lists channel
  items by `IsFolder,SortName` (legacy controllers/list.js, release-12.z), so the page order shows in the web app too.
- **Listing cache:** RadioSync deletes `<cache>/channels/<channel id>` before each pass and lists the channel with `CancellationToken.None`,
  so a pass is never stopped half-way behind a fresh cache file.
- **Channel folder guard:** the channel is flat, so `ChannelFolderGuard` (a global MVC action filter) answers 400 when the Radio channel
  is asked for any folder but itself. Jellyfin 12.2 does not check that a requested folder belongs to the channel; the details were
  reported to Jellyfin privately.
- **Relay option** only sets `IsRemote = false`, which stops the `/Audio/{id}/universal` redirect (Android Auto). Desktop (mpv) and the Cast
  receiver use `Path` directly either way; the web, Android and iOS apps never ask for the redirect (no RemoteAudio feature). The label says so.
- **Disabling = removing:** a disabled station is no longer listed, so Jellyfin deletes its item (playlist entries lost; user data parked
  for 90 days). The page warns on both actions.
- **Validator** also refuses control characters: Jellyfin resets a plugin's whole configuration, silently, when its XML cannot be read back.
- **Logo changes** set the image's DateModified, so the image tag changes and apps that cached the old logo for a year fetch the new one.
