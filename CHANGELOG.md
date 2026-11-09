# Changelog

A release's notes come from its section here (the heading must match the tag), and the app shows them when it offers the update.

## v1.3.3
- **Native playback:** replaced LibVLC with native Windows Media Foundation playback for direct HTTP(S) media and adaptive HLS/DASH streams. Header-authenticated streams play through a seekable HTTP range reader, adaptive streams fall back when manifest parsing fails, and stalled opens time out after 30 seconds instead of hanging.
- **Player fixes:** fixed the buffering spinner staying on screen while a stream was playing, try the next source automatically after a playback failure, reduced stalls in header-based video streams, and added diagnostics to the log for adaptive open failures (including ManifestDownloadFailure).
- **RTL and TV layout:** fixed hero, sidebar, search and library layouts for right-to-left content, kept poster rows and focus scrolling inside the sidebar-safe area, and smoothed repeated focus scrolling.
- **Profiles:** added a profile avatar switcher in the sidebar, and the sidebar now refreshes after profile sync.
- **Xbox polish:** reduced player rendering work, updated the native video surface on the UI thread, and paused the native bridge while WebView2 recovers.

## v1.3.0
- **Jellyfin and Emby:** add your server in Settings → Integrations (Quick Connect or username and password). Its libraries become rows on Home and are searchable, and its files play here directly in their original quality, also listed among your other streams for the same title. Resume points and watched marks come in from the server, and what you watch here goes back to it.
- **Trakt:** connect with a code in Settings → Integrations. What you play is scrobbled, your Trakt history and paused progress come into watched marks and Continue Watching, your watchlist and recommendations are rows on Home, and watched marks you set here go to Trakt.

## v1.2.0
- **Who's watching?** When your Nuvio account has several profiles, the app asks who's watching when it starts, with the PIN for locked profiles. You can turn this off in Settings → Account.
- **Update notices:** the app tells you when a newer release is out, with a QR code to the download. Settings → About → Check for updates checks right away.
- **View log** in Settings → About shows the app's log on the TV, for this session and the last one.
- Sync only fetches what changed since the last sync, like Nuvio's own apps.

## v1.1.0
- Sign in to Nuvio with a code (scan the QR code with your phone) or with email.
- Import addons, library and Continue Watching from a Stremio account.
- Built-in debrid: play torrent results through TorBox, Real-Debrid or Premiumize.
- Cast photos and "More like this" with a TMDB key.
- Gentler sync: progress is sent at most once a minute, and the app backs off when the server is busy.
- The controller pointer is off on Xbox; the D-pad moves the highlight.

## v1.0.0
- First release: Nuvio's layout for the TV, Stremio addons, and VLC playback for MKV, HEVC, Dolby Vision/HDR and lossless audio.
