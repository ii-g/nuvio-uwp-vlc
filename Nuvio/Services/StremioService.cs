using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nuvio.Core;
using Nuvio.Diagnostics;
using Nuvio.Models;
using Windows.Data.Json;

namespace Nuvio.Core
{
    /// <summary>
    /// A built-in source (Trakt, Jellyfin in M7) that answers like an addon, in code.
    /// Ported from addons.js registerProvider / providerAddons.
    /// </summary>
    internal interface IAddonProvider
    {
        string TransportUrl { get; }
        AddonManifest Manifest { get; }
        Task<IReadOnlyList<Meta>> GetCatalogAsync(Catalog catalog, int? skip, string genre, string search);
        Task<Meta> GetMetaAsync(string type, string id);
        Task<IReadOnlyList<StreamItem>> GetStreamsAsync(string type, string id);
    }

    /// <summary>An installed addon answering over HTTP, wrapped as a provider.</summary>
    internal sealed class HttpAddonProvider : IAddonProvider
    {
        private readonly Services.StremioService _service;
        private readonly Addon _addon;

        public HttpAddonProvider(Services.StremioService service, Addon addon)
        {
            _service = service;
            _addon = addon;
        }

        public string TransportUrl { get { return _addon.TransportUrl; } }
        public AddonManifest Manifest { get { return _addon.Manifest; } }

        public Task<IReadOnlyList<Meta>> GetCatalogAsync(Catalog catalog, int? skip, string genre, string search)
        {
            return _service.FetchCatalogHttpAsync(_addon, catalog, skip, genre, search);
        }

        public Task<Meta> GetMetaAsync(string type, string id)
        {
            return _service.FetchMetaHttpAsync(_addon, type, id);
        }

        public Task<IReadOnlyList<StreamItem>> GetStreamsAsync(string type, string id)
        {
            return _service.FetchStreamsHttpAsync(_addon, type, id);
        }
    }
}

namespace Nuvio.Services
{
    /// <summary>One catalog of the enabled addons, as { addon, catalog, key }.</summary>
    internal sealed class CatalogRef
    {
        public Addon Addon { get; set; }
        public Catalog Catalog { get; set; }
        public string Key { get; set; }
    }

    internal sealed class AddonsChangedEventArgs : EventArgs
    {
        public AddonsChangedEventArgs(IReadOnlyList<Addon> addons)
        {
            Addons = addons;
        }

        public IReadOnlyList<Addon> Addons { get; }
    }

    /// <summary>
    /// The content layer: Stremio addon protocol client (manifests, catalogs, metadata,
    /// streams, subtitles), ported from services/addons.js. Protocol reference:
    /// https://github.com/Stremio/stremio-addon-sdk/blob/master/docs/protocol.md
    /// </summary>
    internal sealed class StremioService
    {
        public static readonly IReadOnlyList<string> DefaultAddonUrls = new List<string>
        {
            "https://v3-cinemeta.strem.io/manifest.json",
            "https://opensubtitles-v3.strem.io/manifest.json",
        }.AsReadOnly();

        private const long ManifestRefreshMs = 6 * 60 * 60 * 1000;
        private const int StreamTimeoutMs = 30000;

        private readonly LocalStore _store;
        private readonly List<Func<IReadOnlyList<Addon>>> _providers = new List<Func<IReadOnlyList<Addon>>>();
        private List<Addon> _addons;
        private bool _addonsLoaded;
        private bool _addonsLoadFailed;
        private bool _hasStoredAddons;

        public event EventHandler<AddonsChangedEventArgs> AddonsChanged;

        public StremioService(LocalStore store)
        {
            _store = store;
        }

        // ---------- Storage ----------

        public async Task LoadAddonsAsync()
        {
            if (_addonsLoaded) return;
            _addonsLoaded = true;
            var result = await _store.ReadProfileJsonDetailedAsync("addons").ConfigureAwait(false);
            _addonsLoadFailed = (result.Status == JsonReadStatus.Failed);
            var value = result.Value;
            _addons = new List<Addon>();
            if (value != null && value.ValueType == JsonValueType.Array)
            {
                _hasStoredAddons = true;
                foreach (var item in value.GetArray())
                {
                    var addon = Addon.FromJson(item);
                    if (addon != null && !string.IsNullOrEmpty(addon.TransportUrl)) _addons.Add(addon);
                }
            }
        }

