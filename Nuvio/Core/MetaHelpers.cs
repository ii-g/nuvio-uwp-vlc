using System;
using System.Collections.Generic;
using System.Linq;
using Nuvio.Models;
using Nuvio.Services;

namespace Nuvio.Core
{
    /// <summary>What the Play button should play for a series (port of meta.js upNext).</summary>
    internal sealed class UpNextResult
    {
        public Video Video { get; set; }
        public bool Resume { get; set; }
        public ProgressEntry Entry { get; set; }
    }

    /// <summary>Meta helpers, ported from services/meta.js.</summary>
    internal static class MetaHelpers
    {
        public static bool IsReleased(Video video)
        {
            if (video == null || string.IsNullOrEmpty(video.Released)) return true;
            DateTimeOffset date;
            if (!DateTimeOffset.TryParse(video.Released, out date)) return true;
            return date <= DateTimeOffset.UtcNow;
        }

        /// <summary>Episodes in order, with season/episode numbers filled in (normalised at parse time in Video.FromJson).</summary>
        public static List<Video> SortedVideos(Meta meta)
        {
            if (meta == null || meta.Videos == null) return new List<Video>();
            return meta.Videos
                .Where(v => v != null && !string.IsNullOrEmpty(v.Id))
                .OrderBy(v => v.Season)
                .ThenBy(v => v.Episode)
                .ToList();
        }

        /// <summary>Season numbers, regular seasons first and specials (0) last.</summary>
        public static List<int> SeasonsOf(Meta meta)
        {
            var seasons = SortedVideos(meta).Select(v => v.Season).Distinct().ToList();
            return seasons.Where(s => s > 0).Concat(seasons.Where(s => s == 0)).ToList();
        }

        public static Video NextEpisode(Meta meta, int season, int episode)
        {
            var videos = SortedVideos(meta).Where(v => v.Season > 0).ToList();
            var index = videos.FindIndex(v => v.Season == season && v.Episode == episode);
            if (index < 0) return null;
            return index + 1 < videos.Count ? videos[index + 1] : null;
        }

        public static Video FirstEpisode(Meta meta)
        {
            var videos = SortedVideos(meta);
            return videos.FirstOrDefault(v => v.Season > 0) ?? videos.FirstOrDefault();
        }

        public static UpNextResult UpNext(Meta meta, ProgressService progress)
        {
            var entries = progress.AllProgress()
                .Where(e => e.ContentId == meta.Id && e.Season.HasValue)
                .OrderByDescending(e => e.LastWatched)
                .ToList();
            var latest = entries.FirstOrDefault();
            if (latest != null)
            {
                if (!progress.IsCompleted(latest))
                {
                    var video = SortedVideos(meta).FirstOrDefault(v => v.Season == latest.Season.Value && v.Episode == latest.Episode.Value);
                    if (video != null) return new UpNextResult { Video = video, Resume = true, Entry = latest };
                }
                var next = NextEpisode(meta, latest.Season.Value, latest.Episode.Value);
                if (next != null) return new UpNextResult { Video = next, Resume = false };
            }
            // Nothing in progress: the first episode not marked watched.
            var unwatched = SortedVideos(meta).FirstOrDefault(v => v.Season > 0 && !progress.IsWatched(meta.Id, v.Season, v.Episode));
            return new UpNextResult { Video = unwatched ?? FirstEpisode(meta), Resume = false };
        }

        /// <summary>The year or year range shown for a title.</summary>
        public static string YearText(Meta meta)
        {
            if (meta == null) return string.Empty;
            if (!string.IsNullOrEmpty(meta.ReleaseInfo)) return meta.ReleaseInfo;
            return meta.Year.HasValue ? meta.Year.Value.ToString() : string.Empty;
        }

        public static List<string> GenresOf(Meta meta)
        {
            if (meta == null || meta.Genres == null) return new List<string>();
            return meta.Genres;
        }
    }
}
