# Spikes (checks before 0.1.0)

Each check decides whether a later phase is built. Record the result, date and app versions in the table at the end.
Install the 0.0.1 test build first (see [SETUP.md](../SETUP.md)), then press **Test** on the plugin page: every published station must
be "ready to play".

## S1 Server contract (no device needed)

As a non-admin user with channel access:

1. `GET /Items/<station item id>/PlaybackInfo?UserId=<id>` returns exactly one media source: id `radio-<slug>`, `SupportsDirectPlay: true`,
   container `mp3`, one audio stream with sample rate, channels and bitrate.
2. `GET /Audio/<id>/universal?UserId=<id>&container=mp3&api_key=<token>` (what the web app does) returns `200 audio/mpeg` and keeps streaming.
3. The Android Auto request
   `/Audio/<id>/universal?container=opus,mp3|mp3,aac,m4a,m4b|aac,flac,webma,webm,wav,ogg&transcodingProtocol=hls&transcodingContainer=ts&audioCodec=aac&enableRemoteMedia=true&maxStreamingBitrate=140000000&UserId=<id>&api_key=<token>`
   returns `302` to the station URL.
4. The server log shows no ffprobe of the stream and no `master.m3u8`.

No-go: fix the media source factory; the channel approach stays.

## S2 Official apps, 60 minutes each

- Jellyfin Web in Chrome and Firefox: play, pause, resume, switch station.
- Jellyfin Desktop 2.x: the server log should show no `/Audio` request (mpv fetches the station itself).
- Jellyfin Android 2.7.3 with the screen off: notification, lock screen, Bluetooth controls (car).
- Jellyfin for iOS, locked for 30 minutes.

## S3 Android Auto: playlist visibility (no car needed)

1. Create a public playlist "Radio" by hand (as admin) and add three stations to it.
2. As a second, non-admin user: `GET /UserViews` must include playlists, and `GET /Items?ParentId=<playlist>&SortBy=SortName` must
   return the stations.
3. Still true after a server restart and after 3 hours.

No-go: Android Auto is not supported; an upstream jellyfin-android change is the path.

## S4 Android Auto: device

Desktop Head Unit first, then the car, with the Jellyfin Android app on the **https** server address:

- Playlists → Radio shows the stations with logos; a tap gives audio within 5 seconds; next/previous switches station.
- Pause 10 minutes and resume; drive through a tunnel (60 s without signal); a 1–2 hour session.
- Negative test: the app on the LAN **http** address (a https→http redirect may fail), then the same with **Relay every stream** on.

## S5 Google Cast

Senders: Chrome on `https://<jellyfin>`, the Jellyfin Android app (Play Store build). Targets: a Chromecast with a screen, a Nest
Mini/Audio, a speaker group. Stations: NPO Radio 2 (direct), Radio 538 (StreamTheWorld redirect), Qmusic (DPG redirect).
Pass: 60 minutes without the cast session ending; the server log shows the device fetching the station directly.

## S6 Sonos via the DLNA plugin

DLNA plugin installed, Jellyfin with host networking. Play To → Sonos. Capture what Sonos receives (SetAVTransportURI): MP3, not a
FLAC transcode. Pass: 60 minutes of playback.

## S7 Android TV via Live TV (no plugin code)

By hand: Dashboard → Live TV → add an M3U tuner with a local file (path inside the container) holding three stations, tuner count 0.
Pass: the guide lists them, they play in the video player, zapping works. Decides Phase 4 (the plugin keeps such a file up to date).

## S8 AirPlay from the iOS app

Control Center → AirPlay to a HomePod or an AirPlay-2 Sonos, 30 minutes. Note what the in-app AirPlay button does.

## S9 Metadata

Rename a station and change its logo on the plugin page, Save. Pass: within a minute the item shows the new name and logo, the item id
is unchanged and a favourite is kept; still true after a restart and after 3 hours. Then check (Phase 2 decision) whether adding
`Artists = [group]` creates entries under Music → Artists.

## S10 Script injection (decides Phase 5, now playing)

A stub script logs `ApiClient`, `NativeShell` and `navigator.mediaSession` in Web, Desktop 2.x, Android 2.7.3 and iOS.

## Results

| Check | Date | Versions | Result | Notes |
|---|---|---|---|---|
| S1 | | | | |
| S2 | | | | |
| S3 | | | | |
| S4 | | | | |
| S5 | | | | |
| S6 | | | | |
| S7 | | | | |
| S8 | | | | |
| S9 | | | | |
| S10 | | | | |
