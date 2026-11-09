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
    /// <summary>One genre filter chip. Name null means the "All" chip.</summary>
    internal sealed class GenreChip : ViewModelBase
    {
        private bool _isSelected;

        public GenreChip(string name, string displayName, bool isSelected)
        {
            Name = name;
            DisplayName = displayName;
            _isSelected = isSelected;
        }

        public string Name { get; }
        public string DisplayName { get; }

        public bool IsSelected
        {
            get { return _isSelected; }
            set { SetProperty(ref _isSelected, value); }
        }
    }

    /// <summary>
    /// "View All": one catalog as a grid, with genre filters and more pages as you
    /// scroll. Ported from views/catalog.js.
    /// </summary>
    internal sealed class CatalogViewModel : ViewModelBase
    {
        private const int NearEndThreshold = 14;

        private readonly StremioService _stremio;
        private readonly ProgressService _progress;
        private readonly NavigationService _navigation;
        private readonly CoreDispatcher _dispatcher;
        private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse("Resources");
        private readonly CatalogNavigation _navigationParameter;
        private Addon _addon;
        private Catalog _catalog;
        private string _genre;
        private int _generation;
        private int _skip;
        private bool _exhausted;
        private bool _firstLoadOfGeneration;
        private int _loadGeneration = -1;
        private HashSet<string> _seen = new HashSet<string>();
        private string _title;
        private string _subtitle;
        private bool _isUnavailable;
        private bool _isEmpty;
        private bool _isEmptyState;
        private string _emptyTitle;
        private string _emptyMessage;

        public CatalogViewModel(StremioService stremio, ProgressService progress, NavigationService navigation, CatalogNavigation navigationParameter)
        {
            _stremio = stremio;
            _progress = progress;
            _navigation = navigation;
            _navigationParameter = navigationParameter;
            _dispatcher = Window.Current.Dispatcher;
        }

        /// <summary>Raised when the first page of the current generation has loaded,
        /// so the page can focus the first card (web focus.restore(el)).</summary>
        public event EventHandler FirstPageLoaded;

        public ObservableCollection<CatalogCardItem> Items { get; } = new ObservableCollection<CatalogCardItem>();
        public ObservableCollection<GenreChip> Genres { get; } = new ObservableCollection<GenreChip>();

        public string Title
        {
            get { return _title; }
            private set { SetProperty(ref _title, value); }
        }

        public string Subtitle
        {
            get { return _subtitle; }
            private set { SetProperty(ref _subtitle, value); }
        }

        public bool IsUnavailable
        {
            get { return _isUnavailable; }
            private set { SetProperty(ref _isUnavailable, value); }
        }

        public bool IsEmpty
        {
            get { return _isEmpty; }
            private set { SetProperty(ref _isEmpty, value); }
        }

        /// <summary>True when the grid is replaced by the empty state (unavailable / no titles / load failed).</summary>
        public bool IsEmptyState
        {
            get { return _isEmptyState; }
            private set { SetProperty(ref _isEmptyState, value); }
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

        private void UpdateEmptyState()
        {
            IsEmptyState = _isUnavailable || _isEmpty || HasError;
        }

        /// <summary>Resolves the catalog from the route params and loads the first page.</summary>
        public async Task LoadAsync()
        {
            try
            {
                await _stremio.LoadAddonsAsync().ConfigureAwait(false);
                var found = _navigationParameter != null
                    ? _stremio.FindCatalog(_navigationParameter.TransportUrl, _navigationParameter.Type, _navigationParameter.Id)
                    : null;
                if (found == null)
                {
                    await RunOnUi(() =>
                    {
                        IsUnavailable = true;
                        EmptyTitle = S("CatalogUnavailableTitle", "Catalog unavailable");
                        EmptyMessage = S("CatalogUnavailableMessage", "The addon for this catalog is no longer installed or enabled.");
                        UpdateEmptyState();
                    }).ConfigureAwait(false);
                    return;
                }
                _addon = found.Addon;
                _catalog = found.Catalog;
                var name = _catalog.Name ?? _catalog.Id;
                var fullTitle = _catalog.HideType ? name : name + " - " + Formatting.TypeLabel(_catalog.Type);
                var genreExtra = StremioService.CatalogExtra(_catalog, "genre");
                var genres = genreExtra != null && genreExtra.Options != null ? genreExtra.Options : new List<string>();
                var isRequired = genreExtra != null && genreExtra.IsRequired;
                _genre = isRequired && genres.Count > 0 ? genres[0] : null;
                await RunOnUi(() =>
                {
                    Title = fullTitle;
                    Subtitle = StremioService.AddonName(_addon);
                    Genres.Clear();
                    if (!isRequired) Genres.Add(new GenreChip(null, S("GenreAll", "All"), _genre == null));
                    foreach (var g in genres) Genres.Add(new GenreChip(g, g, g == _genre));
                }).ConfigureAwait(false);
                await ResetAsync().ConfigureAwait(false);
                await LoadMoreAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Catalog page failed to load", ex);
                await RunOnUi(() =>
                {
                    if (Items.Count == 0)
                    {
                        ErrorMessage = ex.Message;
                        EmptyTitle = S("CatalogLoadFailedTitle", "Couldn’t load this catalog");
                        EmptyMessage = ex.Message;
                        UpdateEmptyState();
                    }
                }).ConfigureAwait(false);
            }
        }

        /// <summary>Switches the genre filter (web setGenre): same genre is a no-op;
        /// the chip selection updates in place, so focus stays on the chip instead
        /// of being rebuilt and re-focused by index as in the web.</summary>
        public void SetGenre(string genre)
        {
            if (genre == _genre) return;
            _genre = genre;
            foreach (var chip in Genres) chip.IsSelected = chip.Name == genre;
            var _ = ReloadAsync();
        }

        private async Task ReloadAsync()
        {
            await ResetAsync().ConfigureAwait(false);
            await LoadMoreAsync().ConfigureAwait(false);
        }

        private async Task ResetAsync()
        {
            _generation++;
            _skip = 0;
            _exhausted = false;
            _firstLoadOfGeneration = true;
            // A fresh set object, not a Clear(): a stale in-flight load still holds
            // the old set, so its de-dup can never swallow the new generation's cards.
            _seen = new HashSet<string>();
            await RunOnUi(() =>
            {
                Items.Clear();
                IsEmpty = false;
                ErrorMessage = null;
                UpdateEmptyState();
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Loads the next page (web loadMore). One load per generation at a time;
        /// a stale load's results are dropped by the generation check before they
        /// can touch skip/exhausted/seen or the grid. skip accumulates by the
        /// returned count (including duplicates), exactly as in the web.
        /// </summary>
        public async Task LoadMoreAsync()
        {
            if (_exhausted) return;
            if (_loadGeneration == _generation) return;
            _loadGeneration = _generation;
            var gen = _generation;
            var seen = _seen;
            await RunOnUi(() => { IsLoading = true; }).ConfigureAwait(false);
            try
            {
                var items = await _stremio.FetchCatalogAsync(_addon, _catalog, skip: _skip == 0 ? (int?)null : _skip, genre: _genre).ConfigureAwait(false);
                if (gen != _generation) return;
                var fresh = items.Where(m => !seen.Contains(m.Id)).ToList();
                foreach (var m in fresh) seen.Add(m.Id);
                _skip += items.Count;
                if (fresh.Count == 0 || StremioService.CatalogExtra(_catalog, "skip") == null) _exhausted = true;
                var cards = new List<CatalogCardItem>();
                foreach (var meta in fresh)
                {
                    var progress = _progress.GetProgress(meta.Id, null, null);
                    var fraction = progress != null && progress.Duration > 0 ? (double)progress.Position / progress.Duration : 0;
                    cards.Add(new CatalogCardItem(meta, _progress.IsWatched(meta.Id, null, null), fraction));
                }
                var firstPage = _firstLoadOfGeneration;
                _firstLoadOfGeneration = false;
                await RunOnUi(() =>
                {
                    foreach (var card in cards) Items.Add(card);
                    if (Items.Count == 0)
                    {
                        IsEmpty = true;
                        EmptyTitle = S("CatalogNoTitlesTitle", "No titles found");
                        EmptyMessage = S("CatalogNoTitlesMessage", "This catalog did not return any items.");
                        UpdateEmptyState();
                    }
                }).ConfigureAwait(false);
                if (firstPage) RaiseFirstPageLoaded();
            }
            catch (Exception ex)
            {
                if (gen != _generation) return;
                _exhausted = true;
                Log.Error("Catalog page failed to load", ex);
                await RunOnUi(() =>
                {
                    // The error state shows only when the grid is empty (web loadMore).
                    if (Items.Count == 0)
                    {
                        ErrorMessage = ex.Message;
                        EmptyTitle = S("CatalogLoadFailedTitle", "Couldn’t load this catalog");
                        EmptyMessage = ex.Message;
                        UpdateEmptyState();
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                if (_loadGeneration == gen) _loadGeneration = -1;
                if (gen == _generation)
                {
                    await RunOnUi(() => { IsLoading = false; }).ConfigureAwait(false);
                }
            }
        }

        /// <summary>More pages when focus gets within 14 cards of the end (web nav-focus).</summary>
        public void MaybeLoadMore(int index)
        {
            if (index < 0 || Items.Count == 0) return;
            if (index >= Items.Count - NearEndThreshold)
            {
                var _ = LoadMoreAsync();
            }
        }

        /// <summary>More pages when the scroller gets within 600 px of the bottom (web scroll).</summary>
        public void RequestLoadMore()
        {
            var _ = LoadMoreAsync();
        }

        public void OpenTitle(Meta meta)
        {
            if (meta == null) return;
            _navigation.OpenTitle(meta);
        }

        private void RaiseFirstPageLoaded()
        {
            // The page focuses UI on this event: marshal it, since loads complete off the UI thread.
            var _ = RunOnUi(() =>
            {
                var handler = FirstPageLoaded;
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
