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
using Windows.Data.Json;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Nuvio.ViewModels
{
    /// <summary>One search-result row: one searchCatalogs() catalog, loaded eagerly (web search.js).</summary>
    internal sealed class SearchRowViewModel : ViewModelBase
    {
        private readonly StremioService _stremio;
        private readonly ProgressService _progress;
        private readonly CoreDispatcher _dispatcher;
        private bool _isLoaded;

        public SearchRowViewModel(StremioService stremio, ProgressService progress, CoreDispatcher dispatcher, Addon addon, Catalog catalog)
        {
            _stremio = stremio;
            _progress = progress;
            _dispatcher = dispatcher;
            Addon = addon;
            Catalog = catalog;
            Title = Formatting.TypeLabel(catalog.Type);
            Subtitle = (catalog.Name ?? catalog.Id) + " · " + StremioService.AddonName(addon);
        }

        public Addon Addon { get; }
        public Catalog Catalog { get; }
        public string Title { get; }
        public string Subtitle { get; }
        public ObservableCollection<CatalogCardItem> Items { get; } = new ObservableCollection<CatalogCardItem>();

        public bool IsLoaded
        {
            get { return _isLoaded; }
            private set { SetProperty(ref _isLoaded, value); }
        }

        /// <summary>Loads the row's search results. No 30-slice and no trailing View All
        /// card — the web search rows show every result (search.js has no trailing).</summary>
        public async Task<int> LoadAsync(string query)
        {
            await RunOnUi(() => { IsLoading = true; }).ConfigureAwait(false);
            try
            {
                var items = await _stremio.FetchCatalogAsync(Addon, Catalog, search: query).ConfigureAwait(false);
                var cards = new List<CatalogCardItem>();
                foreach (var meta in items)
                {
                    var progress = _progress.GetProgress(meta.Id, null, null);
                    var fraction = progress != null && progress.Duration > 0 ? (double)progress.Position / progress.Duration : 0;
                    cards.Add(new CatalogCardItem(meta, _progress.IsWatched(meta.Id, null, null), fraction));
                }
                await RunOnUi(() =>
                {
                    Items.Clear();
                    foreach (var card in cards) Items.Add(card);
                    IsLoaded = true;
                }).ConfigureAwait(false);
                return cards.Count;
            }
            catch (Exception ex)
            {
                // A failing row removes itself (web cardRow onEmpty) instead of breaking the search.
                Log.Error("Search row failed to load: " + Title, ex);
                return 0;
            }
            finally
            {
                await RunOnUi(() => { IsLoading = false; }).ConfigureAwait(false);
            }
        }

        private async Task RunOnUi(Action action)
        {
            if (_dispatcher.HasThreadAccess) action();
            else await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action()).AsTask().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Search: debounced run() over one row per searchCatalogs(), recent searches
    /// (profile-scoped, max 10, case-insensitive de-dup). Ported from views/search.js.
    /// </summary>
    internal sealed class SearchViewModel : ViewModelBase
    {
        private const int MaxRecent = 10;

        private readonly StremioService _stremio;
        private readonly ProgressService _progress;
        private readonly LocalStore _store;
        private readonly NavigationService _navigation;
        private readonly CoreDispatcher _dispatcher;
        private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse("Resources");
        private string _lastQuery;
        private int _searchGeneration;
        private bool _isNoProviders;
        private bool _isNoResults;
        private string _noResultsMessage;
        private bool _isRecentVisible;
        private bool _isEmptyHero;

        public SearchViewModel(StremioService stremio, ProgressService progress, LocalStore store, NavigationService navigation)
        {
            _stremio = stremio;
            _progress = progress;
            _store = store;
            _navigation = navigation;
            _dispatcher = Window.Current.Dispatcher;
            var _ = LoadRecentAsync();
            Rows.CollectionChanged += (s, e) => UpdateEmptyHero();
            UpdateEmptyHero();
        }

        /// <summary>Raised when a run's rows have all finished (or there were no providers),
        /// so the page can move focus to the first result after a submit/chip run.</summary>
        public event EventHandler SearchCompleted;

        public ObservableCollection<SearchRowViewModel> Rows { get; } = new ObservableCollection<SearchRowViewModel>();
        public ObservableCollection<string> RecentSearches { get; } = new ObservableCollection<string>();

        /// <summary>Recent searches show only while no query is active (web renderRecent).</summary>
        public bool IsRecentVisible
        {
            get { return _isRecentVisible; }
            private set { SetProperty(ref _isRecentVisible, value); }
        }

        /// <summary>The empty-state hero shows while no query is active and no results
        /// exist: first entry, or the query cleared. It hides the moment a query runs
        /// or results land; a query with no results shows the No Results state instead,
        /// and no providers shows the No Providers state. IsLoading is deliberately
        /// not part of the condition: the empty-query run flips it briefly, and the
        /// hero must not flicker out and back on first entry.</summary>
        public bool IsEmptyHero
        {
            get { return _isEmptyHero; }
            private set { SetProperty(ref _isEmptyHero, value); }
        }

        public bool IsNoProviders
        {
            get { return _isNoProviders; }
            private set { SetProperty(ref _isNoProviders, value); }
        }

        public bool IsNoResults
        {
            get { return _isNoResults; }
            private set { SetProperty(ref _isNoResults, value); }
        }

        public string NoResultsMessage
        {
            get { return _noResultsMessage; }
            private set { SetProperty(ref _noResultsMessage, value); }
        }

        public string LastQuery { get { return _lastQuery ?? string.Empty; } }

        private string S(string key, string fallback)
        {
            var text = _strings.GetString(key);
            return string.IsNullOrEmpty(text) ? fallback : text;
        }

        private async Task LoadRecentAsync()
        {
            try
            {
                var value = await _store.ReadProfileJsonAsync("recentSearches").ConfigureAwait(false);
                var list = new List<string>();
                if (value != null && value.ValueType == JsonValueType.Array)
                {
                    foreach (var item in value.GetArray())
                    {
                        if (item.ValueType == JsonValueType.String) list.Add(item.GetString());
                    }
                }
                await RunOnUi(() =>
                {
                    RecentSearches.Clear();
                    foreach (var q in list) RecentSearches.Add(q);
                    UpdateRecentVisible();
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Loading recent searches failed", ex);
            }
        }

        private void UpdateRecentVisible()
        {
            IsRecentVisible = RecentSearches.Count > 0 && string.IsNullOrEmpty(_lastQuery);
        }

        private void UpdateEmptyHero()
        {
            IsEmptyHero = string.IsNullOrEmpty(_lastQuery) && Rows.Count == 0 && !IsNoProviders;
        }

        /// <summary>Port of search.js run(): the same query twice is a no-op; an empty
        /// query shows recent searches; remember() runs before the providers check.</summary>
        public async Task RunAsync(string query)
        {
            try
            {
                var q = (query ?? string.Empty).Trim();
                if (q == _lastQuery) return;
                _lastQuery = q;
                var generation = ++_searchGeneration;
                await RunOnUi(() =>
                {
                    IsLoading = true;
                    IsNoProviders = false;
                    IsNoResults = false;
                    NoResultsMessage = null;
                    Rows.Clear();
                    UpdateRecentVisible();
                    UpdateEmptyHero();
                }).ConfigureAwait(false);
                if (string.IsNullOrEmpty(q))
                {
                    await RunOnUi(() => { IsLoading = false; UpdateEmptyHero(); }).ConfigureAwait(false);
                    RaiseSearchCompleted();
                    return;
                }
                await RememberAsync(q).ConfigureAwait(false);
                IReadOnlyList<CatalogRef> catalogs;
                try
                {
                    await _stremio.LoadAddonsAsync().ConfigureAwait(false);
                    catalogs = _stremio.SearchCatalogs();
                }
                catch (Exception ex)
                {
                    Log.Error("Loading search providers failed", ex);
                    await RunOnUi(() => { IsLoading = false; UpdateEmptyHero(); }).ConfigureAwait(false);
                    RaiseSearchCompleted();
                    return;
                }
                if (catalogs.Count == 0)
                {
                    await RunOnUi(() =>
                    {
                        IsNoProviders = true;
                        IsLoading = false;
                        UpdateEmptyHero();
                    }).ConfigureAwait(false);
                    RaiseSearchCompleted();
                    return;
                }
                var rows = new List<SearchRowViewModel>();
                foreach (var catalogRef in catalogs)
                {
                    rows.Add(new SearchRowViewModel(_stremio, _progress, _dispatcher, catalogRef.Addon, catalogRef.Catalog));
                }
                await RunOnUi(() =>
                {
                    foreach (var row in rows) Rows.Add(row);
                }).ConfigureAwait(false);
                var tasks = new List<Task<int>>();
                foreach (var row in rows) tasks.Add(row.LoadAsync(q));
                var counts = await Task.WhenAll(tasks).ConfigureAwait(false);
                if (generation != _searchGeneration) return;
                var found = 0;
                foreach (var count in counts) found += count;
                await RunOnUi(() =>
                {
                    // Rows that loaded nothing remove themselves (web cardRow onEmpty).
                    for (var i = Rows.Count - 1; i >= 0; i--)
                    {
                        if (Rows[i].Items.Count == 0) Rows.RemoveAt(i);
                    }
                    if (found == 0)
                    {
                        IsNoResults = true;
                        NoResultsMessage = string.Format(S("SearchNoResultsMessage", "Nothing found for \"{0}\"."), q);
                    }
                    IsLoading = false;
                    UpdateEmptyHero();
                }).ConfigureAwait(false);
                RaiseSearchCompleted();
            }
            catch (Exception ex)
            {
                Log.Error("Search failed", ex);
                await RunOnUi(() => { IsLoading = false; UpdateEmptyHero(); }).ConfigureAwait(false);
                RaiseSearchCompleted();
            }
        }

        private async Task RememberAsync(string q)
        {
            try
            {
                var list = RecentSearches.ToList();
                list.RemoveAll(x => string.Equals(x, q, StringComparison.OrdinalIgnoreCase));
                list.Insert(0, q);
                if (list.Count > MaxRecent) list.RemoveRange(MaxRecent, list.Count - MaxRecent);
                var array = new JsonArray();
                foreach (var item in list) array.Add(JsonHelpers.CreateString(item));
                await _store.WriteProfileJsonAsync("recentSearches", array).ConfigureAwait(false);
                await RunOnUi(() =>
                {
                    RecentSearches.Clear();
                    foreach (var item in list) RecentSearches.Add(item);
                    UpdateRecentVisible();
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Remembering the search failed", ex);
            }
        }

        public void OpenTitle(Meta meta)
        {
            if (meta == null) return;
            _navigation.OpenTitle(meta);
        }

        private void RaiseSearchCompleted()
        {
            // The page focuses UI on this event: marshal it, since runs complete off the UI thread.
            var _ = RunOnUi(() =>
            {
                var handler = SearchCompleted;
                if (handler != null) handler(this, EventArgs.Empty);
            });
        }

        private async Task RunOnUi(Action action)
        {
            if (_dispatcher.HasThreadAccess) action();
            else await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action()).AsTask().ConfigureAwait(false);
        }
    }
}
