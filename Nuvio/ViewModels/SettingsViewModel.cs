using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nuvio.Models;
using Nuvio.Services;
using Windows.ApplicationModel.Resources;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Nuvio.ViewModels
{
    /// <summary>
    /// Settings (Milestone 6, Addons + Debrid only): the addon list and the debrid
    /// account, ported from web/js/views/settings.js (addonsPage / debridPage) and
    /// web/js/services/debrid.js. No UI types here: the page renders from the
    /// AddonsChanged / DebridChanged events and the computed properties below.
    /// Operations return the toast text the page must show, or throw with a short
    /// message. API keys and addon URLs are never written to the log.
    /// </summary>
    internal sealed class SettingsViewModel : ViewModelBase
    {
        private readonly StremioService _stremio;
        private readonly DebridService _debrid;
        private readonly CoreDispatcher _dispatcher;
        private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse("Resources");

        /// <summary>Raised on the UI thread whenever the addon list changes.</summary>
        public event EventHandler AddonsChanged;

        /// <summary>Raised on the UI thread whenever the debrid account changes.</summary>
        public event EventHandler DebridChanged;

        private List<Addon> _addons = new List<Addon>();
        private bool _closed;

        public SettingsViewModel(StremioService stremio, DebridService debrid, AppSettings settings, NavigationService navigation)
        {
            _stremio = stremio;
            _debrid = debrid;
            Settings = settings;
            Navigation = navigation;
            _dispatcher = Window.Current.Dispatcher;
            if (_stremio != null)
            {
                _stremio.AddonsChanged += OnStremioAddonsChanged;
            }
            if (_debrid != null)
            {
                _debrid.DebridChanged += OnDebridChanged;
            }
            RefreshAddons();
            RefreshDebrid();
        }

        public AppSettings Settings { get; }

        public NavigationService Navigation { get; }

        private string S(string key, string fallback)
        {
            var text = _strings.GetString(key);
            return string.IsNullOrEmpty(text) ? fallback : text;
        }

        // ---------- Addons ----------

        /// <summary>The installed addons in their order (a copy; re-read on AddonsChanged).</summary>
        public IReadOnlyList<Addon> Addons
        {
            get { return _addons.ToList().AsReadOnly(); }
        }

        public bool HasAddons { get { return _addons.Count > 0; } }

        private void OnStremioAddonsChanged(object sender, AddonsChangedEventArgs e)
        {
            RefreshAddons();
        }

        private void RefreshAddons()
        {
            var addons = _stremio != null ? _stremio.ListAddons() : new List<Addon>().AsReadOnly();
            RunOnUi(() =>
            {
                if (_closed) return;
                _addons = addons.ToList();
                RaisePropertyChanged(nameof(Addons));
                RaisePropertyChanged(nameof(HasAddons));
                var handler = AddonsChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            });
        }

        /// <summary>Installs an addon from its manifest URL. Validates the URL first; throws InvalidOperationException with a short message on failure.</summary>
        public async Task<string> InstallAddonAsync(string input)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(input) ||
                !Uri.TryCreate(input.Trim(), UriKind.Absolute, out uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                throw new InvalidOperationException(S("SettingsInvalidAddonUrl", "Enter a valid addon URL starting with http:// or https://"));
            }
            var manifest = await _stremio.InstallAddonAsync(input.Trim()).ConfigureAwait(false);
            return string.Format(S("SettingsAddonInstalled", "{0} installed"), manifest != null ? manifest.Name : string.Empty);
        }

        /// <summary>Removes every addon and installs the defaults (Cinemeta + OpenSubtitles) again.</summary>
        public async Task<string> ResetAddonsToDefaultsAsync()
        {
            var entries = StremioService.DefaultAddonUrls
                .Select(url => new KeyValuePair<string, bool>(url, true));
            await _stremio.ReplaceAddonsAsync(entries).ConfigureAwait(false);
            return S("SettingsAddonsReset", "Addons reset");
        }

        public void RemoveAddon(string transportUrl)
        {
            _stremio.RemoveAddon(transportUrl);
        }

        public void MoveAddon(string transportUrl, int delta)
        {
            _stremio.MoveAddon(transportUrl, delta);
        }

        public void SetAddonEnabled(string transportUrl, bool enabled)
        {
            _stremio.SetAddonEnabled(transportUrl, enabled);
        }

        /// <summary>Re-fetches one addon's manifest. Throws on failure (the page toasts the message).</summary>
        public async Task<string> RefreshAddonAsync(string transportUrl)
        {
            await _stremio.RefreshAddonAsync(transportUrl).ConfigureAwait(false);
            return S("SettingsAddonRefreshed", "Addon refreshed");
        }

        // ---------- Addon display helpers (mirror settings.js render) ----------

        /// <summary>Badges for an addon card: Active/Disabled, "&lt;N&gt; catalogs", resource names except "catalog", "Configurable", "Unavailable" when it has an error.</summary>
        public List<string> AddonBadges(Addon addon)
        {
            var badges = new List<string>();
            if (addon == null) return badges;
            var manifest = addon.Manifest;
            badges.Add(addon.Enabled == false
                ? S("SettingsAddonDisabled", "Disabled")
                : S("SettingsAddonActive", "Active"));
            if (manifest != null && manifest.Catalogs != null && manifest.Catalogs.Count > 0)
            {
                badges.Add(string.Format(S("SettingsAddonCatalogs", "{0} catalogs"), manifest.Catalogs.Count));
            }
            if (manifest != null && manifest.Resources != null)
            {
                foreach (var resource in manifest.Resources)
                {
                    if (resource == null || string.IsNullOrEmpty(resource.Name) || resource.Name == "catalog") continue;
                    if (!badges.Contains(resource.Name)) badges.Add(resource.Name);
                }
            }
            if (manifest != null && manifest.BehaviorHintsConfigurable)
            {
                badges.Add(S("SettingsAddonConfigurable", "Configurable"));
            }
            if (!string.IsNullOrEmpty(addon.Error))
            {
                badges.Add(S("SettingsAddonUnavailable", "Unavailable"));
            }
            return badges;
        }

        /// <summary>The transport URL with the scheme stripped, at most 80 characters.</summary>
        public static string AddonDisplayUrl(Addon addon)
        {
            if (addon == null || string.IsNullOrEmpty(addon.TransportUrl)) return string.Empty;
            var url = addon.TransportUrl;
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = url.Substring("https://".Length);
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) url = url.Substring("http://".Length);
            if (url.Length > 80) url = url.Substring(0, 80);
            return url;
        }

        /// <summary>The addon's configure URL: the transport URL with a trailing "manifest.json" replaced by "configure" (case-insensitive, as in settings.js).</summary>
        public static string AddonConfigureUrl(Addon addon)
        {
            if (addon == null || string.IsNullOrEmpty(addon.TransportUrl)) return null;
            var url = addon.TransportUrl;
            const string manifest = "manifest.json";
            if (url.EndsWith(manifest, StringComparison.OrdinalIgnoreCase))
            {
                return url.Substring(0, url.Length - manifest.Length) + "configure";
            }
            return url;
        }

        // ---------- Debrid ----------

        /// <summary>The debrid providers (TorBox / Real-Debrid / Premiumize), in service order.</summary>
        public IReadOnlyList<DebridProviderInfo> DebridProviders
        {
            get { return DebridService.Providers; }
        }

        /// <summary>The connected debrid account, or null when none is connected.</summary>
        public DebridAccount DebridAccount
        {
            get { return _debrid != null ? _debrid.DebridAccount() : null; }
        }

        public bool DebridConnected { get { return DebridAccount != null; } }

        /// <summary>Name of the connected provider, e.g. "TorBox"; empty when not connected.</summary>
        public string DebridProviderName
        {
            get { return _debrid != null ? _debrid.DebridName() ?? string.Empty : string.Empty; }
        }

        /// <summary>Short tag of the connected provider, e.g. "TB"; empty when not connected.</summary>
        public string DebridProviderShort
        {
            get { return _debrid != null ? _debrid.DebridShort() ?? string.Empty : string.Empty; }
        }

        private void OnDebridChanged(object sender, EventArgs e)
        {
            RefreshDebrid();
        }

        private void RefreshDebrid()
        {
            RunOnUi(() =>
            {
                if (_closed) return;
                RaisePropertyChanged(nameof(DebridProviders));
                RaisePropertyChanged(nameof(DebridAccount));
                RaisePropertyChanged(nameof(DebridConnected));
                RaisePropertyChanged(nameof(DebridProviderName));
                RaisePropertyChanged(nameof(DebridProviderShort));
                var handler = DebridChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            });
        }

        /// <summary>
        /// Validates a provider API key, then stores the account on success.
        /// Returns false when the provider rejects the key; throws HttpError (or
        /// another network exception) when the provider could not be reached.
        /// The key itself is never logged.
        /// </summary>
        public async Task<bool> ConnectDebridAsync(string provider, string apiKey)
        {
            if (_debrid == null) throw new InvalidOperationException(S("SettingsDebridUnavailable", "Debrid isn't available yet."));
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(apiKey)) return false;
            var key = apiKey.Trim();
            var accepted = await _debrid.ValidateDebridKeyAsync(provider, key).ConfigureAwait(false);
            if (!accepted) return false;
            _debrid.SetDebridAccount(new DebridAccount { Provider = provider, ApiKey = key });
            return true;
        }

        public void DisconnectDebrid()
        {
            if (_debrid == null) return;
            _debrid.SetDebridAccount(null);
        }

        // ---------- Lifecycle ----------

        /// <summary>Stops change events from applying after the page is gone.</summary>
        public void Close()
        {
            _closed = true;
            if (_stremio != null) _stremio.AddonsChanged -= OnStremioAddonsChanged;
            if (_debrid != null) _debrid.DebridChanged -= OnDebridChanged;
        }

        private void RunOnUi(Action action)
        {
            if (_dispatcher == null || _dispatcher.HasThreadAccess) action();
            else
            {
                var _ = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action()).AsTask();
            }
        }
    }
}
