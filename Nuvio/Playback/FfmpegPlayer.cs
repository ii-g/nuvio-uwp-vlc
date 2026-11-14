using System;
using System.Collections.Generic;
using System.Globalization;
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
        private long _lastHeartbeatTicks;
        private long _positionChangedSinceHeartbeat;
        private long _lastPositionChangedTicks;
        private double _lastPositionSeconds;
        private bool _hardwareDecodingRequested;
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
            player.PlaybackSession.BufferingStarted += OnBufferingStarted;
            player.PlaybackSession.BufferingEnded += OnBufferingEnded;
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
                player.PlaybackSession.BufferingStarted -= OnBufferingStarted;
                player.PlaybackSession.BufferingEnded -= OnBufferingEnded;
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

        public void SetUnavailable(string reason)
        {
            _lastErrorKind = "Unavailable";
            RaiseState("error", reason);
            RaiseEnded("error", reason);
        }

        public void Open(OpenRequest request)
        {
            CancelPendingOpen();
            var generation = Interlocked.Increment(ref _openGeneration);
            _pageId = request == null ? null : request.Id;
            _readySent = false;
            _openFailed = false;
            _lastErrorKind = null;
            _hardwareDecodingRequested = request != null && request.HardwareDecoding;
            _lastPositionSeconds = request == null ? 0 : Math.Max(0, request.StartSeconds);
            _playWhenOpened = false;
            _pendingStartSeconds = 0;
            if (_view == null)
            {
                FailOpen(generation, "The media player is not ready.", "PlayerNotReady");
                return;
            }

            Uri uri;
            if (request == null || !Uri.TryCreate(request.Url, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                FailOpen(generation, "This player accepts direct HTTP or HTTPS media links.", "InvalidSource");
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
                // Messages are included URL-redacted (see DescribeOpenException):
                // the message may carry the stream URL, so it is never logged raw.
                Log.Write("FFmpeg media source could not be opened: " + DescribeOpenException(ex) +
                    " hwdec=" + _hardwareDecodingRequested + " at " + Math.Round(_lastPositionSeconds) + "s");
                FailOpen(generation, "FFmpeg could not open this source.", ClassifyOpenError(ex));
            }
        }

        private async Task OpenSourceAsync(OpenRequest request, Uri uri, long generation, CancellationToken token)
        {
            FFmpegMediaSource ffmpegSource = null;
            HttpRangeStream rangeStream = null;
            IRandomAccessStream randomAccessStream = null;
            // Which open stage failed: "connect" is our own HTTP stack
            // (HttpRangeStream), "probe" is FFmpeg reading the container
            // from that stream, "fetch+probe" is FFmpeg fetching the URI
            // itself (adaptive playlists and header-less sources).
            var stage = "connect";
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
                // Requested decoder only: FFmpegInteropX reports the ACTUAL
                // decoder (VideoStreamInfo.DecoderEngine) once playback has
                // started — see LogActualDecoder, called when the video is
                // ready and on failure. Automatic asks for D3D11 hardware
                // and falls back to software if it is unavailable.
                Log.Write("FFmpeg decoder: " + RequestedDecoderLabel(request.HardwareDecoding));

                var config = CreateConfig(request);
                if (hasHeaders && !adaptive)
                {
                    // The headers ride on our own HTTP stack (HttpRangeStream),
                    // which also gives FFmpeg a seekable stream. FFmpeg itself
                    // never sees the header values.
                    Log.Write("FFmpeg open stage: connect (HttpRangeStream, host=" + uri.Host + ")");
                    rangeStream = await HttpRangeStream.CreateAsync(uri, request.Headers, token);
                    randomAccessStream = rangeStream.AsRandomAccessStream();
                    stage = "probe";
                    Log.Write("FFmpeg open stage: probe (CreateFromStream)");
                    ffmpegSource = await FFmpegMediaSource.CreateFromStreamAsync(randomAccessStream, config).AsTask(token);
                }
                else
                {
                    // FFmpeg fetches the media itself: direct files, and HLS/DASH
                    // playlists whose segments only FFmpeg can resolve. Headers
                    // are passed through FFmpeg's own HTTP options (names only
                    // are ever logged).
                    if (hasHeaders) AddHeaderOptions(config, request.Headers);
                    stage = "fetch+probe";
                    Log.Write("FFmpeg open stage: fetch+probe (CreateFromUri, host=" + uri.Host + ")");
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
                // Messages are included URL-redacted (see DescribeOpenException).
                // The hwdec flag matters here: a failure while hardware decoding
                // was requested may be a D3D11VA failure, not a bad source.
                Log.Write("FFmpeg media source could not be opened: " + DescribeOpenException(ex) +
                    " stage=" + stage +
                    " hwdec=" + _hardwareDecodingRequested + " at " + Math.Round(_lastPositionSeconds) + "s");
                FailOpen(generation, "FFmpeg could not open this source.", ClassifyOpenError(ex));
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
            config.General.ReadAheadBufferDuration = TimeSpan.FromSeconds(10);
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

        // The 30-second open timeout is unchanged; the watchdog now also
        // logs progress every 5 seconds while the open is still waiting,
        // so a stalled open leaves a trail of session states instead of
        // silence followed by a single OpenTimeout line.
        private async Task WatchOpenTimeoutAsync(long generation)
        {
            try
            {
                for (var elapsed = 5; elapsed <= (int)OpenTimeout.TotalSeconds; elapsed += 5)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    if (_disposed || !IsCurrentOpen(generation, CancellationToken.None) || _readySent || _openFailed) return;
                    var tickElapsed = elapsed;
                    var view = _view;
                    if (view == null) return;
                    await view.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        if (!IsCurrentOpen(generation, CancellationToken.None) || _readySent || _openFailed) return;
                        if (tickElapsed < (int)OpenTimeout.TotalSeconds) LogOpenProgress(tickElapsed);
                        else FailOpen(generation, "The stream did not start within 30 seconds. Check the link or network, then try again.", "OpenTimeout");
                    });
                }
                // The loop only fails the open when the timeout lands exactly
                // on a 5-second tick (30s today). If OpenTimeout ever stops
                // being a multiple of 5, the loop ends after the last progress
                // tick without failing the open — so wait out the remainder
                // and fail it here, with the same exit guards. At 30s this is
                // a no-op: the 30s tick already called FailOpen, and its
                // _openFailed guard makes the check below return. FailOpen is
                // already called from background threads elsewhere in this
                // file (OpenSourceAsync), so no dispatcher hop is needed here.
                var timeoutSeconds = (int)OpenTimeout.TotalSeconds;
                var remainderSeconds = timeoutSeconds - (timeoutSeconds / 5) * 5;
                if (remainderSeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(remainderSeconds)).ConfigureAwait(false);
                if (_disposed || !IsCurrentOpen(generation, CancellationToken.None) || _readySent || _openFailed) return;
                FailOpen(generation, "The stream did not start within 30 seconds. Check the link or network, then try again.", "OpenTimeout");
            }
            catch (Exception ex)
            {
                Log.Error("Playback startup watchdog failed", ex);
            }
        }

        // Runs on the UI thread (from the watchdog): session state,
        // buffering and download progress while the open is still waiting.
        private void LogOpenProgress(int elapsedSeconds)
        {
            var session = _mediaPlayer == null ? null : _mediaPlayer.PlaybackSession;
            if (session == null)
            {
                Log.Write("FFmpeg open still waiting (" + elapsedSeconds + "s/" + (int)OpenTimeout.TotalSeconds + "s, no playback session)");
                return;
            }
            Log.Write("FFmpeg open still waiting (" + elapsedSeconds + "s/" + (int)OpenTimeout.TotalSeconds + "s, state=" + session.PlaybackState +
                " pos=" + Math.Round(session.Position.TotalSeconds) + "s" +
                " buffering=" + Math.Round(session.BufferingProgress * 100) + "%" +
                " download=" + Math.Round(session.DownloadProgress * 100) + "%)");
        }

        private bool IsCurrentOpen(long generation, CancellationToken token)
        {
            return !_disposed && !token.IsCancellationRequested &&
                generation == Interlocked.Read(ref _openGeneration) && !_openFailed;
        }

        private void FailOpen(long generation, string message, string errorKind)
        {
            if (generation != Interlocked.Read(ref _openGeneration) || _disposed || _openFailed) return;
            _openFailed = true;
            _playWhenOpened = false;
            // Every failure that reaches the page must carry a real kind:
            // the hybrid player falls back on "player.ended" + errorKind,
            // and a missing kind used to surface there as "unknown".
            _lastErrorKind = string.IsNullOrEmpty(errorKind) ? "OpenError" : errorKind;
            Log.Write("FFmpeg open failed (" + _lastErrorKind + ") at " + Math.Round(_lastPositionSeconds) +
                "s hwdec=" + _hardwareDecodingRequested);
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

        private static string RequestedDecoderLabel(bool hardwareDecoding)
        {
            return hardwareDecoding ? "D3D11 hardware requested (hwdec=true)"
                : "software requested (hwdec=false)";
        }

        // The kind for an open failure is the exception type + HResult only.
        // Never the message: it may carry the stream URL or a token.
        private static string ClassifyOpenError(Exception ex)
        {
            if (ex == null) return "OpenError";
            return ex.GetType().Name + ":0x" + ex.HResult.ToString("X8");
        }

        // Type + HResult + the exception messages, with any URL redacted.
        // The message may carry the stream URL or a token, so it is never
        // logged raw — but type + HResult alone left an instant 0x80004005
        // (E_FAIL) indistinguishable from a network timeout, and the inner
        // message is what names the real failure class.
        private static string DescribeOpenException(Exception ex)
        {
            if (ex == null) return "null";
            var description = DescribeOneException(ex);
            var inner = ex.InnerException;
            var depth = 0;
            while (inner != null && depth < 3)
            {
                description += " inner=" + DescribeOneException(inner);
                inner = inner.InnerException;
                depth++;
            }
            return description;
        }

        private static string DescribeOneException(Exception ex)
        {
            var description = ex.GetType().Name + " HResult=0x" + ex.HResult.ToString("X8");
            if (!string.IsNullOrEmpty(ex.Message))
            {
                var message = RedactUrls(ex.Message);
                if (message.Length > 500) message = message.Substring(0, 500) + "...";
                description += " msg=\"" + message + "\"";
            }
            return description;
        }

        // Replaces any http(s)://... token (which may carry a query string
        // or token) with "(url)"; the rest of the message is diagnostic text.
        private static string RedactUrls(string message)
        {
            var result = message;
            foreach (var scheme in new[] { "https://", "http://" })
            {
                int index;
                while ((index = result.IndexOf(scheme, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var end = index;
                    while (end < result.Length && !char.IsWhiteSpace(result[end])) end++;
                    result = result.Substring(0, index) + "(url)" + result.Substring(end);
                }
            }
            return result;
        }

        // Reports the decoder FFmpeg actually selected for each video stream
        // (FFmpegD3D11HardwareDecoder vs FFmpegSoftwareDecoder vs
        // SystemDecoder), with the hardware-availability status. Only
        // meaningful once playback has started — FFmpegInteropX itself notes
        // VideoStreamInfo shows the actual decoder after playback starts —
        // so this is called when the video is ready and on failure, never
        // at open time, when only the requested mode is known.
        private void LogActualDecoder(string context)
        {
            try
            {
                var source = _ffmpegSource;
                if (source == null || source.VideoStreams == null || source.VideoStreams.Count == 0)
                {
                    Log.Write("FFmpeg decoder (" + context + "): no video stream info available");
                    return;
                }
                foreach (var stream in source.VideoStreams)
                {
                    Log.Write("FFmpeg decoder (" + context + "): actual=" + stream.DecoderEngine +
                        " hwAccel=" + stream.HardwareDecoderStatus +
                        " codec=" + (stream.CodecName ?? string.Empty) +
                        " requested=" + (_hardwareDecodingRequested ? "D3D11 hardware" : "software"));
                }
            }
            catch (Exception ex)
            {
                Log.Write("FFmpeg decoder (" + context + "): could not read decoder info: " + ex.GetType().Name);
            }
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
            try
            {
                var from = _mediaPlayer.PlaybackSession.Position.TotalSeconds;
                _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(seconds);
                Log.Write("FFmpeg seek: from=" + from.ToString("F1", CultureInfo.InvariantCulture) + "s to=" + seconds.ToString("F1", CultureInfo.InvariantCulture) + "s");
            }
            catch (Exception ex) { Log.Error("Seeking FFmpeg playback failed", ex); }
        }
        public void SetVolume(int volume) { if (_mediaPlayer != null) _mediaPlayer.Volume = Math.Max(0, Math.Min(100, volume)) / 100.0; }
        public void SetMute(bool muted) { if (_mediaPlayer != null) _mediaPlayer.IsMuted = muted; }
        public void SetRate(double rate)
        {
            if (_mediaPlayer == null || rate <= 0) return;
            try
            {
                _mediaPlayer.PlaybackSession.PlaybackRate = Math.Max(0.25, Math.Min(4.0, rate));
                Log.Write("FFmpeg rate: " + _mediaPlayer.PlaybackSession.PlaybackRate);
            }
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
            player.PlaybackSession.BufferingStarted -= OnBufferingStarted;
            player.PlaybackSession.BufferingEnded -= OnBufferingEnded;
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
                Log.Write("Starting opened FFmpeg media failed: " + DescribeOpenException(ex) +
                    " hwdec=" + _hardwareDecodingRequested + " at " + Math.Round(_lastPositionSeconds) + "s");
                FailOpen(Interlocked.Read(ref _openGeneration), "FFmpeg could not start playback.", "StartFailed");
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
            LogActualDecoder("ready");
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
            // Keep the freshest position for failure logs even when the
            // progress event below is coalesced.
            _lastPositionSeconds = Math.Max(0, sender.Position.TotalSeconds);
            // Playback heartbeat: the only periodic proof the picture is
            // still advancing. FFmpegInteropX exposes no dropped-frame
            // counter (its public API is stream info only), and neither
            // does MediaPlaybackSession, so a heartbeat whose position
            // stops moving while the state stays Playing is the stall
            // signal — plus buffering/download progress for the cause.
            var now = DateTime.UtcNow;
            Interlocked.Increment(ref _positionChangedSinceHeartbeat);
            var previousEventTicks = Interlocked.Exchange(ref _lastPositionChangedTicks, now.Ticks);
            var previousHeartbeat = Interlocked.Read(ref _lastHeartbeatTicks);
            if (_readySent && sender.PlaybackState == MediaPlaybackState.Playing &&
                now.Ticks - previousHeartbeat >= TimeSpan.TicksPerSecond * 10 &&
                Interlocked.CompareExchange(ref _lastHeartbeatTicks, now.Ticks, previousHeartbeat) == previousHeartbeat)
            {
                // Count includes this event; gap is the silence before it, so a
                // heartbeat after quiet events still shows how long they were quiet.
                var eventCount = Interlocked.Exchange(ref _positionChangedSinceHeartbeat, 0);
                var lastGapMs = previousEventTicks == 0 ? -1 : (now.Ticks - previousEventTicks) / TimeSpan.TicksPerMillisecond;
                LogHeartbeat(sender, eventCount, lastGapMs);
            }
            // PositionChanged can fire several times per second. Coalesce progress updates so
            // the WebView bridge doesn't build a backlog on the Xbox renderer thread.
            var previous = Interlocked.Read(ref _lastPositionEventTicks);
            if (now.Ticks - previous < TimeSpan.TicksPerMillisecond * 250 ||
                Interlocked.CompareExchange(ref _lastPositionEventTicks, now.Ticks, previous) != previous)
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
        private void LogHeartbeat(MediaPlaybackSession session, long eventCount, long lastGapMs)
        {
            try
            {
                // BufferingProgress and DownloadProgress are 0..1 ratios
                // (Microsoft Learn: multiply by 100 for a percentage).
                // pos keeps three decimals so a ~0.59x clock can be told apart
                // from rounding; events counts PositionChanged since the
                // previous heartbeat and lastGap is the silence before the
                // event that triggered this heartbeat.
                Log.Write("FFmpeg heartbeat: state=" + session.PlaybackState +
                    " pos=" + session.Position.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) + "s/" + Math.Round(session.NaturalDuration.TotalSeconds) + "s" +
                    " rate=" + session.PlaybackRate +
                    " buffering=" + Math.Round(session.BufferingProgress * 100) + "%" +
                    " download=" + Math.Round(session.DownloadProgress * 100) + "%" +
                    " events=" + eventCount +
                    " lastGap=" + (lastGapMs < 0 ? "n/a" : lastGapMs + "ms"));
            }
            catch (Exception ex)
            {
                Log.Write("FFmpeg heartbeat: could not read session: " + ex.GetType().Name);
            }
        }
        private void OnBufferingStarted(MediaPlaybackSession sender, object args)
        {
            // BufferingStarted/BufferingEnded mark the exact start/end of
            // a stall; the state machine already reports "buffering", but
            // without these the log cannot tell a rebuffer from a state
            // that was never left. Fires during the open too, at pos 0s.
            if (_disposed || _openFailed) return;
            Log.Write("FFmpeg buffering started at " + Math.Round(_lastPositionSeconds) + "s");
        }
        private void OnBufferingEnded(MediaPlaybackSession sender, object args)
        {
            if (_disposed || _openFailed) return;
            Log.Write("FFmpeg buffering ended at " + Math.Round(_lastPositionSeconds) + "s");
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
            try { _lastPositionSeconds = Math.Max(0, sender.PlaybackSession.Position.TotalSeconds); }
            catch (Exception) { /* fall back to the last throttled position */ }
            Log.Write("FFmpeg playback failed (" + args.Error + ") at " + Math.Round(_lastPositionSeconds) + "s: " + message);
            LogActualDecoder("failed");
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
