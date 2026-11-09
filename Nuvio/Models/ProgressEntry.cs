using Nuvio.Core;
using Windows.Data.Json;

namespace Nuvio.Models
{
    /// <summary>
    /// A watch-progress entry. Keys follow Nuvio: "tt123" for a movie, "tt123_s1e2" for an episode.
    /// </summary>
    internal sealed class ProgressEntry
    {
        public string Key { get; set; }
        public string ContentId { get; set; }
        public string ContentType { get; set; }
        public string VideoId { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        /// <summary>ms.</summary>
        public long Position { get; set; }
        /// <summary>ms.</summary>
        public long Duration { get; set; }
        /// <summary>ms epoch.</summary>
        public long LastWatched { get; set; }
        public string Title { get; set; }
        public string Poster { get; set; }
        public string Background { get; set; }
        public string Logo { get; set; }
        public string EpisodeTitle { get; set; }
        public string Thumbnail { get; set; }
        public StreamItem Stream { get; set; }

        /// <summary>Continue Watching: this entry is finished, so the next episode is what's next.</summary>
        public bool NextUp { get; set; }
        /// <summary>Continue Watching: the following episode, filled in by Home enrichment.</summary>
        public Video Next { get; set; }
        /// <summary>Whether Next was released less than 7 days ago.</summary>
        public bool NextIsNew { get; set; }

        // Display-only, computed by HomeViewModel enrichment — not serialised.
        /// <summary>"Next Up · S1 E2" or "S1 E2 · 12m left".</summary>
        public string DisplaySubLine { get; set; }
        /// <summary>"New Episode" or "Next Up" tag, only when NextUp.</summary>
        public string TagText { get; set; }
        /// <summary>Artwork for the card: next thumbnail, episode thumbnail, background, poster.</summary>
        public string CardImage { get; set; }
        /// <summary>Position / Duration, 0–1.</summary>
        public double ProgressFraction { get; set; }

        public static ProgressEntry FromJson(IJsonValue value)
        {
            var obj = JsonHelpers.GetObject(value);
            if (obj == null) return null;
            return new ProgressEntry
            {
                Key = JsonHelpers.GetString(obj, "key"),
                ContentId = JsonHelpers.GetString(obj, "contentId"),
                ContentType = JsonHelpers.GetString(obj, "contentType"),
                VideoId = JsonHelpers.GetString(obj, "videoId"),
                Season = JsonHelpers.GetInt(obj, "season"),
                Episode = JsonHelpers.GetInt(obj, "episode"),
                Position = JsonHelpers.GetLong(obj, "position") ?? 0,
                Duration = JsonHelpers.GetLong(obj, "duration") ?? 0,
                LastWatched = JsonHelpers.GetLong(obj, "lastWatched") ?? 0,
                Title = JsonHelpers.GetString(obj, "title"),
                Poster = JsonHelpers.GetString(obj, "poster"),
                Background = JsonHelpers.GetString(obj, "background"),
                Logo = JsonHelpers.GetString(obj, "logo"),
                EpisodeTitle = JsonHelpers.GetString(obj, "episodeTitle"),
                Thumbnail = JsonHelpers.GetString(obj, "thumbnail"),
                Stream = StreamItem.FromJson(JsonHelpers.GetObject(obj, "stream")),
            };
        }

        public JsonObject ToJson()
        {
            var obj = new JsonObject();
            if (Key != null) obj.SetNamedValue("key", JsonHelpers.CreateString(Key));
            if (ContentId != null) obj.SetNamedValue("contentId", JsonHelpers.CreateString(ContentId));
            if (ContentType != null) obj.SetNamedValue("contentType", JsonHelpers.CreateString(ContentType));
            if (VideoId != null) obj.SetNamedValue("videoId", JsonHelpers.CreateString(VideoId));
            if (Season.HasValue) obj.SetNamedValue("season", JsonHelpers.CreateNumber(Season.Value));
            if (Episode.HasValue) obj.SetNamedValue("episode", JsonHelpers.CreateNumber(Episode.Value));
            obj.SetNamedValue("position", JsonHelpers.CreateNumber(Position));
            obj.SetNamedValue("duration", JsonHelpers.CreateNumber(Duration));
            obj.SetNamedValue("lastWatched", JsonHelpers.CreateNumber(LastWatched));
            if (Title != null) obj.SetNamedValue("title", JsonHelpers.CreateString(Title));
            if (Poster != null) obj.SetNamedValue("poster", JsonHelpers.CreateString(Poster));
            if (Background != null) obj.SetNamedValue("background", JsonHelpers.CreateString(Background));
            if (Logo != null) obj.SetNamedValue("logo", JsonHelpers.CreateString(Logo));
            if (EpisodeTitle != null) obj.SetNamedValue("episodeTitle", JsonHelpers.CreateString(EpisodeTitle));
            if (Thumbnail != null) obj.SetNamedValue("thumbnail", JsonHelpers.CreateString(Thumbnail));
            if (Stream != null) obj.SetNamedValue("stream", Stream.ToJson());
            return obj;
        }
    }
}