        private async void SaveAndNotifyAsync()
        {
            if (_addonsLoadFailed)
            {
                // The stored file failed to load, so the in-memory list is empty by
                // accident, not by choice: writing it would destroy the stored addons.
                Log.Write("Saving addons refused: stored addons file failed to load; refusing to overwrite it");
                RaiseAddonsChanged();
                return;
            }
            try
            {
                var array = new JsonArray();
                foreach (var addon in _addons) array.Add(addon.ToJson());
                await _store.WriteProfileJsonAsync("addons", array).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Saving addons failed", ex);
            }
            RaiseAddonsChanged();
        }

        private void RaiseAddonsChanged()
        {
            var handler = AddonsChanged;
            if (handler != null) handler(this, new AddonsChangedEventArgs(ListAddons()));
        }

        /// <summary>Reloads from storage, e.g. after switching profile or a sync pull.</summary>
        public void ReloadAddons()
        {
            _addonsLoaded = false;
            _addonsLoadFailed = false;
            _addons = null;
            var _ = LoadAddonsAsync().ContinueWith(t => RaiseAddonsChanged());
        }

        public IReadOnlyList<Addon> ListAddons()
        {
            if (_addons == null) return new List<Addon>().AsReadOnly();
            return _addons.ToList().AsReadOnly();
        }

        // ---------- Providers ----------

        public void RegisterProvider(Func<IReadOnlyList<Addon>> getAddons)
        {
            _providers.Add(getAddons);
        }

        private IReadOnlyList<Addon> ProviderAddons()
        {
            var result = new List<Addon>();
            foreach (var get in _providers)
            {
                try
                {
                    var addons = get();
                    if (addons != null) result.AddRange(addons);
                }
                catch (Exception ex)
                {
                    Log.Error("Provider failed", ex);
                }
            }
            return result;
        }

        /// <summary>Tells screens that the built-in sources changed (connected, disconnected, new libraries).</summary>
        public void ProvidersChanged()
        {
            RaiseAddonsChanged();
        }

        /// <summary>Built-in sources first, then the installed addons in their order.</summary>
        public IReadOnlyList<Addon> EnabledAddons()
        {
            var result = new List<Addon>(ProviderAddons());
            foreach (var addon in ListAddons())
            {
                if (addon.Enabled != false && addon.Manifest != null) result.Add(addon);
            }
            return result;
        }

        // ---------- URLs ----------

        public static string NormalizeAddonUrl(string input)
        {
            var url = (input ?? string.Empty).Trim();
            if (url.Length == 0) throw new InvalidOperationException("Enter an addon URL");
            if (url.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url.Substring("stremio://".Length);
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) throw new InvalidOperationException("Enter an addon URL");
            var path = uri.AbsolutePath;
            if (!path.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase)) path = path.TrimEnd('/') + "/manifest.json";
            var builder = new UriBuilder(uri) { Path = path };
            return builder.Uri.ToString();
        }

        public static string AddonBaseUrl(string transportUrl)
        {
            var uri = new Uri(transportUrl);
            var path = uri.AbsolutePath;
            if (path.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - "/manifest.json".Length);
            var builder = new UriBuilder(uri) { Path = path, Query = string.Empty, Fragment = string.Empty };
            return builder.Uri.ToString().TrimEnd('/');
        }

