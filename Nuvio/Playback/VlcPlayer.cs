using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Nuvio.Diagnostics;
using Windows.Data.Json;
using Windows.UI.Core;

namespace Nuvio.Playback
{
    // OpenRequest is defined once, in NativeMediaPlayer.cs; both engines share it.

    /// <summary>
    /// Plays streams with LibVLC for the Nuvio web app. The page sends commands through
    /// <see cref="Bridge.AppBridge"/> and gets "player.*" events back (see docs/bridge.md).
    /// </summary>
    /// <remarks>
    /// Public members must be called on the UI thread, and events are raised on it. LibVLC is only
    /// touched on a private serial worker, because calls like Stop() block until VLC's threads exit,
    /// and LibVLC must never be called from inside one of its own event callbacks.
    /// </remarks>
    internal sealed class VlcPlayer : IDisposable
    {
        private const int NetworkCachingMs = 3000;
        private const int MaxReconnects = 5;
        // How far from the end a stop has to be before it counts as a dropped connection.
        private const long EarlyEndToleranceMs = 60000;
        private const string PlaybackErrorMessage = "VLC could not play this stream.";
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan TimeReportInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan ReadyFallbackDelay = TimeSpan.FromSeconds(2);
        private static readonly string[] AllowedSchemes = { "http", "https", "rtsp", "rtmp", "rtp", "udp", "mms", "ftp", "smb" };

        private readonly CoreDispatcher _dispatcher;
        private readonly object _queueLock = new object();
        private Task _queue = Task.CompletedTask;

        // UI thread state.
        private long _loadSeq;
        private string _pageId;
        private bool _readySent;
        private bool _durationSent;
        private int _lastBufferingPercent = -1;
        private DateTime _lastTimeReport = DateTime.MinValue;
        private DateTime _lastVlcStatsLog = DateTime.MinValue;
        private bool _vlcBuffering;

        // Set once LibVLC has started or failed to; _libVlc, _mediaPlayer and _initError are only
        // written before that.
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private int _initStarted;
        private LibVLC _libVlc;
        private MediaPlayer _mediaPlayer;
        private string _initError;

        // Worker state.
        private OpenRequest _request;
        private HttpMediaInput _input;
        private int _vlcLogLines;
        private bool _hasPlayed;
        private bool _inputStarted;
        private int _reconnects;
        private bool _wantPaused;
        private int _wantVolume = 100;
        private bool _wantMute;
        private float _wantRate = 1f;
        private int? _wantAudioTrack;
        private int? _wantSpuTrack;
        private long _pendingSeekMs = -1;
        private long _spuDelayUs;
        private long _audioDelayUs;
        private string _aspectMode = "fit";
        private int _viewWidth;
        private int _viewHeight;

        // Swap-chain options from the VideoView's Initialized event. They must be passed to the
        // LibVLC constructor so VLC renders into the VideoView's swap chain instead of creating
        // its own D3D11 device — on Xbox its own device fails with DXGI_ERROR_DEVICE_REMOVED
        // (hr=0x887A0005) and playback is a black screen. The options can arrive after LibVLC
        // has already started (see ApplySwapChainOptions), so the last real set is kept here.
        private readonly object _swapChainLock = new object();
        private string[] _swapChainOptions;
        private bool _swapChainOptionsApplied;
        private bool _coreInitialized;
        private bool _unavailable;
        private bool _disposed;

        // Shared between VLC callback threads, the worker and the UI thread.
        private long _workerLoadSeq;
        private long _lastTimeMs;
        private long _lengthMs;
        private int _trackRefreshQueued;

