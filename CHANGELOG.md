# Changelog

All notable changes to this plugin are listed here. Versions follow MAJOR.MINOR.PATCH;
the GitHub release for a version carries the section below as its notes.

## [0.0.2] - 2026-10-13

- 10 non-stop channels added to the built-in list, each right after its main station: 538 Non-Stop, Qmusic Non-Stop, Sky Radio Hits, Sky Radio Non-Stop@Work, Radio 10 Non-Stop, 100% NL Non-Stop, SLAM! Non-Stop and JOE Non-Stop are switched on; Radio 10 Soul & Jazz and BNR Business Beats are in the list but switched off. Every stream was checked and its format measured on 2026-10-13 (all MP3, 128 kbps).
- Already installed? Open the plugin page, press "Add missing Dutch stations" and then Save. The new channels are placed right after their main station.
- Radio Veronica and Sublime have no non-stop channel, so none was added for them.

## [0.0.1] - 2026-10-13

Test build, published as a GitHub pre-release and installable from this repository's manifest, to run the checks in
docs/SPIKES.md before v0.1.0.

- A **Radio** channel (Home, My Media) in which every station is a live audio item. It plays in Jellyfin's music player on the
  web; Jellyfin Desktop and the Android and iOS apps are being verified.
- 44 Dutch stations built in, 33 of them switched on:
  - NPO, the national commercial stations and 12 regional public broadcasters.
  - Every stream was checked on 2026-10-13 and its format read from the stream itself.
  - Only MP3 streams: Jellyfin cannot convert live AAC.
- A plugin page to manage the stations:
  - Switch stations on and off, rename them, change logos and add your own.
  - Reorder them; the apps follow the page's order.
  - **Test** checks a stream URL and measures its format.
  - A StreamTheWorld node URL is replaced by the stable redirect URL.
  - Playlists (.m3u, .pls) and AAC streams are refused with an explanation.
  - A changed stream URL must be tested again before it can be saved.
- Renamed stations and new logos reach the existing items, so favourites keep working.
- Diagnostics on the plugin page:
  - Which stations Jellyfin would serve, and as what.
  - Users who cannot see the channel: no channel access, forced transcoding of remote sources, or unrated channel content blocked.
- Options:
  - Don't redirect apps (Android Auto) to the station; the server streams it instead.
  - Hide the channel from chosen users, such as a living-room Android TV account.
- The Radio channel refuses requests for folders inside it (it has none).
