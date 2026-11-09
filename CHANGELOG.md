# Changelog

A release's notes come from its section here (the heading must match the tag), and the app shows them when it offers the update.

## v1.5.1
- **VLC fallback black screen fixed:** the VideoView swapchain options could arrive after LibVLC had already initialized, so LibVLC created its own D3D11 device, which failed on Xbox (DXGI_ERROR_DEVICE_REMOVED, hr=0x887A0005) and left a black screen. The swapchain options are now applied to LibVLC when they arrive.
- **Better diagnostics:** the real FFmpeg failure reason is now carried through to the fallback log line instead of showing "unknown", open exceptions log their type and HResult with the playback position, and both engines log the actual decoder mode (D3D11 hardware or software) on every open.
- **Lighter FFmpeg buffering:** the read-ahead buffer is reduced from 30 seconds to 10 seconds to lighten memory and CPU load.
- **Hardware decoding follows the in-app setting:** if hardware decoding is off in Settings, both engines use software decoding — expect slow frames on Xbox.

## v1.5.0
- **FFmpegInteropX is now the primary player:** it decodes MKV/HEVC/AV1/DTS and other formats Windows Media Foundation rejects, with D3D11 hardware decoding, straight into the same MediaPlayer surface — so sources that used to fail first on WMF and then switch to VLC now play on the first try. Audio and subtitle tracks from the file are now selectable, and stalled streams use FFmpeg's reconnect and read-ahead buffer.
- **LibVLC stays as the fallback:** if FFmpeg cannot play a source, playback still switches automatically to VLC for the same source from the same position.
- **VLC slow frames:** hardware decoding is now forced to D3D11 (`:avcodec-hw=d3d11va`) instead of leaving it to VLC's default, which left high-bitrate HEVC on slow software decoding on Xbox. The `vlc error [mem] Invalid get/release function pointers` line in the log is benign VLC 3.0.16 noise (imem probing, removed upstream in 2021), not a broken input — the Windows HTTP input that feeds VLC is unchanged, because VLC's own HTTPS cannot open links on Xbox.

## v1.4.0
- **LibVLC fallback player:** Windows Media Foundation stays the primary player, but when it cannot play a source (SourceNotSupported, DecodingError, or an adaptive manifest it cannot open), playback now switches automatically to LibVLC for the same source from the same position, and tells you it switched. Sources WMF already rejected start with VLC next time, and sources both players rejected are not retried in the same session.
- **Tougher HTTP streams:** the range reader no longer fails when a server rejects HEAD or a range probe (including 403), streams with an unknown length now play instead of erroring, and a default User-Agent is sent. Extension-less links are now checked by Content-Type, so HLS/DASH links without an extension are detected, and a failed adaptive open falls back to the platform resolver.
- **Smarter source order:** mp4/H.264 and adaptive streams are tried before mkv/HEVC/DTS/REMUX ones (nothing is removed, only tried later), automatic fallback now waits briefly between sources and stops after 3 consecutive failures instead of walking the whole list, and a stalled stream is reopened once from the same position before moving on.
- **Xbox guide:** the app no longer shows up as a music player in the Xbox guide (system media controls are off for both players). Pause/play from the guide stops working; use the controls inside the app.

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