        public VlcPlayer(CoreDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        /// <summary>Raised on the UI thread with an event type such as "player.time" and its payload.</summary>
        public event Action<string, JsonObject> EventRaised;

        /// <summary>Raised once LibVLC exists so the VideoView can be attached to it.</summary>
        public event Action<MediaPlayer> MediaPlayerCreated;

        /// <summary>Raised when frames of the current stream start or stop being shown.</summary>
        public event Action<bool> VideoVisibleChanged;

        /// <summary>The LibVLC version once it has started, otherwise null.</summary>
        public string Version { get; private set; }

        public void Initialize(string[] swapChainOptions)
        {
            var options = CopySwapChainOptions(swapChainOptions);
            lock (_swapChainLock)
            {
                // Prefer real options wherever they came from: EnsureVlc can call this with an
                // empty array when the VideoView has not fired Initialized yet, while
                // ApplySwapChainOptions may already have stored the real ones.
                if (options != null)
                {
                    _swapChainOptions = options;
                }
                else
                {
                    options = _swapChainOptions;
                }
            }
            if (Interlocked.Exchange(ref _initStarted, 1) == 1)
            {
                return;
            }
            // Not on the worker queue: a load that arrives first waits on the worker for this.
            Task.Run(() => CreateLibVlc(options));
        }

        /// <summary>
        /// Applies the VideoView's swap-chain options when they arrive after LibVLC has already
        /// started. Called on the UI thread (from HybridMediaPlayer.SetVlcSwapChainOptions).
        /// If LibVLC has not started yet the options are just stored for Initialize. If it
        /// started without real options and nothing is playing, LibVLC is re-created with them
        /// on the worker queue; the new MediaPlayer is published through MediaPlayerCreated so
        /// the VideoView re-attaches. If playback is active the options are only stored: tearing
        /// LibVLC down mid-stream would break the playing stream, and the re-create happens
        /// at the next Stop or Open instead (see MaybeRecreateWithSwapChainOptions).
        /// </summary>
        public void ApplySwapChainOptions(string[] options)
        {
            var copy = CopySwapChainOptions(options);
            if (copy == null || _disposed)
            {
                return;
            }
            lock (_swapChainLock)
            {
                _swapChainOptions = copy;
            }
            if (Interlocked.CompareExchange(ref _initStarted, 0, 0) == 0)
            {
                // Not started yet; Initialize picks the stored options up.
                return;
            }
            Enqueue(() => ApplySwapChainOptionsOnWorker());
        }

        // Worker. Waits out an in-flight first start (it runs on its own task, not this queue,
        // so waiting here cannot deadlock), then re-creates LibVLC only while idle.
        private void ApplySwapChainOptionsOnWorker()
        {
            if (_disposed)
            {
                return;
            }
            if (!_ready.Wait(StartupTimeout))
            {
                Log.Write("LibVLC did not finish starting; keeping the swap-chain options for the next start");
                return;
            }
            lock (_swapChainLock)
            {
                if (_swapChainOptionsApplied)
                {
                    return;
                }
            }
            if (_request != null || _hasPlayed)
            {
                Log.Write("LibVLC swap-chain options arrived while VLC is playing; keeping them for the next start");
                return;
            }
            MaybeRecreateWithSwapChainOptions();
        }

        // Worker. Re-creates LibVLC with the stored swap-chain options, if it was started
        // without them. Call only after playback has been stopped: StopPlayer has already run
        // in Stop/Open, and ApplySwapChainOptionsOnWorker checks for active playback itself.
        // The new MediaPlayer is published through MediaPlayerCreated (inside CreateLibVlc),
        // so the VideoView re-attaches; volume/mute/rate/track preferences live in fields
        // and are re-applied when the next stream plays.
        private void MaybeRecreateWithSwapChainOptions()
        {
            if (_disposed || _unavailable)
            {
                return;
            }
            string[] stored;
            lock (_swapChainLock)
            {
                stored = _swapChainOptions;
                if (stored == null || _swapChainOptionsApplied)
                {
                    return;
                }
            }
            Log.Write("Recreating LibVLC with the video surface swap-chain options");
            StopPlayer();
            if (_libVlc != null)
            {
                _libVlc.Log -= OnLibVlcLog;
            }
            _mediaPlayer?.Dispose();
            _mediaPlayer = null;
            _libVlc?.Dispose();
            _libVlc = null;
            _initError = null;
            Interlocked.Exchange(ref _vlcLogLines, 0);
            // _ready stays set and _initStarted stays 1: creation below runs synchronously on
            // this queue, so any Open queued after this sees the new instance without waiting.
            CreateLibVlc(stored);
        }

        // Runs on a worker thread: Initialize's Task.Run, or the serial worker queue for a
        // re-create (where it runs synchronously). Creates LibVLC with the given swap-chain
        // options and publishes the new MediaPlayer through MediaPlayerCreated.
        private void CreateLibVlc(string[] swapChainOptions)
        {
            var options = new List<string>(swapChainOptions ?? new string[0])
            {
                "--no-video-title-show",
                "--no-osd",
                "--no-sub-autodetect-file",
                "--http-reconnect",
                "--network-caching=" + NetworkCachingMs,
            };
            try
            {
                if (_disposed)
                {
                    return;
                }
                Log.Write("Starting LibVLC");
                if (!_coreInitialized)
                {
                    LibVLCSharp.Shared.Core.Initialize();
                    _coreInitialized = true;
                }
#if DEBUG
                _libVlc = new LibVLC(true, options.ToArray());
#else
                _libVlc = new LibVLC(false, options.ToArray());
#endif
                _mediaPlayer = new MediaPlayer(_libVlc);
                Subscribe(_mediaPlayer);
                _libVlc.Log += OnLibVlcLog;
                lock (_swapChainLock)
                {
                    _swapChainOptionsApplied = swapChainOptions != null && swapChainOptions.Length > 0;
                }
                Log.Write("LibVLC " + _libVlc.Version + " ready");
            }
            catch (Exception ex)
            {
                Log.Error("LibVLC failed to start", ex);
                _initError = ex.Message;
                _mediaPlayer = null;
                _libVlc = null;
                lock (_swapChainLock)
                {
                    _swapChainOptionsApplied = false;
                }
            }
            finally
            {
                _ready.Set();
            }
            var mediaPlayer = _mediaPlayer;
            if (mediaPlayer != null)
            {
                var version = _libVlc.Version;
                RunOnUi(() =>
                {
                    Version = version;
                    MediaPlayerCreated?.Invoke(mediaPlayer);
                });
            }
        }

        // Defensive copy of swap-chain options; null when there are no real options to apply.
        // Option values are never logged.
        private static string[] CopySwapChainOptions(string[] options)
        {
            if (options == null || options.Length == 0)
            {
                return null;
            }
            var copy = new string[options.Length];
            Array.Copy(options, copy, options.Length);
            return copy;
        }

        /// <summary>Marks VLC as unusable so loads fail with a message instead of waiting.</summary>
        public void SetUnavailable(string reason)
        {
            // Set even when LibVLC already started: the surface is gone, so a later
            // swap-chain re-create must not resurrect VLC against it.
            _unavailable = true;
            if (Interlocked.Exchange(ref _initStarted, 1) == 1)
            {
                return;
            }
            _initError = reason;
            _ready.Set();
        }

        public void Open(OpenRequest request)
        {
            BeginTransition();
            var seq = _loadSeq;
            _pageId = request.Id;
            if (string.IsNullOrEmpty(request.Url) || !IsAllowedUrl(request.Url))
            {
                Log.Write("Can't play " + Log.DescribeUrl(request.Url) + ": unsupported link");
                Enqueue(() =>
                {
                    StopPlayer();
                    Interlocked.Exchange(ref _workerLoadSeq, seq);
                    _request = null;
                    _hasPlayed = false;
                });
                RaiseEnded("error", "This kind of link can't be played.");
                return;
            }

            Log.Write("Open " + Log.DescribeUrl(request.Url) + (request.StartSeconds > 0 ? " at " + Math.Round(request.StartSeconds) + "s" : ""));
            RaiseState("opening", null);
            Enqueue(() =>
            {
                if (!_ready.Wait(StartupTimeout) || _mediaPlayer == null)
                {
                    ReportStartFailure(seq);
                    return;
                }
                StopPlayer();
                // A swap-chain re-create deferred while a previous stream was playing takes
                // effect here, before the new stream starts on the fresh instance.
                MaybeRecreateWithSwapChainOptions();
                if (_mediaPlayer == null)
                {
                    ReportStartFailure(seq);
                    return;
                }
                Interlocked.Exchange(ref _workerLoadSeq, seq);
                Interlocked.Exchange(ref _lengthMs, 0);
                _request = request;
                _hasPlayed = false;
                _reconnects = 0;
                _pendingSeekMs = -1;
                _wantPaused = false;
                _wantAudioTrack = null;
                _wantSpuTrack = null;
                StartPlayback(request.StartSeconds);
            });
        }

        public void Stop()
        {
            if (_pageId != null)
            {
                Log.Write("Stop");
            }
            BeginTransition();
            _pageId = null;
            var seq = _loadSeq;
            Enqueue(() =>
            {
                StopPlayer();
                Interlocked.Exchange(ref _workerLoadSeq, seq);
                _request = null;
                _hasPlayed = false;
                // A swap-chain re-create deferred while a stream was playing takes effect now
                // that playback has stopped; the next Open uses the fresh instance.
                MaybeRecreateWithSwapChainOptions();
            });
        }

        public void SetPaused(bool paused)
        {
            Enqueue(() =>
            {
                _wantPaused = paused;
                if (_hasPlayed)
                {
                    _mediaPlayer.SetPause(paused);
                }
            });
        }

        public void TogglePause()
        {
            Enqueue(() =>
            {
                _wantPaused = !_wantPaused;
                if (_hasPlayed)
                {
                    _mediaPlayer.SetPause(_wantPaused);
                }
            });
        }

        public void Seek(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0)
            {
                return;
            }
            var ms = (long)(seconds * 1000);
            Interlocked.Exchange(ref _lastTimeMs, ms);
            Enqueue(() =>
            {
                if (_hasPlayed && _mediaPlayer.IsSeekable)
                {
                    _mediaPlayer.Time = ms;
                }
                else if (!_hasPlayed)
                {
                    _pendingSeekMs = ms;
                }
            });
        }

