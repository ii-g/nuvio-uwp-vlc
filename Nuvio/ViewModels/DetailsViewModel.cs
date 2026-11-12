using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    /// <summary>One episode in the episode track: the video plus its watched/progress state.</summary>
    internal sealed class EpisodeCardItem
    {
        public EpisodeCardItem(Video video, bool isWatched, double progress, string unreleasedTag, string episodeFallback)
        {
            Video = video;
            IsWatched = isWatched;
            Progress = progress;
            UnreleasedTag = unreleasedTag;
            Code = Formatting.FormatEpisodeCode(video.Season, video.Episode);
            EpisodeNumberText = "E" + video.Episode;
            Title = !string.IsNullOrEmpty(video.Name) ? video.Name
                : (!string.IsNullOrEmpty(video.Title) ? video.Title : episodeFallback + " " + video.Episode);
            Overview = video.Overview ?? video.Description;
            Thumbnail = video.Thumbnail;
        }

        public Video Video { get; }
        public bool IsWatched { get; }
        public double Progress { get; }
        /// <summary>formatAirs() tag for an unreleased episode, else null.</summary>
        public string UnreleasedTag { get; }
        public string Code { get; }
        public string EpisodeNumberText { get; }
        public string Title { get; }
        public string Overview { get; }
        public string Thumbnail { get; }
    }

    /// <summary>One credits line: Cast / Director / Writer with the first 8 names joined.</summary>
    internal sealed class CreditLine
    {
        public string Label { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// Details: the full title screen — hero, actions, episodes, credits, TMDB extras.
    /// Ported from views/details.js. The page owns event subscriptions and focus; this
    /// class owns loading and state, and is safe to call from any thread (all UI-bound
    /// sets are marshalled through the dispatcher).
    /// </summary>
    internal sealed class DetailsViewModel : ViewModelBase
    {
        private readonly StremioService _stremio;
        private readonly ProgressService _progress;
        private readonly LibraryService _library;
        private readonly NavigationService _navigation;
        private readonly AppSettings _settings;
        private readonly TmdbService _tmdb;
        private readonly CoreDispatcher _dispatcher;
        private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse("Resources");
        private int _loadGeneration;

        /// <summary>Raised when a load starts (preview shown): the page puts up the preview backdrop.</summary>
        public event EventHandler LoadStarted;
        /// <summary>Raised when the full meta is applied: the page shows its backdrop and focuses Play.</summary>
        public event EventHandler LoadCompleted;
        /// <summary>Raised when TMDB extras are applied: the page refreshes the backdrop.</summary>
        public event EventHandler TmdbExtrasLoaded;

        private Meta _meta;
        private Meta _previewMeta;
        private string _title;
        private string _logoUrl;
        private string _yearText;
        private string _runtimeText;
        private string _ratingText;
        private string _typeText;
        private string _description;
        private string _backdropUrl;
        private string _playLabel;
        private string _libraryLabel;
        private string _watchedLabel;
        private string _trailerId;
        private bool _isInLibrary;
        private bool _isWatched;
        private bool _hasTrailer;
        private int _selectedSeason;
        private bool _seasonChosen;
        private List<TmdbSimilarItem> _similarRaw = new List<TmdbSimilarItem>();

        public DetailsViewModel(StremioService stremio, ProgressService progress, LibraryService library, NavigationService navigation, AppSettings settings)
        {
            _stremio = stremio;
            _progress = progress;
            _library = library;
            _navigation = navigation;
            _settings = settings;
            _tmdb = new TmdbService(settings);
            _dispatcher = Window.Current.Dispatcher;
        }

        public Meta Meta
        {
            get { return _meta; }
            private set { SetProperty(ref _meta, value); }
        }

        public Meta PreviewMeta
        {
            get { return _previewMeta; }
            private set { SetProperty(ref _previewMeta, value); }
        }

        public bool IsMovie { get { return Meta != null && Meta.Type == "movie"; } }

        /// <summary>The title shown while loading: the loaded meta, else the catalog preview.</summary>
        public Meta HeroMeta { get { return Meta ?? PreviewMeta; } }

        public string Title
        {
            get { return _title; }
            private set { SetProperty(ref _title, value); }
        }

        public string LogoUrl
        {
            get { return _logoUrl; }
            private set { SetProperty(ref _logoUrl, value); }
        }

        public string YearText
        {
            get { return _yearText; }
            private set { SetProperty(ref _yearText, value); }
        }

        public string RuntimeText
        {
            get { return _runtimeText; }
            private set { SetProperty(ref _runtimeText, value); }
        }

        public string RatingText
        {
            get { return _ratingText; }
            private set { SetProperty(ref _ratingText, value); }
        }

        public string TypeText
        {
            get { return _typeText; }
            private set { SetProperty(ref _typeText, value); }
        }

        public string Description
        {
            get { return _description; }
            private set { SetProperty(ref _description, value); }
        }

        /// <summary>Backdrop URL; the page cross-fades to it when it changes (load, TMDB fill).</summary>
        public string BackdropUrl
        {
            get { return _backdropUrl; }
            private set { SetProperty(ref _backdropUrl, value); }
        }

        public List<string> Genres { get; private set; } = new List<string>();

        /// <summary>"Play" / "Resume · 1h 12m left" / "Play S1 E2" / "Resume S1 E2 · …".</summary>
        public string PlayLabel
        {
            get { return _playLabel; }
            private set { SetProperty(ref _playLabel, value); }
        }

        /// <summary>True when Play resumes rather than starting from the beginning.</summary>
        public bool PlayIsResume { get; private set; }

        /// <summary>The episode Play/Resume targets, null for a movie.</summary>
        public Video PlayTargetVideo { get; private set; }

        public bool IsInLibrary
        {
            get { return _isInLibrary; }
            private set { SetProperty(ref _isInLibrary, value); }
        }

        public string LibraryLabel
        {
            get { return _libraryLabel; }
            private set { SetProperty(ref _libraryLabel, value); }
        }

        /// <summary>Movies only.</summary>
        public bool IsWatched
        {
            get { return _isWatched; }
            private set { SetProperty(ref _isWatched, value); }
        }

        public string WatchedLabel
        {
            get { return _watchedLabel; }
            private set { SetProperty(ref _watchedLabel, value); }
        }

        public string TrailerId
        {
            get { return _trailerId; }
            private set { SetProperty(ref _trailerId, value); }
        }

        public bool HasTrailer
        {
            get { return _hasTrailer; }
            private set { SetProperty(ref _hasTrailer, value); }
        }

        public List<int> Seasons { get; private set; } = new List<int>();

        public int SelectedSeason
        {
            get { return _selectedSeason; }
            private set { SetProperty(ref _selectedSeason, value); }
        }

        public ObservableCollection<EpisodeCardItem> Episodes { get; } = new ObservableCollection<EpisodeCardItem>();
        public ObservableCollection<CreditLine> Credits { get; } = new ObservableCollection<CreditLine>();
        public ObservableCollection<TmdbCastMember> CastItems { get; } = new ObservableCollection<TmdbCastMember>();
        public ObservableCollection<CatalogCardItem> SimilarItems { get; } = new ObservableCollection<CatalogCardItem>();

        public bool HasEpisodes { get { return Meta != null && Meta.Type != "movie" && MetaHelpers.SortedVideos(Meta).Count > 0; } }
        public bool HasCredits { get { return Credits.Count > 0; } }
        public bool HasCast { get { return CastItems.Count > 0; } }
        public bool HasSimilar { get { return SimilarItems.Count > 0; } }

        /// <summary>Centered spinner: loading with no preview artwork to show yet.</summary>
        public bool ShowLoadingSpinner { get { return IsLoading && PreviewMeta == null && !HasError; } }

        /// <summary>The hero + sections: a loaded meta or a preview, and no load error.</summary>
        public bool ShowContent { get { return (Meta != null || PreviewMeta != null) && !HasError; } }

        /// <summary>The episode to focus after a season change: the next-up episode in the
        /// season, else the season's first episode, else null.</summary>
        public string SeasonFocusVideoId { get; private set; }

        private string S(string key, string fallback)
        {
            var text = _strings.GetString(key);
            return string.IsNullOrEmpty(text) ? fallback : text;
        }

        public async Task LoadAsync(string type, string id, Meta preview)
        {
            var generation = ++_loadGeneration;
            await RunOnUi(() =>
            {
                IsLoading = true;
                ErrorMessage = null;
                PreviewMeta = preview;
                Meta = null;
                Seasons = new List<int>();
                SelectedSeason = 0;
                _seasonChosen = false;
                Episodes.Clear();
                Credits.Clear();
                CastItems.Clear();
                SimilarItems.Clear();
                _similarRaw = new List<TmdbSimilarItem>();
                RaisePropertyChanged(nameof(HasEpisodes));
                RaisePropertyChanged(nameof(HasCredits));
                RaisePropertyChanged(nameof(HasCast));
                RaisePropertyChanged(nameof(HasSimilar));
                RaisePropertyChanged(nameof(ShowLoadingSpinner));
                RaisePropertyChanged(nameof(ShowContent));
                if (preview != null) UpdateHeroFrom(preview);
            }).ConfigureAwait(false);
            var started = LoadStarted;
            if (started != null) started(this, EventArgs.Empty);
            try
            {
                var meta = await _stremio.FetchMetaAsync(type, id).ConfigureAwait(false);
                if (generation != _loadGeneration) return;
                if (preview != null)
                {
                    // Catalog previews sometimes have better artwork than the meta addon.
                    meta.Logo = meta.Logo ?? preview.Logo;
                    meta.Background = meta.Background ?? preview.Background;
                    meta.Poster = meta.Poster ?? preview.Poster;
                }
                await RunOnUi(() =>
                {
                    Meta = meta;
                    UpdateHeroFrom(meta);
                    UpdateCredits();
                    RefreshDynamicState();
                    IsLoading = false;
                    RaisePropertyChanged(nameof(IsMovie));
                    RaisePropertyChanged(nameof(HasEpisodes));
                    RaisePropertyChanged(nameof(ShowLoadingSpinner));
                    RaisePropertyChanged(nameof(ShowContent));
                }).ConfigureAwait(false);
                var completed = LoadCompleted;
                if (completed != null) completed(this, EventArgs.Empty);
                var _ = LoadTmdbExtrasAsync(meta, generation);
            }
            catch (Exception ex)
            {
                Log.Error("Loading details failed", ex);
                if (generation != _loadGeneration) return;
                await RunOnUi(() =>
                {
                    ErrorMessage = ex.Message;
                    IsLoading = false;
                    RaisePropertyChanged(nameof(ShowLoadingSpinner));
                    RaisePropertyChanged(nameof(ShowContent));
                }).ConfigureAwait(false);
                // The error state is in place above; signal the page the
                // same way the success path does, so its ContentReady fires
                // and the arrival settle completes instead of burning all
                // 10 watchdog attempts with the guard armed.
                var completed = LoadCompleted;
                if (completed != null) completed(this, EventArgs.Empty);
            }
        }

        private void UpdateHeroFrom(Meta meta)
        {
            Title = meta.Name ?? string.Empty;
            LogoUrl = meta.Logo;
            YearText = MetaHelpers.YearText(meta);
            RuntimeText = Formatting.FormatRuntime(meta.Runtime);
            RatingText = string.IsNullOrEmpty(meta.ImdbRating) ? string.Empty : "★ " + meta.ImdbRating;
            TypeText = Formatting.TypeLabel(meta.Type);
            Description = meta.Description ?? string.Empty;
            Genres = MetaHelpers.GenresOf(meta).Take(4).ToList();
            RaisePropertyChanged(nameof(Genres));
            BackdropUrl = meta.Background;
        }

        private void UpdateCredits()
        {
            Credits.Clear();
            if (Meta == null) return;
            var credits = new[]
            {
                new { Label = S("Cast", "Cast"), List = Meta.Cast },
                new { Label = S("Director", "Director"), List = Meta.Director },
                new { Label = S("Writer", "Writer"), List = Meta.Writer },
            };
            foreach (var credit in credits)
            {
                if (credit.List == null || credit.List.Count == 0) continue;
                Credits.Add(new CreditLine { Label = credit.Label, Text = string.Join(", ", credit.List.Take(8)) });
            }
            RaisePropertyChanged(nameof(HasCredits));
        }

        /// <summary>
        /// Recomputes everything that progress/watched/library events can change: the Play
        /// label, the library/watched buttons, the trailer, and the episode track. The
        /// season stays; if it is no longer valid it resets from upNext.
        /// </summary>
        public void RefreshDynamicState()
        {
            var meta = Meta;
            if (meta == null) return;
            UpdatePlayLabel();
            IsInLibrary = _library.InLibrary(meta.Id);
            LibraryLabel = IsInLibrary ? S("InLibrary", "In Library") : S("AddToLibrary", "Add to Library");
            IsWatched = meta.Type == "movie" && _progress.IsWatched(meta.Id, null, null);
            WatchedLabel = IsWatched ? S("Watched", "Watched") : S("MarkWatched", "Mark Watched");
            TrailerId = ((meta.Trailers ?? new List<TrailerRef>()).FirstOrDefault(t => t != null && !string.IsNullOrEmpty(t.Source)) ?? new TrailerRef()).Source
                ?? ((meta.TrailerStreams ?? new List<TrailerStreamRef>()).FirstOrDefault(t => t != null && !string.IsNullOrEmpty(t.YtId)) ?? new TrailerStreamRef()).YtId;
            HasTrailer = !string.IsNullOrEmpty(TrailerId);
            UpdateEpisodes();
        }

        private void UpdatePlayLabel()
        {
            var meta = Meta;
            if (meta == null)
            {
                PlayLabel = S("Play", "Play");
                PlayIsResume = false;
                PlayTargetVideo = null;
                return;
            }
            if (meta.Type == "movie")
            {
                var entry = _progress.GetProgress(meta.Id, null, null);
                if (entry != null && !_progress.IsCompleted(entry) && entry.Position > 30000)
                {
                    PlayLabel = S("Resume", "Resume") + " · " + Formatting.FormatTimeLeft(entry.Duration - entry.Position);
                    PlayIsResume = true;
                }
                else
                {
                    PlayLabel = S("Play", "Play");
                    PlayIsResume = false;
                }
                PlayTargetVideo = null;
                return;
            }
            var next = MetaHelpers.UpNext(meta, _progress);
            if (next.Video == null)
            {
                PlayLabel = S("Play", "Play");
                PlayIsResume = false;
                PlayTargetVideo = null;
                return;
            }
            var code = Formatting.FormatEpisodeCode(next.Video.Season, next.Video.Episode);
            if (next.Resume && next.Entry != null)
            {
                PlayLabel = S("Resume", "Resume") + " " + code + " · " + Formatting.FormatTimeLeft(next.Entry.Duration - next.Entry.Position);
                PlayIsResume = true;
            }
            else
            {
                PlayLabel = S("Play", "Play") + " " + code;
                PlayIsResume = false;
            }
            PlayTargetVideo = next.Video;
        }

        private void UpdateEpisodes()
        {
            var meta = Meta;
            if (meta == null || meta.Type == "movie")
            {
                Seasons = new List<int>();
                SelectedSeason = 0;
                _seasonChosen = false;
                Episodes.Clear();
                return;
            }
            var seasons = MetaHelpers.SeasonsOf(meta);
            Seasons = seasons;
            RaisePropertyChanged(nameof(Seasons));
            // Default season from upNext (web: the next episode's season, else the first).
            // 0 is a real season (Specials), so "not chosen yet" needs its own flag.
            if (!_seasonChosen || !seasons.Contains(SelectedSeason))
            {
                var next = MetaHelpers.UpNext(meta, _progress);
                SelectedSeason = next.Video != null && seasons.Contains(next.Video.Season)
                    ? next.Video.Season
                    : (seasons.Count > 0 ? seasons[0] : 0);
                _seasonChosen = true;
            }
            FillEpisodes();
        }

        private void FillEpisodes()
        {
            var meta = Meta;
            Episodes.Clear();
            if (meta == null) return;
            var episodeFallback = S("Episode", "Episode");
            foreach (var video in MetaHelpers.SortedVideos(meta).Where(v => v.Season == SelectedSeason))
            {
                var entry = _progress.GetProgress(meta.Id, video.Season, video.Episode);
                var watched = _progress.IsWatched(meta.Id, video.Season, video.Episode);
                var progress = entry != null && entry.Duration > 0 ? (double)entry.Position / entry.Duration : 0;
                var unreleased = MetaHelpers.IsReleased(video) ? null : Formatting.FormatAirs(video.Released);
                Episodes.Add(new EpisodeCardItem(video, watched, progress, unreleased, episodeFallback));
            }
            SeasonFocusVideoId = GetNextUpVideoId();
        }

        /// <summary>The next-up episode's id in the current season, else the season's first episode.</summary>
        public string GetNextUpVideoId()
        {
            var meta = Meta;
            if (meta == null) return null;
            var next = MetaHelpers.UpNext(meta, _progress);
            if (next.Video != null && next.Video.Season == SelectedSeason) return next.Video.Id;
            var first = MetaHelpers.SortedVideos(meta).FirstOrDefault(v => v.Season == SelectedSeason);
            return first != null ? first.Id : null;
        }

        public void SelectSeason(int season)
        {
            if (Meta == null || season == SelectedSeason) return;
            SelectedSeason = season;
            _seasonChosen = true;
            FillEpisodes();
        }

        public bool ToggleLibrary()
        {
            var meta = Meta;
            if (meta == null) return false;
            return _library.ToggleLibrary(meta);
        }

        public void ToggleWatchedMovie()
        {
            var meta = Meta;
            if (meta == null || meta.Type != "movie") return;
            _progress.MarkWatched(meta.Id, null, null, !IsWatched, meta.Name, meta.Type);
        }

        // ---------- Episode choice menu (Y / Menu) ----------

        public void MarkEpisodeWatched(Video video, bool value)
        {
            var meta = Meta;
            if (meta == null || video == null) return;
            _progress.MarkWatched(meta.Id, video.Season, video.Episode, value, meta.Name, meta.Type);
        }

        public void MarkPreviousWatched(Video video)
        {
            var meta = Meta;
            if (meta == null || video == null) return;
            foreach (var v in MetaHelpers.SortedVideos(meta))
            {
                var inScope = v.Season > 0 && (v.Season < video.Season || (v.Season == video.Season && v.Episode < video.Episode));
                if (inScope && MetaHelpers.IsReleased(v))
                {
                    _progress.MarkWatched(meta.Id, v.Season, v.Episode, true, meta.Name, meta.Type);
                }
            }
        }

        public void MarkSeasonWatched(Video video, bool value)
        {
            var meta = Meta;
            if (meta == null || video == null) return;
            foreach (var v in MetaHelpers.SortedVideos(meta))
            {
                if (v.Season == video.Season && MetaHelpers.IsReleased(v))
                {
                    _progress.MarkWatched(meta.Id, v.Season, v.Episode, value, meta.Name, meta.Type);
                }
            }
        }

        // ---------- TMDB extras ----------

        private async Task LoadTmdbExtrasAsync(Meta meta, int generation)
        {
            TmdbExtras extras;
            try
            {
                extras = await _tmdb.GetExtrasAsync(meta).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("TMDB lookup failed", ex);
                return;
            }
            if (extras == null || generation != _loadGeneration) return;
            await RunOnUi(() =>
            {
                if (Meta == null) return;
                if (string.IsNullOrEmpty(Meta.Background) && !string.IsNullOrEmpty(extras.Background))
                {
                    Meta.Background = extras.Background;
                    BackdropUrl = extras.Background;
                }
                if (string.IsNullOrEmpty(Meta.Logo) && !string.IsNullOrEmpty(extras.Logo))
                {
                    Meta.Logo = extras.Logo;
                    LogoUrl = extras.Logo;
                }
                if (extras.Cast.Count > 0)
                {
                    CastItems.Clear();
                    foreach (var member in extras.Cast) CastItems.Add(member);
                    RaisePropertyChanged(nameof(HasCast));
                }
                if (extras.Similar.Count > 0)
                {
                    _similarRaw = extras.Similar;
                    SimilarItems.Clear();
                    foreach (var item in extras.Similar)
                    {
                        SimilarItems.Add(new CatalogCardItem(item.Meta, false, 0));
                    }
                    RaisePropertyChanged(nameof(HasSimilar));
                }
            }).ConfigureAwait(false);
            var extrasLoaded = TmdbExtrasLoaded;
            if (extrasLoaded != null) extrasLoaded(this, EventArgs.Empty);
        }

        /// <summary>
        /// Opens a "More Like This" pick: resolves its IMDb id, then opens the title.
        /// Returns false when the title cannot be opened (no IMDb id, or the lookup failed).
        /// </summary>
        public async Task<bool> OpenSimilarAsync(Meta item)
        {
            if (item == null) return false;
            var raw = _similarRaw.FirstOrDefault(s => s.Meta != null && s.Meta.Id == item.Id);
            if (raw == null) return false;
            string imdbId;
            try
            {
                imdbId = await _tmdb.GetImdbIdAsync(raw.Kind, raw.TmdbId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("TMDB IMDb lookup failed", ex);
                return false;
            }
            if (string.IsNullOrEmpty(imdbId)) return false;
            _navigation.OpenTitle(new Meta
            {
                Id = imdbId,
                Type = item.Type,
                Name = item.Name,
                Poster = item.Poster,
                Background = item.Background,
            });
            return true;
        }

        private async Task RunOnUi(Action action)
        {
            if (_dispatcher.HasThreadAccess) action();
            else await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action()).AsTask().ConfigureAwait(false);
        }
    }
}
