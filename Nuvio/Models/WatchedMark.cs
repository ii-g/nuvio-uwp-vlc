using Nuvio.Core;
using Windows.Data.Json;

namespace Nuvio.Models
{
    /// <summary>A watched mark, keyed by watchedKey ("contentId:season:episode" or bare "contentId").</summary>
    internal sealed class WatchedMark
    {
        public string ContentId { get; set; }
        public string Type { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        /// <summary>ms epoch.</summary>
        public long WatchedAt { get; set; }
        public string Title { get; set; }

        public static WatchedMark FromJson(IJsonValue value)
        {
            var obj = JsonHelpers.GetObject(value);
            if (obj == null) return null;
            return new WatchedMark
            {
                ContentId = JsonHelpers.GetString(obj, "contentId"),
                Type = JsonHelpers.GetString(obj, "type"),
                Season = JsonHelpers.GetInt(obj, "season"),
                Episode = JsonHelpers.GetInt(obj, "episode"),
                WatchedAt = JsonHelpers.GetLong(obj, "watchedAt") ?? 0,
                Title = JsonHelpers.GetString(obj, "title"),
            };
        }

        public JsonObject ToJson()
        {
            var obj = new JsonObject();
            if (ContentId != null) obj.SetNamedValue("contentId", JsonHelpers.CreateString(ContentId));
            if (Type != null) obj.SetNamedValue("type", JsonHelpers.CreateString(Type));
            if (Season.HasValue) obj.SetNamedValue("season", JsonHelpers.CreateNumber(Season.Value));
            if (Episode.HasValue) obj.SetNamedValue("episode", JsonHelpers.CreateNumber(Episode.Value));
            obj.SetNamedValue("watchedAt", JsonHelpers.CreateNumber(WatchedAt));
            if (Title != null) obj.SetNamedValue("title", JsonHelpers.CreateString(Title));
            return obj;
        }
    }
}