        public void SetVolume(int volume)
        {
            var v = Math.Max(0, Math.Min(200, volume));
            Enqueue(() =>
            {
                _wantVolume = v;
                if (_hasPlayed)
                {
                    _mediaPlayer.Volume = v;
                }
            });
        }

        public void SetMute(bool muted)
        {
            Enqueue(() =>
            {
                _wantMute = muted;
                if (_hasPlayed)
                {
                    _mediaPlayer.Mute = muted;
                }
            });
        }

        public void SetRate(double rate)
        {
            if (double.IsNaN(rate) || rate <= 0)
            {
                return;
            }
            var r = (float)Math.Min(4, rate);
            Enqueue(() =>
            {
                _wantRate = r;
                if (_hasPlayed)
                {
                    _mediaPlayer.SetRate(r);
                }
            });
        }

        /// <summary>A VLC audio track id from "player.tracks", or -1 for none.</summary>
        public void SetAudioTrack(int id)
        {
            Enqueue(() =>
            {
                _wantAudioTrack = id;
                ApplyTrackPreferences();
                QueueTrackRefresh();
            });
        }

        /// <summary>A VLC subtitle track id from "player.tracks", or -1 for none.</summary>
        public void SetSubtitleTrack(int id)
        {
            Enqueue(() =>
            {
                _wantSpuTrack = id;
                ApplyTrackPreferences();
                QueueTrackRefresh();
            });
        }

