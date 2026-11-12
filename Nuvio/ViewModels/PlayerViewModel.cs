using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Nuvio.Core;
using Nuvio.Diagnostics;
using Nuvio.Models;
using Nuvio.Services;
using Windows.ApplicationModel.Resources;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Nuvio.ViewModels
{
    /// <summary>One row of the subtitles choice menu.</summary>
    internal enum SubtitleMenuKind
    {
        Header,
        Off,
        Track,
        Addon,
        Delay,
    }

    internal sealed class SubtitleMenuItem
    {
        public SubtitleMenuKind Kind { get; set; }
        public string Label { get; set; }
        public string Description { get; set; }
        public bool IsSelected { get; set; }
        public int TrackId { get; set; }
        public SubtitleItem Sub { get; set; }
    }

    /// <summary>One row of the audio choice menu.</summary>
    internal sealed class AudioMenuItem
    {
        public string Label { get; set; }
        public string Description { get; set; }
        public bool IsSelected { get; set; }
        public int TrackId { get; set; }
    }

    /// <summary>One row of the speed choice menu.</summary>
    internal sealed class SpeedMenuItem
    {
        public string Label { get; set; }
        public double Value { get; set; }
        public bool IsSelected { get; set; }
    }

    /// <summary>One row of the aspect-ratio choice menu.</summary>
    internal sealed class AspectMenuItem
    {
        public string Label { get; set; }
        public string Value { get; set; }
        public bool IsSelected { get; set; }
    }

    /// <summary>One row of the sources choice menu (original list order).</summary>
    internal sealed class SourceMenuItem
    {
        public string Label { get; set; }
        public string Description { get; set; }
        public string Badge { get; set; }
        public bool IsSelected { get; set; }
        public int Index { get; set; }
    }

    /// <summary>One row of the subtitle-delay menu.</summary>
    internal sealed class DelayMenuItem
    {
        public string Label { get; set; }
        public string Description { get; set; }
        public double Step { get; set; }
    }

    /// <summary>One row of the episodes choice menu (same season only).</summary>
    internal sealed class EpisodeMenuItem
    {
        public string Label { get; set; }
        public string Description { get; set; }
        public bool IsSelected { get; set; }
        public Video Video { get; set; }
    }

    /// <summary>
    /// Player: the full playback state machine, ported from web/js/views/player.js.
    /// No UI types except DispatcherTimer; constructed on the UI thread. The page
    /// owns the XAML, the ContentDialog menus and focus; this class owns state,
    /// timers, fallback/stall logic, progress, subtitles and next-episode flow.
    /// Playback goes through PlayerService only — never through the engines.
    /// </summary>
    internal sealed class PlayerViewModel : ViewModelBase
    {
        private const int OpenTimeoutSeconds = 35;
        private const int HideControlsMs = 4000;
        private const int SaveEveryMs = 5000;
        private const int MaxConsecutiveFailures = 3;
        private const int FallbackDelayMs = 1500;
        private const int StallCheckMs = 5000;
        private const int StallTimeoutMs = 15000;
        private const int SeekStepSeconds = 10;
        private const int ScrubCommitMs = 700;

        private static readonly double[] Speeds = { 0.5, 0.75, 1, 1.25, 1.5, 2 };
        private static readonly string[][] Aspects =
        {
            new[] { "fit", "Fit" },
            new[] { "fill", "Fill (crop)" },
            new[] { "stretch", "Stretch" },
            new[] { "16:9", "16:9" },
            new[] { "4:3", "4:3" },
            new[] { "21:9", "21:9" },
        };
        private static readonly double[] DelaySteps = { -2, -1, -0.5, -0.25, -0.1, 0, 0.1, 0.25, 0.5, 1, 2 };

        private sealed class NextInfo
        {
            public bool AtEnd;
            public int Countdown;
            public Video Video;
            public Meta FullMeta;
        }

        private sealed class NextTarget
        {
            public Meta Full;
            public Video Next;
        }

        private readonly PlayerService _player;
        private readonly StremioService _stremio;
        private readonly ProgressService _progress;
        private readonly AppSettings _settings;
        private readonly NavigationService _navigation;
        private readonly SubtitleService _subtitles;
        private readonly CoreDispatcher _dispatcher;
        private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse("Resources");

        private readonly DispatcherTimer _openTimer = new DispatcherTimer();
        private readonly DispatcherTimer _hideTimer = new DispatcherTimer();
        private readonly DispatcherTimer _stallTimer = new DispatcherTimer();
        private readonly DispatcherTimer _scrubTimer = new DispatcherTimer();
        private readonly DispatcherTimer _fallbackTimer = new DispatcherTimer();
        private readonly DispatcherTimer _nextTimer = new DispatcherTimer();

        private bool _initialised;
        private bool _restart;
        private bool _fallbackToStreams;
        private string _contentId;
        private int? _season;
        private int? _episode;
        private string _videoId;
        private List<StreamItem> _playbackSources = new List<StreamItem>();
        private List<StreamItem> _sources = new List<StreamItem>();
        private Meta _fullMeta;

        private string _playState = "opening";
        private bool _ready;
        private double _duration;
        private double _lastTime;
        private DateTime _lastTimeAt = DateTime.UtcNow;
        private DateTime _lastSavedAt = DateTime.UtcNow;
        private double _rate = 1;
        private string _aspect = "fit";
        private int _bufferingPercent;
        private PlayerTracks _tracks;
        private bool _tracksInitialised;
        private List<SubtitleItem> _addonSubtitles = new List<SubtitleItem>();
        private bool _addonSubtitlesLoaded;
        private Task _addonSubtitlesTask;
        private SubtitleItem _activeAddonSub;
        private List<SubtitleCue> _activeCues;
        private double _subtitleDelay;
        private bool _subtitleChosen;
        private string _currentSubtitleText = string.Empty;
        private bool _ended;
        private bool _failed;
        private bool _closed;
        private bool _suppressEngineStop;
        private int _consecutiveFailures;
        private int _stallReconnects;
        private bool _fallbackNoticeShown;
        private bool _sessionStarted;
        private StreamItem _fallbackTarget;
        private NextInfo _nextInfo;
        private bool _nextDismissed;
        private double? _scrubTarget;
        private int _scrubRepeat;
        private bool _controlsVisible = true;

        private string _sourceLabel = string.Empty;
        private string _loadingText;
        private bool _hasNextSource;
        private Video _nextVideo;
        private string _nextThumbnail;
        private string _nextTitle = string.Empty;
        private int _nextCountdown;
        private string _nextLabel = string.Empty;
        private bool _isNextVisible;
        private string _titleText = string.Empty;
        private string _episodeText = string.Empty;
        private string _positionText = "0:00";
        private string _remainingText = string.Empty;
        private double _fraction;
        private string _scrubPreviewText = "0:00";
        private bool _isScrubbing;
        private string _playPauseGlyph = "\uE768";
        private string _rateText = "1×";
        private string _episodesMenuTitle = string.Empty;

        private double _subtitleFontSize;
        private string _subtitleColorHex;
        private string _subtitleBackground;
        private int _subtitleOffsetPercent;

        /// <summary>Raised with the toast text; the page forwards it to the shell.</summary>
        public event Action<string> ToastRequested;
        /// <summary>Raised with a focus hint: "play", "seek", "back", "next", "error".</summary>
        public event Action<string> FocusRequested;
        /// <summary>Raised with the flash glyph ("\uE768" play / "\uE769" pause).</summary>
        public event Action<string> FlashRequested;
        /// <summary>Raised when the subtitle-delay menu must open (again) after a choice.</summary>
        public event EventHandler DelayMenuRequested;

        public PlayerViewModel(PlayerService player, StremioService stremio, ProgressService progress, AppSettings settings, NavigationService navigation)
        {
            _player = player;
            _stremio = stremio;
            _progress = progress;
            _settings = settings;
            _navigation = navigation;
            _subtitles = new SubtitleService(stremio);
            _dispatcher = Window.Current.Dispatcher;

            _loadingText = S("PlayerOpeningStream", "Opening stream…");
            ReadSubtitleStyle();

            _openTimer.Tick += OnOpenTimerTick;
            _hideTimer.Tick += OnHideTimerTick;
            _stallTimer.Tick += OnStallTimerTick;
            _stallTimer.Interval = TimeSpan.FromMilliseconds(StallCheckMs);
            _stallTimer.Start();
            _scrubTimer.Tick += OnScrubTimerTick;
            _fallbackTimer.Tick += OnFallbackTimerTick;
            _nextTimer.Tick += OnNextTimerTick;

            // NOTE (contract): PlayerService carries the payload events for the
            // primitive values as Action<T> — EventHandler<string/int/double>
            // cannot compile (T must derive from EventArgs). PlayerTracks is
            // not EventArgs-derived either, so TracksChanged is Action too.
            // The remaining custom-type events stay EventHandler<T> with
            // EventArgs-derived payloads.
            _player.StateChanged += OnPlayerStateChanged;
            _player.TimeUpdated += OnPlayerTimeUpdated;
            _player.DurationChanged += OnPlayerDurationChanged;
            _player.BufferingChanged += OnPlayerBufferingChanged;
            _player.TracksChanged += OnPlayerTracksChanged;
            _player.Ready += OnPlayerReady;
            _player.Ended += OnPlayerEnded;
            _player.EngineSwitched += OnPlayerEngineSwitched;
            AppEvents.SettingsChanged += OnSettingsChanged;
        }

        // ---------- Public state (bound by the page) ----------

        public Meta Meta { get; private set; }
        public Video Video { get; private set; }
        public bool IsSeries { get; private set; }
        public StreamItem Stream { get; private set; }
        public List<StreamItem> Sources { get { return _sources; } }
        public bool HasMultipleSources { get { return _sources.Count > 1; } }

        public string PlayState
        {
            get { return _playState; }
            private set
            {
                if (SetProperty(ref _playState, value)) RaisePropertyChanged(nameof(IsPlaying));
            }
        }

        public bool IsPlaying { get { return _playState == "playing"; } }

        public bool Ready
        {
            get { return _ready; }
            private set
            {
                if (SetProperty(ref _ready, value)) RaisePropertyChanged(nameof(IsLoadingVisible));
            }
        }

        public double Duration
        {
            get { return _duration; }
            private set { SetProperty(ref _duration, value); }
        }

        /// <summary>Last position reported by the engine (seconds).</summary>
        public double LastTime { get { return _lastTime; } }

        public double Rate
        {
            get { return _rate; }
            private set { SetProperty(ref _rate, value); }
        }

        public string RateText
        {
            get { return _rateText; }
            private set { SetProperty(ref _rateText, value); }
        }

        public string Aspect
        {
            get { return _aspect; }
            private set { SetProperty(ref _aspect, value); }
        }

        public int BufferingPercent
        {
            get { return _bufferingPercent; }
            private set { SetProperty(ref _bufferingPercent, value); }
        }

        public PlayerTracks Tracks
        {
            get { return _tracks; }
            private set { SetProperty(ref _tracks, value); }
        }

        public bool TracksInitialised
        {
            get { return _tracksInitialised; }
            private set { SetProperty(ref _tracksInitialised, value); }
        }

        public List<SubtitleItem> AddonSubtitles
        {
            get { return _addonSubtitles; }
            private set { SetProperty(ref _addonSubtitles, value); }
        }

        public bool AddonSubtitlesLoaded
        {
            get { return _addonSubtitlesLoaded; }
            private set { SetProperty(ref _addonSubtitlesLoaded, value); }
        }

        public SubtitleItem ActiveAddonSub
        {
            get { return _activeAddonSub; }
            private set { SetProperty(ref _activeAddonSub, value); }
        }

        public double SubtitleDelay
        {
            get { return _subtitleDelay; }
            private set { SetProperty(ref _subtitleDelay, value); }
        }

        public string CurrentSubtitleText
        {
            get { return _currentSubtitleText; }
            private set { SetProperty(ref _currentSubtitleText, value); }
        }

        /// <summary>2.4rem at 100% (web --sub-size), 1rem = 8 epx on Xbox.</summary>
        public double SubtitleFontSize { get { return _subtitleFontSize; } }
        public string SubtitleColorHex { get { return _subtitleColorHex; } }
        /// <summary>"none" | "shadow" | "box" (web .player-subs[data-background]).</summary>
        public string SubtitleBackground { get { return _subtitleBackground; } }
        public int SubtitleOffsetPercent { get { return _subtitleOffsetPercent; } }

        public string SourceLabel
        {
            get { return _sourceLabel; }
            private set { SetProperty(ref _sourceLabel, value); }
        }

        public string LoadingText
        {
            get { return _loadingText; }
            private set { SetProperty(ref _loadingText, value); }
        }

        /// <summary>Loading overlay: opening, not ready, no error box.</summary>
        public bool IsLoadingVisible { get { return !Ready && !HasError; } }

        public bool HasNextSource
        {
            get { return _hasNextSource; }
            private set { SetProperty(ref _hasNextSource, value); }
        }

        public Video NextVideo
        {
            get { return _nextVideo; }
            private set { SetProperty(ref _nextVideo, value); }
        }

        public string NextThumbnail
        {
            get { return _nextThumbnail; }
            private set { SetProperty(ref _nextThumbnail, value); }
        }

        public string NextTitle
        {
            get { return _nextTitle; }
            private set { SetProperty(ref _nextTitle, value); }
        }

        public int NextCountdown
        {
            get { return _nextCountdown; }
            private set
            {
                if (SetProperty(ref _nextCountdown, value))
                {
                    NextLabel = string.Format(CultureInfo.InvariantCulture,
                        S("PlayerNextEpisodeLabel", "Next Episode · {0}s"), value);
                }
            }
        }

        public string NextLabel
        {
            get { return _nextLabel; }
            private set { SetProperty(ref _nextLabel, value); }
        }

        public bool IsNextVisible
        {
            get { return _isNextVisible; }
            private set { SetProperty(ref _isNextVisible, value); }
        }

        public string TitleText
        {
            get { return _titleText; }
            private set { SetProperty(ref _titleText, value); }
        }

        public string EpisodeText
        {
            get { return _episodeText; }
            private set { SetProperty(ref _episodeText, value); }
        }

        public string LoadingLogoUrl { get { return Meta != null ? Meta.Logo : null; } }
        public string BackdropUrl { get { return Meta != null ? Meta.Background : null; } }

        public string PositionText
        {
            get { return _positionText; }
            private set { SetProperty(ref _positionText, value); }
        }

        public string RemainingText
        {
            get { return _remainingText; }
            private set { SetProperty(ref _remainingText, value); }
        }

        public double Fraction
        {
            get { return _fraction; }
            private set { SetProperty(ref _fraction, value); }
        }

        public string ScrubPreviewText
        {
            get { return _scrubPreviewText; }
            private set { SetProperty(ref _scrubPreviewText, value); }
        }

        public bool IsScrubbing
        {
            get { return _isScrubbing; }
            private set { SetProperty(ref _isScrubbing, value); }
        }

        public bool HasScrubTarget { get { return _scrubTarget.HasValue; } }

        /// <summary>Play icon when paused, pause icon when playing (web setIcon).</summary>
        public string PlayPauseGlyph
        {
            get { return _playPauseGlyph; }
            private set { SetProperty(ref _playPauseGlyph, value); }
        }

        public string EpisodesMenuTitle
        {
            get { return _episodesMenuTitle; }
            private set { SetProperty(ref _episodesMenuTitle, value); }
        }

        public bool ControlsVisible
        {
            get { return _controlsVisible; }
            private set { SetProperty(ref _controlsVisible, value); }
        }

        /// <summary>Set by the page while any ContentDialog menu is open; blocks auto-hide.</summary>
        public bool MenuOpen { get; set; }

        public bool Closed { get { return _closed; } }

        private string S(string key, string fallback)
        {
            var text = _strings.GetString(key);
            return string.IsNullOrEmpty(text) ? fallback : text;
        }

        public void RequestToast(string message)
        {
            var handler = ToastRequested;
            if (handler != null) handler(message);
        }

        private void RaiseFocus(string hint)
        {
            var handler = FocusRequested;
            if (handler != null) handler(hint);
        }

        private void RaiseFlash(string glyph)
        {
            var handler = FlashRequested;
            if (handler != null) handler(glyph);
        }

        private void RaiseDelayMenuRequested()
        {
            var handler = DelayMenuRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        // ---------- Init / start ----------

        public void Init(PlayerNavigation param)
        {
            if (_initialised || param == null || param.Meta == null || param.Stream == null) return;
            _initialised = true;
            Meta = param.Meta;
            Video = param.Video;
            _restart = param.Restart;
            _fallbackToStreams = param.FallbackToStreams;
            TitleText = Meta.Name ?? string.Empty;
            RaisePropertyChanged(nameof(LoadingLogoUrl));
            RaisePropertyChanged(nameof(BackdropUrl));

            var video = param.Video;
            _contentId = Meta.Id;
            _videoId = Meta.Id;
            int? season = null;
            int? episode = null;
            if (video != null)
            {
                _videoId = video.Id ?? Meta.Id;
                if (Meta.Type != "movie")
                {
                    EpisodeText = JoinNonEmpty(" · ", Formatting.FormatEpisodeCode(video.Season, video.Episode),
                        !string.IsNullOrEmpty(video.Name) ? video.Name : video.Title);
                }
                if (Meta.Type != "movie")
                {
                    season = video.Season;
                    episode = video.Episode;
                    // Video.Season/Episode are non-nullable ints (0 when unset);
                    // a colon id with both at 0 means the numbers were never
                    // parsed (web: video.season == null) — parse the id.
                    if (video.Season == 0 && video.Episode == 0
                        && !string.IsNullOrEmpty(video.Id) && video.Id.IndexOf(':') >= 0)
                    {
                        var parsed = ProgressService.ParseVideoId(video.Id, Meta.Type);
                        if (parsed.Season.HasValue)
                        {
                            season = parsed.Season;
                            episode = parsed.Episode;
                            _contentId = parsed.ContentId;
                        }
                    }
                }
            }
            _season = season;
            _episode = episode;
            IsSeries = Meta.Type != "movie" && video != null && season.HasValue;
            RaisePropertyChanged(nameof(IsSeries));

            var list = param.Streams ?? new List<StreamItem> { param.Stream };
            _sources = list;
            RaisePropertyChanged(nameof(Sources));
            RaisePropertyChanged(nameof(HasMultipleSources));
            _playbackSources = BuildPlaybackSources(list);

            var startAt = _restart || !_settings.GetBool("resumePlayback", true)
                ? 0
                : _progress.ResumeSeconds(_contentId, _season, _episode);
            if (startAt > 0)
            {
                RequestToast(string.Format(CultureInfo.InvariantCulture,
                    S("PlayerResumingFrom", "Resuming from {0}"), Formatting.FormatClock(startAt)));
            }
            Start(param.Stream, startAt);
            ShowControls(null);
        }

        /// <summary>Chosen stream first, the rest lightest-first by PlaybackScore.</summary>
        private static List<StreamItem> BuildPlaybackSources(List<StreamItem> list)
        {
            if (list.Count <= 1) return new List<StreamItem>(list);
            var rest = new List<StreamEntry>();
            for (var i = 1; i < list.Count; i++)
            {
                rest.Add(new StreamEntry
                {
                    Stream = list[i],
                    Info = StreamAnalysis.DescribeStream(list[i], list[i].AddonName ?? ""),
                    // SortForPlayback only reads Stream/Info; the serving
                    // addon is known here by URL/name only, not as an Addon.
                    Addon = null,
                    Order = i,
                });
            }
            var sorted = StreamAnalysis.SortForPlayback(rest);
            var result = new List<StreamItem> { list[0] };
            foreach (var entry in sorted) result.Add(entry.Stream);
            return result;
        }

        public void Start(StreamItem target, double at)
        {
            StopTimer(_openTimer);
            StopTimer(_fallbackTimer);
            _failed = false;
            Report("stop");
            Stream = target;
            Ready = false;
            _ended = false;
            TracksInitialised = false;
            BufferingPercent = 0;
            ErrorMessage = null;
            LoadingText = S("PlayerOpeningStream", "Opening stream…");
            var info = StreamAnalysis.DescribeStream(target, target.AddonName ?? "");
            SourceLabel = JoinNonEmpty(" · ", target.AddonName, info != null ? info.Resolution : null);
            _lastTime = at;
            _lastTimeAt = DateTime.UtcNow;
            _lastSavedAt = DateTime.UtcNow;
            _player.Open(target, at);
            _openTimer.Interval = TimeSpan.FromSeconds(OpenTimeoutSeconds);
            _openTimer.Start();
            _player.SetAspect(Aspect);
            if (Rate != 1) _player.SetRate(Rate);
            RaisePropertyChanged(nameof(IsLoadingVisible));
            RenderTimeline();
        }

        // ---------- Engine events ----------

        private void Dispatch(Action action)
        {
            if (_dispatcher.HasThreadAccess) action();
            else
            {
                var _ = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
            }
        }

        private void OnPlayerStateChanged(string state)
        {
            Dispatch(() => HandleStateChanged(state));
        }

        private void HandleStateChanged(string state)
        {
            if (_failed || _closed) return;
            var was = PlayState;
            PlayState = state;
            if (state == "playing" || state == "paused")
            {
                _lastTimeAt = DateTime.UtcNow;
                PlayPauseGlyph = state == "playing" ? "\uE769" : "\uE768";
                if (state == "paused")
                {
                    ShowControls(null);
                    Save(true);
                    Report("pause");
                }
                else
                {
                    ScheduleHide();
                    if (was == "paused") Report("resume");
                }
            }
            if (state == "buffering") LoadingText = S("PlayerReconnecting", "Reconnecting…");
            UpdateLoading();
            if (ControlsVisible) RenderTimeline();
            RenderSubtitles();
        }

        private void OnPlayerTimeUpdated(object sender, PlayerTimeEventArgs e)
        {
            Dispatch(() =>
            {
                if (_closed) return;
                _lastTime = e.Time;
                _lastTimeAt = DateTime.UtcNow;
                if (e.Duration > 0 && Duration <= 0) Duration = e.Duration;
                MaybeOfferNext();
                MaybeSave();
                if (ControlsVisible) RenderTimeline();
                RenderSubtitles();
            });
        }

        private void OnPlayerDurationChanged(double duration)
        {
            Dispatch(() =>
            {
                if (!_closed) Duration = duration;
            });
        }

        private void OnPlayerBufferingChanged(int percent)
        {
            Dispatch(() =>
            {
                if (_closed) return;
                BufferingPercent = percent;
                UpdateLoading();
            });
        }

        private void OnPlayerTracksChanged(PlayerTracks tracks)
        {
            Dispatch(() =>
            {
                if (_closed) return;
                Tracks = tracks;
                var audio = tracks != null && tracks.Audio != null ? tracks.Audio : new List<PlayerTrack>();
                var subs = tracks != null && tracks.Subtitles != null ? tracks.Subtitles : new List<PlayerTrack>();
                if (!TracksInitialised && (audio.Count > 0 || subs.Count > 0))
                {
                    TracksInitialised = true;
                    ApplyTrackPreferences();
                }
            });
        }

        private void OnPlayerReady(object sender, EventArgs e)
        {
            Dispatch(() =>
            {
                if (_failed || _closed) return;
                StopTimer(_openTimer);
                Ready = true;
                _consecutiveFailures = 0;
                _stallReconnects = 0;
                Report("start");
                UpdateLoading();
                RenderTimeline();
                RenderSubtitles();
                RaiseFocus("play");
                ScheduleHide();
                if (_settings.GetBool("autoSelectSubtitles", false))
                {
                    var _ = LoadAddonSubtitlesAsync(false);
                }
            });
        }

        private void OnPlayerEnded(object sender, PlayerEndedEventArgs e)
        {
            Dispatch(() =>
            {
                if (_closed) return;
                if (e.Reason == "eof")
                {
                    _ended = true;
                    Save(true, finished: true);
                    Report("stop");
                    if (IsSeries && _settings.GetBool("autoplayNext", true) && !_nextDismissed)
                    {
                        var _ = StartNextCountdownAsync(true);
                    }
                    else
                    {
                        Exit();
                    }
                    return;
                }
                TryNextSource(e.Message);
            });
        }

        private void OnPlayerEngineSwitched(object sender, PlayerEngineEventArgs e)
        {
            Dispatch(() =>
            {
                if (_closed) return;
                if (e.Engine == "LibVLC")
                {
                    RequestToast(string.IsNullOrEmpty(e.Message)
                        ? S("PlayerSwitchedToVlc", "Switched to VLC playback.")
                        : e.Message);
                }
            });
        }

        private void OnSettingsChanged(object sender, SettingsChangedEventArgs e)
        {
            Dispatch(() =>
            {
                ReadSubtitleStyle();
                RaisePropertyChanged(nameof(SubtitleFontSize));
                RaisePropertyChanged(nameof(SubtitleColorHex));
                RaisePropertyChanged(nameof(SubtitleBackground));
                RaisePropertyChanged(nameof(SubtitleOffsetPercent));
            });
        }

        private void ReadSubtitleStyle()
        {
            _subtitleFontSize = 19.2 * _settings.GetInt("subtitleSize", 100) / 100.0;
            _subtitleColorHex = _settings.GetString("subtitleColor", "#ffffff");
            _subtitleBackground = _settings.GetString("subtitleBackground", "none");
            _subtitleOffsetPercent = _settings.GetInt("subtitleOffset", 8);
        }

        private void UpdateLoading()
        {
            if (_failed) return;
            // BufferingPercent is the fraction buffered ahead for the current
            // segment, not the fraction of the whole file: it rarely reaches
            // 100% while playing, so it must not drive the spinner. Show the
            // percentage only while opening.
            if (!Ready && BufferingPercent > 0 && BufferingPercent < 100)
            {
                LoadingText = string.Format(CultureInfo.InvariantCulture,
                    S("PlayerBuffering", "Buffering… {0}%"), BufferingPercent);
            }
        }

        // ---------- Fallback / stall ----------

        private void OnOpenTimerTick(object sender, object e)
        {
            StopTimer(_openTimer);
            if (_closed || Ready || _failed) return;
            TryNextSource(S("PlayerNoResponse",
                "The player did not respond. Check the stream link or network, then retry or choose another source."));
        }

        private void OnFallbackTimerTick(object sender, object e)
        {
            StopTimer(_fallbackTimer);
            if (_closed || _failed || _fallbackTarget == null) return;
            var target = _fallbackTarget;
            _fallbackTarget = null;
            Start(target, Ready ? CurrentTime() : _lastTime);
        }

        private void OnStallTimerTick(object sender, object e)
        {
            if (_closed || _failed || !Ready || PlayState != "playing") return;
            if ((DateTime.UtcNow - _lastTimeAt).TotalMilliseconds < StallTimeoutMs) return;
            if (_stallReconnects < 1)
            {
                _stallReconnects++;
                LoadingText = S("PlayerReconnecting", "Reconnecting…");
                RequestToast(S("PlayerStalledReconnecting", "Stream stalled. Reconnecting…"));
                Start(Stream, CurrentTime());
            }
            else
            {
                TryNextSource(S("PlayerStalledTryNext", "The stream stalled. Trying the next available source."));
            }
        }

        private void TryNextSource(string reason)
        {
            if (_closed || _failed) return;
            _consecutiveFailures++;
            if (_consecutiveFailures >= MaxConsecutiveFailures)
            {
                _player.Stop();
                ShowError((string.IsNullOrEmpty(reason) ? S("PlayerStreamCouldNotBePlayed", "This stream could not be played.") : reason)
                    + " " + string.Format(CultureInfo.InvariantCulture,
                        S("PlayerTriedSources", "Tried {0} sources without success."), _consecutiveFailures));
                return;
            }
            var currentIndex = _playbackSources.FindIndex(s => s.Url == Stream.Url);
            var next = _playbackSources.Skip(currentIndex + 1).FirstOrDefault(s => StreamAnalysis.IsPlayable(s));
            if (next == null)
            {
                _player.Stop();
                ShowError(string.IsNullOrEmpty(reason)
                    ? S("PlayerStreamCouldNotBePlayed", "This stream could not be played.")
                    : reason);
                return;
            }
            Save(true);
            if (!_fallbackNoticeShown)
            {
                _fallbackNoticeShown = true;
                RequestToast(S("PlayerSourceFailed", "This source failed. Trying the next available source."));
            }
            _fallbackTarget = next;
            _fallbackTimer.Interval = TimeSpan.FromMilliseconds(FallbackDelayMs);
            _fallbackTimer.Start();
        }

        private void ShowError(string message)
        {
            _failed = true;
            StopTimer(_openTimer);
            StopTimer(_fallbackTimer);
            Ready = false;
            RaisePropertyChanged(nameof(IsLoadingVisible));
            if (_fallbackToStreams)
            {
                RequestToast(S("PlayerLinkNoLongerWorks", "That link no longer works. Pick a source."));
                _navigation.PlayVideo(Meta, Video);
                return;
            }
            var index = _playbackSources.FindIndex(s => s.Url == Stream.Url);
            HasNextSource = _playbackSources.Skip(index + 1).Any(s => StreamAnalysis.IsPlayable(s));
            ErrorMessage = message;
            RaiseFocus("error");
        }

        /// <summary>Error box: Try next source.</summary>
        public void TryNextFromError()
        {
            if (!HasError) return;
            var index = _playbackSources.FindIndex(s => s.Url == Stream.Url);
            var next = _playbackSources.Skip(index + 1).FirstOrDefault(s => StreamAnalysis.IsPlayable(s));
            if (next == null) return;
            Start(next, _lastTime);
        }

        /// <summary>Error box: Retry the same source.</summary>
        public void RetryFromError()
        {
            if (!HasError) return;
            Start(Stream, _lastTime);
        }

        // ---------- Progress / reporting ----------

        /// <summary>While playing+ready, extrapolates from the last time event.</summary>
        public double CurrentTime()
        {
            if (PlayState == "playing" && Ready)
            {
                var extrapolated = _lastTime + (DateTime.UtcNow - _lastTimeAt).TotalSeconds * Rate;
                return Duration > 0 ? Math.Min(Duration, extrapolated) : extrapolated;
            }
            return _lastTime;
        }

        private void MaybeSave()
        {
            if ((DateTime.UtcNow - _lastSavedAt).TotalMilliseconds > SaveEveryMs)
            {
                Save();
                Report("progress");
            }
        }

        public void Save(bool force = false, bool finished = false)
        {
            if (Duration <= 0 || (!Ready && !force)) return;
            var position = finished ? Duration : CurrentTime();
            if (position < 5 && !finished) return;
            _lastSavedAt = DateTime.UtcNow;
            var stream = Stream;
            _progress.SaveProgress(new ProgressEntry
            {
                ContentId = _contentId,
                ContentType = Meta != null ? Meta.Type : null,
                VideoId = _videoId,
                Season = _season,
                Episode = _episode,
                Position = (long)Math.Round(position * 1000),
                Duration = (long)Math.Round(Duration * 1000),
                Title = Meta != null ? Meta.Name : null,
                Poster = Meta != null ? Meta.Poster : null,
                Background = Meta != null ? Meta.Background : null,
                Logo = Meta != null ? Meta.Logo : null,
                EpisodeTitle = Video != null ? (Video.Name ?? Video.Title) : null,
                Thumbnail = Video != null ? Video.Thumbnail : null,
                Stream = stream == null ? null : new StreamItem
                {
                    Url = stream.Url,
                    Name = stream.Name,
                    Title = stream.Title ?? stream.Description,
                    AddonUrl = stream.AddonUrl,
                    AddonName = stream.AddonName,
                    BehaviorHints = stream.BehaviorHints,
                    Jellyfin = stream.Jellyfin,
                },
            }, important: force);
        }

        private void Report(string phase)
        {
            if (phase == "start")
            {
                if (_sessionStarted) return;
                _sessionStarted = true;
            }
            else if (!_sessionStarted)
            {
                return;
            }
            if (phase == "stop") _sessionStarted = false;
            // PlaybackEventArgs has only the 11-arg constructor (get-only props).
            _player.ReportPlayback(new PlaybackEventArgs(phase,
                _contentId,
                Meta != null ? Meta.Type : null,
                _videoId,
                _season,
                _episode,
                Meta != null ? Meta.Name : null,
                _ended ? Duration : CurrentTime(),
                Duration,
                PlayState == "paused",
                Stream));
        }

        // ---------- Controls ----------

        public void ShowControls(string focusHint)
        {
            ControlsVisible = true;
            if (focusHint != null) RaiseFocus(focusHint);
            else RaiseFocus("play");
            ScheduleHide();
        }

        public void HideControls()
        {
            if (MenuOpen || PlayState != "playing" || !Ready) return;
            ControlsVisible = false;
        }

        public void ScheduleHide()
        {
            StopTimer(_hideTimer);
            _hideTimer.Interval = TimeSpan.FromMilliseconds(HideControlsMs);
            _hideTimer.Start();
        }

        private void OnHideTimerTick(object sender, object e)
        {
            StopTimer(_hideTimer);
            HideControls();
        }

        public void Poke()
        {
            if (!ControlsVisible) ShowControls(null);
            else ScheduleHide();
        }

        public void TogglePause()
        {
            if (!Ready) return;
            if (PlayState == "playing")
            {
                _player.SetPaused(true);
                RaiseFlash("\uE769");
            }
            else
            {
                _player.SetPaused(false);
                RaiseFlash("\uE768");
            }
        }

        public void SeekTo(double time)
        {
            var target = Math.Max(0, Duration > 0 ? Math.Min(Duration - 1, time) : time);
            _lastTime = target;
            _lastTimeAt = DateTime.UtcNow;
            _player.Seek(target);
            RenderTimeline();
            if (_nextInfo != null && target < Duration - 60) CancelNext(false);
        }

        public void SeekBy(double delta)
        {
            SeekTo(CurrentTime() + delta);
        }

        public void Scrub(int direction, bool repeat)
        {
            _scrubRepeat = repeat ? _scrubRepeat + 1 : 0;
            var step = _scrubRepeat > 20 ? 60 : _scrubRepeat > 8 ? 30 : SeekStepSeconds;
            var baseTime = _scrubTarget.HasValue ? _scrubTarget.Value : CurrentTime();
            var max = Duration > 0 ? Duration : double.MaxValue;
            _scrubTarget = Math.Max(0, Math.Min(max, baseTime + direction * step));
            IsScrubbing = true;
            RenderTimeline();
            StopTimer(_scrubTimer);
            _scrubTimer.Interval = TimeSpan.FromMilliseconds(ScrubCommitMs);
            _scrubTimer.Start();
        }

        private void OnScrubTimerTick(object sender, object e)
        {
            CommitScrub();
        }

        public void CommitScrub()
        {
            StopTimer(_scrubTimer);
            if (!_scrubTarget.HasValue) return;
            var target = _scrubTarget.Value;
            _scrubTarget = null;
            IsScrubbing = false;
            SeekTo(target);
        }

        public void CancelScrub()
        {
            StopTimer(_scrubTimer);
            _scrubTarget = null;
            _scrubRepeat = 0;
            IsScrubbing = false;
            RenderTimeline();
        }

        private void RenderTimeline()
        {
            var t = _scrubTarget.HasValue ? _scrubTarget.Value : CurrentTime();
            Fraction = Duration > 0 ? Math.Min(1, t / Duration) : 0;
            PositionText = Formatting.FormatClock(t);
            RemainingText = Duration > 0 ? "-" + Formatting.FormatClock(Math.Max(0, Duration - t)) : string.Empty;
            ScrubPreviewText = Formatting.FormatClock(t);
        }

        // ---------- Subtitles ----------

        private void RenderSubtitles()
        {
            List<SubtitleCue> cues = null;
            if (ActiveAddonSub != null) cues = _activeCues ?? ActiveAddonSub.Cues;
            if (cues == null)
            {
                if (CurrentSubtitleText != string.Empty) CurrentSubtitleText = string.Empty;
                return;
            }
            var active = SubtitleParser.CuesAt(cues, CurrentTime() - SubtitleDelay);
            var text = string.Join("\n", active.Select(c => StripSubtitleMarkup(c.Text)));
            if (text != CurrentSubtitleText) CurrentSubtitleText = text;
        }

        /// <summary>
        /// The parser keeps only &lt;i&gt;/&lt;b&gt;/&lt;u&gt; tags; a XAML TextBlock
        /// cannot render them, so they are stripped (italics/bold are lost —
        /// the web keeps them via innerHTML). Entities are unescaped.
        /// </summary>
        private static string StripSubtitleMarkup(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var clean = Regex.Replace(text, "<[^>]*>", string.Empty);
            return clean
                .Replace("&amp;", "&")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", "\"")
                .Replace("&#39;", "'");
        }

        public Task LoadAddonSubtitlesAsync(bool force)
        {
            if (AddonSubtitlesLoaded) return Task.FromResult(0);
            if (!force && !_settings.GetBool("autoSelectSubtitles", false)) return Task.FromResult(0);
            if (_addonSubtitlesTask != null) return _addonSubtitlesTask;
            _addonSubtitlesTask = LoadAddonSubtitlesInnerAsync();
            return _addonSubtitlesTask;
        }

        private async Task LoadAddonSubtitlesInnerAsync()
        {
            var fromStream = new List<SubtitleItem>();
            var subs = Stream != null ? Stream.Subtitles : null;
            if (subs != null)
            {
                foreach (var s in subs)
                {
                    if (s == null || string.IsNullOrEmpty(s.Url)) continue;
                    fromStream.Add(new SubtitleItem
                    {
                        Url = s.Url,
                        Lang = s.Lang,
                        Id = s.Id,
                        AddonName = Stream.AddonName ?? "Stream",
                    });
                }
            }
            List<SubtitleItem> fromAddons = new List<SubtitleItem>();
            try
            {
                var hints = Stream != null ? Stream.BehaviorHints : null;
                var extra = new Dictionary<string, string>();
                if (hints != null)
                {
                    if (!string.IsNullOrEmpty(hints.VideoHash)) extra["videoHash"] = hints.VideoHash;
                    if (hints.VideoSize.HasValue) extra["videoSize"] = hints.VideoSize.Value.ToString(CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(hints.Filename)) extra["filename"] = hints.Filename;
                }
                fromAddons = await _subtitles.FetchAddonSubtitlesAsync(Meta.Type, _videoId, extra).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Addon subtitles failed to load", ex);
            }
            if (_closed) return;
            var combined = new List<SubtitleItem>();
            combined.AddRange(fromStream);
            combined.AddRange(fromAddons);
            for (var i = 0; i < combined.Count; i++) combined[i].Key = i + ":" + combined[i].Url;
            await RunOnUi(() =>
            {
                AddonSubtitles = combined;
                AddonSubtitlesLoaded = true;
                _addonSubtitlesTask = null;
                if (TracksInitialised) ApplySubtitlePreference(false);
            }).ConfigureAwait(false);
        }

        public async Task SelectAddonSubtitleAsync(SubtitleItem sub)
        {
            if (sub == null)
            {
                ActiveAddonSub = null;
                _activeCues = null;
                RenderSubtitles();
                return;
            }
            _player.SetSubtitleTrack(-1);
            ActiveAddonSub = sub;
            if (sub.CuesLoaded && sub.Cues != null)
            {
                _activeCues = sub.Cues;
                RenderSubtitles();
                return;
            }
            _activeCues = null;
            List<SubtitleCue> cues;
            try
            {
                cues = await _subtitles.LoadCuesAsync(sub).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Loading subtitle cues failed", ex);
                RequestToast(S("PlayerSubtitlesCouldNotLoad", "Couldn't load those subtitles"));
                if (ActiveAddonSub == sub)
                {
                    ActiveAddonSub = null;
                    _activeCues = null;
                    RenderSubtitles();
                }
                return;
            }
            if (_closed) return;
            if (ActiveAddonSub == sub)
            {
                sub.Cues = cues;
                sub.CuesLoaded = true;
                _activeCues = cues;
                RenderSubtitles();
            }
        }

        private void ApplyTrackPreferences()
        {
            var audioLang = _settings.GetString("preferredAudioLanguage", "eng");
            var audio = Tracks != null && Tracks.Audio != null ? Tracks.Audio : new List<PlayerTrack>();
            if (!string.IsNullOrEmpty(audioLang) && audio.Count > 1)
            {
                var current = audio.FirstOrDefault(t => t.Id == Tracks.AudioId);
                if (current == null || !LanguageNames.LanguageMatches(current.Lang ?? current.Name, audioLang))
                {
                    var wanted = audio.FirstOrDefault(t =>
                        LanguageNames.LanguageMatches(t.Lang, audioLang) ||
                        LanguageNames.LanguageMatches(t.Name, audioLang));
                    if (wanted != null) _player.SetAudioTrack(wanted.Id);
                }
            }
            ApplySubtitlePreference(true);
        }

        private void ApplySubtitlePreference(bool fromTracks)
        {
            if (_subtitleChosen || !_settings.GetBool("autoSelectSubtitles", false)) return;
            var langs = new List<string>();
            var preferred = _settings.GetString("preferredSubtitleLanguage", "eng");
            var secondary = _settings.GetString("secondarySubtitleLanguage", "");
            if (!string.IsNullOrEmpty(preferred)) langs.Add(preferred);
            if (!string.IsNullOrEmpty(secondary)) langs.Add(secondary);
            var subs = Tracks != null && Tracks.Subtitles != null ? Tracks.Subtitles : new List<PlayerTrack>();
            foreach (var lang in langs)
            {
                var embedded = subs.FirstOrDefault(t =>
                    LanguageNames.LanguageMatches(t.Lang, lang) ||
                    LanguageNames.LanguageMatches(t.Name, lang));
                if (embedded != null)
                {
                    _subtitleChosen = true;
                    _player.SetSubtitleTrack(embedded.Id);
                    return;
                }
                var addon = AddonSubtitles.FirstOrDefault(s => LanguageNames.LanguageMatches(s.Lang, lang));
                if (addon != null)
                {
                    _subtitleChosen = true;
                    var _ = SelectAddonSubtitleAsync(addon);
                    return;
                }
            }
            if (!fromTracks && AddonSubtitles.Count > 0) _subtitleChosen = true;
        }

        // ---------- Menus (data + actions; the page builds the dialogs) ----------

        public List<SubtitleMenuItem> GetSubtitleMenuItems()
        {
            var items = new List<SubtitleMenuItem>();
            var subtitleId = Tracks != null ? Tracks.SubtitleId : -1;
            items.Add(new SubtitleMenuItem
            {
                Kind = SubtitleMenuKind.Off,
                Label = S("PlayerOff", "Off"),
                IsSelected = ActiveAddonSub == null && subtitleId < 0,
            });
            var subs = Tracks != null && Tracks.Subtitles != null ? Tracks.Subtitles : new List<PlayerTrack>();
            if (subs.Count > 0)
            {
                items.Add(new SubtitleMenuItem { Kind = SubtitleMenuKind.Header, Label = S("PlayerInVideo", "In the video") });
                foreach (var t in subs)
                {
                    items.Add(new SubtitleMenuItem
                    {
                        Kind = SubtitleMenuKind.Track,
                        Label = TrackLabel(t),
                        Description = !string.IsNullOrEmpty(t.Codec) ? t.Codec.ToUpperInvariant() : null,
                        IsSelected = ActiveAddonSub == null && t.Id == subtitleId,
                        TrackId = t.Id,
                    });
                }
            }
            if (AddonSubtitles.Count > 0)
            {
                var prefs = new List<string>();
                var preferred = _settings.GetString("preferredSubtitleLanguage", "eng");
                var secondary = _settings.GetString("secondarySubtitleLanguage", "");
                if (!string.IsNullOrEmpty(preferred)) prefs.Add(preferred);
                if (!string.IsNullOrEmpty(secondary)) prefs.Add(secondary);
                var sorted = AddonSubtitles
                    .OrderBy(s =>
                    {
                        var i = prefs.FindIndex(p => LanguageNames.LanguageMatches(s.Lang, p));
                        return i < 0 ? prefs.Count : i;
                    })
                    .ThenBy(s => LanguageNames.LanguageName(s.Lang), StringComparer.CurrentCulture)
                    .ToList();
                items.Add(new SubtitleMenuItem { Kind = SubtitleMenuKind.Header, Label = S("PlayerFromAddons", "From addons") });
                var counts = new Dictionary<string, int>();
                foreach (var s in sorted)
                {
                    var name = LanguageNames.LanguageName(s.Lang);
                    int n;
                    counts.TryGetValue(name, out n);
                    n++;
                    counts[name] = n;
                    var total = sorted.Count(x => LanguageNames.LanguageName(x.Lang) == name);
                    items.Add(new SubtitleMenuItem
                    {
                        Kind = SubtitleMenuKind.Addon,
                        Label = total > 1 ? name + " " + n : name,
                        Description = s.AddonName,
                        IsSelected = ActiveAddonSub != null && ActiveAddonSub.Key == s.Key,
                        Sub = s,
                    });
                }
            }
            items.Add(new SubtitleMenuItem { Kind = SubtitleMenuKind.Header, Label = S("PlayerOptions", "Options") });
            items.Add(new SubtitleMenuItem
            {
                Kind = SubtitleMenuKind.Delay,
                Label = S("PlayerSubtitleDelayMenu", "Subtitle delay"),
                Description = (SubtitleDelay >= 0 ? "+" : "") + SubtitleDelay.ToString("F2", CultureInfo.InvariantCulture) + "s",
            });
            return items;
        }

        public List<AudioMenuItem> GetAudioMenuItems()
        {
            var items = new List<AudioMenuItem>();
            var audio = Tracks != null && Tracks.Audio != null ? Tracks.Audio : new List<PlayerTrack>();
            foreach (var t in audio)
            {
                items.Add(new AudioMenuItem
                {
                    Label = TrackLabel(t),
                    Description = !string.IsNullOrEmpty(t.Codec) ? t.Codec.ToUpperInvariant() : null,
                    IsSelected = t.Id == Tracks.AudioId,
                    TrackId = t.Id,
                });
            }
            return items;
        }

        public List<SpeedMenuItem> GetSpeedMenuItems()
        {
            var items = new List<SpeedMenuItem>();
            foreach (var speed in Speeds)
            {
                items.Add(new SpeedMenuItem
                {
                    Label = speed == 1
                        ? S("PlayerNormalSpeed", "Normal")
                        : speed.ToString("0.##", CultureInfo.InvariantCulture) + "×",
                    Value = speed,
                    IsSelected = speed == Rate,
                });
            }
            return items;
        }

        public List<AspectMenuItem> GetAspectMenuItems()
        {
            var items = new List<AspectMenuItem>();
            foreach (var pair in Aspects)
            {
                items.Add(new AspectMenuItem
                {
                    Label = AspectLabel(pair[0], pair[1]),
                    Value = pair[0],
                    IsSelected = pair[0] == Aspect,
                });
            }
            return items;
        }

        private string AspectLabel(string value, string fallback)
        {
            switch (value)
            {
                case "fit": return S("PlayerFit", "Fit");
                case "fill": return S("PlayerFillCrop", "Fill (crop)");
                case "stretch": return S("PlayerStretch", "Stretch");
                default: return fallback;
            }
        }

        public List<SourceMenuItem> GetSourcesMenuItems()
        {
            var items = new List<SourceMenuItem>();
            for (var i = 0; i < _sources.Count; i++)
            {
                var s = _sources[i];
                var info = StreamAnalysis.DescribeStream(s, s.AddonName ?? "");
                var badges = new List<string>();
                if (info != null && info.Badges != null)
                {
                    foreach (var badge in info.Badges) badges.Add(badge);
                }
                var descriptionParts = new List<string>();
                if (!string.IsNullOrEmpty(s.AddonName)) descriptionParts.Add(s.AddonName);
                if (info != null && !string.IsNullOrEmpty(info.SizeText)) descriptionParts.Add(info.SizeText);
                descriptionParts.AddRange(badges);
                items.Add(new SourceMenuItem
                {
                    Label = info != null && !string.IsNullOrEmpty(info.Name) ? info.Name : (s.Name ?? s.Url ?? string.Empty),
                    Description = string.Join(" · ", descriptionParts),
                    Badge = info != null ? info.Resolution : null,
                    IsSelected = Stream != null && s.Url == Stream.Url,
                    Index = i,
                });
            }
            return items;
        }

        public List<DelayMenuItem> GetDelayMenuItems()
        {
            var items = new List<DelayMenuItem>();
            foreach (var step in DelaySteps)
            {
                items.Add(new DelayMenuItem
                {
                    Label = step == 0
                        ? S("PlayerDelayReset", "Reset")
                        : (step > 0 ? "+" : "") + step.ToString("0.##", CultureInfo.InvariantCulture) + "s",
                    Description = step == 0
                        ? S("PlayerDelayBackToNoDelay", "Back to no delay")
                        : step > 0
                            ? S("PlayerDelayLater", "Subtitles later")
                            : S("PlayerDelayEarlier", "Subtitles earlier"),
                    Step = step,
                });
            }
            return items;
        }

        /// <summary>Null when the episode list could not be loaded (toast already shown).</summary>
        public async Task<List<EpisodeMenuItem>> GetEpisodesMenuItemsAsync()
        {
            if (Meta == null || !_season.HasValue) return new List<EpisodeMenuItem>();
            var full = Meta;
            if (full.Videos == null)
            {
                try
                {
                    full = await _stremio.FetchMetaAsync(full.Type, full.Id).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error("Loading episode list failed", ex);
                    RequestToast(S("PlayerEpisodeListFailed", "Couldn't load the episode list"));
                    return null;
                }
                if (full == null) return null;
            }
            _fullMeta = full;
            EpisodesMenuTitle = _season.Value == 0
                ? S("Specials", "Specials")
                : string.Format(CultureInfo.InvariantCulture, S("SeasonLabel", "Season {0}"), _season.Value);
            var items = new List<EpisodeMenuItem>();
            foreach (var v in MetaHelpers.SortedVideos(full).Where(v => v.Season == _season.Value))
            {
                var name = !string.IsNullOrEmpty(v.Name) ? v.Name : v.Title;
                items.Add(new EpisodeMenuItem
                {
                    Label = v.Episode + ". " + (!string.IsNullOrEmpty(name) ? name : S("Episode", "Episode") + " " + v.Episode),
                    Description = MetaHelpers.IsReleased(v) ? null : S("PlayerNotReleased", "Not released yet"),
                    IsSelected = v.Episode == _episode,
                    Video = v,
                });
            }
            return items;
        }

        public void ChooseSubtitle(SubtitleMenuItem item)
        {
            if (item == null) return;
            _subtitleChosen = true;
            switch (item.Kind)
            {
                case SubtitleMenuKind.Off:
                    ActiveAddonSub = null;
                    _activeCues = null;
                    _player.SetSubtitleTrack(-1);
                    RenderSubtitles();
                    break;
                case SubtitleMenuKind.Track:
                    ActiveAddonSub = null;
                    _activeCues = null;
                    RenderSubtitles();
                    _player.SetSubtitleTrack(item.TrackId);
                    break;
                case SubtitleMenuKind.Addon:
                    var _ = SelectAddonSubtitleAsync(item.Sub);
                    break;
                case SubtitleMenuKind.Delay:
                    RaiseDelayMenuRequested();
                    break;
            }
        }

        public void ChooseAudio(int trackId)
        {
            _player.SetAudioTrack(trackId);
        }

        public void ChooseSpeed(double speed)
        {
            Rate = speed;
            RateText = speed == 1 ? "1×" : speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";
            _lastTime = CurrentTime();
            _lastTimeAt = DateTime.UtcNow;
            _player.SetRate(speed);
        }

        public void ChooseAspect(string aspect)
        {
            Aspect = aspect;
            _player.SetAspect(aspect);
        }

        public void ChooseSource(int index)
        {
            if (index < 0 || index >= _sources.Count) return;
            var choice = _sources[index];
            if (Stream != null && choice.Url == Stream.Url) return;
            Save(true);
            Start(choice, CurrentTime());
        }

        public void ChooseEpisode(Video video)
        {
            if (video == null || video.Episode == _episode) return;
            Save(true);
            _navigation.PlayVideo(_fullMeta ?? Meta, video);
        }

        /// <summary>Delay steps are cumulative (web); 0 resets. Reopens the delay menu.</summary>
        public void ChooseDelay(double step)
        {
            SubtitleDelay = step == 0 ? 0 : SubtitleDelay + step;
            _player.SetSubtitleDelay((int)Math.Round(SubtitleDelay * 1000));
            RequestToast(string.Format(CultureInfo.InvariantCulture,
                S("PlayerSubtitleDelayToast", "Subtitle delay {0}s"),
                (SubtitleDelay >= 0 ? "+" : "") + SubtitleDelay.ToString("F2", CultureInfo.InvariantCulture)));
            RaiseDelayMenuRequested();
        }

        private static string TrackLabel(PlayerTrack track)
        {
            var lang = !string.IsNullOrEmpty(track.Lang) ? LanguageNames.LanguageName(track.Lang) : null;
            var name = track.Title ?? track.Name ?? string.Empty;
            name = Regex.Replace(name, @"^Track \d+\s*-?\s*", string.Empty, RegexOptions.IgnoreCase);
            if (name.StartsWith("[", StringComparison.Ordinal)) name = name.Substring(1);
            if (name.EndsWith("]", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 1);
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(lang)) parts.Add(lang);
            if (!string.IsNullOrEmpty(name) && name != lang) parts.Add(name);
            return parts.Count > 0 ? string.Join(" · ", parts) : "Track " + track.Id;
        }

        // ---------- Next episode ----------

        private void MaybeOfferNext()
        {
            if (!IsSeries || !_settings.GetBool("autoplayNext", true) || _nextDismissed || _nextInfo != null) return;
            if (Duration <= 0 || Duration < 300) return;
            var remaining = Duration - CurrentTime();
            if (remaining > 0 && remaining <= Math.Max(30, Duration * 0.03))
            {
                var _ = StartNextCountdownAsync(false);
            }
        }

        private async Task<NextTarget> FindNextVideoAsync()
        {
            var full = Meta;
            if (full == null || !_season.HasValue || !_episode.HasValue) return null;
            if (full.Videos == null) full = await _stremio.FetchMetaAsync(full.Type, full.Id).ConfigureAwait(false);
            if (full == null) return null;
            var next = MetaHelpers.NextEpisode(full, _season.Value, _episode.Value);
            if (next == null || !MetaHelpers.IsReleased(next)) return null;
            return new NextTarget { Full = full, Next = next };
        }

        public async Task StartNextCountdownAsync(bool atEnd)
        {
            if (_nextInfo != null)
            {
                if (atEnd) _nextInfo.AtEnd = true;
                return;
            }
            var info = new NextInfo { AtEnd = atEnd, Countdown = _settings.GetInt("nextEpisodeCountdown", 10) };
            _nextInfo = info;
            NextCountdown = info.Countdown;
            NextTarget found = null;
            try
            {
                found = await FindNextVideoAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Finding next episode failed", ex);
            }
            if (_nextInfo != info || _closed) return;
            if (found == null)
            {
                _nextInfo = null;
                _nextDismissed = true;
                IsNextVisible = false;
                if (atEnd) Exit();
                return;
            }
            info.Video = found.Next;
            info.FullMeta = found.Full;
            await RunOnUi(() =>
            {
                NextVideo = found.Next;
                NextThumbnail = found.Next.Thumbnail;
                NextTitle = Formatting.FormatEpisodeCode(found.Next.Season, found.Next.Episode)
                    + " " + (!string.IsNullOrEmpty(found.Next.Name) ? found.Next.Name : found.Next.Title ?? string.Empty);
                IsNextVisible = true;
                RaiseFocus("next");
                _nextTimer.Interval = TimeSpan.FromSeconds(1);
                _nextTimer.Start();
            }).ConfigureAwait(false);
        }

        private void OnNextTimerTick(object sender, object e)
        {
            var info = _nextInfo;
            if (info == null) return;
            // Count down only once the episode has ended, or while it plays its credits.
            if (PlayState != "playing" && !info.AtEnd) return;
            info.Countdown--;
            if (info.Countdown <= 0)
            {
                var _ = PlayNextAsync();
            }
            else
            {
                NextCountdown = info.Countdown;
            }
        }

        public void CancelNext(bool byUser)
        {
            if (_nextInfo == null) return;
            StopTimer(_nextTimer);
            var atEnd = _nextInfo.AtEnd;
            _nextInfo = null;
            _nextDismissed = byUser;
            IsNextVisible = false;
            NextVideo = null;
            if (atEnd && byUser) Exit();
            else ShowControls(null);
        }

        public async Task PlayNextAsync()
        {
            NextTarget target = null;
            if (_nextInfo != null && _nextInfo.Video != null)
            {
                target = new NextTarget { Full = _nextInfo.FullMeta, Next = _nextInfo.Video };
            }
            if (_nextInfo != null) StopTimer(_nextTimer);
            _nextInfo = null;
            IsNextVisible = false;
            NextVideo = null;
            if (target == null)
            {
                try
                {
                    target = await FindNextVideoAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error("Finding next episode failed", ex);
                    target = null;
                }
                if (target == null)
                {
                    RequestToast(S("PlayerNoNextEpisode", "There is no next episode yet"));
                    return;
                }
            }
            Save(true, finished: _ended);
            var full = target.Full;
            var next = target.Next;
            LoadingText = S("PlayerFindingSource", "Finding a source…");
            Ready = false;
            RaisePropertyChanged(nameof(IsLoadingVisible));
            // Same addon, and the same release group when the addon marks one.
            // M4: no debrid, so only http streams are usable — do NOT call any debrid API.
            var addon = Stream != null
                ? _stremio.EnabledAddons().FirstOrDefault(a => a.TransportUrl == Stream.AddonUrl)
                : null;
            var bingeGroup = Stream != null && Stream.BehaviorHints != null ? Stream.BehaviorHints.BingeGroup : null;
            StreamItem picked = null;
            var siblings = new List<StreamItem>();
            if (addon != null)
            {
                try
                {
                    var streams = (await _stremio.FetchStreamsAsync(addon, Meta.Type, next.Id).ConfigureAwait(false))
                        .Where(s => StreamAnalysis.IsPlayable(s)).ToList();
                    foreach (var s in streams)
                    {
                        s.AddonUrl = addon.TransportUrl;
                        s.AddonName = Stream.AddonName;
                        siblings.Add(s);
                    }
                    picked = (!string.IsNullOrEmpty(bingeGroup)
                        ? siblings.FirstOrDefault(s => s.BehaviorHints != null && s.BehaviorHints.BingeGroup == bingeGroup)
                        : null) ?? siblings.FirstOrDefault();
                }
                catch (Exception ex)
                {
                    Log.Error("Finding next episode streams failed", ex);
                    picked = null;
                }
            }
            if (_closed) return;
            if (picked != null)
            {
                // The old page's Dispose runs after the new page opens the
                // player; it must not stop the engine out from under it.
                _suppressEngineStop = true;
                _navigation.OpenPlayer(full, next, picked, siblings, restart: false, fallbackToStreams: false);
            }
            else
            {
                _navigation.PlayVideo(full, next);
            }
        }

        // ---------- Exit / dispose ----------

        public void Exit()
        {
            if (_closed) return;
            CommitScrub();
            Save(true);
            Report("stop");
            _navigation.GoBackOrHome();
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            Save(true);
            Report("stop");
            StopTimer(_openTimer);
            StopTimer(_hideTimer);
            StopTimer(_stallTimer);
            StopTimer(_scrubTimer);
            StopTimer(_fallbackTimer);
            StopTimer(_nextTimer);
            _player.StateChanged -= OnPlayerStateChanged;
            _player.TimeUpdated -= OnPlayerTimeUpdated;
            _player.DurationChanged -= OnPlayerDurationChanged;
            _player.BufferingChanged -= OnPlayerBufferingChanged;
            _player.TracksChanged -= OnPlayerTracksChanged;
            _player.Ready -= OnPlayerReady;
            _player.Ended -= OnPlayerEnded;
            _player.EngineSwitched -= OnPlayerEngineSwitched;
            AppEvents.SettingsChanged -= OnSettingsChanged;
            if (!_suppressEngineStop) _player.Stop();
        }

        // ---------- Helpers ----------

        private static void StopTimer(DispatcherTimer timer)
        {
            if (timer != null) timer.Stop();
        }

        private static string JoinNonEmpty(string separator, params string[] parts)
        {
            var kept = new List<string>();
            foreach (var part in parts)
            {
                if (!string.IsNullOrEmpty(part)) kept.Add(part);
            }
            return string.Join(separator, kept);
        }

        private async Task RunOnUi(Action action)
        {
            if (_dispatcher.HasThreadAccess) action();
            else await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action()).AsTask().ConfigureAwait(false);
        }
    }
}
