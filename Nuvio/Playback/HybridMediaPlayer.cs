using System;
using System.Collections.Generic;
using LibVLCSharp.Platforms.Windows;
using Nuvio.Diagnostics;
using Windows.Data.Json;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;

namespace Nuvio.Playback
{
    /// <summary>
    /// Plays streams with FFmpegInteropX first and falls back to LibVLC for the
    /// same source when FFmpeg cannot play it. FFmpeg is the primary engine
    /// (it decodes the containers and codecs WMF rejects — MKV/HEVC/AV1/DTS —
    /// with D3D11 hardware decoding, straight into a plain MediaPlayer); VLC
    /// is the fallback for the sources FFmpeg rejects.
    ///
    /// The web app sees a single player: both engines raise the same "player.*"
    /// events, and a switch is announced with a "player.engine" event so the page
    /// can tell the user playback moved to VLC. A failed engine is remembered per
    /// source URL, so a source FFmpeg already rejected starts with VLC next time,
    /// and a source both engines rejected is not opened again in this session.
    /// LibVLC is only started when a fallback is actually needed, so consoles
    /// that play everything with FFmpeg never pay VLC's memory cost.
    /// NativeMediaPlayer (WMF) is no longer on the playback path; it stays in
    /// the project, unused, as a reference for the MediaPlayer event plumbing.
    /// </summary>
    internal sealed class HybridMediaPlayer : IDisposable
    {
        private const int MaxRememberedUrls = 200;

        private enum ActiveEngine { None, Ffmpeg, Vlc }

        private readonly FfmpegPlayer _primary = new FfmpegPlayer();
        private VlcPlayer _vlc;
        private VideoView _vlcView;
        private CoreDispatcher _dispatcher;
        private string[] _vlcSwapChainOptions;
        private bool _vlcUnavailable;
        private bool _disposed;

        private ActiveEngine _active = ActiveEngine.None;
        private OpenRequest _currentRequest;
        private string _pageId;
        private double _lastTimeSeconds;
        private bool _fellBack;

        private readonly HashSet<string> _primaryFailedUrls = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _bothFailedUrls = new HashSet<string>(StringComparer.Ordinal);

        public event Action<string, JsonObject> EventRaised;

        public string Version { get; private set; } = "FFmpegInteropX + LibVLC";