        public void SetSubtitleDelay(long ms)
        {
            var us = ms * 1000;
            Enqueue(() =>
            {
                _spuDelayUs = us;
                if (_hasPlayed)
                {
                    _mediaPlayer.SetSpuDelay(us);
                }
            });
        }

        public void SetAudioDelay(long ms)
        {
            var us = ms * 1000;
            Enqueue(() =>
            {
                _audioDelayUs = us;
                if (_hasPlayed)
                {
                    _mediaPlayer.SetAudioDelay(us);
                }
            });
        }

        /// <summary>"fit", "fill" (crop to the screen), "stretch", or a ratio such as "16:9".</summary>
        public void SetAspect(string mode)
        {
            Enqueue(() =>
            {
                _aspectMode = string.IsNullOrEmpty(mode) ? "fit" : mode;
                ApplyVideoScale();
            });
        }

        public void SetViewSize(double width, double height)
        {
            var w = (int)Math.Round(width);
            var h = (int)Math.Round(height);
            if (w <= 0 || h <= 0)
            {
                return;
            }
            Enqueue(() =>
            {
                _viewWidth = w;
                _viewHeight = h;
                ApplyVideoScale();
            });
        }

        public void Dispose()
        {
            _disposed = true;
            EventRaised = null;
            MediaPlayerCreated = null;
            VideoVisibleChanged = null;
            Enqueue(() =>
            {
                StopPlayer();
                _mediaPlayer?.Dispose();
                _mediaPlayer = null;
                _libVlc?.Dispose();
                _libVlc = null;
            });
        }

        // Worker. Reports a LibVLC start failure (including a failed swap-chain re-create)
        // for the load that is waiting on it.
        private void ReportStartFailure(long seq)
        {
            var message = "VLC failed to start" + (_initError != null ? ": " + _initError : " (timed out).");
            Log.Write(message);
            RunOnUi(() =>
            {
                if (seq == _loadSeq)
                {
                    RaiseEnded("error", message);
                }
            });
        }

        private void BeginTransition()
        {
            _loadSeq++;
            _readySent = false;
            _durationSent = false;
            _lastBufferingPercent = -1;
            _vlcBuffering = false;
            VideoVisibleChanged?.Invoke(false);
        }

        // Worker: (re)opens the current request, resuming at startSeconds.
        private void StartPlayback(double startSeconds)
        {
            StopPlayer();
            _inputStarted = false;
            Interlocked.Exchange(ref _lastTimeMs, (long)(startSeconds * 1000));
            var options = new List<string>
            {
                ":network-caching=" + NetworkCachingMs,
                ":http-reconnect",
            };
            if (startSeconds > 0)
            {
                options.Add(":start-time=" + startSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            }
            if (!_request.HardwareDecoding)
            {
                options.Add(":avcodec-hw=none");
                Log.Write("VLC hardware decoding disabled; using software decoding");
            }
            else
            {
                // Force D3D11 hardware decoding. Leaving it to VLC's default
                // left high-bitrate HEVC on slow software decoding on Xbox.
                // (The "vlc error [mem] Invalid get/release function
                // pointers" log line is unrelated benign noise from VLC
                // 3.0.16 probing the imem access module — removed upstream
                // in 2021 — not a broken input.)
                options.Add(":avcodec-hw=d3d11va");
            }
            Media media;
            // Plain files keep going through Windows' HTTP stack: VLC's own
            // HTTPS failed to open links on the Xbox, and only the Windows
            // stack sends arbitrary request headers. HLS/DASH playlists need
            // VLC to fetch the segments itself, so they stay on VLC's HTTP.
            if (UseWindowsHttp(_request.Url))
            {
                options.Add(":file-caching=" + NetworkCachingMs);
                _input = new HttpMediaInput(_request.Url, _request.Headers);
                media = new Media(_libVlc, _input, options.ToArray());
            }
            else
            {
                string value;
                if (_request.Headers.TryGetValue("User-Agent", out value))
                {
                    options.Add(":http-user-agent=" + value);
                }
                if (_request.Headers.TryGetValue("Referer", out value))
                {
                    options.Add(":http-referrer=" + value);
                }
                media = new Media(_libVlc, _request.Url, FromType.FromLocation, options.ToArray());
            }
            using (media)
            {
                var accepted = _mediaPlayer.Play(media);
                Log.Write("VLC play requested (accepted=" + accepted + " state=" + SafeState() + ")");
            }
        }

        // Worker. Stops playback; a pending download is broken off first so Stop doesn't wait on it.
        private void StopPlayer()
        {
            _input?.Abort();
            _mediaPlayer?.Stop();
            _input?.Dispose();
            _input = null;
        }

        // Plain files go through Windows' HTTP stack. HLS and DASH playlists need VLC to fetch the
        // segments itself, so they stay on VLC's own HTTP code.
        private static bool UseWindowsHttp(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return false;
            }
            var path = uri.AbsolutePath;
            return !path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
        }

