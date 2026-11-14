using System;
using System.Collections.Generic;
using Nuvio.Core;
using Nuvio.Diagnostics;
using Nuvio.Models;
using Nuvio.Playback;
using Windows.Data.Json;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Nuvio.Services
{
    /// <summary>One embedded audio/subtitle track from a "player.tracks" event.</summary>
    internal sealed class PlayerTrack
    {
        public int Id { get; set; }
        public string Lang { get; set; }
        public string Name { get; set; }
        public string Title { get; set; }
        public string Codec { get; set; }
    }

    /// <summary>Embedded tracks for the current source, with the selected ids.</summary>
    internal sealed class PlayerTracks
    {
        public List<PlayerTrack> Audio { get; set; }
        public List<PlayerTrack> Subtitles { get; set; }
        public int AudioId { get; set; }
        public int SubtitleId { get; set; }
    }

    internal sealed class PlayerTimeEventArgs : EventArgs
    {
        public double Time { get; }
        public double Duration { get; }
        public PlayerTimeEventArgs(double time, double duration) { Time = time; Duration = duration; }
    }

    internal sealed class PlayerEndedEventArgs : EventArgs
    {
        public string Reason { get; }
        public string Message { get; }
        public PlayerEndedEventArgs(string r, string m) { Reason = r; Message = m; }
    }

    internal sealed class PlayerEngineEventArgs : EventArgs
    {
        public string Engine { get; }
        public string Message { get; }
        public PlayerEngineEventArgs(string e, string m) { Engine = e; Message = m; }
    }

    /// <summary>
    /// Typed adapter over HybridMediaPlayer.EventRaised (architecture §10.2): the
    /// JSON "player.*" events become typed C# events on the UI thread. The engines
    /// are a black box and are never touched here.
    ///
    /// HybridMediaPlayer swallows a primary "player.state" 'error' and re-raises
    /// "player.ended" error after a VLC fallback attempt, so an 'error' state is
    /// never surfaced as fatal here either — it is ignored outright. Events whose
    /// "id" is not the current open id are stale opens and are ignored too.
    /// </summary>
    internal sealed class PlayerService
    {
        private readonly HybridMediaPlayer _player;
        private readonly AppSettings _settings;
        private readonly CoreDispatcher _dispatcher;

        private string _state = "stopped";
        private double _currentTime;
        private double _duration;
        private double _rate = 1;
        private string _aspect = "fit";
        private bool _isReady;
        private DateTime _lastTimeAt = DateTime.UtcNow;
        private string _currentOpenId;

        public PlayerService(HybridMediaPlayer player, AppSettings settings)
        {
            _player = player ?? throw new ArgumentNullException(nameof(player));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _dispatcher = Window.Current != null ? Window.Current.Dispatcher : null;
            _player.EventRaised += OnPlayerEvent;
        }

        public string State { get { return _state; } }

        /// <summary>
        /// While playing, extrapolates lastTime + (now - lastTimeAt) * rate (capped
        /// at the duration), mirroring the web player.js CurrentTime().
        /// </summary>
        public double CurrentTime
        {
            get
            {
                if (_state == "playing")
                {
                    var estimated = _currentTime + (DateTime.UtcNow - _lastTimeAt).TotalSeconds * _rate;
                    if (_duration > 0 && estimated > _duration) estimated = _duration;
                    return Math.Max(0, estimated);
                }
                return _currentTime;
            }
        }

        public double Duration { get { return _duration; } }
        public double Rate { get { return _rate; } }
        public string Aspect { get { return _aspect; } }
        public bool IsReady { get { return _isReady; } }
        public bool IsPlaying { get { return State == "playing"; } }

        public event Action<string> StateChanged;
        public event EventHandler<PlayerTimeEventArgs> TimeUpdated;
        public event Action<double> DurationChanged;
        public event Action<int> BufferingChanged;
        public event Action<PlayerTracks> TracksChanged;
        public event EventHandler Ready;
        public event EventHandler<PlayerEndedEventArgs> Ended;
        public event EventHandler<PlayerEngineEventArgs> EngineSwitched;

        public void Open(StreamItem stream, double startSeconds)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            var request = new OpenRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                Url = stream.Url,
                StartSeconds = Math.Max(0, startSeconds),
                Headers = StreamAnalysis.StreamHeaders(stream),
                HardwareDecoding = _settings.GetBool("hardwareDecoding", true),
            };
            _currentOpenId = request.Id;
            _currentTime = request.StartSeconds;
            _duration = 0;
            _isReady = false;
            _lastTimeAt = DateTime.UtcNow;
            _player.Open(request);
        }

        public void Stop()
        {
            _currentOpenId = null;
            _isReady = false;
            _state = "stopped";
            _player.Stop();
        }

        public void SetPaused(bool paused)
        {
            _player.SetPaused(paused);
        }

        public void TogglePause()
        {
            _player.TogglePause();
        }

        public void Pause()
        {
            SetPaused(true);
        }

        public void Seek(double seconds)
        {
            _player.Seek(Math.Max(0, seconds));
        }

        public void SetRate(double rate)
        {
            if (rate <= 0) return;
            _rate = rate;
            _player.SetRate(rate);
        }

        public void SetAudioTrack(int id)
        {
            _player.SetAudioTrack(id);
        }

        public void SetSubtitleTrack(int id)
        {
            _player.SetSubtitleTrack(id);
        }

        public void SetSubtitleDelay(int milliseconds)
        {
            _player.SetSubtitleDelay(milliseconds);
        }

        public void SetAspect(string aspect)
        {
            _aspect = string.IsNullOrEmpty(aspect) ? "fit" : aspect;
            _player.SetAspect(_aspect);
        }

        public void ReportPlayback(PlaybackEventArgs args)
        {
            AppEvents.RaisePlayback(args);
        }

        private void OnPlayerEvent(string type, JsonObject payload)
        {
            // Events arrive on background threads (FFmpeg) or the UI thread (VLC):
            // parse and raise everything on the UI thread, fire-and-forget.
            // "player.tracks" alone is regenerated here: its payload is the only
            // one carrying programmatically built JsonArray values, and reading
            // those cross-thread threw InvalidCastException in ParseTracks.
            if (string.Equals(type, "player.tracks", StringComparison.Ordinal) && payload != null)
                payload = RegenerateTracksPayload(payload);
            PostToUi(() => HandlePlayerEvent(type, payload));
        }

        /// <summary>
        /// Rebuilds a "player.tracks" payload from its JSON text so the UI thread
        /// reads a freshly parsed tree instead of the programmatically built one:
        /// FfmpegPlayer builds the audio/subtitles arrays with new JsonArray() +
        /// SetNamedValue on a background thread, and reading them cross-thread via
        /// GetNamedValue threw InvalidCastException ("Player event dispatch failed",
        /// one per playback), so TracksChanged was never raised. Stringify runs
        /// here on the arriving thread: the creating thread for FFmpeg, but the
        /// UI thread for VLC (its arrays are built on the worker thread while the
        /// payload itself is raised inside RunOnUi) — a failure there keeps the
        /// original payload, i.e. the old behaviour, with no regression. The
        /// parsed tree is the same shape the Stremio/Meta/TMDB readers already
        /// consume without errors. The field set is preserved exactly — both
        /// engines tag every event with "id" — so the "id" stale-open filter in
        /// HandlePlayerEvent behaves the same as before. On any failure the
        /// original payload is kept, so behaviour is unchanged.
        /// </summary>
        private static JsonObject RegenerateTracksPayload(JsonObject payload)
        {
            try
            {
                var regenerated = JsonHelpers.ParseObject(payload.Stringify());
                if (regenerated != null) return regenerated;
                Log.Write("Regenerating player.tracks payload failed: parsed payload was null; keeping the original payload.");
            }
            catch (Exception ex)
            {
                Log.Error("Regenerating player.tracks payload failed", ex);
            }
            return payload;
        }

        private void PostToUi(Action action)
        {
            try
            {
                var dispatcher = _dispatcher;
                if (dispatcher == null || dispatcher.HasThreadAccess)
                {
                    action();
                    return;
                }
                var ignored = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    try { action(); }
                    catch (Exception ex) { Log.Error("Player event dispatch failed", ex); }
                });
            }
            catch (Exception ex)
            {
                Log.Error("Player event dispatch failed", ex);
            }
        }

        private void HandlePlayerEvent(string type, JsonObject payload)
        {
            if (payload != null && _currentOpenId != null)
            {
                var id = JsonHelpers.GetString(payload, "id");
                if (id != null && !string.Equals(id, _currentOpenId, StringComparison.Ordinal)) return;
            }
            switch (type)
            {
                case "player.state":
                    HandleState(JsonHelpers.GetString(payload, "state"));
                    break;
                case "player.time":
                    HandleTime(payload);
                    break;
                case "player.duration":
                    _duration = Math.Max(0, JsonHelpers.GetDouble(payload, "duration") ?? 0);
                    RaiseDurationChanged(_duration);
                    break;
                case "player.buffering":
                    RaiseBufferingChanged((int)Math.Round(JsonHelpers.GetDouble(payload, "percent") ?? 0));
                    break;
                case "player.tracks":
                    RaiseTracksChanged(ParseTracks(payload));
                    break;
                case "player.ready":
                    _isReady = true;
                    RaiseReady();
                    break;
                case "player.ended":
                    _state = "stopped";
                    _isReady = false;
                    RaiseEnded(new PlayerEndedEventArgs(
                        JsonHelpers.GetString(payload, "reason"),
                        JsonHelpers.GetString(payload, "message")));
                    break;
                case "player.engine":
                    RaiseEngineSwitched(new PlayerEngineEventArgs(
                        JsonHelpers.GetString(payload, "engine"),
                        JsonHelpers.GetString(payload, "message")));
                    break;
                default:
                    // "player.video" and anything else: subscribed, unused (as in the web player).
                    break;
            }
        }

        private void HandleState(string state)
        {
            if (string.IsNullOrEmpty(state)) return;
            // 'error' is swallowed by the hybrid player (a fallback or an ended
            // error follows); never surface it as fatal here.
            if (string.Equals(state, "error", StringComparison.Ordinal)) return;
            _state = state;
            if (state == "playing" || state == "paused") _lastTimeAt = DateTime.UtcNow;
            RaiseStateChanged(state);
        }

        private void HandleTime(JsonObject payload)
        {
            var time = JsonHelpers.GetDouble(payload, "time") ?? 0;
            var duration = JsonHelpers.GetDouble(payload, "duration") ?? 0;
            _currentTime = Math.Max(0, time);
            _lastTimeAt = DateTime.UtcNow;
            if (duration > 0) _duration = duration;
            RaiseTimeUpdated(new PlayerTimeEventArgs(_currentTime, _duration));
        }

        /// <summary>
        /// Both engines send audio[]/subtitles[] of {id, lang, name, title, codec}
        /// plus audioId/subtitleId — but only VLC sets "title" (its track
        /// description); FFmpeg sets "name" only. "selected" is the fallback
        /// when an id is missing.
        /// </summary>
        private static PlayerTracks ParseTracks(JsonObject payload)
        {
            var audio = ParseTrackList(JsonHelpers.GetArray(payload, "audio"));
            var subtitles = ParseTrackList(JsonHelpers.GetArray(payload, "subtitles"));
            var audioId = JsonHelpers.GetInt(payload, "audioId")
                ?? SelectedId(JsonHelpers.GetArray(payload, "audio")) ?? -1;
            var subtitleId = JsonHelpers.GetInt(payload, "subtitleId")
                ?? SelectedId(JsonHelpers.GetArray(payload, "subtitles")) ?? -1;
            return new PlayerTracks
            {
                Audio = audio,
                Subtitles = subtitles,
                AudioId = audioId,
                SubtitleId = subtitleId,
            };
        }

        private static List<PlayerTrack> ParseTrackList(JsonArray array)
        {
            var result = new List<PlayerTrack>();
            if (array == null) return result;
            foreach (var item in array)
            {
                var obj = JsonHelpers.GetObject(item);
                if (obj == null) continue;
                result.Add(new PlayerTrack
                {
                    Id = JsonHelpers.GetInt(obj, "id") ?? -1,
                    Lang = JsonHelpers.GetString(obj, "lang"),
                    Name = JsonHelpers.GetString(obj, "name"),
                    Title = JsonHelpers.GetString(obj, "title"),
                    Codec = JsonHelpers.GetString(obj, "codec"),
                });
            }
            return result;
        }

        private static int? SelectedId(JsonArray array)
        {
            if (array == null) return null;
            foreach (var item in array)
            {
                var obj = JsonHelpers.GetObject(item);
                if (obj == null) continue;
                if (JsonHelpers.GetBool(obj, "selected") == true) return JsonHelpers.GetInt(obj, "id");
            }
            return null;
        }

        private void RaiseStateChanged(string state)
        {
            var handler = StateChanged;
            if (handler != null) handler(state);
        }

        private void RaiseTimeUpdated(PlayerTimeEventArgs args)
        {
            var handler = TimeUpdated;
            if (handler != null) handler(this, args);
        }

        private void RaiseDurationChanged(double duration)
        {
            var handler = DurationChanged;
            if (handler != null) handler(duration);
        }

        private void RaiseBufferingChanged(int percent)
        {
            var handler = BufferingChanged;
            if (handler != null) handler(percent);
        }

        private void RaiseTracksChanged(PlayerTracks tracks)
        {
            var handler = TracksChanged;
            if (handler != null) handler(tracks);
        }

        private void RaiseReady()
        {
            var handler = Ready;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseEnded(PlayerEndedEventArgs args)
        {
            var handler = Ended;
            if (handler != null) handler(this, args);
        }

        private void RaiseEngineSwitched(PlayerEngineEventArgs args)
        {
            var handler = EngineSwitched;
            if (handler != null) handler(this, args);
        }
    }
}
