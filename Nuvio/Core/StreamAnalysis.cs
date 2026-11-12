using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Nuvio.Models;

namespace Nuvio.Core
{
    /// <summary>What kind of link a stream result is (port of streamKind in services/streams.js).</summary>
    internal enum StreamKind
    {
        Http,
        Torrent,
        External,
        Youtube,
        Unsupported
    }

    /// <summary>Everything the stream list shows about one result (port of describeStream).</summary>
    internal sealed class StreamInfo
    {
        public string AddonName { get; set; }
        public string Name { get; set; }
        public string Detail { get; set; }
        public string Resolution { get; set; }
        public double Rank { get; set; }
        public List<string> Badges { get; set; }
        public long Size { get; set; }
        public string SizeText { get; set; }
        public int? Seeders { get; set; }
        public string Debrid { get; set; }
        public bool Cached { get; set; }
        public StreamKind Kind { get; set; }
        public string BingeGroup { get; set; }
    }

    /// <summary>A stream result together with its description and serving addon.</summary>
    internal sealed class StreamEntry
    {
        public StreamItem Stream { get; set; }
        public StreamInfo Info { get; set; }
        public Addon Addon { get; set; }
        public int Order { get; set; }
    }

    /// <summary>
    /// Pure stream-result logic, ported exactly from web/js/services/streams.js.
    /// No network, no state: every method is a pure function of its inputs.
    /// </summary>
    internal static class StreamAnalysis
    {
        private sealed class Resolution
        {
            public string Label;
            public double Rank;
            public Regex Pattern;
        }

        private static readonly Resolution[] Resolutions = new Resolution[]
        {
            new Resolution { Label = "4K", Rank = 4, Pattern = new Regex(@"\b(2160p|4k|uhd)\b", RegexOptions.IgnoreCase) },
            new Resolution { Label = "1440p", Rank = 3.5, Pattern = new Regex(@"\b1440p\b", RegexOptions.IgnoreCase) },
            new Resolution { Label = "1080p", Rank = 3, Pattern = new Regex(@"\b(1080p|fhd)\b", RegexOptions.IgnoreCase) },
            new Resolution { Label = "720p", Rank = 2, Pattern = new Regex(@"\b(720p|hd)\b", RegexOptions.IgnoreCase) },
            new Resolution { Label = "576p", Rank = 1.5, Pattern = new Regex(@"\b576p\b", RegexOptions.IgnoreCase) },
            new Resolution { Label = "480p", Rank = 1, Pattern = new Regex(@"\b(480p|sd)\b", RegexOptions.IgnoreCase) },
        };

        private static readonly Regex DebridTags =
            new Regex(@"\[(RD|AD|PM|TB|DL|ED|OC|PKP|EN|TRB|DLR|EASY|PIO|SEEDR)(\+|\u26A1| download)?\]", RegexOptions.IgnoreCase);

        private static readonly Regex SizePattern =
            new Regex(@"(\d+(?:[.,]\d+)?)\s?(TB|TiB|GB|GiB|MB|MiB)\b", RegexOptions.IgnoreCase);

        private static readonly Regex SeedersPattern = new Regex("👤\\s*(\\d+)");

        private static readonly Dictionary<string, int> RiskyBadges = new Dictionary<string, int>
        {
            { "HEVC", 3 },
            { "AV1", 3 },
            { "DTS-HD", 2 },
            { "DTS", 2 },
            { "TrueHD", 2 },
            { "DV", 2 },
            { "HDR10+", 1 },
            { "REMUX", 2 },
        };

        /// <summary>"http" (playable here), "torrent", "external", "youtube" or "unsupported".</summary>
        public static StreamKind GetKind(StreamItem stream)
        {
            if (stream == null) return StreamKind.Unsupported;
            if (!string.IsNullOrEmpty(stream.Url) &&
                (Regex.IsMatch(stream.Url, @"^https?://", RegexOptions.IgnoreCase) ||
                 Regex.IsMatch(stream.Url, @"^(rtsp|rtmp|mms|ftp|smb)://", RegexOptions.IgnoreCase)))
            {
                return StreamKind.Http;
            }
            if (!string.IsNullOrEmpty(stream.InfoHash)) return StreamKind.Torrent;
            if (!string.IsNullOrEmpty(stream.YtId)) return StreamKind.Youtube;
            if (!string.IsNullOrEmpty(stream.ExternalUrl)) return StreamKind.External;
            return StreamKind.Unsupported;
        }