        // Called on VLC threads. Only warnings and errors, and only so many, to keep the log small.
        private void OnLibVlcLog(object sender, LogEventArgs e)
        {
            try
            {
                if (e.Level < LogLevel.Warning || Interlocked.Increment(ref _vlcLogLines) > 400)
                {
                    return;
                }
                Log.Write("vlc " + e.Level.ToString().ToLowerInvariant() + " [" + e.Module + "] " + e.Message);
            }
            catch
            {
                // Never let logging break VLC's thread.
            }
        }

        private void Subscribe(MediaPlayer mediaPlayer)
        {
            mediaPlayer.Opening += (s, e) => QueueFromVlc(OnOpening);
            mediaPlayer.Playing += (s, e) => QueueFromVlc(OnPlaying);
            mediaPlayer.Paused += (s, e) => PostFromVlc(seq => OnPaused());
            mediaPlayer.Stopped += (s, e) => QueueFromVlc(OnStopped);
            mediaPlayer.Buffering += (s, e) =>
            {
                var cache = e.Cache;
                PostFromVlc(seq => OnBuffering(cache));
            };
            mediaPlayer.TimeChanged += (s, e) =>
            {
                var time = e.Time;
                Interlocked.Exchange(ref _lastTimeMs, time);
                PostFromVlc(seq => OnTimeChanged(time));
            };
            mediaPlayer.LengthChanged += (s, e) =>
            {
                var length = e.Length;
                Interlocked.Exchange(ref _lengthMs, length);
                PostFromVlc(seq => OnLengthChanged(length));
            };
            mediaPlayer.Vout += (s, e) =>
            {
                if (e.Count > 0)
                {
                    QueueFromVlc(OnVideoOutput);
                }
            };
            mediaPlayer.ESAdded += (s, e) => QueueTrackRefresh();
            mediaPlayer.ESDeleted += (s, e) => QueueTrackRefresh();
            mediaPlayer.ESSelected += (s, e) => QueueTrackRefresh();
            mediaPlayer.EndReached += (s, e) => QueueFromVlc(OnEndReached);
            mediaPlayer.EncounteredError += (s, e) => QueueFromVlc(OnEncounteredError);
        }

        // Worker. VLC's state line for the open: before this, a VLC load
        // that never reached Playing left no trace at all in the log —
        // the "Open ..." line was the last word.
        private void OnOpening(long seq)
        {
            Log.Write("VLC opening (state=" + SafeState() + ")");
        }

        // Worker.
        private void OnStopped(long seq)
        {
            Log.Write("VLC stopped (state=" + SafeState() + ")");
        }