        public static string ResourceUrl(Addon addon, string resource, string type, string id, IDictionary<string, string> extra)
        {
            var url = AddonBaseUrl(addon.TransportUrl) + "/" + resource + "/" + Uri.EscapeDataString(type) + "/" + Uri.EscapeDataString(id);
            if (extra != null)
            {
                var pairs = new List<string>();
                foreach (var pair in extra)
                {
                    if (string.IsNullOrEmpty(pair.Value)) continue;
                    pairs.Add(Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value));
                }
                if (pairs.Count > 0) url += "/" + string.Join("&", pairs);
            }
            return url + ".json";
        }

        // ---------- Manifests ----------

        public async Task<AddonManifest> FetchManifestAsync(string transportUrl)
        {
            var value = await HttpService.GetJsonAsync(transportUrl, 0, 20000).ConfigureAwait(false);
            var manifest = AddonManifest.FromJson(value);
            if (manifest == null) throw new HttpError("Invalid response from " + new Uri(transportUrl).Host, 0);
            if (string.IsNullOrEmpty(manifest.Id)) throw new InvalidOperationException("Manifest missing \"id\"");
            if (string.IsNullOrEmpty(manifest.Name)) throw new InvalidOperationException("Manifest missing \"name\"");
            if (string.IsNullOrEmpty(manifest.Version)) throw new InvalidOperationException("Manifest missing \"version\"");
            return manifest;
        }

        /// <summary>First run: install the default addons.</summary>
        public async Task EnsureDefaultAddonsAsync()
        {
            await LoadAddonsAsync().ConfigureAwait(false);
            if (_addonsLoadFailed)
            {
                Log.Write("Default addons not installed: stored addons file could not be read; stored file left untouched");
                return;
            }
            if (_hasStoredAddons) return;
            var installed = new List<Addon>();
            var tasks = new List<Task>();
            foreach (var url in DefaultAddonUrls)
            {
                tasks.Add(InstallDefaultAsync(url, installed));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
            var urlOrder = new List<string>(DefaultAddonUrls);
            installed.Sort((a, b) => urlOrder.IndexOf(a.TransportUrl).CompareTo(urlOrder.IndexOf(b.TransportUrl)));
            _addons = installed;
            _hasStoredAddons = true;
            SaveAndNotifyAsync();
        }

        private async Task InstallDefaultAsync(string url, List<Addon> installed)
        {
            try
            {
                var manifest = await FetchManifestAsync(url).ConfigureAwait(false);
                lock (installed) installed.Add(new Addon { TransportUrl = url, Manifest = manifest, Enabled = true, RefreshedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            }
            catch (Exception ex)
            {
                Log.Error("Default addon failed to install: " + Log.DescribeUrl(url), ex);
            }
        }

        /// <summary>Re-reads manifests that are more than a few hours old, in the background.</summary>
        public async Task RefreshStaleManifestsAsync()
        {
            await LoadAddonsAsync().ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var addon in ListAddons())
            {
                if (now - addon.RefreshedAt < ManifestRefreshMs) continue;
                try
                {
                    await RefreshAddonAsync(addon.TransportUrl).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error("Manifest refresh failed: " + Log.DescribeUrl(addon.TransportUrl), ex);
                }
            }
        }

        public async Task RefreshAddonAsync(string transportUrl)
        {
            await LoadAddonsAsync().ConfigureAwait(false);
            var manifest = await FetchManifestAsync(transportUrl).ConfigureAwait(false);
            foreach (var addon in _addons)
            {
                if (addon.TransportUrl != transportUrl) continue;
                addon.Manifest = manifest;
                addon.RefreshedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                addon.Error = null;
            }
            SaveAndNotifyAsync();
        }

        // ---------- Addon list management ----------

        public async Task<AddonManifest> InstallAddonAsync(string input)
        {
            var transportUrl = NormalizeAddonUrl(input);
            await LoadAddonsAsync().ConfigureAwait(false);
            if (_addons.Any(a => a.TransportUrl == transportUrl)) throw new InvalidOperationException("That addon is already installed.");
            var manifest = await FetchManifestAsync(transportUrl).ConfigureAwait(false);
            _addons.Add(new Addon { TransportUrl = transportUrl, Manifest = manifest, Enabled = true, RefreshedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            _hasStoredAddons = true;
            SaveAndNotifyAsync();
            return manifest;
        }

        /// <summary>Adds addons that aren't installed yet (e.g. from a Stremio account), keeping the existing ones.</summary>
        public void MergeAddons(IEnumerable<Addon> entries)
        {
            if (_addons == null) _addons = new List<Addon>();
            var known = new HashSet<string>(_addons.Select(a => a.TransportUrl));
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.TransportUrl) || known.Contains(entry.TransportUrl)) continue;
                known.Add(entry.TransportUrl);
                if (entry.Manifest == null) entry.Manifest = new AddonManifest();
                entry.Enabled = true;
                entry.RefreshedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _addons.Add(entry);
            }
            _hasStoredAddons = true;
            SaveAndNotifyAsync();
        }

        public void RemoveAddon(string transportUrl)
        {
            if (_addons == null) return;
            _addons = _addons.Where(a => a.TransportUrl != transportUrl).ToList();
            SaveAndNotifyAsync();
        }

        public void MoveAddon(string transportUrl, int delta)
        {
            if (_addons == null) return;
            var index = _addons.FindIndex(a => a.TransportUrl == transportUrl);
            var target = index + delta;
            if (index < 0 || target < 0 || target >= _addons.Count) return;
            var item = _addons[index];
            _addons.RemoveAt(index);
            _addons.Insert(target, item);
            SaveAndNotifyAsync();
        }

        public void SetAddonEnabled(string transportUrl, bool enabled)
        {
            if (_addons == null) return;
            foreach (var addon in _addons)
            {
                if (addon.TransportUrl == transportUrl) addon.Enabled = enabled;
            }
            SaveAndNotifyAsync();
        }

        /// <summary>Replaces the whole list, keeping manifests already known (used by account sync).</summary>
        public async Task ReplaceAddonsAsync(IEnumerable<KeyValuePair<string, bool>> entries)
        {
            await LoadAddonsAsync().ConfigureAwait(false);
            var known = _addons.ToDictionary(a => a.TransportUrl);
            var next = new List<Addon>();
            foreach (var entry in entries)
            {
                var transportUrl = NormalizeAddonUrl(entry.Key);
                var enabled = entry.Value;
                Addon existing;
                if (known.TryGetValue(transportUrl, out existing) && existing.Manifest != null)
                {
                    existing.Enabled = enabled;
                    next.Add(existing);
                    continue;
                }
                try
                {
                    var manifest = await FetchManifestAsync(transportUrl).ConfigureAwait(false);
                    next.Add(new Addon { TransportUrl = transportUrl, Manifest = manifest, Enabled = enabled, RefreshedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                }
                catch (Exception ex)
                {
                    Log.Error("Addon unavailable: " + Log.DescribeUrl(transportUrl), ex);
                    next.Add(new Addon { TransportUrl = transportUrl, Manifest = null, Enabled = enabled, Error = ex.Message });
                }
            }
            _addons = next;
            _hasStoredAddons = true;
            SaveAndNotifyAsync();
        }

        public static string AddonName(Addon addon)
        {
            if (addon == null) return string.Empty;
            if (addon.Manifest != null && !string.IsNullOrEmpty(addon.Manifest.Name)) return addon.Manifest.Name;
            try
            {
                return new Uri(addon.TransportUrl).Host;
            }
            catch (Exception)
            {
                return addon.TransportUrl ?? string.Empty;
            }
        }

        /// <summary>Whether an addon serves a resource ("meta", "stream", "subtitles") for this type and id.</summary>
        public static bool AddonSupports(Addon addon, string resource, string type, string id)
        {
            var manifest = addon == null ? null : addon.Manifest;
            if (manifest == null) return false;
            ManifestResource entry = null;
            foreach (var resourceEntry in manifest.Resources)
            {
                if (resourceEntry != null && resourceEntry.Name == resource) { entry = resourceEntry; break; }
            }
            if (entry == null) return false;
            var types = entry.Types ?? manifest.Types ?? new List<string>();
            if (!string.IsNullOrEmpty(type) && types.Count > 0 && !types.Contains(type)) return false;
            var prefixes = entry.IdPrefixes ?? manifest.IdPrefixes;
            if (!string.IsNullOrEmpty(id) && prefixes != null && prefixes.Count > 0)
            {
                var matches = false;
                foreach (var prefix in prefixes)
                {
                    if (id.StartsWith(prefix, StringComparison.Ordinal)) { matches = true; break; }
                }
                if (!matches) return false;
            }
            return true;
        }

        // ---------- Catalogs ----------

        private static IReadOnlyList<CatalogExtraEntry> CatalogExtras(Catalog catalog)
        {
            if (catalog.Extra != null && catalog.Extra.Count > 0) return catalog.Extra;
            var required = catalog.ExtraRequired ?? new List<string>();
            var result = new List<CatalogExtraEntry>();
            foreach (var name in catalog.ExtraSupported ?? new List<string>())
            {
                result.Add(new CatalogExtraEntry { Name = name, IsRequired = required.Contains(name) });
            }
            return result;
        }

        public static CatalogExtraEntry CatalogExtra(Catalog catalog, string name)
        {
            return CatalogExtras(catalog).FirstOrDefault(e => e.Name == name);
        }

        /// <summary>Every catalog of the enabled addons, as { addon, catalog, key }.</summary>
        public IReadOnlyList<CatalogRef> AllCatalogs()
        {
            var result = new List<CatalogRef>();
            foreach (var addon in EnabledAddons())
            {
                if (addon.Manifest == null) continue;
                foreach (var catalog in addon.Manifest.Catalogs ?? new List<Catalog>())
                {
                    if (catalog == null || string.IsNullOrEmpty(catalog.Type) || string.IsNullOrEmpty(catalog.Id)) continue;
                    result.Add(new CatalogRef { Addon = addon, Catalog = catalog, Key = addon.TransportUrl + "|" + catalog.Type + "|" + catalog.Id });
                }
            }
            return result;
        }

        /// <summary>Catalogs that can be shown as a row without extra input (no required search or genre).</summary>
        public IReadOnlyList<CatalogRef> HomeCatalogs()
        {
            return AllCatalogs().Where(c => !CatalogExtras(c.Catalog).Any(e => e.IsRequired)).ToList();
        }

        public IReadOnlyList<CatalogRef> SearchCatalogs()
        {
            return AllCatalogs().Where(c =>
            {
                var extras = CatalogExtras(c.Catalog);
                return extras.Any(e => e.Name == "search") && !extras.Any(e => e.IsRequired && e.Name != "search");
            }).ToList();
        }

        public CatalogRef FindCatalog(string transportUrl, string type, string id)
        {
            return AllCatalogs().FirstOrDefault(c => c.Addon.TransportUrl == transportUrl && c.Catalog.Type == type && c.Catalog.Id == id);
        }

        public async Task<IReadOnlyList<Meta>> FetchCatalogAsync(Addon addon, Catalog catalog, int? skip = null, string genre = null, string search = null)
        {
            if (addon.Handler != null)
            {
                return await addon.Handler.GetCatalogAsync(catalog, skip, genre, search).ConfigureAwait(false);
            }
            return await FetchCatalogHttpAsync(addon, catalog, skip, genre, search).ConfigureAwait(false);
        }

        internal async Task<IReadOnlyList<Meta>> FetchCatalogHttpAsync(Addon addon, Catalog catalog, int? skip, string genre, string search)
        {
            var extra = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(search)) extra["search"] = search;
            if (!string.IsNullOrEmpty(genre)) extra["genre"] = genre;
            if (skip.HasValue && skip.Value != 0) extra["skip"] = skip.Value.ToString();
            var url = ResourceUrl(addon, "catalog", catalog.Type, catalog.Id, extra);
            var data = await HttpService.GetJsonAsync(url, search != null ? 2 * 60 * 1000 : 30 * 60 * 1000).ConfigureAwait(false);
            var result = new List<Meta>();
            var obj = JsonHelpers.GetObject(data);
            var metas = obj != null ? JsonHelpers.GetArray(obj, "metas") : null;
            if (metas == null) return result;
            foreach (var item in metas)
            {
                var meta = Meta.FromJson(item);
                if (meta == null || string.IsNullOrEmpty(meta.Id) || string.IsNullOrEmpty(meta.Name)) continue;
                if (string.IsNullOrEmpty(meta.Type)) meta.Type = catalog.Type;
                result.Add(meta);
            }
            return result;
        }

        // ---------- Metadata ----------

        public async Task<Meta> FetchMetaAsync(string type, string id)
        {
            var candidates = EnabledAddons().Where(a => AddonSupports(a, "meta", type, id)).ToList();
            if (candidates.Count == 0) throw new InvalidOperationException("No addon provides details for this title.");
            Exception lastError = null;
            foreach (var addon in candidates)
            {
                try
                {
                    Meta meta;
                    if (addon.Handler != null) meta = await addon.Handler.GetMetaAsync(type, id).ConfigureAwait(false);
                    else meta = await FetchMetaHttpAsync(addon, type, id).ConfigureAwait(false);
                    if (meta != null)
                    {
                        meta.ServingAddon = addon;
                        return meta;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }
            throw lastError ?? new InvalidOperationException("Could not load details from any addon.");
        }

        internal async Task<Meta> FetchMetaHttpAsync(Addon addon, string type, string id)
        {
            var url = ResourceUrl(addon, "meta", type, id, null);
            var data = await HttpService.GetJsonAsync(url, 60 * 60 * 1000).ConfigureAwait(false);
            var obj = JsonHelpers.GetObject(data);
            var metaValue = obj != null ? JsonHelpers.GetObject(obj, "meta") : null;
            if (metaValue == null) return null;
            var meta = Meta.FromJson(metaValue);
            if (meta == null) return null;
            if (string.IsNullOrEmpty(meta.Type)) meta.Type = type;
            if (string.IsNullOrEmpty(meta.Id)) meta.Id = id;
            return meta;
        }

        // ---------- Streams ----------

        /// <summary>Addons that may have streams for this video.</summary>
        public IReadOnlyList<Addon> StreamAddons(string type, string id)
        {
            return EnabledAddons().Where(a => AddonSupports(a, "stream", type, id)).ToList();
        }

        /// <summary>Asks one addon for streams. Resolves to an array (possibly empty).</summary>
        public async Task<IReadOnlyList<StreamItem>> FetchStreamsAsync(Addon addon, string type, string id)
        {
            if (addon.Handler != null)
            {
                return await addon.Handler.GetStreamsAsync(type, id).ConfigureAwait(false);
            }
            return await FetchStreamsHttpAsync(addon, type, id).ConfigureAwait(false);
        }

        internal async Task<IReadOnlyList<StreamItem>> FetchStreamsHttpAsync(Addon addon, string type, string id)
        {
            var url = ResourceUrl(addon, "stream", type, id, null);
            var data = await HttpService.GetJsonAsync(url, 10 * 60 * 1000, StreamTimeoutMs).ConfigureAwait(false);
            var result = new List<StreamItem>();
            var obj = JsonHelpers.GetObject(data);
            var streams = obj != null ? JsonHelpers.GetArray(obj, "streams") : null;
            if (streams == null) return result;
            foreach (var item in streams)
            {
                var stream = StreamItem.FromJson(item);
                if (stream != null) result.Add(stream);
            }
            return result;
        }

        // ---------- Subtitles ----------

        public async Task<IReadOnlyList<SubtitleRef>> FetchSubtitlesAsync(string type, string id, IDictionary<string, string> extra = null)
        {
            var sources = EnabledAddons().Where(a => AddonSupports(a, "subtitles", type, id)).ToList();
            var tasks = new List<Task<IReadOnlyList<SubtitleRef>>>();
            foreach (var addon in sources)
            {
                tasks.Add(FetchSubtitlesFromAsync(addon, type, id, extra));
            }
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var combined = new List<SubtitleRef>();
            foreach (var list in results) combined.AddRange(list);
            return combined;
        }

        private async Task<IReadOnlyList<SubtitleRef>> FetchSubtitlesFromAsync(Addon addon, string type, string id, IDictionary<string, string> extra)
        {
            try
            {
                var url = ResourceUrl(addon, "subtitles", type, id, extra);
                var data = await HttpService.GetJsonAsync(url, 30 * 60 * 1000, 15000).ConfigureAwait(false);
                var result = new List<SubtitleRef>();
                var obj = JsonHelpers.GetObject(data);
                var subtitles = obj != null ? JsonHelpers.GetArray(obj, "subtitles") : null;
                if (subtitles == null) return result;
                var addonName = AddonName(addon);
                foreach (var item in subtitles)
                {
                    var subObj = JsonHelpers.GetObject(item);
                    if (subObj == null) continue;
                    var subUrl = JsonHelpers.GetString(subObj, "url");
                    if (string.IsNullOrEmpty(subUrl)) continue;
                    result.Add(new SubtitleRef
                    {
                        Url = subUrl,
                        Lang = JsonHelpers.GetString(subObj, "lang"),
                        Id = JsonHelpers.GetString(subObj, "id"),
                        AddonName = addonName,
                    });
                }
                return result;
            }
            catch (Exception ex)
            {
                Log.Error("Fetching subtitles failed: " + Log.DescribeUrl(addon.TransportUrl), ex);
                return new List<SubtitleRef>();
            }
        }
    }
}
