using System;
using System.Collections.Generic;

namespace Nuvio.Core
{
    /// <summary>
    /// Language names, ported verbatim from services/subtitles.js (LANGUAGE_CHOICES +
    /// LANGUAGES). Note: the architecture doc says "30 pairs" for LANGUAGE_CHOICES, but the
    /// code it quotes actually carries 35; the code is copied here as-is.
    /// </summary>
    internal static class LanguageNames
    {
        private static readonly Dictionary<string, string> Languages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "eng", "English" }, { "en", "English" },
            { "spa", "Spanish" }, { "es", "Spanish" }, { "spn", "Spanish" },
            { "fre", "French" }, { "fra", "French" }, { "fr", "French" },
            { "ger", "German" }, { "deu", "German" }, { "de", "German" },
            { "ita", "Italian" }, { "it", "Italian" },
            { "por", "Portuguese" }, { "pt", "Portuguese" },
            { "pob", "Portuguese (BR)" }, { "pt-br", "Portuguese (BR)" },
            { "rus", "Russian" }, { "ru", "Russian" },
            { "ara", "Arabic" }, { "ar", "Arabic" },
            { "hin", "Hindi" }, { "hi", "Hindi" },
            { "jpn", "Japanese" }, { "ja", "Japanese" },
            { "kor", "Korean" }, { "ko", "Korean" },
            { "chi", "Chinese" }, { "zho", "Chinese" }, { "zh", "Chinese" }, { "ze", "Chinese" },
            { "zht", "Chinese (Traditional)" }, { "zhs", "Chinese (Simplified)" },
            { "chs", "Chinese (Simplified)" }, { "cht", "Chinese (Traditional)" },
            { "zh-tw", "Chinese (Traditional)" }, { "zh-cn", "Chinese (Simplified)" },
            { "dut", "Dutch" }, { "nld", "Dutch" }, { "nl", "Dutch" },
            { "pol", "Polish" }, { "pl", "Polish" },
            { "tur", "Turkish" }, { "tr", "Turkish" },
            { "swe", "Swedish" }, { "sv", "Swedish" },
            { "nor", "Norwegian" }, { "nob", "Norwegian" }, { "no", "Norwegian" },
            { "dan", "Danish" }, { "da", "Danish" },
            { "fin", "Finnish" }, { "fi", "Finnish" },
            { "gre", "Greek" }, { "ell", "Greek" }, { "el", "Greek" },
            { "heb", "Hebrew" }, { "he", "Hebrew" },
            { "hun", "Hungarian" }, { "hu", "Hungarian" },
            { "cze", "Czech" }, { "ces", "Czech" }, { "cs", "Czech" },
            { "rum", "Romanian" }, { "ron", "Romanian" }, { "ro", "Romanian" },
            { "bul", "Bulgarian" }, { "bg", "Bulgarian" },
            { "hrv", "Croatian" }, { "hr", "Croatian" },
            { "srp", "Serbian" }, { "sr", "Serbian" },
            { "slv", "Slovenian" }, { "slo", "Slovak" }, { "slk", "Slovak" },
            { "ukr", "Ukrainian" }, { "uk", "Ukrainian" },
            { "vie", "Vietnamese" }, { "vi", "Vietnamese" },
            { "tha", "Thai" }, { "th", "Thai" },
            { "ind", "Indonesian" }, { "id", "Indonesian" },
            { "may", "Malay" }, { "msa", "Malay" },
            { "per", "Persian" }, { "fas", "Persian" }, { "fa", "Persian" },
            { "ben", "Bengali" }, { "bn", "Bengali" },
            { "tam", "Tamil" }, { "ta", "Tamil" },
            { "tel", "Telugu" }, { "te", "Telugu" },
            { "mal", "Malayalam" }, { "ml", "Malayalam" },
            { "urd", "Urdu" }, { "ur", "Urdu" },
            { "cat", "Catalan" },
            { "baq", "Basque" }, { "eus", "Basque" },
            { "glg", "Galician" },
            { "est", "Estonian" },
            { "lav", "Latvian" },
            { "lit", "Lithuanian" },
            { "ice", "Icelandic" }, { "isl", "Icelandic" },
            { "alb", "Albanian" }, { "sqi", "Albanian" },
            { "mac", "Macedonian" }, { "mkd", "Macedonian" },
            { "bos", "Bosnian" },
            { "tgl", "Filipino" }, { "fil", "Filipino" },
        };

        /// <summary>The [code, name] pairs offered for subtitle/audio language settings (ISO 639-2).</summary>
        public static readonly IReadOnlyList<KeyValuePair<string, string>> LanguageChoices = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("eng", "English"),
            new KeyValuePair<string, string>("spa", "Spanish"),
            new KeyValuePair<string, string>("fre", "French"),
            new KeyValuePair<string, string>("ger", "German"),
            new KeyValuePair<string, string>("ita", "Italian"),
            new KeyValuePair<string, string>("por", "Portuguese"),
            new KeyValuePair<string, string>("pob", "Portuguese (BR)"),
            new KeyValuePair<string, string>("rus", "Russian"),
            new KeyValuePair<string, string>("ara", "Arabic"),
            new KeyValuePair<string, string>("hin", "Hindi"),
            new KeyValuePair<string, string>("jpn", "Japanese"),
            new KeyValuePair<string, string>("kor", "Korean"),
            new KeyValuePair<string, string>("chi", "Chinese"),
            new KeyValuePair<string, string>("dut", "Dutch"),
            new KeyValuePair<string, string>("pol", "Polish"),
            new KeyValuePair<string, string>("tur", "Turkish"),
            new KeyValuePair<string, string>("swe", "Swedish"),
            new KeyValuePair<string, string>("nor", "Norwegian"),
            new KeyValuePair<string, string>("dan", "Danish"),
            new KeyValuePair<string, string>("fin", "Finnish"),
            new KeyValuePair<string, string>("gre", "Greek"),
            new KeyValuePair<string, string>("heb", "Hebrew"),
            new KeyValuePair<string, string>("hun", "Hungarian"),
            new KeyValuePair<string, string>("cze", "Czech"),
            new KeyValuePair<string, string>("rum", "Romanian"),
            new KeyValuePair<string, string>("ukr", "Ukrainian"),
            new KeyValuePair<string, string>("vie", "Vietnamese"),
            new KeyValuePair<string, string>("tha", "Thai"),
            new KeyValuePair<string, string>("ind", "Indonesian"),
            new KeyValuePair<string, string>("per", "Persian"),
            new KeyValuePair<string, string>("tam", "Tamil"),
            new KeyValuePair<string, string>("tel", "Telugu"),
            new KeyValuePair<string, string>("mal", "Malayalam"),
            new KeyValuePair<string, string>("ben", "Bengali"),
            new KeyValuePair<string, string>("urd", "Urdu"),
        }.AsReadOnly();

        public static string LanguageName(string code)
        {
            if (string.IsNullOrEmpty(code)) return "Unknown";
            var key = code.ToLowerInvariant();
            string name;
            if (Languages.TryGetValue(key, out name)) return name;
            var dash = key.IndexOfAny(new[] { '-', '_' });
            if (dash > 0 && Languages.TryGetValue(key.Substring(0, dash), out name)) return name;
            return code;
        }

        /// <summary>Whether a track/subtitle language matches a preferred ISO 639-2 code such as "eng".</summary>
        public static bool LanguageMatches(string code, string preferred)
        {
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(preferred)) return false;
            return string.Equals(LanguageName(code).ToLowerInvariant(), LanguageName(preferred).ToLowerInvariant(), StringComparison.Ordinal);
        }
    }
}