        /// <summary>Binds the FFmpeg surface now; the VLC surface is bound when VLC starts.</summary>
        public void Initialize(MediaPlayerElement mediaView, VideoView vlcView, CoreDispatcher dispatcher)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HybridMediaPlayer));
            _vlcView = vlcView;
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _primary.EventRaised += OnPrimaryEvent;
            if (mediaView != null) _primary.Initialize(mediaView);
            else _primary.SetUnavailable("the media surface could not be created");
            if (_vlcView != null) _vlcView.Opacity = 0;
        }

        /// <summary>Swap-chain options from the VLC VideoView's Initialized event.</summary>
        public void SetVlcSwapChainOptions(string[] options)
        {
            _vlcSwapChainOptions = options;
        }

        /// <summary>Marks the FFmpeg surface unusable (its creation failed).</summary>
        public void SetUnavailable(string reason)
        {
            // Only the primary surface is affected: a later open can still fall
            // back to VLC, whose surface is a separate control.
            _primary.SetUnavailable(reason);
        }

        /// <summary>Marks the VLC surface unusable (its creation failed).</summary>
        public void SetVlcUnavailable(string reason)
        {
            _vlcUnavailable = true;
            if (_vlc != null) _vlc.SetUnavailable(reason);
        }

        public void Open(OpenRequest request)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HybridMediaPlayer));
            _currentRequest = request;
            _pageId = request == null ? null : request.Id;
            _lastTimeSeconds = request == null ? 0 : Math.Max(0, request.StartSeconds);
            _fellBack = false;
            if (request == null || string.IsNullOrEmpty(request.Url))
            {
                OpenWithPrimary(request);
                return;
            }
            if (_bothFailedUrls.Contains(request.Url))
            {
                Log.Write("Skipping source both players already failed to play (host " + HostOf(request.Url) + ")");
                RaiseState("error", "Neither FFmpeg nor VLC could play this stream.");
                RaiseEnded("error", "Neither FFmpeg nor VLC could play this stream.");
                return;
            }
            if (_primaryFailedUrls.Contains(request.Url))
            {
                Log.Write("FFmpeg previously failed for this source; starting with VLC (host " + HostOf(request.Url) + ")");
                OpenWithVlc(request, _lastTimeSeconds, true);
                return;
            }
            OpenWithPrimary(request);
        }

        private void OpenWithPrimary(OpenRequest request)
        {
            _active = ActiveEngine.Ffmpeg;
            if (_vlc != null)
            {
                try { _vlc.Stop(); } catch (Exception ex) { Log.Error("Stopping VLC for an FFmpeg open failed", ex); }
            }
            _primary.Open(request);
        }

        private void OpenWithVlc(OpenRequest request, double startSeconds, bool announce)
        {
            if (request == null || string.IsNullOrEmpty(request.Url))
            {
                RaiseState("error", "This player accepts direct HTTP or HTTPS media links.");
                RaiseEnded("error", "This player accepts direct HTTP or HTTPS media links.");
                return;
            }
            if (_vlcUnavailable)
            {
                Log.Write("VLC is unavailable; cannot fall back for host " + HostOf(request.Url));
                RaiseState("error", "FFmpeg could not play this stream, and VLC is unavailable.");
                RaiseEnded("error", "FFmpeg could not play this stream, and VLC is unavailable.");
                return;
            }
            _active = ActiveEngine.Vlc;
            EnsureVlc();
            var vlcRequest = new OpenRequest
            {
                Id = request.Id,
                Url = request.Url,
                StartSeconds = Math.Max(0, startSeconds),
                HardwareDecoding = request.HardwareDecoding,
            };
            if (request.Headers != null)
            {
                foreach (var header in request.Headers) vlcRequest.Headers[header.Key] = header.Value;
            }
            if (announce)
            {
                RaiseEngine("LibVLC", "Playing with VLC.");
            }
            _vlc.Open(vlcRequest);
        }

        private void EnsureVlc()
        {
            if (_vlc != null) return;
            _vlc = new VlcPlayer(_dispatcher);
            _vlc.EventRaised += OnVlcEvent;
            _vlc.MediaPlayerCreated += mediaPlayer =>
            {
                if (_vlcView != null) _vlcView.MediaPlayer = mediaPlayer;
            };
            _vlc.VideoVisibleChanged += visible =>
            {
                if (_vlcView != null) _vlcView.Opacity = visible ? 1 : 0;
            };
            // LibVLC starts on a worker thread inside Initialize; Open() waits
            // for it (up to 20 s) before giving up, so a lazy start here only
            // costs the fallback a short delay the first time it is needed.
            _vlc.Initialize(_vlcSwapChainOptions ?? new string[0]);
            Log.Write("LibVLC started as the fallback player");
        }

        private void OnPrimaryEvent(string type, JsonObject payload)
        {
            if (_disposed || _active != ActiveEngine.Ffmpeg) return;
            if (type == "player.time") UpdateLastTime(payload);
            if (type == "player.state" && GetString(payload, "state") == "error") return; // ended follows with the kind
            if (type == "player.ended" && GetString(payload, "reason") == "error")
            {
                TryFallbackToVlc(payload);
                return;
            }
            EventRaised?.Invoke(type, payload);
        }

        private void OnVlcEvent(string type, JsonObject payload)
        {
            if (_disposed || _active != ActiveEngine.Vlc) return;
            if (type == "player.time") UpdateLastTime(payload);
            if (type == "player.ended" && GetString(payload, "reason") == "error")
            {
                var url = _currentRequest == null ? null : _currentRequest.Url;
                if (!string.IsNullOrEmpty(url))
                {
                    _bothFailedUrls.Add(url);
                    CapRememberedUrls();
                }
            }
            EventRaised?.Invoke(type, payload);
        }

        private void TryFallbackToVlc(JsonObject primaryEnded)
        {
            // Nothing to fall back to without a URL (e.g. an empty link the
            // primary player already rejected): report the failure as-is.
            if (_currentRequest == null || string.IsNullOrEmpty(_currentRequest.Url))
            {
                EventRaised?.Invoke("player.ended", primaryEnded);
                return;
            }
            if (_fellBack)
            {
                EventRaised?.Invoke("player.ended", primaryEnded);
                return;
            }
            _fellBack = true;
            var url = _currentRequest == null ? null : _currentRequest.Url;
            if (!string.IsNullOrEmpty(url))
            {
                _primaryFailedUrls.Add(url);
                CapRememberedUrls();
            }
            var kind = GetString(primaryEnded, "errorKind");
            Log.Write("FFmpeg playback failed (" + (kind ?? "unknown") + ") for host " + HostOf(url) +
                "; falling back to VLC at " + Math.Round(_lastTimeSeconds) + "s");
            if (_vlcUnavailable)
            {
                EventRaised?.Invoke("player.ended", primaryEnded);
                return;
            }
            RaiseEngine("LibVLC", "FFmpeg could not play this source. Trying VLC…");
            OpenWithVlc(_currentRequest, _lastTimeSeconds, false);
        }

        private void CapRememberedUrls()
        {
            if (_primaryFailedUrls.Count > MaxRememberedUrls) _primaryFailedUrls.Clear();
            if (_bothFailedUrls.Count > MaxRememberedUrls) _bothFailedUrls.Clear();
        }

        private void UpdateLastTime(JsonObject payload)
        {
            var value = payload.GetNamedValue("time", JsonValue.CreateNullValue());
            if (value.ValueType == JsonValueType.Number) _lastTimeSeconds = Math.Max(0, value.GetNumber());
        }

        private void RaiseEngine(string engine, string message)
        {
            var payload = new JsonObject();
            payload.SetNamedValue("engine", JsonValue.CreateStringValue(engine));
            payload.SetNamedValue("message", JsonValue.CreateStringValue(message));
            Raise("player.engine", payload);
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

        private static string GetString(JsonObject payload, string name)
        {
            var value = payload.GetNamedValue(name, JsonValue.CreateNullValue());
            return value.ValueType == JsonValueType.String ? value.GetString() : null;
        }

        // Host only, never the path or query: stream URLs may carry tokens.
        private static string HostOf(string url)
        {
            Uri uri;
            return url != null && Uri.TryCreate(url, UriKind.Absolute, out uri) ? uri.Host : "unknown";
        }

        public void Stop()
        {
            _active = ActiveEngine.None;
            _currentRequest = null;
            _pageId = null;
            _primary.Stop();
            if (_vlc != null)
            {
                try { _vlc.Stop(); } catch (Exception ex) { Log.Error("Stopping VLC failed", ex); }
            }
        }

        public void SetPaused(bool paused)
        {
            if (_active == ActiveEngine.Vlc && _vlc != null) _vlc.SetPaused(paused);
            else _primary.SetPaused(paused);
        }

        public void TogglePause()
        {
            if (_active == ActiveEngine.Vlc && _vlc != null) _vlc.TogglePause();
            else _primary.TogglePause();
        }

        public void Seek(double seconds)
        {
            if (_active == ActiveEngine.Vlc && _vlc != null) _vlc.Seek(seconds);
            else _primary.Seek(seconds);
        }

        public void SetVolume(int volume)
        {
            if (_active == ActiveEngine.Vlc && _vlc != null) _vlc.SetVolume(volume);
            else _primary.SetVolume(volume);
        }

        public void SetMute(bool muted)
        {
            if (_active == ActiveEngine.Vlc && _vlc != null) _vlc.SetMute(muted);
            else _primary.SetMute(muted);
        }

        public void SetRate(double rate)
        {
            if (_active == ActiveEngine.Vlc && _vlc != null) _vlc.SetRate(rate);
            else _primary.SetRate(rate);
        }

        public void SetAudioTrack(int trackId)
        {
            if (_active == ActiveEngine.Vlc && _vlc != null) _vlc.SetAudioTrack(trackId);
            else _primary.SetAudioTrack(trackId);
        }

        public void SetSubtitleTrack(int trackId)
        {
            if (_active == ActiveEngine.Vlc && _vlc != null) _vlc.SetSubtitleTrack(trackId);
            else _primary.SetSubtitleTrack(trackId);
        }

        public void SetSubtitleDelay(long milliseconds)
        {
            if (_active == ActiveEngine.Vlc && _vlc != null) _vlc.SetSubtitleDelay(milliseconds);
            else _primary.SetSubtitleDelay(milliseconds);
        }

        public void SetAudioDelay(long milliseconds)
        {
            if (_active == ActiveEngine.Vlc && _vlc != null) _vlc.SetAudioDelay(milliseconds);
            else _primary.SetAudioDelay(milliseconds);
        }

        public void SetAspect(string mode)
        {
            if (_active == ActiveEngine.Vlc && _vlc != null) _vlc.SetAspect(mode);
            else _primary.SetAspect(mode);
        }

        public void SetViewSize(double width, double height)
        {
            _primary.SetViewSize(width, height);
            if (_vlc != null) _vlc.SetViewSize(width, height);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _active = ActiveEngine.None;
            _primary.EventRaised -= OnPrimaryEvent;
            _primary.Dispose();
            if (_vlc != null)
            {
                _vlc.EventRaised -= OnVlcEvent;
                _vlc.Dispose();
                _vlc = null;
            }
            _vlcView = null;
        }
    }
}
