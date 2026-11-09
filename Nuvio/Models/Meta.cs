using System.Collections.Generic;
using Nuvio.Core;
using Windows.Data.Json;

namespace Nuvio.Models
{
    internal sealed class TrailerRef
    {
        public string Source { get; set; }
    }

    internal sealed class TrailerStreamRef
    {
        public string YtId { get; set; }
    }

    /// <summary>
    /// A title's metadata, mirroring the Stremio meta object. Catalog metas are partial
    /// (preview) by design — most fields are optional.
    /// </summary>
    internal sealed class Meta
    {
        public string Id { get; set; }
        /// <summary>'movie' | 'series' | 'channel' | 'tv' | 'anime' …</summary>
        public string Type { get; set; }
        public string Name { get; set; }
        public string Poster { get; set; }
        public string Background { get; set; }
        public string Logo { get; set; }
        public string Description { get; set; }
        /// <summary>Runtime as text, e.g. '128 min'.</summary>
        public string Runtime { get; set; }
        /// <summary>IMDb rating, string or number on the wire; stored normalised to text.</summary>
        public string ImdbRating { get; set; }
        public string ReleaseInfo { get; set; }
        public int? Year { get; set; }
        public List<string> Genres { get; set; }
        public List<string> Cast { get; set; }
        public List<string> Director { get; set; }
        public List<string> Writer { get; set; }
        public List<Video> Videos { get; set; }
        public List<TrailerRef> Trailers { get; set; }
        public List<TrailerStreamRef> TrailerStreams { get; set; }
        /// <summary>'poster' | 'landscape' | 'square' …</summary>
        public string PosterShape { get; set; }
        public string Released { get; set; }

        /// <summary>The serving addon, attached by FetchMetaAsync — not part of the server payload.</summary>
        public Addon ServingAddon { get; set; }

        public static Meta FromJson(IJsonValue value)
        {
            var obj = JsonHelpers.GetObject(value);
            if (obj == null) return null;
            return new Meta
            {
                Id = JsonHelpers.GetString(obj, "id"),
                Type = JsonHelpers.GetString(obj, "type"),
                Name = JsonHelpers.GetString(obj, "name"),
                Poster = JsonHelpers.GetString(obj, "poster"),
                Background = JsonHelpers.GetString(obj, "background"),
                Logo = JsonHelpers.GetString(obj, "logo"),
                Description = JsonHelpers.GetString(obj, "description"),
                Runtime = JsonHelpers.GetString(obj, "runtime"),
                ImdbRating = JsonHelpers.GetString(obj, "imdbRating"),
                ReleaseInfo = JsonHelpers.GetString(obj, "releaseInfo"),
                Year = JsonHelpers.GetInt(obj, "year"),
                Genres = JsonHelpers.GetStringList(obj, "genres") ?? JsonHelpers.GetStringList(obj, "genre"),
                Cast = JsonHelpers.GetStringList(obj, "cast"),
                Director = JsonHelpers.GetStringList(obj, "director"),
                Writer = JsonHelpers.GetStringList(obj, "writer"),
                Videos = ParseVideos(JsonHelpers.GetArray(obj, "videos")),
                Trailers = ParseTrailers(JsonHelpers.GetArray(obj, "trailers")),
                TrailerStreams = ParseTrailerStreams(JsonHelpers.GetArray(obj, "trailerStreams")),
                PosterShape = JsonHelpers.GetString(obj, "posterShape"),
                Released = JsonHelpers.GetString(obj, "released"),
            };
        }

        private static List<Video> ParseVideos(JsonArray array)
        {
            if (array == null) return null;
            var result = new List<Video>();
            foreach (var item in array)
            {
                var video = Video.FromJson(item);
                if (video != null) result.Add(video);
            }
            return result;
        }

        private static List<TrailerRef> ParseTrailers(JsonArray array)
        {
            if (array == null) return null;
            var result = new List<TrailerRef>();
            foreach (var item in array)
            {
                var obj = JsonHelpers.GetObject(item);
                if (obj == null) continue;
                result.Add(new TrailerRef { Source = JsonHelpers.GetString(obj, "source") });
            }
            return result;
        }

        private static List<TrailerStreamRef> ParseTrailerStreams(JsonArray array)
        {
            if (array == null) return null;
            var result = new List<TrailerStreamRef>();
            foreach (var item in array)
            {
                var obj = JsonHelpers.GetObject(item);
                if (obj == null) continue;
                result.Add(new TrailerStreamRef { YtId = JsonHelpers.GetString(obj, "ytId") });
            }
            return result;
        }

        public JsonObject ToJson()
        {
            var obj = new JsonObject();
            if (Id != null) obj.SetNamedValue("id", JsonHelpers.CreateString(Id));
            if (Type != null) obj.SetNamedValue("type", JsonHelpers.CreateString(Type));
            if (Name != null) obj.SetNamedValue("name", JsonHelpers.CreateString(Name));
            if (Poster != null) obj.SetNamedValue("poster", JsonHelpers.CreateString(Poster));
            if (Background != null) obj.SetNamedValue("background", JsonHelpers.CreateString(Background));
            if (Logo != null) obj.SetNamedValue("logo", JsonHelpers.CreateString(Logo));
            if (Description != null) obj.SetNamedValue("description", JsonHelpers.CreateString(Description));
            if (Runtime != null) obj.SetNamedValue("runtime", JsonHelpers.CreateString(Runtime));
            if (ImdbRating != null) obj.SetNamedValue("imdbRating", JsonHelpers.CreateString(ImdbRating));
            if (ReleaseInfo != null) obj.SetNamedValue("releaseInfo", JsonHelpers.CreateString(ReleaseInfo));
            if (Year.HasValue) obj.SetNamedValue("year", JsonHelpers.CreateNumber(Year.Value));
            if (Genres != null) obj.SetNamedValue("genres", StringArray(Genres));
            if (PosterShape != null) obj.SetNamedValue("posterShape", JsonHelpers.CreateString(PosterShape));
            if (Released != null) obj.SetNamedValue("released", JsonHelpers.CreateString(Released));
            return obj;
        }

        private static JsonArray StringArray(IEnumerable<string> values)
        {
            var array = new JsonArray();
            foreach (var value in values) array.Add(JsonHelpers.CreateString(value));
            return array;
        }
    }
}
