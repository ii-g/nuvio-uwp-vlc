using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Nuvio.Models;

namespace Nuvio.Core
{
    /// <summary>
    /// SRT/WebVTT/ASS parsing, ported exactly from web/js/services/subtitles.js.
    /// Addon subtitles are drawn by the app over the video (VLC draws subtitles
    /// built into the file). Cues are { Start (s), End (s), Text }, sorted by start.
    /// </summary>
    internal static class SubtitleParser
    {
        private static readonly Regex TimestampPattern =
            new Regex(@"(?:(\d+):)?(\d{1,2}):(\d{1,2})[.,](\d{1,3})");

        private static readonly Regex KeepTagPattern =
            new Regex(@"</?(i|b|u|em|strong)>", RegexOptions.IgnoreCase);

        private static readonly Regex StripTagPattern =
            new Regex(@"<(?!\/?(i|b|u)>)[^>]*>", RegexOptions.IgnoreCase);

        private static readonly Regex OverridePattern = new Regex(@"\{\\[^}]*\}");

        private static readonly Regex AssOverridePattern = new Regex(@"\{[^}]*\}");

        private static readonly Regex BackslashNPattern = new Regex(@"\\N", RegexOptions.IgnoreCase);

        public static List<SubtitleCue> ParseSubtitles(string text)
        {
            if (text == null) return new List<SubtitleCue>();
            var clean = text;
            if (clean.StartsWith("\uFEFF", StringComparison.Ordinal)) clean = clean.Substring(1);
            clean = clean.Replace("\r\n", "\n").Replace("\r", "\n");
            if (Regex.IsMatch(clean, @"^\s*\[Script Info\]", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(clean, @"^Dialogue:", RegexOptions.Multiline))
            {
                return ParseAss(clean);
            }
            return ParseSrtOrVtt(clean);
        }

        private static double ToSeconds(string stamp)
        {
            if (string.IsNullOrEmpty(stamp)) return double.NaN;
            var match = TimestampPattern.Match(stamp.Trim());
            if (!match.Success) return double.NaN;
            double hours = 0;
            if (match.Groups[1].Success) double.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out hours);
            double minutes;
            double seconds;
            double ms;
            if (!double.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes)) return double.NaN;
            if (!double.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)) return double.NaN;
            var msText = match.Groups[4].Value.PadRight(3, '0');
            if (!double.TryParse(msText, NumberStyles.Integer, CultureInfo.InvariantCulture, out ms)) return double.NaN;
            return hours * 3600 + minutes * 60 + seconds + ms / 1000;
        }

        private static string CleanText(string text)
        {
            var kept = KeepTagPattern.Replace(text, match =>
                match.Value.ToLowerInvariant().Replace("em", "i").Replace("strong", "b"));
            var stripped = StripTagPattern.Replace(kept, string.Empty);
            stripped = OverridePattern.Replace(stripped, string.Empty);
            return stripped.Trim();
        }

        private static List<SubtitleCue> ParseSrtOrVtt(string text)
        {
            var cues = new List<SubtitleCue>();
            foreach (var block in Regex.Split(text, @"\n{2,}"))
            {
                var lines = block.Split('\n');
                var timeIndex = -1;
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("-->"))
                    {
                        timeIndex = i;
                        break;
                    }
                }
                if (timeIndex < 0) continue;
                var parts = lines[timeIndex].Split(new string[] { "-->" }, StringSplitOptions.None);
                if (parts.Length < 2) continue;
                var start = ToSeconds(parts[0]);
                var end = ToSeconds(parts[1].Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty);
                var body = CleanText(string.Join("\n", lines.Skip(timeIndex + 1)));
                if (!double.IsNaN(start) && !double.IsNaN(end) && !string.IsNullOrEmpty(body))
                {
                    cues.Add(new SubtitleCue { Start = start, End = end, Text = body });
                }
            }
            return cues.OrderBy(c => c.Start).ToList();
        }

        private static List<SubtitleCue> ParseAss(string text)
        {
            var cues = new List<SubtitleCue>();
            string[] format = null;
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase) && format == null)
                {
                    var fields = line.Substring(7).Split(',').Select(s => s.Trim().ToLowerInvariant()).ToArray();
                    if (fields.Contains("text")) format = fields;
                    continue;
                }
                if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
                var used = format ?? new string[] { "layer", "start", "end", "style", "name", "marginl", "marginr", "marginv", "effect", "text" };
                var parts = line.Substring(9).Split(',');
                var body = string.Join(",", parts.Skip(used.Length - 1));
                body = AssOverridePattern.Replace(body, string.Empty);
                body = BackslashNPattern.Replace(body, "\n");
                body = body.Replace("\\h", " ");
                body = body.Trim();
                var start = ToSeconds(FieldValue(used, parts, "start"));
                var end = ToSeconds(FieldValue(used, parts, "end"));
                if (!double.IsNaN(start) && !double.IsNaN(end) && !string.IsNullOrEmpty(body))
                {
                    cues.Add(new SubtitleCue { Start = start, End = end, Text = body });
                }
            }
            return cues.OrderBy(c => c.Start).ToList();
        }

        private static string FieldValue(string[] fields, string[] parts, string name)
        {
            var index = Array.IndexOf(fields, name);
            if (index < 0 || index >= parts.Length) return string.Empty;
            return parts[index];
        }

        /// <summary>The cues showing at time t (seconds).</summary>
        public static List<SubtitleCue> CuesAt(List<SubtitleCue> cues, double t)
        {
            var active = new List<SubtitleCue>();
            if (cues == null || cues.Count == 0) return active;
            var lo = 0;
            var hi = cues.Count - 1;
            // Last cue starting at or before t.
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                if (cues[mid].Start <= t) lo = mid + 1;
                else hi = mid - 1;
            }
            for (var i = hi; i >= 0 && i > hi - 8; i--)
            {
                if (cues[i].End > t) active.Insert(0, cues[i]);
            }
            return active;
        }
    }
}
