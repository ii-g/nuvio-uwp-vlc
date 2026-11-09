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
    /// <summary>One card in a catalog row: the meta plus its watched/progress state.</summary>
    internal sealed class CatalogCardItem
    {
        public CatalogCardItem(Meta meta, bool isWatched, double progress)
        {
            Meta = meta;
            IsWatched = isWatched;
            Progress = progress;
        }

        private CatalogCardItem()
        {
            IsViewAll = true;
        }

        public static CatalogCardItem ViewAll()
        {
            return new CatalogCardItem();
        }

        public Meta Meta { get; }
        public bool IsWatched { get; }
        public double Progress { get; }
        public bool IsViewAll { get; }
    }

    /// <summary>One catalog row on Home: title, subtitle, shape, lazily loaded items.</summary>
    internal sealed class CatalogRowViewModel : ViewModelBase
    {
        private readonly StremioService _stremio;
        private readonly ProgressService _progress;
        private readonly AppSettings _settings;
        private readonly CoreDispatcher _dispatcher;
        private bool _isLoaded;

        public CatalogRowViewModel(StremioService stremio, ProgressService progress, AppSettings settings, CoreDispatcher dispatcher, Addon addon, Catalog catalog, string title, string subtitle)
        {
            _stremio = stremio;
            _progress = progress;
            _settings = settings;
            _dispatcher = dispatcher;
            Addon = addon;
            Catalog = catalog;
            Title = title;
            Subtitle = subtitle;
            Shape = (catalog.PosterShape ?? "poster").ToLowerInvariant();
        }

        public Addon Addon { get; }
        public Catalog Catalog { get; }
        public string Title { get; }
        public string Subtitle { get; }
        public string Shape { get; }
        public ObservableCollection<CatalogCardItem> Items { get; } = new ObservableCollection<CatalogCardItem>();

        public bool IsLoaded
        {
            get { return _isLoaded; }
            private set { SetProperty(ref _isLoaded, value); }
        }

        /// <summary>Loads the row's items (hideUnreleased filter, slice to 30). Returns true if it has items.</summary>
        public async Task<bool> LoadAsync()
        {
            IsLoading = true;
            try
            {
                var items = await _stremio.FetchCatalogAsync(Addon, Catalog).ConfigureAwait(false);
                if (_settings.HideUnreleased)
                {
                    items = items.Where(m => string.IsNullOrEmpty(m.Released) || ParseReleased(m.Released) <= DateTimeOffset.UtcNow).ToList();
                }
                var cards = new List<CatalogCardItem>();
                foreach (var meta in items.Take(30))
                {
                    var progress = _progress.GetProgress(meta.Id, null, null);
                    var fraction = progress != null && progress.Duration > 0 ? (double)progress.Position / progress.Duration : 0;
                    cards.Add(new CatalogCardItem(meta, _progress.IsWatched(meta.Id, null, null), fraction));
                }
                await RunOnUi(() =>
                {
                    Items.Clear();
                    foreach (var card in cards) Items.Add(card);
                    if (cards.Count > 0) Items.Add(CatalogCardItem.ViewAll());
                    IsLoaded = true;
                }).ConfigureAwait(false);
                return cards.Count > 0;
            }
            catch (Exception ex)
            {
                Log.Error("Row failed to load: " + Title, ex);
                return false;
            }
            finally
            {
                await RunOnUi(() => { IsLoading = false; }).ConfigureAwait(false);
            }
        }

        private static DateTimeOffset ParseReleased(string released)
        {
            DateTimeOffset date;
            return DateTimeOffset.TryParse(released, out date) ? date : DateTimeOffset.MaxValue;
        }

        private async Task RunOnUi(Action action)
        {
            if (_dispatcher.HasThreadAccess) action();
            else await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action()).AsTask().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Home: hero items, Continue Watching (with FetchMetaAsync enrichment), catalog rows.
    /// Reloads on addons/progress events. Ported from views/home.js.
    /// </summary>
    internal sealed class HomeViewModel : ViewModelBase
    {
        private const int HeroItemsMax = 6;
        private const int HeroCatalogsMax = 3;

        private readonly StremioService _stremio;
        private readonly ProgressService _progress;
        private readonly AppSettings _settings;
        private readonly NavigationService _navigation;
        private readonly CoreDispatcher _dispatcher;
        private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse("Resources");
        private readonly DispatcherTimer _progressRefreshTimer;
        private int _loadGeneration;
        private int _heroIndex;
        private bool _isEmpty;
        private string _emptyTitle;
        private string _emptyMessage;
        private string _heroYear;
        private string _heroRating;
        private string _heroType;
        private string _heroDescription;
        private string _heroPlayLabel;
        private bool _heroIsMovie;

        public HomeViewModel(StremioService stremio, ProgressService progress, AppSettings settings, NavigationService navigation)
        {
            _stremio = stremio;
            _progress = progress;
            _settings = settings;
            _navigation = navigation;
            _dispatcher = Window.Current.Dispatcher;
            _progressRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _progressRefreshTimer.Tick += OnProgressRefreshTick;
            _stremio.AddonsChanged += OnAddonsChanged;
            _progress.ProgressChanged += OnProgressChanged;
        }

        /// <summary>Raised after the Continue Watching row is rebuilt, so the page can restore focus.</summary>
        public event EventHandler ContinueRefreshed;

        public ObservableCollection<Meta> HeroItems { get; } = new ObservableCollection<Meta>();
        public ObservableCollection<ProgressEntry> ContinueItems { get; } = new ObservableCollection<ProgressEntry>();
        public ObservableCollection<CatalogRowViewModel> CatalogRows { get; } = new ObservableCollection<CatalogRowViewModel>();

        public int HeroIndex
        {
            get { return _heroIndex; }
            private set { SetProperty(ref _heroIndex, value); }
        }

        public Meta CurrentHero
        {
            get { return HeroItems.Count == 0 ? null : HeroItems[Math.Max(0, Math.Min(HeroIndex, HeroItems.Count - 1))]; }
        }

        public bool HasHero { get { return HeroItems.Count > 0; } }

        public string HeroYear
        {
            get { return _heroYear; }
            private set { SetProperty(ref _heroYear, value); }
        }

        public string HeroRating
        {
            get { return _heroRating; }
            private set { SetProperty(ref _heroRating, value); }
        }

        public string HeroType
        {
            get { return _heroType; }
            private set { SetProperty(ref _heroType, value); }
        }

        public string HeroDescription
        {
            get { return _heroDescription; }
            private set { SetProperty(ref _heroDescription, value); }
        }

        public string HeroPlayLabel
        {
            get { return _heroPlayLabel; }
            private set { SetProperty(ref _heroPlayLabel, value); }
        }

        public bool HeroIsMovie
        {
            get { return _heroIsMovie; }
            private set { SetProperty(ref _heroIsMovie, value); }
        }

        public List<string> HeroGenres { get; private set; } = new List<string>();

        public bool HasContinueItems { get { return ContinueItems.Count > 0; } }

        public bool IsEmpty
        {
            get { return _isEmpty; }
            private set { SetProperty(ref _isEmpty, value); }
        }

        public string EmptyTitle
        {
            get { return _emptyTitle; }
            private set { SetProperty(ref _emptyTitle, value); }
        }

        public string EmptyMessage
        {
            get { return _emptyMessage; }
            private set { SetProperty(ref _emptyMessage, value); }
        }

        private string S(string key, string fallback)
        {
            var text = _strings.GetString(key);
            return string.IsNullOrEmpty(text) ? fallback : text;
        }

        private void OnAddonsChanged(object sender, AddonsChangedEventArgs e)
        {
            var _ = LoadAsync();
        }

        private void OnProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            // Progress is saved every few seconds during playback; refresh once playback is over.
            _progressRefreshTimer.Stop();
            _progressRefreshTimer.Start();
        }

        private async void OnProgressRefreshTick(object sender, object e)
        {
            _progressRefreshTimer.Stop();
            await RefreshContinueAsync().ConfigureAwait(false);
        }

        public async Task LoadAsync()
        {
            var generation = ++_loadGeneration;
            IsLoading = true;
            ErrorMessage = null;
            try
            {
                await _stremio.LoadAddonsAsync().ConfigureAwait(false);
                var catalogs = _stremio.HomeCatalogs();
                var enabled = _stremio.EnabledAddons();
                var empty = enabled.Count == 0 || catalogs.Count == 0;
                await RunOnUi(() =>
                {
                    HeroItems.Clear();
                    ContinueItems.Clear();
                    CatalogRows.Clear();
                    HeroIndex = 0;
                    UpdateHeroDisplay();
                    IsEmpty = empty;
                    if (empty)
                    {
                        if (enabled.Count == 0)
                        {
                            EmptyTitle = S("HomeEmptyNoAddonsTitle", "Make Nuvio yours.");
                            EmptyMessage = S("HomeEmptyNoAddonsMessage", "Add an addon to start building your library.");
                        }
                        else
                        {
                            EmptyTitle = S("HomeEmptyNoCatalogsTitle", "No Home content available");
                            EmptyMessage = S("HomeEmptyNoCatalogsMessage", "Your enabled addons do not provide catalogs that can be shown on Home.");
                        }
                    }
                }).ConfigureAwait(false);
                if (empty)
                {
                    await RunOnUi(() => { IsLoading = false; }).ConfigureAwait(false);
                    return;
                }
                if (_settings.ShowHero)
                {
                    await LoadHeroAsync(catalogs, generation).ConfigureAwait(false);
                }
                if (generation != _loadGeneration) return;
                await LoadContinueAsync(generation).ConfigureAwait(false);
                if (generation != _loadGeneration) return;
                var rows = new List<CatalogRowViewModel>();
                foreach (var catalogRef in catalogs)
                {
                    var title = _settings.ShowCatalogType && !catalogRef.Catalog.HideType
                        ? (catalogRef.Catalog.Name ?? catalogRef.Catalog.Id) + " - " + Formatting.TypeLabel(catalogRef.Catalog.Type)
                        : (catalogRef.Catalog.Name ?? catalogRef.Catalog.Id);
                    rows.Add(new CatalogRowViewModel(_stremio, _progress, _settings, _dispatcher, catalogRef.Addon, catalogRef.Catalog, title, StremioService.AddonName(catalogRef.Addon)));
                }
                await RunOnUi(() =>
                {
                    foreach (var row in rows) CatalogRows.Add(row);
                    IsLoading = false;
                }).ConfigureAwait(false);
                // Rows load one at a time in the background; a row whose load returns empty removes itself.
                var _ = LoadRowsAsync(rows, generation);
            }
            catch (Exception ex)
            {
                Log.Error("Loading Home failed", ex);
                await RunOnUi(() =>
                {
                    ErrorMessage = ex.Message;
                    IsLoading = false;
                }).ConfigureAwait(false);
            }
        }

        private async Task LoadRowsAsync(IReadOnlyList<CatalogRowViewModel> rows, int generation)
        {
            foreach (var row in rows)
            {
                if (generation != _loadGeneration) return;
                var hasItems = await row.LoadAsync().ConfigureAwait(false);
                if (!hasItems)
                {
                    await RunOnUi(() => { CatalogRows.Remove(row); }).ConfigureAwait(false);
                }
            }
        }

        private async Task LoadHeroAsync(IReadOnlyList<CatalogRef> catalogs, int generation)
        {
            var items = new List<Meta>();
            foreach (var catalogRef in catalogs.Take(HeroCatalogsMax))
            {
                try
                {
                    var metas = await _stremio.FetchCatalogAsync(catalogRef.Addon, catalogRef.Catalog).ConfigureAwait(false);
                    foreach (var meta in metas)
                    {
                        if (items.Count >= HeroItemsMax) break;
                        if (string.IsNullOrEmpty(meta.Background) && string.IsNullOrEmpty(meta.Poster)) continue;
                        if (items.Any(m => m.Id == meta.Id)) continue;
                        items.Add(meta);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Hero catalog failed to load", ex);
                }
                if (items.Count >= HeroItemsMax) break;
            }
            if (generation != _loadGeneration) return;
            await RunOnUi(() =>
            {
                foreach (var meta in items) HeroItems.Add(meta);
                HeroIndex = 0;
                UpdateHeroDisplay();
                RaisePropertyChanged(nameof(HasHero));
            }).ConfigureAwait(false);
        }

        private async Task LoadContinueAsync(int generation)
        {
            var entries = await LoadContinueEntriesAsync().ConfigureAwait(false);
            if (generation != _loadGeneration) return;
            await RunOnUi(() =>
            {
                ContinueItems.Clear();
                foreach (var entry in entries) ContinueItems.Add(entry);
                RaisePropertyChanged(nameof(HasContinueItems));
            }).ConfigureAwait(false);
        }

        public async Task RefreshContinueAsync()
        {
            var generation = _loadGeneration;
            await LoadContinueAsync(generation).ConfigureAwait(false);
            var handler = ContinueRefreshed;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private async Task<IReadOnlyList<ProgressEntry>> LoadContinueEntriesAsync()
        {
            var entries = _progress.ContinueWatching();
            var resolved = new List<ProgressEntry>();
            foreach (var entry in entries)
            {
                // Entries synced from other devices only carry ids and positions; fill in the artwork.
                var needsMeta = entry.NextUp || string.IsNullOrEmpty(entry.Title) || (string.IsNullOrEmpty(entry.Background) && string.IsNullOrEmpty(entry.Poster));
                if (!needsMeta)
                {
                    FillContinueDisplay(entry);
                    resolved.Add(entry);
                    continue;
                }
                Meta meta = null;
                try
                {
                    meta = await _stremio.FetchMetaAsync(entry.ContentType, entry.ContentId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error("Continue Watching meta failed to load", ex);
                }
                if (meta == null)
                {
                    if (entry.NextUp) continue;
                    FillContinueDisplay(entry);
                    resolved.Add(entry);
                    continue;
                }
                if (string.IsNullOrEmpty(entry.Title)) entry.Title = meta.Name;
                if (string.IsNullOrEmpty(entry.Poster)) entry.Poster = meta.Poster;
                if (string.IsNullOrEmpty(entry.Background)) entry.Background = meta.Background;
                if (string.IsNullOrEmpty(entry.Logo)) entry.Logo = meta.Logo;
                if (entry.Season.HasValue && string.IsNullOrEmpty(entry.Thumbnail))
                {
                    var video = MetaHelpers.SortedVideos(meta).FirstOrDefault(v => v.Season == entry.Season.Value && v.Episode == (entry.Episode ?? 0));
                    if (video != null)
                    {
                        if (!string.IsNullOrEmpty(video.Thumbnail)) entry.Thumbnail = video.Thumbnail;
                        if (string.IsNullOrEmpty(entry.EpisodeTitle)) entry.EpisodeTitle = video.Name ?? video.Title;
                    }
                }
                if (!entry.NextUp)
                {
                    FillContinueDisplay(entry);
                    resolved.Add(entry);
                    continue;
                }
                var next = MetaHelpers.NextEpisode(meta, entry.Season ?? 0, entry.Episode ?? 0);
                if (next == null || !MetaHelpers.IsReleased(next)) continue;
                entry.Next = next;
                entry.NextIsNew = !string.IsNullOrEmpty(next.Released)
                    && DateTimeOffset.TryParse(next.Released, out var released)
                    && (DateTimeOffset.UtcNow - released).TotalMilliseconds < 7 * 86400000
                    && (DateTimeOffset.UtcNow - released).TotalMilliseconds >= 0;
                FillContinueDisplay(entry);
                resolved.Add(entry);
            }
            return resolved;
        }

        private void FillContinueDisplay(ProgressEntry entry)
        {
            entry.CardImage = entry.NextUp && entry.Next != null && !string.IsNullOrEmpty(entry.Next.Thumbnail)
                ? entry.Next.Thumbnail
                : (!string.IsNullOrEmpty(entry.Thumbnail) ? entry.Thumbnail
                    : (!string.IsNullOrEmpty(entry.Background) ? entry.Background : entry.Poster));
            entry.ProgressFraction = entry.Duration > 0 ? (double)entry.Position / entry.Duration : 0;
            entry.TagText = entry.NextIsNew ? S("NewEpisode", "New Episode") : S("NextUp", "Next Up");
            if (entry.NextUp)
            {
                entry.DisplaySubLine = S("NextUp", "Next Up")
                    + (entry.Next != null ? " · " + Formatting.FormatEpisodeCode(entry.Next.Season, entry.Next.Episode) : string.Empty);
            }
            else
            {
                var parts = new List<string>();
                var episode = Formatting.FormatEpisodeCode(entry.Season, entry.Episode);
                if (!string.IsNullOrEmpty(episode)) parts.Add(episode);
                var left = Formatting.FormatTimeLeft(entry.Duration - entry.Position);
                if (!string.IsNullOrEmpty(left)) parts.Add(left);
                entry.DisplaySubLine = string.Join(" · ", parts);
            }
        }

        public void AdvanceHero()
        {
            if (HeroItems.Count < 2) return;
            SetHeroIndex((HeroIndex + 1) % HeroItems.Count);
        }

        public void SetHeroIndex(int index)
        {
            if (HeroItems.Count == 0) return;
            var wrapped = ((index % HeroItems.Count) + HeroItems.Count) % HeroItems.Count;
            HeroIndex = wrapped;
            UpdateHeroDisplay();
        }

        private void UpdateHeroDisplay()
        {
            var meta = CurrentHero;
            if (meta == null)
            {
                HeroYear = string.Empty;
                HeroRating = string.Empty;
                HeroType = string.Empty;
                HeroDescription = string.Empty;
                HeroPlayLabel = S("Play", "Play");
                HeroIsMovie = false;
                HeroGenres = new List<string>();
                RaisePropertyChanged(nameof(HeroGenres));
                return;
            }
            HeroYear = MetaHelpers.YearText(meta);
            HeroRating = string.IsNullOrEmpty(meta.ImdbRating) ? string.Empty : "★ " + meta.ImdbRating;
            HeroType = Formatting.TypeLabel(meta.Type);
            HeroDescription = meta.Description ?? string.Empty;
            HeroIsMovie = meta.Type == "movie";
            HeroPlayLabel = HeroIsMovie ? S("Play", "Play") : S("Watch", "Watch");
            HeroGenres = MetaHelpers.GenresOf(meta).Take(3).ToList();
            RaisePropertyChanged(nameof(HeroGenres));
            RaisePropertyChanged(nameof(CurrentHero));
        }

        public void OpenTitle(Meta meta)
        {
            if (meta == null) return;
            _navigation.OpenTitle(meta);
        }

        public void OpenCatalog(CatalogRowViewModel row)
        {
            if (row == null) return;
            _navigation.NavigatePlaceholder(row.Title, "Catalogs land in Milestone 2.", "Milestone 2");
        }

        public void ResumeEntry(ProgressEntry entry)
        {
            if (entry == null) return;
            _navigation.ResumeEntry(entry);
        }

        public void DismissContinueWatching(string contentId)
        {
            _progress.DismissContinueWatching(contentId);
        }

        public void PlayHero()
        {
            var meta = CurrentHero;
            if (meta == null) return;
            if (HeroIsMovie) _navigation.PlayVideo(meta, null);
            else _navigation.OpenTitle(meta);
        }

        private async Task RunOnUi(Action action)
        {
            if (_dispatcher.HasThreadAccess) action();
            else await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action()).AsTask().ConfigureAwait(false);
        }
    }
}