        public static bool IsPlayable(StreamItem stream)
        {
            return GetKind(stream) == StreamKind.Http;
        }

        private static string StreamText(StreamItem stream)
        {
            var hints = stream.BehaviorHints;
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(stream.Name)) parts.Add(stream.Name);
            if (!string.IsNullOrEmpty(stream.Title)) parts.Add(stream.Title);
            if (!string.IsNullOrEmpty(stream.Description)) parts.Add(stream.Description);
            if (hints != null && !string.IsNullOrEmpty(hints.Filename)) parts.Add(hints.Filename);
            return string.Join("\n", parts);
        }

        private static long ParseSize(string text, StreamBehaviorHints hints)
        {
            if (hints != null && hints.VideoSize.HasValue && hints.VideoSize.Value > 0) return hints.VideoSize.Value;
            var match = SizePattern.Match(text ?? string.Empty);
            if (!match.Success) return 0;
            double value;
            if (!double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return 0;
            var unit = match.Groups[2].Value.ToUpperInvariant();
            double factor;
            if (unit.StartsWith("T", StringComparison.Ordinal)) factor = 1024.0 * 1024 * 1024 * 1024;
            else if (unit.StartsWith("G", StringComparison.Ordinal)) factor = 1024.0 * 1024 * 1024;
            else factor = 1024.0 * 1024;
            return (long)(value * factor);
        }

        public static string FormatSize(long bytes)
        {
            if (bytes <= 0) return string.Empty;
            const double gigabyte = 1024.0 * 1024 * 1024;
            if (bytes >= gigabyte)
            {
                return (bytes / gigabyte).ToString(bytes >= 10 * gigabyte ? "F0" : "F1", CultureInfo.InvariantCulture) + " GB";
            }
            return Math.Round(bytes / (1024.0 * 1024), MidpointRounding.AwayFromZero) + " MB";
        }

        /// <summary>Everything the stream list shows about a result.</summary>
        public static StreamInfo DescribeStream(StreamItem stream, string addonName)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            var text = StreamText(stream);
            var hints = stream.BehaviorHints;
            Resolution resolution = null;
            foreach (var candidate in Resolutions)
            {
                if (candidate.Pattern.IsMatch(text))
                {
                    resolution = candidate;
                    break;
                }
            }
            var badges = new List<string>();
            if (Regex.IsMatch(text, @"\b(DV|DoVi|dolby[ .]?vision)\b", RegexOptions.IgnoreCase)) badges.Add("DV");
            if (Regex.IsMatch(text, @"\bHDR10\+", RegexOptions.IgnoreCase)) badges.Add("HDR10+");
            else if (Regex.IsMatch(text, @"\bHDR(10)?\b", RegexOptions.IgnoreCase)) badges.Add("HDR");
            if (Regex.IsMatch(text, @"\b(x265|h\.?265|hevc)\b", RegexOptions.IgnoreCase)) badges.Add("HEVC");
            else if (Regex.IsMatch(text, @"\bav1\b", RegexOptions.IgnoreCase)) badges.Add("AV1");
            if (Regex.IsMatch(text, @"\batmos\b", RegexOptions.IgnoreCase)) badges.Add("Atmos");
            if (Regex.IsMatch(text, @"\btrue-?hd\b", RegexOptions.IgnoreCase)) badges.Add("TrueHD");
            else if (Regex.IsMatch(text, @"\bdts[- ]?(hd|x|ma)\b", RegexOptions.IgnoreCase)) badges.Add("DTS-HD");
            else if (Regex.IsMatch(text, @"\bdts\b", RegexOptions.IgnoreCase)) badges.Add("DTS");
            else if (Regex.IsMatch(text, @"\b(ddp|dd\+|e-?ac-?3)\b", RegexOptions.IgnoreCase)) badges.Add("DD+");
            if (Regex.IsMatch(text, @"\bremux\b", RegexOptions.IgnoreCase)) badges.Add("REMUX");
            var debridTag = DebridTags.Match(text);
            var cached = (text.IndexOf("⚡", StringComparison.Ordinal) >= 0 ||
                          Regex.IsMatch(text, @"\+\]", RegexOptions.IgnoreCase) ||
                          Regex.IsMatch(text, @"\bcached\b", RegexOptions.IgnoreCase)) &&
                         !Regex.IsMatch(text, @"\bnot cached\b|\buncached\b", RegexOptions.IgnoreCase);
            var size = ParseSize(text, hints);
            var seedersMatch = SeedersPattern.Match(text);
            int? seeders = null;
            if (seedersMatch.Success)
            {
                int parsed;
                if (int.TryParse(seedersMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) seeders = parsed;
            }

            var nameLines = new List<string>();
            foreach (var line in (stream.Name ?? string.Empty).Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) nameLines.Add(trimmed);
            }
            var detail = (stream.Title ?? stream.Description ?? string.Empty).Trim();
            return new StreamInfo
            {
                AddonName = addonName,
                // Addons put their own name plus a short quality label in "name".
                Name = nameLines.Count > 0 ? string.Join(" ", nameLines) : addonName,
                Detail = detail,
                Resolution = resolution != null ? resolution.Label : null,
                Rank = resolution != null ? resolution.Rank : 0,
                Badges = badges,
                Size = size,
                SizeText = FormatSize(size),
                Seeders = seeders,
                Debrid = debridTag.Success ? debridTag.Groups[1].Value.ToUpperInvariant() : null,
                Cached = cached,
                Kind = GetKind(stream),
                BingeGroup = hints != null ? hints.BingeGroup : null,
            };
        }

