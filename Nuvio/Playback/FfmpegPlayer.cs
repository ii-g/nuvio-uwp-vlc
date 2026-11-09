using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using FFmpegInteropX;
using Nuvio.Diagnostics;
using Windows.Data.Json;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Controls;

namespace Nuvio.Playback
{
    /// <summary>
    /// Primary playback engine: FFmpegInteropX decodes the stream (including the
    /// containers and codecs Windows Media Foundation rejects, such as MKV/HEVC/
    /// AV1/DTS) and hands the result to a plain UWP MediaPlayer, so it plays on
    /// the same MediaPlayerElement surface WMF used — no second video view.
    /// Hardware decoding is D3D11 through VideoDecoderMode.Automatic.
    ///
    /// Sources that need request headers (Origin/User-Agent/Referer) are fed
    /// through the seekable HttpRangeStream, which sends the headers itself;
    /// everything else is opened by FFmpeg from the URI, with reconnect options
    /// so dropped connections are picked back up. HLS/DASH playlists are also
    /// opened from the URI, because FFmpeg has to fetch the segments itself.
    /// </summary>
    internal sealed class FfmpegPlayer : IDisposable
    {
        private const string DefaultUserAgent = "NuvioUWP";
        private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);

        private MediaPlayerElement _view;
        private MediaPlayer _mediaPlayer;
        private FFmpegMediaSource _ffmpegSource;
        private MediaPlaybackItem _playbackItem;
        private HttpRangeStream _rangeStream;
        private IRandomAccessStream _randomAccessStream;
        private string _pageId;
        private bool _readySent;
        private bool _openFailed;
        private string _lastErrorKind; // e.g. DecodingError, SourceNotSupported, NetworkError
        private bool _disposed;
        private long _openGeneration;
        private long _lastPositionEventTicks;
        private double _pendingStartSeconds;
        private bool _playWhenOpened;
        private CancellationTokenSource _openCancellation;

        public event Action<string, JsonObject> EventRaised;
        public string Version { get; private set; } = "FFmpegInteropX";

