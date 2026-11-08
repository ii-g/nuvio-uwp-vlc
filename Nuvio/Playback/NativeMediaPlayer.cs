using System;
using System.Collections.Generic;
using Nuvio.Diagnostics;
using Windows.Data.Json;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.UI.Xaml.Controls;

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
        private string _pageId;
        private bool _readySent;
        private bool _disposed;

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

        public void SetUnavailable(string reason) { RaiseEnded("error", reason); }

        public void Open(OpenRequest request)
        {
            _pageId = request == null ? null : request.Id;
            _readySent = false;
            if (_mediaPlayer == null)
            {
                RaiseState("error", "The native media player is not ready.");
                RaiseEnded("error", "The native media player is not ready.");
                return;
            }
            try
            {
                Uri uri;
                if (request == null || !Uri.TryCreate(request.Url, UriKind.Absolute, out uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw new NotSupportedException("This player accepts direct HTTP or HTTPS media links.");
                if (request.Headers != null && request.Headers.Count > 0)
                    throw new NotSupportedException("This link requires custom HTTP headers, which the native UWP player cannot apply to direct media sources.");
                _mediaPlayer.Pause();
                _mediaPlayer.Source = null;
                RaiseState("opening", null);
                OpenSourceAsync(request, uri);
            }
            catch (Exception ex)
            {
                Log.Error("Native media source could not be opened", ex);
                RaiseState("error", ex.Message);
                RaiseEnded("error", ex.Message);
            }
        }

        private async void OpenSourceAsync(OpenRequest request, Uri uri)
        {
            try
            {
                MediaSource source;
                var path = uri.AbsolutePath.ToLowerInvariant();
                if (path.EndsWith(".m3u8") || path.EndsWith(".mpd") || path.EndsWith(".m3u"))
                {
                    var result = await AdaptiveMediaSource.CreateFromUriAsync(uri);
                    if (result.Status != AdaptiveMediaSourceCreationStatus.Success)
                        throw new InvalidOperationException("Windows Media Foundation could not open this adaptive stream (" + result.Status + ").");
                    source = MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource);
                }
                else source = MediaSource.CreateFromUri(uri);

                if (_mediaPlayer == null || _disposed) return;
                _mediaPlayer.Source = source;
                _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, request.StartSeconds));
                _mediaPlayer.Play();
            }
            catch (Exception ex)
            {
                Log.Error("Native media source could not be opened", ex);
                RaiseState("error", ex.Message);
                RaiseEnded("error", ex.Message);
            }
        }

        public void Stop()
        {
            if (_mediaPlayer == null) return;
            try { _mediaPlayer.Pause(); _mediaPlayer.Source = null; }
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
                try { _mediaPlayer.Pause(); _mediaPlayer.Source = null; _mediaPlayer.Dispose(); }
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
            RaiseEnded("eof", null);
        }
        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            var message = string.IsNullOrWhiteSpace(args.ErrorMessage) ? "Windows Media Foundation could not play this media." : args.ErrorMessage;
            Log.Write("Native playback failed (" + args.Error + "): " + message);
            if (_view != null) _view.Opacity = 0;
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