        /// <summary>Request headers the stream asks the player to send (behaviorHints.proxyHeaders).</summary>
        public static Dictionary<string, string> StreamHeaders(StreamItem stream)
        {
            var headers = new Dictionary<string, string>();
            if (stream == null || stream.BehaviorHints == null || stream.BehaviorHints.ProxyRequestHeaders == null) return headers;
            foreach (var pair in stream.BehaviorHints.ProxyRequestHeaders)
            {
                if (pair.Value != null) headers[pair.Key] = pair.Value;
            }
            return headers;
        }

        /// <summary>Returns a new list; the input list is never mutated.</summary>
        public static List<StreamEntry> SortStreams(List<StreamEntry> entries, string mode)
        {
            if (entries == null) return new List<StreamEntry>();
            var sorted = new List<StreamEntry>(entries);
            if (mode == "quality")
            {
                sorted.Sort((a, b) =>
                {
                    var byRank = b.Info.Rank.CompareTo(a.Info.Rank);
                    if (byRank != 0) return byRank;
                    var byCached = (b.Info.Cached ? 1 : 0).CompareTo(a.Info.Cached ? 1 : 0);
                    if (byCached != 0) return byCached;
                    return b.Info.Size.CompareTo(a.Info.Size);
                });
            }
            else if (mode == "size")
            {
                sorted.Sort((a, b) => b.Info.Size.CompareTo(a.Info.Size));
            }
            return sorted;
        }

        /// <summary>
        /// How likely the primary (FFmpegInteropX) player is to open this stream first try.
        /// Lower is tried earlier. mp4/H.264 and adaptive HLS/DASH go first; mkv and streams
        /// whose titles promise HEVC/AV1/DTS/TrueHD/REMUX go last.
        /// </summary>
        public static double PlaybackScore(StreamItem stream, StreamInfo info)
        {
            double score = 0;
            var url = stream != null ? stream.Url ?? string.Empty : string.Empty;
            var path = url.Split('?')[0].ToLowerInvariant();
            if (path.EndsWith(".m3u8", StringComparison.Ordinal) || path.EndsWith(".mpd", StringComparison.Ordinal)) score -= 1;
            else if (path.EndsWith(".mp4", StringComparison.Ordinal) || path.EndsWith(".m4v", StringComparison.Ordinal)) score += 0;
            else if (path.EndsWith(".mkv", StringComparison.Ordinal)) score += 3;
            else if (path.EndsWith(".webm", StringComparison.Ordinal)) score += 2;
            else score += 2; // No recognizable extension (e.g. /redirect): the container cannot be guessed from the URL.
            if (info != null && info.Badges != null)
            {
                foreach (var badge in info.Badges)
                {
                    int risk;
                    if (RiskyBadges.TryGetValue(badge, out risk)) score += risk;
                }
            }
            if (info != null && info.Cached) score -= 1;
            return score;
        }

        /// <summary>Entries ordered for playback attempts, best first. Returns a new list.</summary>
        public static List<StreamEntry> SortForPlayback(List<StreamEntry> entries)
        {
            if (entries == null) return new List<StreamEntry>();
            var sorted = new List<StreamEntry>(entries);
            sorted.Sort((a, b) =>
            {
                var byScore = PlaybackScore(a.Stream, a.Info).CompareTo(PlaybackScore(b.Stream, b.Info));
                if (byScore != 0) return byScore;
                return b.Info.Rank.CompareTo(a.Info.Rank);
            });
            return sorted;
        }
    }
}
