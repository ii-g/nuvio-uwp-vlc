using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nuvio.Core;
using Nuvio.Diagnostics;
using Nuvio.Models;

namespace Nuvio.Services
{
    /// <summary>
    /// The fetch half of services/subtitles.js: addon subtitle discovery via
    /// StremioService and cue loading over HTTP. Parsing lives in
    /// Core/SubtitleParser.cs. Never logs a subtitle URL beyond its host:
    /// URLs may carry tokens in the query string.
    /// </summary>
    internal sealed class SubtitleService
    {
        private readonly StremioService _stremio;
        private readonly Dictionary<string, List<SubtitleCue>> _cueCache = new Dictionary<string, List<SubtitleCue>>();

        public SubtitleService(StremioService stremio)
        {
            _stremio = stremio ?? throw new ArgumentNullException(nameof(stremio));
        }

        /// <summary>Addon subtitles for a video, each keyed '&lt;index&gt;:&lt;url&gt;'.</summary>
        public async Task<List<SubtitleItem>> FetchAddonSubtitlesAsync(string type, string videoId, IDictionary<string, string> extra)
        {
            var refs = await _stremio.FetchSubtitlesAsync(type, videoId, extra).ConfigureAwait(false);
            var result = new List<SubtitleItem>();
            if (refs == null) return result;
            var index = 0;
            foreach (var sub in refs)
            {
                result.Add(new SubtitleItem
                {
                    Url = sub.Url,
                    Lang = sub.Lang,
                    Id = sub.Id,
                    AddonName = sub.AddonName,
                    Key = index + ":" + sub.Url,
                });
                index++;
            }
            return result;
        }

        /// <summary>
        /// Loads (and caches, by URL) the parsed cues for one subtitle. Returns an
        /// empty list when there is no URL or the fetch/parse fails; a failure is
        /// not cached, so a later retry can succeed.
        /// </summary>
        public async Task<List<SubtitleCue>> LoadCuesAsync(SubtitleItem sub)
        {
            if (sub == null || string.IsNullOrEmpty(sub.Url)) return new List<SubtitleCue>();
            List<SubtitleCue> cached;
            if (_cueCache.TryGetValue(sub.Url, out cached))
            {
                sub.Cues = cached;
                sub.CuesLoaded = true;
                return cached;
            }
            try
            {
                var text = await HttpService.GetTextAsync(sub.Url, 20000).ConfigureAwait(false);
                var cues = SubtitleParser.ParseSubtitles(text);
                _cueCache[sub.Url] = cues;
                sub.Cues = cues;
                sub.CuesLoaded = true;
                return cues;
            }
            catch (Exception ex)
            {
                Log.Error("Loading subtitle cues failed: " + Log.DescribeUrl(sub.Url), ex);
                return new List<SubtitleCue>();
            }
        }
    }
}
