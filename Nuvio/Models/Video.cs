using Nuvio.Core;
using Windows.Data.Json;

namespace Nuvio.Models
{
    /// <summary>An episode (a Stremio video). Season/episode numbers are normalised at parse time, as in meta.js sortedVideos.</summary>
    internal sealed class Video
    {
        /// <summary>e.g. 'tt123:1:2'.</summary>
        public string Id { get; set; }
        /// <summary>0 = Specials.</summary>
        public int Season { get; set; }
        public int Episode { get; set; }
        /// <summary>Alias for episode, kept for round-tripping.</summary>
        public int? Number { get; set; }
        public string Name { get; set; }
        public string Title { get; set; }
        public string Overview { get; set; }
        public string Description { get; set; }
        public string Released { get; set; }
        public string Thumbnail { get; set; }

        public static Video FromJson(IJsonValue value)
        {
            var obj = JsonHelpers.GetObject(value);
            if (obj == null) return null;
            var number = JsonHelpers.GetInt(obj, "number");
            return new Video
            {
                Id = JsonHelpers.GetString(obj, "id"),
                Season = JsonHelpers.GetInt(obj, "season") ?? 0,
                Episode = JsonHelpers.GetInt(obj, "episode") ?? number ?? 0,
                Number = number,
                Name = JsonHelpers.GetString(obj, "name"),
                Title = JsonHelpers.GetString(obj, "title"),
                Overview = JsonHelpers.GetString(obj, "overview"),
                Description = JsonHelpers.GetString(obj, "description"),
                Released = JsonHelpers.GetString(obj, "released"),
                Thumbnail = JsonHelpers.GetString(obj, "thumbnail"),
            };
        }

        public JsonObject ToJson()
        {
            var obj = new JsonObject();
            if (Id != null) obj.SetNamedValue("id", JsonHelpers.CreateString(Id));
            obj.SetNamedValue("season", JsonHelpers.CreateNumber(Season));
            obj.SetNamedValue("episode", JsonHelpers.CreateNumber(Episode));
            if (Name != null) obj.SetNamedValue("name", JsonHelpers.CreateString(Name));
            if (Title != null) obj.SetNamedValue("title", JsonHelpers.CreateString(Title));
            if (Overview != null) obj.SetNamedValue("overview", JsonHelpers.CreateString(Overview));
            if (Description != null) obj.SetNamedValue("description", JsonHelpers.CreateString(Description));
            if (Released != null) obj.SetNamedValue("released", JsonHelpers.CreateString(Released));
            if (Thumbnail != null) obj.SetNamedValue("thumbnail", JsonHelpers.CreateString(Thumbnail));
            return obj;
        }
    }
}