        // The player may already be disposed when a queued callback runs;
        // never let a state read throw.
        private string SafeState()
        {
            try
            {
                var player = _mediaPlayer;
                return player == null ? "no-player" : player.State.ToString();
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        // Worker.
        private void OnPlaying(long seq)
        {
            _hasPlayed = true;
            if (!_inputStarted)
            {
                _inputStarted = true;
                Log.Write("Playing (state=" + SafeState() + ") at " + Math.Round(Interlocked.Read(ref _lastTimeMs) / 1000.0) + "s");
                if (_pendingSeekMs >= 0 && _mediaPlayer.IsSeekable)
                {
                    _mediaPlayer.Time = _pendingSeekMs;
                }
                _pendingSeekMs = -1;
                if (_spuDelayUs != 0)
                {
                    _mediaPlayer.SetSpuDelay(_spuDelayUs);
                }
                if (_audioDelayUs != 0)
                {
                    _mediaPlayer.SetAudioDelay(_audioDelayUs);
                }
                ApplyVideoScale();
            }
            _mediaPlayer.Volume = _wantVolume;
            _mediaPlayer.Mute = _wantMute;
            if (Math.Abs(_mediaPlayer.Rate - _wantRate) > 0.001f)
            {
                _mediaPlayer.SetRate(_wantRate);
            }
            if (_wantPaused)
            {
                _mediaPlayer.SetPause(true);
            }
            ApplyTrackPreferences();
            RefreshTracks(seq);

            var hasVideoOutput = _mediaPlayer.VoutCount > 0;
            var paused = _wantPaused;
            RunOnUi(() =>
            {
                if (seq != _loadSeq)
                {
                    return;
                }
                RaiseState(paused ? "paused" : "playing", null);
                if (hasVideoOutput)
                {
                    MarkVideoReady(seq, true);
                }
                else
                {
                    // Audio-only streams never create a video output.
                    ScheduleReadyFallback(seq);
                }
            });
        }

        // UI thread.
        private void OnPaused()
        {
            Log.Write("VLC paused at " + Math.Round(Interlocked.Read(ref _lastTimeMs) / 1000.0) + "s");
            RaiseState("paused", null);
        }

        // Worker.
        private void OnVideoOutput(long seq)
        {
            uint width = 0;
            uint height = 0;
            _mediaPlayer.Size(0, ref width, ref height);
            if (width > 0 && height > 0)
            {
                Log.Write("VLC video output ready (" + width + "x" + height + ")");
            }
            RunOnUi(() =>
            {
                if (seq != _loadSeq)
                {
                    return;
                }
                if (width > 0 && height > 0)
                {
                    var payload = new JsonObject();
                    payload.SetNamedValue("width", JsonValue.CreateNumberValue(width));
                    payload.SetNamedValue("height", JsonValue.CreateNumberValue(height));
                    Raise("player.video", payload);
                }
                MarkVideoReady(seq, true);
            });
        }

        // Worker.
        private void OnEndReached(long seq)
        {
            // VLC reports a dropped HTTP connection as a normal end of stream. If that happens well
            // before the end, pick the stream back up where it stopped instead of ending playback.
            var length = Interlocked.Read(ref _lengthMs);
            var time = Interlocked.Read(ref _lastTimeMs);
            if (_hasPlayed && length > 0 && time + EarlyEndToleranceMs < length && TryReconnect(time / 1000.0))
            {
                return;
            }
            // A stream that ends before it ever played failed to open; reporting it as finished
            // would make the app move on to the next episode.
            var failedToOpen = !_hasPlayed;
            Log.Write(failedToOpen ? "Stream ended before it played (state=" + SafeState() + ")" : "Stream finished");
            RunOnUi(() =>
            {
                if (seq == _loadSeq)
                {
                    RaiseEnded(failedToOpen ? "error" : "eof", failedToOpen ? PlaybackErrorMessage : null);
                }
            });
        }

        // Worker.
        private void OnEncounteredError(long seq)
        {
            var length = Interlocked.Read(ref _lengthMs);
            var time = Interlocked.Read(ref _lastTimeMs);
            if (_hasPlayed && TryReconnect(length > 0 ? time / 1000.0 : 0))
            {
                return;
            }
            Log.Write("VLC could not play the stream (state=" + SafeState() + ") at " + Math.Round(Interlocked.Read(ref _lastTimeMs) / 1000.0) + "s");
            RunOnUi(() =>
            {
                if (seq == _loadSeq)
                {
                    RaiseEnded("error", PlaybackErrorMessage);
                }
            });
        }

        // Worker.
        private bool TryReconnect(double resumeSeconds)
        {
            if (_request == null || _reconnects >= MaxReconnects)
            {
                return false;
            }
            _reconnects++;
            Log.Write("Stream dropped, reconnecting at " + Math.Round(resumeSeconds) + "s (attempt " + _reconnects + ")");
            var seq = Interlocked.Read(ref _workerLoadSeq);
            RunOnUi(() =>
            {
                if (seq == _loadSeq)
                {
                    RaiseState("buffering", null);
                }
            });
            StartPlayback(resumeSeconds);
            return true;
        }

        // Worker.
        private void ApplyTrackPreferences()
        {
            if (_mediaPlayer == null || !_hasPlayed)
            {
                return;
            }
            if (_wantAudioTrack.HasValue && _mediaPlayer.AudioTrack != _wantAudioTrack.Value)
            {
                _mediaPlayer.SetAudioTrack(_wantAudioTrack.Value);
            }
            if (_wantSpuTrack.HasValue && _mediaPlayer.Spu != _wantSpuTrack.Value)
            {
                _mediaPlayer.SetSpu(_wantSpuTrack.Value);
            }
        }

        // Worker. "fill" crops the picture to the screen, "stretch" distorts it to the screen and a
        // ratio forces that shape; "fit" lets VLC letterbox.
        private void ApplyVideoScale()
        {
            if (_mediaPlayer == null)
            {
                return;
            }
            var viewAspect = _viewWidth > 0 && _viewHeight > 0 ? _viewWidth + ":" + _viewHeight : null;
            switch (_aspectMode)
            {
                case "fill":
                    _mediaPlayer.AspectRatio = null;
                    _mediaPlayer.CropGeometry = viewAspect;
                    break;
                case "stretch":
                    _mediaPlayer.CropGeometry = null;
                    _mediaPlayer.AspectRatio = viewAspect;
                    break;
                case "fit":
                    _mediaPlayer.AspectRatio = null;
                    _mediaPlayer.CropGeometry = null;
                    break;
                default:
                    _mediaPlayer.CropGeometry = null;
                    _mediaPlayer.AspectRatio = _aspectMode.Contains(":") ? _aspectMode : null;
                    break;
            }
        }

        private void QueueTrackRefresh()
        {
            // A file with many tracks raises a burst of ES events; refresh once per burst.
            if (Interlocked.Exchange(ref _trackRefreshQueued, 1) == 1)
            {
                return;
            }
            Enqueue(() =>
            {
                Interlocked.Exchange(ref _trackRefreshQueued, 0);
                if (_mediaPlayer == null || _request == null)
                {
                    return;
                }
                ApplyTrackPreferences();
                RefreshTracks(Interlocked.Read(ref _workerLoadSeq));
            });
        }

        // Worker. Track ids are VLC's own ES ids; -1 means none.
        private void RefreshTracks(long seq)
        {
            MediaTrack[] mediaTracks;
            using (var media = _mediaPlayer.Media)
            {
                mediaTracks = media != null ? media.Tracks : new MediaTrack[0];
            }
            var currentAudio = _mediaPlayer.AudioTrack;
            var currentSpu = _mediaPlayer.Spu;
            var audio = TracksToJson(_mediaPlayer.AudioTrackDescription, mediaTracks, TrackType.Audio, currentAudio);
            var subtitles = TracksToJson(_mediaPlayer.SpuDescription, mediaTracks, TrackType.Text, currentSpu);

            RunOnUi(() =>
            {
                if (seq != _loadSeq)
                {
                    return;
                }
                var payload = new JsonObject();
                payload.SetNamedValue("audio", audio);
                payload.SetNamedValue("subtitles", subtitles);
                payload.SetNamedValue("audioId", JsonValue.CreateNumberValue(currentAudio));
                payload.SetNamedValue("subtitleId", JsonValue.CreateNumberValue(currentSpu));
                Raise("player.tracks", payload);
            });
        }

        private static JsonArray TracksToJson(LibVLCSharp.Shared.Structures.TrackDescription[] descriptions,
            MediaTrack[] mediaTracks, TrackType trackType, int selectedId)
        {
            var array = new JsonArray();
            if (descriptions == null)
            {
                return array;
            }
            foreach (var description in descriptions)
            {
                if (description.Id < 0)
                {
                    continue; // "Disable"
                }
                var item = new JsonObject();
                item.SetNamedValue("id", JsonValue.CreateNumberValue(description.Id));
                item.SetNamedValue("name", JsonValue.CreateStringValue(description.Name ?? string.Empty));
                item.SetNamedValue("selected", JsonValue.CreateBooleanValue(description.Id == selectedId));
                foreach (var mediaTrack in mediaTracks)
                {
                    if (mediaTrack.Id != description.Id || mediaTrack.TrackType != trackType)
                    {
                        continue;
                    }
                    if (!string.IsNullOrEmpty(mediaTrack.Language))
                    {
                        item.SetNamedValue("lang", JsonValue.CreateStringValue(mediaTrack.Language));
                    }
                    if (!string.IsNullOrEmpty(mediaTrack.Description))
                    {
                        item.SetNamedValue("title", JsonValue.CreateStringValue(mediaTrack.Description));
                    }
                    var codec = FourCcToString(mediaTrack.Codec);
                    if (codec != null)
                    {
                        item.SetNamedValue("codec", JsonValue.CreateStringValue(codec));
                    }
                    break;
                }
                array.Add(item);
            }
            return array;
        }

        private void OnBuffering(float cache)
        {
            var percent = (int)Math.Round(cache);
            var previous = _lastBufferingPercent;
            if (percent == previous)
            {
                return;
            }
            _lastBufferingPercent = percent;
            // Startup buffering (before the first Playing) is covered by
            // the Opening/Playing lines; a dip mid-playback is the stall
            // signal the log was missing.
            if (_hasPlayed && percent < 100 && !_vlcBuffering)
            {
                _vlcBuffering = true;
                Log.Write("VLC buffering started (cache=" + percent + "%) at " + Math.Round(Interlocked.Read(ref _lastTimeMs) / 1000.0) + "s");
            }
            else if (_vlcBuffering && percent == 100)
            {
                _vlcBuffering = false;
                Log.Write("VLC buffering ended at " + Math.Round(Interlocked.Read(ref _lastTimeMs) / 1000.0) + "s");
            }
            var payload = new JsonObject();
            payload.SetNamedValue("percent", JsonValue.CreateNumberValue(percent));
            Raise("player.buffering", payload);
        }

        private void OnLengthChanged(long lengthMs)
        {
            // VLC sometimes refines the length after its first estimate; the first is good enough
            // and keeps the seek bar from jumping.
            if (_durationSent || lengthMs <= 0)
            {
                return;
            }
            _durationSent = true;
            var payload = new JsonObject();
            payload.SetNamedValue("duration", JsonValue.CreateNumberValue(lengthMs / 1000.0));
            Raise("player.duration", payload);
        }

        private void OnTimeChanged(long timeMs)
        {
            var now = DateTime.UtcNow;
            if (now - _lastTimeReport < TimeReportInterval)
            {
                return;
            }
            _lastTimeReport = now;
            var payload = new JsonObject();
            payload.SetNamedValue("time", JsonValue.CreateNumberValue(Math.Max(0, timeMs) / 1000.0));
            payload.SetNamedValue("duration", JsonValue.CreateNumberValue(Math.Max(0, Interlocked.Read(ref _lengthMs)) / 1000.0));
            Raise("player.time", payload);
            // Periodic proof the decoder is still producing frames: VLC's
            // own counters (decoded/displayed/lost pictures, lost audio
            // buffers, bitrates) come from Media.Statistics. A heartbeat
            // that stops arriving while Playing means time itself froze.
            if (_hasPlayed && now - _lastVlcStatsLog >= TimeSpan.FromSeconds(10))
            {
                _lastVlcStatsLog = now;
                LogVlcStats(timeMs);
            }
        }

        // UI thread. Media.Statistics wraps libvlc_media_get_stats; the
        // "--stats" option defaults to true in VLC, so the counters are
        // live while playing. All-zero counters while Playing would mean
        // statistics are unavailable for this input.
        private void LogVlcStats(long timeMs)
        {
            try
            {
                var player = _mediaPlayer;
                if (player == null) return;
                using (var media = player.Media)
                {
                    if (media == null) return;
                    var stats = media.Statistics;
                    Log.Write("VLC stats: time=" + Math.Round(timeMs / 1000.0) + "s state=" + SafeState() +
                        " decodedVideo=" + stats.DecodedVideo + " displayed=" + stats.DisplayedPictures +
                        " lost=" + stats.LostPictures + " lostABuf=" + stats.LostAudioBuffers +
                        " inputBitrate=" + Math.Round(stats.InputBitrate) + " demuxBitrate=" + Math.Round(stats.DemuxBitrate) +
                        " readBytes=" + stats.ReadBytes);
                }
            }
            catch (Exception ex)
            {
                Log.Write("VLC stats: could not read statistics: " + ex.GetType().Name);
            }
        }

        private void MarkVideoReady(long seq, bool hasVideoOutput)
        {
            if (seq != _loadSeq || _readySent)
            {
                return;
            }
            _readySent = true;
            Log.Write(hasVideoOutput ? "VLC video track ready" : "VLC ready (no video output yet)");
            Raise("player.ready", new JsonObject());
            VideoVisibleChanged?.Invoke(true);
        }

        private async void ScheduleReadyFallback(long seq)
        {
            await Task.Delay(ReadyFallbackDelay);
            MarkVideoReady(seq, false);
        }

        private void RaiseState(string state, string message)
        {
            var payload = new JsonObject();
            payload.SetNamedValue("state", JsonValue.CreateStringValue(state));
            if (message != null)
            {
                payload.SetNamedValue("message", JsonValue.CreateStringValue(message));
            }
            Raise("player.state", payload);
        }

        private void RaiseEnded(string reason, string errorMessage)
        {
            var payload = new JsonObject();
            payload.SetNamedValue("reason", JsonValue.CreateStringValue(reason));
            if (errorMessage != null)
            {
                payload.SetNamedValue("message", JsonValue.CreateStringValue(errorMessage));
            }
            Raise("player.ended", payload);
        }

        // UI thread. Tags the event with the page's id for the current load.
        private void Raise(string type, JsonObject payload)
        {
            payload.SetNamedValue("id", _pageId != null ? JsonValue.CreateStringValue(_pageId) : JsonValue.CreateNullValue());
            EventRaised?.Invoke(type, payload);
        }

        // Runs work on the worker, if the stream VLC raised the event for is still the loaded one.
        private void QueueFromVlc(Action<long> work)
        {
            var seq = Interlocked.Read(ref _workerLoadSeq);
            Enqueue(() =>
            {
                if (seq == Interlocked.Read(ref _workerLoadSeq))
                {
                    work(seq);
                }
            });
        }

        // Runs work on the UI thread, if the stream VLC raised the event for is still the loaded one.
        private void PostFromVlc(Action<long> work)
        {
            var seq = Interlocked.Read(ref _workerLoadSeq);
            RunOnUi(() =>
            {
                if (seq == _loadSeq)
                {
                    work(seq);
                }
            });
        }

        private void Enqueue(Action action)
        {
            lock (_queueLock)
            {
                _queue = _queue.ContinueWith(_ =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("VLC worker", ex);
                    }
                }, TaskScheduler.Default);
            }
        }

        private void RunOnUi(Action action)
        {
            var ignored = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error("VLC UI callback", ex);
                }
            });
        }

        private static bool IsAllowedUrl(string url)
        {
            var colon = url.IndexOf(':');
            return colon > 0 && Array.IndexOf(AllowedSchemes, url.Substring(0, colon).ToLowerInvariant()) >= 0;
        }

        private static string FourCcToString(uint fourcc)
        {
            if (fourcc == 0)
            {
                return null;
            }
            var chars = new char[4];
            for (var i = 0; i < 4; i++)
            {
                chars[i] = (char)((fourcc >> (8 * i)) & 0xFF);
            }
            var text = new string(chars).Trim(' ', '\0');
            return text.Length == 0 ? null : text.ToLowerInvariant();
        }
    }
}
