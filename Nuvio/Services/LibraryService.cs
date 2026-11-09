using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nuvio.Core;
using Nuvio.Diagnostics;
using Nuvio.Models;
using Windows.Data.Json;

namespace Nuvio.Services
{
    internal sealed class LibraryChangedEventArgs : EventArgs
    {
        public LibraryItem Added { get; set; }
        public LibraryItem Removed { get; set; }
        public bool Replaced { get; set; }
    }

    /// <summary>Saved titles, ported from services/library.js (local only in M1; sync is M5).</summary>
    internal sealed class LibraryService
    {
        private readonly LocalStore _store;
        private Dictionary<string, LibraryItem> _items = new Dictionary<string, LibraryItem>();
        private Task _loadTask;

        public event EventHandler<LibraryChangedEventArgs> LibraryChanged;

        public LibraryService(LocalStore store)
        {
            _store = store;
        }

        public Task LoadAsync()
        {
            if (_loadTask != null) return _loadTask;
            _loadTask = LoadInnerAsync();
            return _loadTask;
        }

        private async Task LoadInnerAsync()
        {
            _items = new Dictionary<string, LibraryItem>();
            var value = await _store.ReadProfileJsonAsync("library").ConfigureAwait(false);
            var obj = JsonHelpers.GetObject(value);
            if (obj == null) return;
            foreach (var pair in obj)
            {
                var item = LibraryItem.FromJson(pair.Value);
                if (item != null && !string.IsNullOrEmpty(item.Id)) _items[item.Id] = item;
            }
        }

        private void EnsureLoaded()
        {
            if (_loadTask != null) return;
            _items = new Dictionary<string, LibraryItem>();
            var _ = LoadAsync();
        }

        private async void SaveAsync(LibraryChangedEventArgs change)
        {
            try
            {
                var obj = new JsonObject();
                foreach (var item in _items.Values) obj.SetNamedValue(item.Id, item.ToJson());
                await _store.WriteProfileJsonAsync("library", obj).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Saving library failed", ex);
            }
            var handler = LibraryChanged;
            if (handler != null) handler(this, change ?? new LibraryChangedEventArgs());
        }

        public void ReloadLibrary()
        {
            _loadTask = null;
            _items = new Dictionary<string, LibraryItem>();
            var _ = LoadAsync().ContinueWith(t =>
            {
                var handler = LibraryChanged;
                if (handler != null) handler(this, new LibraryChangedEventArgs());
            });
        }

        public IReadOnlyList<LibraryItem> LibraryItems()
        {
            EnsureLoaded();
            return _items.Values.OrderByDescending(i => i.AddedAt).ToList();
        }

        public bool InLibrary(string id)
        {
            EnsureLoaded();
            return !string.IsNullOrEmpty(id) && _items.ContainsKey(id);
        }

        public static LibraryItem LibraryItemFromMeta(Meta meta)
        {
            return new LibraryItem
            {
                Id = meta.Id,
                Type = meta.Type,
                Name = meta.Name ?? string.Empty,
                Poster = meta.Poster,
                PosterShape = (meta.PosterShape ?? "poster").ToUpperInvariant(),
                Background = meta.Background,
                Logo = meta.Logo,
                Description = meta.Description,
                ReleaseInfo = meta.ReleaseInfo ?? (meta.Year.HasValue ? meta.Year.Value.ToString() : null),
                ImdbRating = string.IsNullOrEmpty(meta.ImdbRating) ? null : meta.ImdbRating,
                Genres = meta.Genres ?? new List<string>(),
                AddedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
        }

        public void AddToLibrary(Meta meta)
        {
            EnsureLoaded();
            var item = LibraryItemFromMeta(meta);
            _items[meta.Id] = item;
            SaveAsync(new LibraryChangedEventArgs { Added = item });
        }

        public void RemoveFromLibrary(string id)
        {
            EnsureLoaded();
            LibraryItem removed;
            if (!_items.TryGetValue(id, out removed)) return;
            _items.Remove(id);
            SaveAsync(new LibraryChangedEventArgs { Removed = removed });
        }

        public bool ToggleLibrary(Meta meta)
        {
            if (InLibrary(meta.Id)) RemoveFromLibrary(meta.Id);
            else AddToLibrary(meta);
            return InLibrary(meta.Id);
        }

        /// <summary>Adds several items at once (imports). Each is synced like a normal add.</summary>
        public void AddImportedLibraryItems(IEnumerable<LibraryItem> list)
        {
            EnsureLoaded();
            foreach (var item in list)
            {
                if (item == null || string.IsNullOrEmpty(item.Id)) continue;
                _items[item.Id] = item;
                var handler = LibraryChanged;
                if (handler != null) handler(this, new LibraryChangedEventArgs { Added = item });
            }
            SaveAsync(null);
        }

        /// <summary>Replaces everything (account sync).</summary>
        public void ReplaceLibrary(IEnumerable<LibraryItem> list)
        {
            EnsureLoaded();
            _items = new Dictionary<string, LibraryItem>();
            foreach (var item in list)
            {
                if (item == null || string.IsNullOrEmpty(item.Id)) continue;
                _items[item.Id] = item;
            }
            SaveAsync(new LibraryChangedEventArgs { Replaced = true });
        }
    }
}
