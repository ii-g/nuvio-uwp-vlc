using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Nuvio.Diagnostics;
using Windows.Data.Json;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.UI.Xaml.Controls;
using Windows.Storage.Streams;
using Windows.Web.Http;

namespace Nuvio.Playback
{
    internal sealed class OpenRequest
    {
        public string Id;
        public string Url;
        public double StartSeconds;
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public bool HardwareDecoding = true;
    }

    /// <summary>Native Windows Media Foundation playback for the UWP app.</summary>
    internal sealed class NativeMediaPlayer : IDisposable
    {
        private MediaPlayerElement _view;
        private MediaPlayer _mediaPlayer;
        private MediaSource _source;
        private HttpRangeStream _rangeStream;
        private IRandomAccessStream _randomAccessStream;
        private Windows.Web.Http.HttpClient _adaptiveHttpClient;
        private string _pageId;
        private bool _readySent;
        private bool _openFailed;
        private bool _disposed;
        private long _openGeneration;
        private long _lastPositionEventTicks;
        private CancellationTokenSource _openCancellation;
        private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);

        public event Action<string, JsonObject> EventRaised;
        public string Version { get; private set; } = "Windows Media Foundation";

        public void Initialize(MediaPlayerElement view)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NativeMediaPlayer));
            if (_mediaPlayer != null) return;
            if (view == null) throw new ArgumentNullException(nameof(view));
            _view = view;
            _mediaPlayer = new MediaPlayer { AutoPlay = false, IsMuted = false, Volume = 1.0 };
            _mediaPlayer.MediaFailed += OnMediaFailed;
            _mediaPlayer.MediaEnded += OnMediaEnded;
            _mediaPlayer.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
            _mediaPlayer.PlaybackSession.PositionChanged += OnPositionChanged;
            _mediaPlayer.PlaybackSession.NaturalDurationChanged += OnNaturalDurationChanged;
            _mediaPlayer.PlaybackSession.BufferingProgressChanged += OnBufferingProgressChanged;
            _view.SetMediaPlayer(_mediaPlayer);
            Log.Write("Native Windows Media Foundation player ready");
        }

        public void SetUnavailable(string reason) { RaiseState("error", reason); RaiseEnded("error", reason); }

        public void Open(OpenRequest request)
        {
            CancelPendingOpen();
            var generation = Interlocked.Increment(ref _openGeneration);
            _pageId = request == null ? null : request.Id;
            _readySent = false;
            _openFailed = false;
            if (_mediaPlayer == null)
            {
                FailOpen(generation, "The native media player is not ready.");
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
                _mediaPlayer.Pause();
                ClearCurrentSource();
                if (_view != null) _view.Opacity = 0;
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
                Log.Error("Native media source could not be opened", ex);
                FailOpen(generation, ex.Message);
            }
        }

        private async Task OpenSourceAsync(OpenRequest request, Uri uri, long generation, CancellationToken token)
        {
            MediaSource source = null;
            HttpRangeStream rangeStream = null;
            IRandomAccessStream randomAccessStream = null;
            Windows.Web.Http.HttpClient adaptiveClient = null;
            try
            {
                var hasHeaders = request.Headers != null && request.Headers.Count > 0;
                var path = uri.AbsolutePath.ToLowerInvariant();
                var adaptive = path.EndsWith(".m3u8") || path.EndsWith(".mpd") || path.EndsWith(".m3u");
                if (adaptive)
                {
                    AdaptiveMediaSourceCreationResult result;
                    if (hasHeaders)
                    {
                        adaptiveClient = CreateAdaptiveClient(request.Headers);
                        result = await AdaptiveMediaSource.CreateFromUriAsync(uri, adaptiveClient).AsTask(token);
                    }
                    else result = await AdaptiveMediaSource.CreateFromUriAsync(uri).AsTask(token);

                    token.ThrowIfCancellationRequested();
                    if (result.Status != AdaptiveMediaSourceCreationStatus.Success)
                    {
                        // MediaSource.CreateFromUri uses the platform's normal format converter
                        // and can recognize manifests that AdaptiveMediaSource rejects during
                        // explicit parsing. Preserve authenticated adaptive handling; this fallback
                        // is only safe when the URL needs no custom request headers.
                        if (!hasHeaders && result.Status == AdaptiveMediaSourceCreationStatus.ManifestParseFailure)
                        {
                            Log.Write("Adaptive manifest parsing failed; retrying with the platform URI source");
                            source = MediaSource.CreateFromUri(uri);
                        }
                        else
                        {
                            throw new InvalidOperationException("Windows Media Foundation could not open this adaptive stream (" + result.Status + ").");
                        }
                    }
                    else source = MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource);
                }
                else if (hasHeaders)
                {
                    rangeStream = await HttpRangeStream.CreateAsync(uri, request.Headers, token);
                    randomAccessStream = rangeStream.AsRandomAccessStream();
                    source = MediaSource.CreateFromStream(randomAccessStream, rangeStream.ContentType);
                }
                else source = MediaSource.CreateFromUri(uri);

                if (!IsCurrentOpen(generation, token)) return;
                _mediaPlayer.Source = source;
                _source = source;
                _rangeStream = rangeStream;
                _randomAccessStream = randomAccessStream;
                _adaptiveHttpClient = adaptiveClient;
                source = null;
                rangeStream = null;
                randomAccessStream = null;
                adaptiveClient = null;
                _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, request.StartSeconds));
                _mediaPlayer.Play();
            }
            catch (OperationCanceledException)
            {
                // A replacement request, Stop(), disposal, or the open timeout cancelled this load.
            }
            catch (Exception ex)
            {
                Log.Error("Native media source could not be opened", ex);
                FailOpen(generation, ex.Message);
            }
            finally
            {
                source?.Dispose();
                randomAccessStream?.Dispose();
                rangeStream?.Dispose();
                adaptiveClient?.Dispose();
            }
        }

        private static Windows.Web.Http.HttpClient CreateAdaptiveClient(IDictionary<string, string> headers)
        {
            var client = new Windows.Web.Http.HttpClient();
            foreach (var header in headers)
            {
                if (string.IsNullOrWhiteSpace(header.Key) || header.Key.Equals("Range", StringComparison.OrdinalIgnoreCase)) continue;
                if (!client.DefaultRequestHeaders.TryAppendWithoutValidation(header.Key, header.Value ?? string.Empty))
                {
                    client.Dispose();
                    throw new InvalidOperationException("The media request header '" + header.Key + "' cannot be applied to this adaptive stream.");
                }
            }
            return client;
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
            CancelPendingOpen();
            try
            {
                if (_mediaPlayer != null) _mediaPlayer.Pause();
            }
            catch (Exception ex) { Log.Error("Clearing failed native media source failed", ex); }
            if (_view != null) _view.Opacity = 0;
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
            catch (Exception ex) { Log.Error("Clearing native media source failed", ex); }
            try { _source?.Dispose(); } catch (Exception ex) { Log.Error("Closing media source failed", ex); }
            try { _randomAccessStream?.Dispose(); } catch (Exception ex) { Log.Error("Closing media stream failed", ex); }
            try { _rangeStream?.Dispose(); } catch (Exception ex) { Log.Error("Closing HTTP range stream failed", ex); }
            try { _adaptiveHttpClient?.Dispose(); } catch (Exception ex) { Log.Error("Closing adaptive HTTP client failed", ex); }
            _source = null;
            _randomAccessStream = null;
            _rangeStream = null;
            _adaptiveHttpClient = null;
        }

        public void Stop()
        {
            Interlocked.Increment(ref _openGeneration);
            CancelPendingOpen();
            _openFailed = true;
            if (_mediaPlayer == null) return;
            try { _mediaPlayer.Pause(); ClearCurrentSource(); }
            catch (Exception ex) { Log.Error("Stopping native playback failed", ex); }
            if (_view != null) _view.Opacity = 0;
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
            catch (Exception ex) { Log.Error("Seeking native playback failed", ex); }
        }
        public void SetVolume(int volume) { if (_mediaPlayer != null) _mediaPlayer.Volume = Math.Max(0, Math.Min(100, volume)) / 100.0; }
        public void SetMute(bool muted) { if (_mediaPlayer != null) _mediaPlayer.IsMuted = muted; }
        public void SetRate(double rate)
        {
            if (_mediaPlayer == null || rate <= 0) return;
            try { _mediaPlayer.PlaybackSession.PlaybackRate = Math.Max(0.25, Math.Min(4.0, rate)); }
            catch (Exception ex) { Log.Error("Setting playback rate failed", ex); }
        }

        // These controls remain harmless: arbitrary remote MediaSource URLs do not expose reliable
        // per-track selection or synchronization controls through the UWP MediaPlayerElement API.
        public void SetAudioTrack(int trackId) { }
        public void SetSubtitleTrack(int trackId) { }
        public void SetSubtitleDelay(long milliseconds) { }
        public void SetAudioDelay(long milliseconds) { }
        public void SetAspect(string mode)
        {
            if (_view != null) _view.Stretch = string.Equals(mode, "fill", StringComparison.OrdinalIgnoreCase)
                ? Windows.UI.Xaml.Media.Stretch.Fill : Windows.UI.Xaml.Media.Stretch.Uniform;
        }
        public void SetViewSize(double width, double height) { }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_mediaPlayer != null)
            {
                _mediaPlayer.MediaFailed -= OnMediaFailed;
                _mediaPlayer.MediaEnded -= OnMediaEnded;
                _mediaPlayer.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
                _mediaPlayer.PlaybackSession.PositionChanged -= OnPositionChanged;
                _mediaPlayer.PlaybackSession.NaturalDurationChanged -= OnNaturalDurationChanged;
                _mediaPlayer.PlaybackSession.BufferingProgressChanged -= OnBufferingProgressChanged;
                try { _mediaPlayer.Pause(); ClearCurrentSource(); _mediaPlayer.Dispose(); }
                catch (Exception ex) { Log.Error("Disposing native playback failed", ex); }
                _mediaPlayer = null;
            }
            _view = null;
        }

        private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            if (sender.PlaybackState == MediaPlaybackState.Playing)
            {
                if (!_readySent)
                {
                    _readySent = true;
                    CancelPendingOpen();
                    Raise("player.ready", new JsonObject());
                    if (_view != null) _view.Opacity = 1;
                }
                RaiseState("playing", null);
            }
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
            if (_view != null) _view.Opacity = 0;
            ClearCurrentSource();
            RaiseEnded("eof", null);
        }
        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            var message = string.IsNullOrWhiteSpace(args.ErrorMessage) ? "Windows Media Foundation could not play this media." : args.ErrorMessage;
            Log.Write("Native playback failed (" + args.Error + "): " + message);
            if (_openFailed || _disposed) return;
            _openFailed = true;
            CancelPendingOpen();
            if (_view != null) _view.Opacity = 0;
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
            Raise("player.ended", payload);
        }
        private void Raise(string type, JsonObject payload)
        {
            payload.SetNamedValue("id", _pageId == null ? JsonValue.CreateNullValue() : JsonValue.CreateStringValue(_pageId));
            EventRaised?.Invoke(type, payload);
        }
    }
}
