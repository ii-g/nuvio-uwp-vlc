using System;
using Nuvio.Core;
using Nuvio.Diagnostics;
using Windows.Data.Json;
using Windows.Globalization;
using Windows.Storage;

namespace Nuvio.Services
{
    /// <summary>
    /// The 29 settings keys (core/store.js DEFAULT_SETTINGS), stored as one JSON object in
    /// LocalSettings["settings"]. Unknown stored keys are kept, as in store.js.
    /// </summary>
    internal sealed class AppSettings
    {
        /// <summary>Set once by MainPage at boot; converters and view models read through it.</summary>
        public static AppSettings Current { get; set; }

        private const string SettingsKey = "settings";
        private JsonObject _current;

        public event EventHandler<SettingsChangedEventArgs> SettingsChanged;

        public AppSettings()
        {
            Load();
        }

        private void Load()
        {
            _current = Defaults();
            try
            {
                var raw = ApplicationData.Current.LocalSettings.Values[SettingsKey] as string;
                var stored = JsonHelpers.ParseObject(raw);
                if (stored != null)
                {
                    foreach (var pair in stored) _current.SetNamedValue(pair.Key, pair.Value);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Loading settings failed", ex);
            }
        }

        private static JsonObject Defaults()
        {
            var defaults = new JsonObject();
            // Playback
            defaults.SetNamedValue("preferredAudioLanguage", JsonHelpers.CreateString("eng"));
            defaults.SetNamedValue("preferredSubtitleLanguage", JsonHelpers.CreateString("eng"));
            defaults.SetNamedValue("secondarySubtitleLanguage", JsonHelpers.CreateString(""));
            defaults.SetNamedValue("autoSelectSubtitles", JsonHelpers.CreateBool(false));
            defaults.SetNamedValue("autoplayNext", JsonHelpers.CreateBool(true));
            defaults.SetNamedValue("nextEpisodeCountdown", JsonHelpers.CreateNumber(10));
            defaults.SetNamedValue("resumePlayback", JsonHelpers.CreateBool(true));
            defaults.SetNamedValue("hardwareDecoding", JsonHelpers.CreateBool(true));
            // Streams
            defaults.SetNamedValue("showUnsupportedStreams", JsonHelpers.CreateBool(false));
            defaults.SetNamedValue("streamSort", JsonHelpers.CreateString("addon"));
            defaults.SetNamedValue("rememberLastStream", JsonHelpers.CreateBool(true));
            // Subtitle appearance
            defaults.SetNamedValue("subtitleSize", JsonHelpers.CreateNumber(100));
            defaults.SetNamedValue("subtitleColor", JsonHelpers.CreateString("#ffffff"));
            defaults.SetNamedValue("subtitleBackground", JsonHelpers.CreateString("none"));
            defaults.SetNamedValue("subtitleOffset", JsonHelpers.CreateNumber(8));
            // Appearance
            defaults.SetNamedValue("amoled", JsonHelpers.CreateBool(false));
            defaults.SetNamedValue("appLanguage", JsonHelpers.CreateString("ar"));
            defaults.SetNamedValue("showHero", JsonHelpers.CreateBool(true));
            defaults.SetNamedValue("posterSize", JsonHelpers.CreateString("standard"));
            defaults.SetNamedValue("showCatalogType", JsonHelpers.CreateBool(true));
            defaults.SetNamedValue("hideUnreleased", JsonHelpers.CreateBool(false));
            // Integrations
            defaults.SetNamedValue("tmdbApiKey", JsonHelpers.CreateString(""));
            defaults.SetNamedValue("traktScrobble", JsonHelpers.CreateBool(true));
            defaults.SetNamedValue("traktImportHistory", JsonHelpers.CreateBool(true));
            defaults.SetNamedValue("traktHomeRows", JsonHelpers.CreateBool(true));
            defaults.SetNamedValue("traktClientId", JsonHelpers.CreateString(""));
            defaults.SetNamedValue("traktClientSecret", JsonHelpers.CreateString(""));
            // Device
            defaults.SetNamedValue("controllerCursor", JsonHelpers.CreateBool(false));
            // Account
            defaults.SetNamedValue("askProfileAtLaunch", JsonHelpers.CreateBool(true));
            return defaults;
        }

        public string GetString(string name, string fallback = null)
        {
            return JsonHelpers.GetString(_current, name, fallback);
        }

        public bool GetBool(string name, bool fallback = false)
        {
            return JsonHelpers.GetBool(_current, name) ?? fallback;
        }

        public int GetInt(string name, int fallback = 0)
        {
            return JsonHelpers.GetInt(_current, name) ?? fallback;
        }

        public double GetDouble(string name, double fallback = 0)
        {
            return JsonHelpers.GetDouble(_current, name) ?? fallback;
        }

        public IJsonValue GetValue(string name)
        {
            if (_current == null || !_current.ContainsKey(name)) return null;
            return _current.GetNamedValue(name);
        }

        public void Set(string name, IJsonValue value)
        {
            var existing = GetValue(name);
            if (existing != null && existing.Stringify() == value.Stringify()) return;
            _current.SetNamedValue(name, value);
            Persist();
            var handler = SettingsChanged;
            if (handler != null) handler(this, new SettingsChangedEventArgs(name, value));
            AppEvents.RaiseSettingsChanged(name, value);
        }

        public void Set(string name, string value)
        {
            Set(name, JsonHelpers.CreateString(value));
        }

        public void Set(string name, bool value)
        {
            Set(name, JsonHelpers.CreateBool(value));
        }

        public void Set(string name, int value)
        {
            Set(name, JsonHelpers.CreateNumber(value));
        }

        public void Set(string name, double value)
        {
            Set(name, JsonHelpers.CreateNumber(value));
        }

        public void Reset()
        {
            _current = Defaults();
            Persist();
            var handler = SettingsChanged;
            if (handler != null) handler(this, new SettingsChangedEventArgs(null, null));
            AppEvents.RaiseSettingsChanged(null, null);
        }

        private void Persist()
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[SettingsKey] = _current.Stringify();
            }
            catch (Exception ex)
            {
                Log.Error("Saving settings failed", ex);
            }
        }