        public void Initialize(MediaPlayerElement view)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FfmpegPlayer));
            if (_mediaPlayer != null) return;
            if (view == null) throw new ArgumentNullException(nameof(view));
            _view = view;
            CreateMediaPlayer();
        }

        private void CreateMediaPlayer()
        {
            if (_view == null) throw new InvalidOperationException("The media surface is not available.");
            if (_mediaPlayer != null) return;

            var player = new MediaPlayer { AutoPlay = false, IsMuted = false, Volume = 1.0 };
            // The app is a video player, not a music player: keep Xbox's guide
            // from showing it as a background-audio app. Pausing/playing from
            // the guide stops working after this; control stays in the app.
            try { player.SystemMediaTransportControls.IsEnabled = false; }
            catch (Exception ex) { Log.Write("Disabling system media transport controls failed: " + ex.Message); }
            try { player.CommandManager.IsEnabled = false; }
            catch (Exception ex) { Log.Write("Disabling the media command manager failed: " + ex.Message); }
            player.MediaFailed += OnMediaFailed;
            player.MediaEnded += OnMediaEnded;
            player.MediaOpened += OnMediaOpened;
            player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
            player.PlaybackSession.PositionChanged += OnPositionChanged;
            player.PlaybackSession.NaturalDurationChanged += OnNaturalDurationChanged;
            player.PlaybackSession.NaturalVideoSizeChanged += OnNaturalVideoSizeChanged;
            player.PlaybackSession.BufferingProgressChanged += OnBufferingProgressChanged;
            try
            {
                _view.SetMediaPlayer(player);
                _mediaPlayer = player;
                Log.Write("FFmpegInteropX player ready");
            }
            catch
            {
                player.MediaFailed -= OnMediaFailed;
                player.MediaEnded -= OnMediaEnded;
                player.MediaOpened -= OnMediaOpened;
                player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
                player.PlaybackSession.PositionChanged -= OnPositionChanged;
                player.PlaybackSession.NaturalDurationChanged -= OnNaturalDurationChanged;
                player.PlaybackSession.NaturalVideoSizeChanged -= OnNaturalVideoSizeChanged;
                player.PlaybackSession.BufferingProgressChanged -= OnBufferingProgressChanged;
                player.Dispose();
                throw;
            }
        }

        private void SetViewOpacity(double opacity)
        {
            var view = _view;
            if (view == null) return;
            try
            {
                if (view.Dispatcher.HasThreadAccess)
                {
                    view.Opacity = opacity;
                    return;
                }

                var ignored = view.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    if (_disposed || !ReferenceEquals(_view, view)) return;
                    view.Opacity = opacity;
                });
            }
            catch (Exception ex)
            {
                Log.Error("Updating the media surface failed", ex);
            }
        }

        public void SetUnavailable(string reason) { RaiseState("error", reason); RaiseEnded("error", reason); }

        public void Open(OpenRequest request)
        {
            CancelPendingOpen();
            var generation = Interlocked.Increment(ref _openGeneration);
            _pageId = request == null ? null : request.Id;
            _readySent = false;
            _openFailed = false;
            _lastErrorKind = null;
            _playWhenOpened = false;
            _pendingStartSeconds = 0;
            if (_view == null)
            {
                FailOpen(generation, "The media player is not ready.");
                return;
            }

            Uri uri;
            if (request == null || !Uri.TryCreate(request.Url, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                FailOpen(generation, "This player accepts direct HTTP or HTTPS media links.");
                return;
            }
            try
            {
                CreateMediaPlayer();
                _mediaPlayer.Pause();
                ClearCurrentSource();
                if (_view != null) SetViewOpacity(0);
                _openCancellation = new CancellationTokenSource();
                var token = _openCancellation.Token;
                RaiseState("opening", null);

                // These tasks never block the WebView message/UI thread. The source resolver catches
                // every failure itself, while a separate watchdog covers MediaPlayer.Open/Play stalls.
                _ = OpenSourceAsync(request, uri, generation, token);
                _ = WatchOpenTimeoutAsync(generation);
            }
            catch (Exception ex)
            {
                Log.Error("FFmpeg media source could not be opened", ex);
                FailOpen(generation, ex.Message);
            }
        }

        private async Task OpenSourceAsync(OpenRequest request, Uri uri, long generation, CancellationToken token)
        {
            FFmpegMediaSource ffmpegSource = null;
            HttpRangeStream rangeStream = null;
            IRandomAccessStream randomAccessStream = null;
            try
            {
                var hasHeaders = request.Headers != null && request.Headers.Count > 0;
                var path = uri.AbsolutePath.ToLowerInvariant();
                var adaptive = path.EndsWith(".m3u8") || path.EndsWith(".mpd") || path.EndsWith(".m3u");
                // Diagnostic only: header names, host and path — never header
                // values or the query string, which may carry tokens.
                Log.Write("Opening FFmpeg media: adaptive=" + adaptive +
                    " headers=" + (hasHeaders ? string.Join(",", request.Headers.Keys) : "none") +
                    " host=" + uri.Host + " path=" + uri.AbsolutePath);

                var config = CreateConfig(request);
                if (hasHeaders && !adaptive)
                {
                    // The headers ride on our own HTTP stack (HttpRangeStream),
                    // which also gives FFmpeg a seekable stream. FFmpeg itself
                    // never sees the header values.
                    rangeStream = await HttpRangeStream.CreateAsync(uri, request.Headers, token);
                    randomAccessStream = rangeStream.AsRandomAccessStream();
                    ffmpegSource = await FFmpegMediaSource.CreateFromStreamAsync(randomAccessStream, config).AsTask(token);
                }
                else
                {
                    // FFmpeg fetches the media itself: direct files, and HLS/DASH
                    // playlists whose segments only FFmpeg can resolve. Headers
                    // are passed through FFmpeg's own HTTP options (names only
                    // are ever logged).
                    if (hasHeaders) AddHeaderOptions(config, request.Headers);
                    ffmpegSource = await FFmpegMediaSource.CreateFromUriAsync(uri.AbsoluteUri, config).AsTask(token);
                }
                token.ThrowIfCancellationRequested();

                if (!IsCurrentOpen(generation, token)) return;
                var activePlayer = _mediaPlayer;
                _ffmpegSource = ffmpegSource;
                _playbackItem = ffmpegSource.CreateMediaPlaybackItem();
                _rangeStream = rangeStream;
                _randomAccessStream = randomAccessStream;
                ffmpegSource = null;
                rangeStream = null;
                randomAccessStream = null;
                _pendingStartSeconds = Math.Max(0, request.StartSeconds);
                _playWhenOpened = true;
                Log.Write("Playing with FFmpegInteropX (host " + uri.Host + ")");
                activePlayer.Source = _playbackItem;
            }
            catch (OperationCanceledException)
            {
                // A replacement request, Stop(), disposal, or the open timeout cancelled this load.
            }
            catch (Exception ex)
            {
                Log.Error("FFmpeg media source could not be opened", ex);
                FailOpen(generation, ex.Message);
            }
            finally
            {
                // FFmpegMediaSource may not implement IClosable; release it
                // through IDisposable only if it does.
                (ffmpegSource as IDisposable)?.Dispose();
                randomAccessStream?.Dispose();
                rangeStream?.Dispose();
            }
        }

        // Reconnect on dropped connections, a read-ahead buffer so short stalls
        // do not stop playback, and D3D11 hardware decoding unless the page
        // asked for software decoding.
        private static MediaSourceConfig CreateConfig(OpenRequest request)
        {
            var config = new MediaSourceConfig();
            var options = config.FFmpegOptions;
            options["reconnect"] = "1";
            options["reconnect_streamed"] = "1";
            options["reconnect_on_network_error"] = "1";
            options["timeout"] = "10000000";
            options["stimeout"] = "10000000";
            config.General.ReadAheadBufferEnabled = true;
            config.General.ReadAheadBufferDuration = TimeSpan.FromSeconds(30);
            config.Video.VideoDecoderMode = request != null && request.HardwareDecoding
                ? VideoDecoderMode.Automatic
                : VideoDecoderMode.ForceFFmpegSoftwareDecoder;
            return config;
        }

        // FFmpeg's HTTP protocol reads these options when it opens the URI
        // itself (and its segments). Values are never logged.
        private static void AddHeaderOptions(MediaSourceConfig config, IDictionary<string, string> headers)
        {
            var lines = new List<string>();
            var hasUserAgent = false;
            foreach (var header in headers)
            {
                if (string.IsNullOrWhiteSpace(header.Key) ||
                    header.Key.Equals("Range", StringComparison.OrdinalIgnoreCase)) continue;
                if (header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) hasUserAgent = true;
                lines.Add(header.Key + ": " + (header.Value ?? string.Empty));
            }
            if (!hasUserAgent) lines.Add("User-Agent: " + DefaultUserAgent);
            config.FFmpegOptions["headers"] = string.Join("\r\n", lines) + "\r\n";
        }

        private async Task WatchOpenTimeoutAsync(long generation)
        {
            try
            {
                await Task.Delay(OpenTimeout).ConfigureAwait(false);
                var view = _view;
                if (view == null || _disposed) return;
                await view.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    if (IsCurrentOpen(generation, CancellationToken.None) && !_readySent && !_openFailed)
                    {
                        FailOpen(generation, "The stream did not start within 30 seconds. Check the link or network, then try again.");
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error("Playback startup watchdog failed", ex);
            }
        }

        private bool IsCurrentOpen(long generation, CancellationToken token)
        {
            return !_disposed && !token.IsCancellationRequested &&
                generation == Interlocked.Read(ref _openGeneration) && !_openFailed;
        }

        private void FailOpen(long generation, string message)
        {
            if (generation != Interlocked.Read(ref _openGeneration) || _disposed || _openFailed) return;
            _openFailed = true;
            _playWhenOpened = false;
            CancelPendingOpen();
            try
            {
                if (_mediaPlayer != null) _mediaPlayer.Pause();
            }
            catch (Exception ex) { Log.Error("Clearing failed FFmpeg media source failed", ex); }
            if (_view != null) SetViewOpacity(0);
            ClearCurrentSource();
            RaiseState("error", message);
            RaiseEnded("error", message);
        }

        private void CancelPendingOpen()
        {
            var cancellation = _openCancellation;
            _openCancellation = null;
            if (cancellation == null) return;
            try { cancellation.Cancel(); }
            catch (Exception ex) { Log.Error("Cancelling pending media open failed", ex); }
            cancellation.Dispose();
        }

        private void ClearCurrentSource()
        {
            try { if (_mediaPlayer != null) _mediaPlayer.Source = null; }
            catch (Exception ex) { Log.Error("Clearing FFmpeg media source failed", ex); }
            DisposeCurrentSourceResources();
        }

        private void DisposeCurrentSourceResources()
        {
            try { _playbackItem = null; } catch (Exception ex) { Log.Error("Clearing FFmpeg playback item failed", ex); }
            // The FFmpegMediaSource must stay alive while its item plays; it is
            // only released here, after the player source was cleared above.
            // It may not implement IClosable; release it only if it does.
            try { (_ffmpegSource as IDisposable)?.Dispose(); } catch (Exception ex) { Log.Error("Closing FFmpeg media source failed", ex); }
            try { _randomAccessStream?.Dispose(); } catch (Exception ex) { Log.Error("Closing media stream failed", ex); }
            try { _rangeStream?.Dispose(); } catch (Exception ex) { Log.Error("Closing HTTP range stream failed", ex); }
            _ffmpegSource = null;
            _randomAccessStream = null;
            _rangeStream = null;
        }

        public void Stop()
        {
            Interlocked.Increment(ref _openGeneration);
            CancelPendingOpen();
            _openFailed = true;
            _playWhenOpened = false;
            if (_view != null) SetViewOpacity(0);
            ReleaseMediaPlayer();
            RaiseState("stopped", null);
        }

        public void SetPaused(bool paused) { if (_mediaPlayer != null) { if (paused) _mediaPlayer.Pause(); else _mediaPlayer.Play(); } }
        public void TogglePause()
        {
            if (_mediaPlayer == null) return;
            if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _mediaPlayer.Pause();
            else _mediaPlayer.Play();
        }
        public void Seek(double seconds)
        {
            if (_mediaPlayer == null || seconds < 0) return;
            try { _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(seconds); }
            catch (Exception ex) { Log.Error("Seeking FFmpeg playback failed", ex); }
        }
        public void SetVolume(int volume) { if (_mediaPlayer != null) _mediaPlayer.Volume = Math.Max(0, Math.Min(100, volume)) / 100.0; }
        public void SetMute(bool muted) { if (_mediaPlayer != null) _mediaPlayer.IsMuted = muted; }
        public void SetRate(double rate)
        {
            if (_mediaPlayer == null || rate <= 0) return;
            try { _mediaPlayer.PlaybackSession.PlaybackRate = Math.Max(0.25, Math.Min(4.0, rate)); }
            catch (Exception ex) { Log.Error("Setting playback rate failed", ex); }
        }

        // Track selection goes through the MediaPlaybackItem FFmpeg produced.
        // Ids are the index in the audio/subtitle list (-1 for none); the page
        // gets the same ids in the "player.tracks" event.
        public void SetAudioTrack(int trackId)
        {
            try
            {
                var item = _playbackItem;
                if (item == null || trackId < 0 || trackId >= item.AudioTracks.Count) return;
                item.AudioTracks.SelectedIndex = trackId;
                RefreshTracks();
            }
            catch (Exception ex) { Log.Error("Selecting FFmpeg audio track failed", ex); }
        }
        public void SetSubtitleTrack(int trackId)
        {
            try
            {
                var item = _playbackItem;
                if (item == null) return;
                for (var i = 0; i < item.TimedMetadataTracks.Count; i++)
                {
                    item.TimedMetadataTracks.SetPresentationMode((uint)i, i == trackId
                        ? TimedMetadataTrackPresentationMode.PlatformPresented
                        : TimedMetadataTrackPresentationMode.Disabled);
                }
                RefreshTracks();
            }
            catch (Exception ex) { Log.Error("Selecting FFmpeg subtitle track failed", ex); }
        }
        // FFmpegInteropX exposes stream delays on the media source, but the
        // page only ever sets them to 0 in practice; keep them harmless.
        public void SetSubtitleDelay(long milliseconds) { }
        public void SetAudioDelay(long milliseconds) { }
        public void SetAspect(string mode)
        {
            if (_view != null) _view.Stretch = string.Equals(mode, "fill", StringComparison.OrdinalIgnoreCase)
                ? Windows.UI.Xaml.Media.Stretch.Fill : Windows.UI.Xaml.Media.Stretch.Uniform;
        }
        public void SetViewSize(double width, double height) { }

        private void ReleaseMediaPlayer()
        {
            var player = _mediaPlayer;
            if (player == null)
            {
                ClearCurrentSource();
                return;
            }

            try { player.Pause(); }
            catch (Exception ex) { Log.Error("Pausing FFmpeg playback for cleanup failed", ex); }

            // Detach the source and rendering surface before disposing the source/player. Keeping
            // the surface bound to a stopped player can retain decoder/GPU resources after exit.
            try { player.Source = null; }
            catch (Exception ex) { Log.Error("Clearing FFmpeg media source for cleanup failed", ex); }
            try { if (_view != null) _view.SetMediaPlayer(null); }
            catch (Exception ex) { Log.Error("Detaching the media surface failed", ex); }
            DisposeCurrentSourceResources();

            player.MediaFailed -= OnMediaFailed;
            player.MediaEnded -= OnMediaEnded;
            player.MediaOpened -= OnMediaOpened;
            player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
            player.PlaybackSession.PositionChanged -= OnPositionChanged;
            player.PlaybackSession.NaturalDurationChanged -= OnNaturalDurationChanged;
            player.PlaybackSession.NaturalVideoSizeChanged -= OnNaturalVideoSizeChanged;
            player.PlaybackSession.BufferingProgressChanged -= OnBufferingProgressChanged;
            try { player.Dispose(); }
            catch (Exception ex) { Log.Error("Disposing FFmpeg playback failed", ex); }
            _mediaPlayer = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Interlocked.Increment(ref _openGeneration);
            CancelPendingOpen();
            ReleaseMediaPlayer();
            _view = null;
        }

        private void OnMediaOpened(MediaPlayer sender, object args)
        {
            if (_disposed || _openFailed || !_playWhenOpened || !ReferenceEquals(sender, _mediaPlayer)) return;
            _playWhenOpened = false;
            try
            {
                // FastSeek needs the session: hand FFmpeg the player's session
                // once the item has opened.
                try
                {
                    if (_ffmpegSource != null) _ffmpegSource.PlaybackSession = sender.PlaybackSession;
                }
                catch (Exception ex) { Log.Write("Assigning the FFmpeg playback session failed: " + ex.Message); }
                if (_pendingStartSeconds > 0)
                    sender.PlaybackSession.Position = TimeSpan.FromSeconds(_pendingStartSeconds);
                sender.Play();
                Log.Write("FFmpeg media source opened; starting playback");
                RefreshTracks();
            }
            catch (Exception ex)
            {
                Log.Error("Starting opened FFmpeg media failed", ex);
                FailOpen(Interlocked.Read(ref _openGeneration), ex.Message);
            }
        }

        private void RefreshTracks()
        {
            try
            {
                var source = _ffmpegSource;
                var item = _playbackItem;
                if (source == null || item == null) return;
                var payload = new JsonObject();
                var audio = new JsonArray();
                var currentAudio = item.AudioTracks != null ? item.AudioTracks.SelectedIndex : -1;
                var audioIndex = 0;
                foreach (var stream in source.AudioStreams)
                {
                    var entry = new JsonObject();
                    entry.SetNamedValue("id", JsonValue.CreateNumberValue(audioIndex));
                    entry.SetNamedValue("name", JsonValue.CreateStringValue(stream.Name ?? string.Empty));
                    entry.SetNamedValue("selected", JsonValue.CreateBooleanValue(audioIndex == currentAudio));
                    if (!string.IsNullOrEmpty(stream.Language))
                        entry.SetNamedValue("lang", JsonValue.CreateStringValue(stream.Language));
                    if (!string.IsNullOrEmpty(stream.CodecName))
                        entry.SetNamedValue("codec", JsonValue.CreateStringValue(stream.CodecName));
                    audio.Add(entry);
                    audioIndex++;
                }
                var subtitles = new JsonArray();
                var currentSubtitle = -1;
                if (item.TimedMetadataTracks != null)
                {
                    for (var i = 0; i < item.TimedMetadataTracks.Count; i++)
                    {
                        if (item.TimedMetadataTracks.GetPresentationMode((uint)i) == TimedMetadataTrackPresentationMode.PlatformPresented)
                            currentSubtitle = i;
                    }
                }
                var subtitleIndex = 0;
                foreach (var stream in source.SubtitleStreams)
                {
                    var entry = new JsonObject();
                    entry.SetNamedValue("id", JsonValue.CreateNumberValue(subtitleIndex));
                    entry.SetNamedValue("name", JsonValue.CreateStringValue(stream.Name ?? string.Empty));
                    entry.SetNamedValue("selected", JsonValue.CreateBooleanValue(subtitleIndex == currentSubtitle));
                    if (!string.IsNullOrEmpty(stream.Language))
                        entry.SetNamedValue("lang", JsonValue.CreateStringValue(stream.Language));
                    if (!string.IsNullOrEmpty(stream.CodecName))
                        entry.SetNamedValue("codec", JsonValue.CreateStringValue(stream.CodecName));
                    subtitles.Add(entry);
                    subtitleIndex++;
                }
                payload.SetNamedValue("audio", audio);
                payload.SetNamedValue("subtitles", subtitles);
                payload.SetNamedValue("audioId", JsonValue.CreateNumberValue(currentAudio));
                payload.SetNamedValue("subtitleId", JsonValue.CreateNumberValue(currentSubtitle));
                Raise("player.tracks", payload);
            }
            catch (Exception ex)
            {
                Log.Error("Reading FFmpeg tracks failed", ex);
            }
        }

        private void OnNaturalVideoSizeChanged(MediaPlaybackSession sender, object args)
        {
            SignalVideoReady(sender);
        }

        private void SignalVideoReady(MediaPlaybackSession session)
        {
            if (_disposed || _openFailed || _readySent ||
                session.PlaybackState != MediaPlaybackState.Playing ||
                session.NaturalVideoHeight == 0 || session.NaturalVideoWidth == 0)
                return;

            _readySent = true;
            CancelPendingOpen();
            Log.Write("FFmpeg video track ready (" + session.NaturalVideoWidth + "x" + session.NaturalVideoHeight + ")");
            Raise("player.ready", new JsonObject());
            if (_view != null) SetViewOpacity(1);
        }

        private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            // Playback callbacks may arrive after Stop() or on a media worker thread.
            if (_disposed || _openFailed) return;
            if (sender.PlaybackState == MediaPlaybackState.Playing)
            {
                SignalVideoReady(sender);
                RaiseState("playing", null);
                // BufferingProgress rarely reaches 100% while streaming,
                // so tell the page buffering is complete once we are playing.
                var bufferingPayload = new JsonObject();
                bufferingPayload.SetNamedValue("percent", JsonValue.CreateNumberValue(100));
                Raise("player.buffering", bufferingPayload);
            }
            else if (sender.PlaybackState == MediaPlaybackState.Buffering) RaiseState("buffering", null);
            else if (sender.PlaybackState == MediaPlaybackState.Paused) RaiseState("paused", null);
            else RaiseState("stopped", null);
        }
        private void OnPositionChanged(MediaPlaybackSession sender, object args)
        {
            // PositionChanged can fire several times per second. Coalesce progress updates so
            // the WebView bridge doesn't build a backlog on the Xbox renderer thread.
            var now = DateTime.UtcNow.Ticks;
            var previous = Interlocked.Read(ref _lastPositionEventTicks);
            if (now - previous < TimeSpan.TicksPerMillisecond * 250 ||
                Interlocked.CompareExchange(ref _lastPositionEventTicks, now, previous) != previous)
                return;

            var payload = new JsonObject();
            payload.SetNamedValue("time", JsonValue.CreateNumberValue(Math.Max(0, sender.Position.TotalSeconds)));
            payload.SetNamedValue("duration", JsonValue.CreateNumberValue(Math.Max(0, sender.NaturalDuration.TotalSeconds)));
            Raise("player.time", payload);
        }
        private void OnNaturalDurationChanged(MediaPlaybackSession sender, object args)
        {
            if (sender.NaturalDuration.TotalSeconds <= 0) return;
            var payload = new JsonObject();
            payload.SetNamedValue("duration", JsonValue.CreateNumberValue(sender.NaturalDuration.TotalSeconds));
            Raise("player.duration", payload);
        }
        private void OnBufferingProgressChanged(MediaPlaybackSession sender, object args)
        {
            var payload = new JsonObject();
            payload.SetNamedValue("percent", JsonValue.CreateNumberValue(Math.Max(0, Math.Min(100, sender.BufferingProgress * 100))));
            Raise("player.buffering", payload);
        }
        private void OnMediaEnded(MediaPlayer sender, object args)
        {
            _playWhenOpened = false;
            if (_view != null) SetViewOpacity(0);
            ClearCurrentSource();
            RaiseEnded("eof", null);
        }
        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            var message = string.IsNullOrWhiteSpace(args.ErrorMessage) ? "FFmpeg could not play this media." : args.ErrorMessage;
            _lastErrorKind = args.Error.ToString();
            Log.Write("FFmpeg playback failed (" + args.Error + "): " + message);
            if (_openFailed || _disposed) return;
            _openFailed = true;
            _playWhenOpened = false;
            CancelPendingOpen();
            if (_view != null) SetViewOpacity(0);
            ClearCurrentSource();
            RaiseState("error", message);
            RaiseEnded("error", message);
        }
        private void RaiseState(string state, string message)
        {
            var payload = new JsonObject();
            payload.SetNamedValue("state", JsonValue.CreateStringValue(state));
            if (message != null) payload.SetNamedValue("message", JsonValue.CreateStringValue(message));
            Raise("player.state", payload);
        }
        private void RaiseEnded(string reason, string message)
        {
            var payload = new JsonObject();
            payload.SetNamedValue("reason", JsonValue.CreateStringValue(reason));
            if (message != null) payload.SetNamedValue("message", JsonValue.CreateStringValue(message));
            // Lets the hybrid player tell codec failures (which VLC can play)
            // apart from other errors. Never carries header values or URLs.
            if (reason == "error" && !string.IsNullOrEmpty(_lastErrorKind))
                payload.SetNamedValue("errorKind", JsonValue.CreateStringValue(_lastErrorKind));
            Raise("player.ended", payload);
        }
        private void Raise(string type, JsonObject payload)
        {
            payload.SetNamedValue("id", _pageId == null ? JsonValue.CreateNullValue() : JsonValue.CreateStringValue(_pageId));
            EventRaised?.Invoke(type, payload);
        }
    }
}
