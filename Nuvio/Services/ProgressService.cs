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
    /// <summary>Parsed "tt123:1:2" video id (port of progress.js parseVideoId).</summary>
    internal sealed class ParsedVideoId
    {
        public string ContentId { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
    }

    internal sealed class ProgressChangedEventArgs : EventArgs
    {
        public ProgressEntry Entry { get; set; }
        public bool Important { get; set; }
        public IReadOnlyList<ProgressEntry> Removed { get; set; }
        public bool Replaced { get; set; }
    }

    internal sealed class WatchedChangedEventArgs : EventArgs
    {
        public string ContentId { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public bool Value { get; set; }
        public string Title { get; set; }
        public string Type { get; set; }
        public bool Imported { get; set; }
        public bool Replaced { get; set; }
    }

    /// <summary>
    /// Watch progress (resume points, Continue Watching) and watched marks, ported from
    /// services/progress.js (local only in M1; sync is M5).
    /// </summary>
    internal sealed class ProgressService
    {
        public const double CompletedFraction = 0.9;
        private const long MinResumeMs = 30000;

        private readonly LocalStore _store;
        private Dictionary<string, ProgressEntry> _progress = new Dictionary<string, ProgressEntry>();
        private Dictionary<string, WatchedMark> _watched = new Dictionary<string, WatchedMark>();
        private Dictionary<string, long> _dismissed = new Dictionary<string, long>();
        private Task _loadTask;

        public event EventHandler<ProgressChangedEventArgs> ProgressChanged;
        public event EventHandler<WatchedChangedEventArgs> WatchedChanged;

        public ProgressService(LocalStore store)
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
            _progress = ReadEntries(await _store.ReadProfileJsonAsync("progress").ConfigureAwait(false));
            _watched = ReadWatched(await _store.ReadProfileJsonAsync("watched").ConfigureAwait(false));
            _dismissed = new Dictionary<string, long>();
            var dismissedValue = await _store.ReadProfileJsonAsync("cwDismissed").ConfigureAwait(false);
            var dismissedObj = JsonHelpers.GetObject(dismissedValue);
            if (dismissedObj != null)
            {
                foreach (var pair in dismissedObj)
                {
                    var stamp = JsonHelpers.GetLong(pair.Value);
                    if (stamp.HasValue) _dismissed[pair.Key] = stamp.Value;
                }
            }
        }

        private static Dictionary<string, ProgressEntry> ReadEntries(IJsonValue value)
        {
            var result = new Dictionary<string, ProgressEntry>();
            var obj = JsonHelpers.GetObject(value);
            if (obj == null) return result;
            foreach (var pair in obj)
            {
                var entry = ProgressEntry.FromJson(pair.Value);
                if (entry != null && !string.IsNullOrEmpty(entry.Key)) result[entry.Key] = entry;
            }
            return result;
        }

        private static Dictionary<string, WatchedMark> ReadWatched(IJsonValue value)
        {
            var result = new Dictionary<string, WatchedMark>();
            var obj = JsonHelpers.GetObject(value);
            if (obj == null) return result;
            foreach (var pair in obj)
            {
                var mark = WatchedMark.FromJson(pair.Value);
                if (mark != null && !string.IsNullOrEmpty(mark.ContentId)) result[pair.Key] = mark;
            }
            return result;
        }

        private void EnsureLoaded()
        {
            if (_loadTask != null) return;
            var _ = LoadAsync();
        }

        public void ReloadProgress()
        {
            _loadTask = null;
            _progress = new Dictionary<string, ProgressEntry>();
            _watched = new Dictionary<string, WatchedMark>();
            var _ = LoadAsync().ContinueWith(t =>
            {
                RaiseProgressChanged(new ProgressChangedEventArgs());
                RaiseWatchedChanged(new WatchedChangedEventArgs());
            });
        }

        private void RaiseProgressChanged(ProgressChangedEventArgs args)
        {
            var handler = ProgressChanged;
            if (handler != null) handler(this, args);
        }

        private void RaiseWatchedChanged(WatchedChangedEventArgs args)
        {
            var handler = WatchedChanged;
            if (handler != null) handler(this, args);
        }

        private async void SaveProgressFileAsync()
        {
            try
            {
                var obj = new JsonObject();
                foreach (var entry in _progress.Values) obj.SetNamedValue(entry.Key, entry.ToJson());
                await _store.WriteProfileJsonAsync("progress", obj).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Saving progress failed", ex);
            }
        }

        private async void SaveWatchedFileAsync()
        {
            try
            {
                var obj = new JsonObject();
                foreach (var pair in _watched) obj.SetNamedValue(pair.Key, pair.Value.ToJson());
                await _store.WriteProfileJsonAsync("watched", obj).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Saving watched marks failed", ex);
            }
        }

        private async void SaveDismissedFileAsync()
        {
            try
            {
                var obj = new JsonObject();
                foreach (var pair in _dismissed) obj.SetNamedValue(pair.Key, JsonHelpers.CreateNumber(pair.Value));
                await _store.WriteProfileJsonAsync("cwDismissed", obj).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Saving dismissed Continue Watching failed", ex);
            }
        }

        // ---------- Keys ----------

        public static string ProgressKey(string contentId, int? season, int? episode)
        {
            return season.HasValue && episode.HasValue
                ? contentId + "_s" + season.Value + "e" + episode.Value
                : contentId;
        }

        public static string WatchedKey(string contentId, int? season, int? episode)
        {
            return season.HasValue && episode.HasValue
                ? contentId + ":" + season.Value + ":" + episode.Value
                : contentId;
        }

        /// <summary>Parses a Stremio video id like "tt123:1:2" into its show id, season and episode.</summary>
        public static ParsedVideoId ParseVideoId(string videoId, string type)
        {
            var parts = (videoId ?? string.Empty).Split(':');
            if (type == "series" && parts.Length >= 3)
            {
                int episode;
                int season;
                if (int.TryParse(parts[parts.Length - 1], out episode) && int.TryParse(parts[parts.Length - 2], out season))
                {
                    return new ParsedVideoId
                    {
                        ContentId = string.Join(":", parts.Take(parts.Length - 2)),
                        Season = season,
                        Episode = episode,
                    };
                }
            }
            return new ParsedVideoId { ContentId = videoId, Season = null, Episode = null };
        }

        // ---------- Progress ----------

        public ProgressEntry GetProgress(string contentId, int? season, int? episode)
        {
            EnsureLoaded();
            ProgressEntry entry;
            return _progress.TryGetValue(ProgressKey(contentId, season, episode), out entry) ? entry : null;
        }

        public bool IsCompleted(ProgressEntry entry)
        {
            return entry != null && entry.Duration > 0 && (double)entry.Position / entry.Duration >= CompletedFraction;
        }

        /// <summary>Where to resume this video, in seconds (0 to start from the beginning).</summary>
        public double ResumeSeconds(string contentId, int? season, int? episode)
        {
            var entry = GetProgress(contentId, season, episode);
            if (entry == null || IsCompleted(entry) || entry.Position < MinResumeMs) return 0;
            return entry.Position / 1000.0;
        }

        /// <summary>
        /// Records progress. info carries contentId/contentType/videoId/season/episode/position
        /// (ms)/duration (ms) plus display fields (title, poster, background, logo, episodeTitle,
        /// thumbnail, stream).
        /// </summary>
        public ProgressEntry SaveProgress(ProgressEntry info, bool important = false, bool keepTime = false)
        {
            EnsureLoaded();
            var key = ProgressKey(info.ContentId, info.Season, info.Episode);
            ProgressEntry previous;
            _progress.TryGetValue(key, out previous);
            var lastWatched = keepTime && info.LastWatched > 0 ? info.LastWatched : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (keepTime && previous != null && previous.LastWatched > lastWatched) return previous;
            var entry = MergeEntry(previous, info);
            entry.Key = key;
            entry.LastWatched = lastWatched;
            _progress[key] = entry;
            SaveProgressFileAsync();
            if (IsCompleted(entry) && !IsWatched(info.ContentId, info.Season, info.Episode))
            {
                MarkWatched(info.ContentId, info.Season, info.Episode, true, info.Title, info.ContentType);
            }
            // important: a pause, stop or finish, as opposed to the regular save during playback.
            RaiseProgressChanged(new ProgressChangedEventArgs { Entry = entry, Important = important });
            return entry;
        }

        private static ProgressEntry MergeEntry(ProgressEntry previous, ProgressEntry info)
        {
            if (previous == null) return CloneEntry(info);
            var merged = CloneEntry(previous);
            if (info.ContentId != null) merged.ContentId = info.ContentId;
            if (info.ContentType != null) merged.ContentType = info.ContentType;
            if (info.VideoId != null) merged.VideoId = info.VideoId;
            if (info.Season.HasValue) merged.Season = info.Season;
            if (info.Episode.HasValue) merged.Episode = info.Episode;
            merged.Position = info.Position;
            merged.Duration = info.Duration;
            if (info.Title != null) merged.Title = info.Title;
            if (info.Poster != null) merged.Poster = info.Poster;
            if (info.Background != null) merged.Background = info.Background;
            if (info.Logo != null) merged.Logo = info.Logo;
            if (info.EpisodeTitle != null) merged.EpisodeTitle = info.EpisodeTitle;
            if (info.Thumbnail != null) merged.Thumbnail = info.Thumbnail;
            if (info.Stream != null) merged.Stream = info.Stream;
            return merged;
        }

        private static ProgressEntry CloneEntry(ProgressEntry source)
        {
            return new ProgressEntry
            {
                Key = source.Key,
                ContentId = source.ContentId,
                ContentType = source.ContentType,
                VideoId = source.VideoId,
                Season = source.Season,
                Episode = source.Episode,
                Position = source.Position,
                Duration = source.Duration,
                LastWatched = source.LastWatched,
                Title = source.Title,
                Poster = source.Poster,
                Background = source.Background,
                Logo = source.Logo,
                EpisodeTitle = source.EpisodeTitle,
                Thumbnail = source.Thumbnail,
                Stream = source.Stream,
            };
        }

        public void RemoveProgress(string contentId)
        {
            EnsureLoaded();
            var removed = new List<ProgressEntry>();
            var doomed = _progress.Keys.Where(k => _progress[k].ContentId == contentId).ToList();
            foreach (var key in doomed)
            {
                removed.Add(_progress[key]);
                _progress.Remove(key);
            }
            SaveProgressFileAsync();
            RaiseProgressChanged(new ProgressChangedEventArgs { Removed = removed });
        }

        public IReadOnlyList<ProgressEntry> AllProgress()
        {
            EnsureLoaded();
            return _progress.Values.ToList();
        }

        public void ReplaceProgress(IEnumerable<ProgressEntry> entries)
        {
            EnsureLoaded();
            _progress = new Dictionary<string, ProgressEntry>();
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Key)) continue;
                _progress[entry.Key] = entry;
            }
            SaveProgressFileAsync();
            RaiseProgressChanged(new ProgressChangedEventArgs { Replaced = true });
        }

        /// <summary>
        /// Continue Watching: the latest entry per title. Unfinished ones resume; finished
        /// episodes of a series are returned with nextUp set so the caller can look up the
        /// following episode.
        /// </summary>
        public IReadOnlyList<ProgressEntry> ContinueWatching()
        {
            EnsureLoaded();
            var latest = new Dictionary<string, ProgressEntry>();
            foreach (var entry in _progress.Values)
            {
                ProgressEntry current;
                if (!latest.TryGetValue(entry.ContentId, out current) || entry.LastWatched > current.LastWatched)
                {
                    latest[entry.ContentId] = entry;
                }
            }
            var result = new List<ProgressEntry>();
            foreach (var entry in latest.Values)
            {
                long dismissedAt;
                if (_dismissed.TryGetValue(entry.ContentId, out dismissedAt) && dismissedAt >= entry.LastWatched) continue;
                var copy = CloneEntry(entry);
                copy.NextUp = IsCompleted(entry);
                if (copy.NextUp ? entry.ContentType == "series" : entry.Position >= MinResumeMs) result.Add(copy);
            }
            result.Sort((a, b) => b.LastWatched.CompareTo(a.LastWatched));
            return result.Take(30).ToList();
        }

        public void DismissContinueWatching(string contentId)
        {
            EnsureLoaded();
            _dismissed[contentId] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            SaveDismissedFileAsync();
            RaiseProgressChanged(new ProgressChangedEventArgs());
        }

        // ---------- Watched ----------

        public bool IsWatched(string contentId, int? season, int? episode)
        {
            EnsureLoaded();
            if (_watched.ContainsKey(WatchedKey(contentId, season, episode))) return true;
            return !season.HasValue && _watched.ContainsKey(contentId);
        }

        public void MarkWatched(string contentId, int? season, int? episode, bool value = true, string title = "", string type = null)
        {
            EnsureLoaded();
            var key = WatchedKey(contentId, season, episode);
            if (value)
            {
                _watched[key] = new WatchedMark
                {
                    ContentId = contentId,
                    Type = type ?? (season.HasValue ? "series" : null),
                    Season = season,
                    Episode = episode,
                    WatchedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Title = title,
                };
            }
            else
            {
                _watched.Remove(key);
            }
            SaveWatchedFileAsync();
            if (!value)
            {
                // Unwatching also clears the finished progress so it can be resumed or started again.
                var entry = GetProgress(contentId, season, episode);
                if (entry != null && IsCompleted(entry))
                {
                    _progress.Remove(entry.Key);
                    SaveProgressFileAsync();
                    RaiseProgressChanged(new ProgressChangedEventArgs());
                }
            }
            RaiseWatchedChanged(new WatchedChangedEventArgs
            {
                ContentId = contentId,
                Season = season,
                Episode = episode,
                Value = value,
                Title = title,
                Type = type ?? (season.HasValue ? "series" : null),
            });
        }

        public IReadOnlyList<WatchedMark> AllWatched()
        {
            EnsureLoaded();
            return _watched.Values.ToList();
        }

        /// <summary>
        /// Adds watched marks from another service (Trakt history, a Jellyfin server) in one go.
        /// Marks that already exist are kept. Returns how many were new.
        /// </summary>
        public int ImportWatched(IEnumerable<WatchedMark> items)
        {
            EnsureLoaded();
            var added = 0;
            foreach (var item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.ContentId)) continue;
                var key = WatchedKey(item.ContentId, item.Season, item.Episode);
                if (_watched.ContainsKey(key)) continue;
                _watched[key] = new WatchedMark
                {
                    ContentId = item.ContentId,
                    Type = item.Type ?? (item.Season.HasValue ? "series" : "movie"),
                    Season = item.Season,
                    Episode = item.Episode,
                    WatchedAt = item.WatchedAt > 0 ? item.WatchedAt : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Title = item.Title ?? string.Empty,
                };
                added++;
            }
            if (added > 0)
            {
                SaveWatchedFileAsync();
                RaiseWatchedChanged(new WatchedChangedEventArgs { Imported = true });
            }
            return added;
        }

        public void ReplaceWatched(IEnumerable<WatchedMark> list)
        {
            EnsureLoaded();
            _watched = new Dictionary<string, WatchedMark>();
            foreach (var item in list)
            {
                if (item == null || string.IsNullOrEmpty(item.ContentId)) continue;
                _watched[WatchedKey(item.ContentId, item.Season, item.Episode)] = item;
            }
            SaveWatchedFileAsync();
            RaiseWatchedChanged(new WatchedChangedEventArgs { Replaced = true });
        }
    }
}