        // Typed conveniences for the keys Milestone 1 reads.
        public string AppLanguage { get { return NormalizeLanguageTag(GetString("appLanguage", "ar")); } }

        /// <summary>Normalizes a stored language tag so a stray separator or whitespace
        /// cannot silently flip the layout: "ar_EG" and " ar " both mean Arabic, and an
        /// empty tag means the Arabic default.</summary>
        public static string NormalizeLanguageTag(string language)
        {
            if (string.IsNullOrWhiteSpace(language)) return "ar";
            var tag = language.Trim().Replace('_', '-');
            return tag.Length == 0 ? "ar" : tag;
        }

        /// <summary>True for Arabic language tags ("ar", "ar-SA", ...): the app reads RTL then.
        /// An absent value means the Arabic default.</summary>
        public static bool IsRightToLeft(string language)
        {
            var tag = NormalizeLanguageTag(language);
            return tag.Equals("ar", StringComparison.OrdinalIgnoreCase)
                || tag.StartsWith("ar-", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>RTL from the app language the user chose (the stored appLanguage tag,
        /// normalized) — the same value assigned to PrimaryLanguageOverride, so it is the
        /// language the labels actually render in. ApplicationLanguages.Languages[0] must
        /// NOT decide the direction: it is the console's system-language list, not the
        /// override — the override affects resource loading only. Build 170 proved the
        /// split on the Xbox: Arabic labels with an LTR hero and LTR rows, because
        /// Languages[0] was a non-Arabic system tag while resources resolved Arabic.
        /// (The b81ec44 "fix" derived RTL from Languages[0], so it read the same wrong
        /// source and the hero stayed LTR.) The resolved list and the resource-context
        /// language are still logged, so the next Xbox log proves which source
        /// disagreed; the stored tag decides.</summary>
        public static bool EffectiveIsRightToLeft()
        {
            string stored = null;
            try
            {
                if (Current != null) stored = Current.AppLanguage;
            }
            catch (Exception)
            {
                stored = null;
            }

            string resolved = null;
            try
            {
                var languages = ApplicationLanguages.Languages;
                if (languages != null && languages.Count > 0) resolved = languages[0];
            }
            catch (Exception)
            {
                resolved = null;
            }

            string resourceLanguage = null;
            try
            {
                var context = Windows.ApplicationModel.Resources.Core.ResourceContext.GetForViewIndependentUse();
                if (context != null && context.Languages != null && context.Languages.Count > 0)
                    resourceLanguage = context.Languages[0];
            }
            catch (Exception)
            {
                resourceLanguage = null;
            }

            var rtl = IsRightToLeft(stored);
            Log.Write("Effective direction: stored '" + (stored ?? "?") + "', resource '" + (resourceLanguage ?? "?") + "', Languages[0] '" + (resolved ?? "?") + "' (ignored) => " + (rtl ? "RTL" : "LTR"));
            return rtl;
        }
        public bool ShowHero { get { return GetBool("showHero", true); } }
        public bool ShowCatalogType { get { return GetBool("showCatalogType", true); } }
        public bool HideUnreleased { get { return GetBool("hideUnreleased", false); } }
        public string PosterSize { get { return GetString("posterSize", "standard"); } }
        public bool Amoled { get { return GetBool("amoled", false); } }
        public bool ControllerCursor { get { return GetBool("controllerCursor", false); } }
        public bool RememberLastStream { get { return GetBool("rememberLastStream", true); } }
        public bool HardwareDecoding { get { return GetBool("hardwareDecoding", true); } }
    }
}
