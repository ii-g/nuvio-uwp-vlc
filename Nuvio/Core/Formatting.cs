using System;
using System.Globalization;
using Windows.ApplicationModel.Resources;

namespace Nuvio.Core
{
    /// <summary>Display formatting, ported from core/format.js.</summary>
    internal static class Formatting
    {
        private static readonly ResourceLoader Loader = ResourceLoader.GetForViewIndependentUse("Resources");

        private static string S(string key, string fallback)
        {
            var text = Loader.GetString(key);
            return string.IsNullOrEmpty(text) ? fallback : text;
        }

        public static string FormatClock(double seconds)
        {
            if (!IsFinite(seconds) || seconds < 0) seconds = 0;
            var s = (int)Math.Floor(seconds % 60);
            var m = (int)Math.Floor((seconds / 60) % 60);
            var hrs = (int)Math.Floor(seconds / 3600);
            var mm = hrs > 0 ? m.ToString("D2", CultureInfo.InvariantCulture) : m.ToString(CultureInfo.InvariantCulture);
            return (hrs > 0 ? hrs.ToString(CultureInfo.InvariantCulture) + ":" : "") + mm + ":" + s.ToString("D2", CultureInfo.InvariantCulture);
        }

        /// <summary>"1h 12m left" from milliseconds.</summary>
        public static string FormatTimeLeft(long ms)
        {
            if (ms <= 0) return string.Empty;
            var minutes = (int)Math.Round(ms / 60000.0);
            if (minutes < 1) return S("AlmostDone", "Almost done");
            var hrs = minutes / 60;
            var mins = minutes % 60;
            // TODO i18n: the "Nh Mm left" pattern has no Arabic translation in web/js/core/i18n.js,
            // so it ships in English rather than a guessed translation.
            return (hrs > 0 ? hrs.ToString(CultureInfo.InvariantCulture) + "h " + mins.ToString(CultureInfo.InvariantCulture) + "m" : mins.ToString(CultureInfo.InvariantCulture) + "m") + " left";
        }

        public static string FormatRuntime(string runtime)
        {
            if (string.IsNullOrEmpty(runtime)) return string.Empty;
            int minutes;
            if (!int.TryParse(LeadDigits(runtime), NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes)) return runtime;
            if (runtime.IndexOf('h') >= 0 || runtime.IndexOf('H') >= 0) return runtime;
            var hrs = minutes / 60;
            return hrs > 0
                ? hrs.ToString(CultureInfo.InvariantCulture) + "h " + (minutes % 60).ToString(CultureInfo.InvariantCulture) + "m"
                : minutes.ToString(CultureInfo.InvariantCulture) + "m";
        }

        private static string LeadDigits(string text)
        {
            var i = 0;
            while (i < text.Length && char.IsDigit(text[i])) i++;
            return text.Substring(0, i);
        }

        public static string FormatEpisodeCode(int? season, int? episode)
        {
            if (!season.HasValue || !episode.HasValue) return string.Empty;
            return "S" + season.Value.ToString(CultureInfo.InvariantCulture) + " E" + episode.Value.ToString(CultureInfo.InvariantCulture);
        }

        public static string FormatDate(string value)
        {
            DateTimeOffset date;
            if (string.IsNullOrEmpty(value) || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return string.Empty;
            return date.ToString("d MMM yyyy", CultureInfo.CurrentUICulture);
        }

        /// <summary>"Airs today", "Airs in 3 days", or a date, for unreleased episodes.</summary>
        public static string FormatAirs(string value)
        {
            DateTimeOffset date;
            if (string.IsNullOrEmpty(value) || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return S("Upcoming", "Upcoming");
            var days = (int)Math.Ceiling((date.Date - DateTimeOffset.UtcNow.Date).TotalDays);
            if (days <= 0) return S("AirsToday", "Airs today");
            if (days == 1) return S("AirsTomorrow", "Airs tomorrow");
            if (days < 14) return string.Format(CultureInfo.InvariantCulture, S("AirsInDays", "Airs in {0} days"), days);
            return S("Airs", "Airs") + " " + FormatDate(value);
        }

        public static string TypeLabel(string type)
        {
            switch (type)
            {
                case "movie": return S("TypeMovie", "Movie");
                case "series": return S("TypeSeries", "Series");
                case "channel": return S("TypeChannel", "Channel");
                case "tv": return S("TypeTv", "TV");
                case "anime": return S("TypeAnime", "Anime");
                default:
                    if (string.IsNullOrEmpty(type)) return string.Empty;
                    return char.ToUpperInvariant(type[0]) + type.Substring(1);
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
