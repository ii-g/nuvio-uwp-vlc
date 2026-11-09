using System.Collections.Generic;
using Nuvio.Core;
using Windows.Data.Json;

namespace Nuvio.Models
{
    /// <summary>A saved title ("Nuvio Library"). Uses the same fields as the library sync shape.</summary>
    internal sealed class LibraryItem
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public string Poster { get; set; }
        /// <summary>Uppercased: 'POSTER' … (preserved quirk from library.js).</summary>
        public string PosterShape { get; set; }
        public string Background { get; set; }
        public string Logo { get; set; }
        public string Description { get; set; }
        public string ReleaseInfo { get; set; }
        public string ImdbRating { get; set; }
        public List<string> Genres { get; set; }
        /// <summary>Sync only.</summary>
        public string AddonBaseUrl { get; set; }
        /// <summary>ms epoch.</summary>
        public long AddedAt { get; set; }

        public static LibraryItem FromJson(IJsonValue value)
        {
            var obj = JsonHelpers.GetObject(value);
            if (obj == null) return null;
            return new LibraryItem
            {
                Id = JsonHelpers.GetString(obj, "id"),
                Type = JsonHelpers.GetString(obj, "type"),
                Name = JsonHelpers.GetString(obj, "name"),
                Poster = JsonHelpers.GetString(obj, "poster"),
                PosterShape = JsonHelpers.GetString(obj, "posterShape"),
                Background = JsonHelpers.GetString(obj, "background"),
                Logo = JsonHelpers.GetString(obj, "logo"),
                Description = JsonHelpers.GetString(obj, "description"),
                ReleaseInfo = JsonHelpers.GetString(obj, "releaseInfo"),
                ImdbRating = JsonHelpers.GetString(obj, "imdbRating"),
                Genres = JsonHelpers.GetStringList(obj, "genres") ?? new List<string>(),
                AddonBaseUrl = JsonHelpers.GetString(obj, "addonBaseUrl"),
                AddedAt = JsonHelpers.GetLong(obj, "addedAt") ?? 0,
            };
        }

        public JsonObject ToJson()
        {
            var obj = new JsonObject();
            if (Id != null) obj.SetNamedValue("id", JsonHelpers.CreateString(Id));
            if (Type != null) obj.SetNamedValue("type", JsonHelpers.CreateString(Type));
            if (Name != null) obj.SetNamedValue("name", JsonHelpers.CreateString(Name));
            if (Poster != null) obj.SetNamedValue("poster", JsonHelpers.CreateString(Poster));
            if (PosterShape != null) obj.SetNamedValue("posterShape", JsonHelpers.CreateString(PosterShape));
            if (Background != null) obj.SetNamedValue("background", JsonHelpers.CreateString(Background));
            if (Logo != null) obj.SetNamedValue("logo", JsonHelpers.CreateString(Logo));
            if (Description != null) obj.SetNamedValue("description", JsonHelpers.CreateString(Description));
            if (ReleaseInfo != null) obj.SetNamedValue("releaseInfo", JsonHelpers.CreateString(ReleaseInfo));
            if (ImdbRating != null) obj.SetNamedValue("imdbRating", JsonHelpers.CreateString(ImdbRating));
            var genres = new JsonArray();
            if (Genres != null)
            {
                foreach (var genre in Genres) genres.Add(JsonHelpers.CreateString(genre));
            }
            obj.SetNamedValue("genres", genres);
            if (AddonBaseUrl != null) obj.SetNamedValue("addonBaseUrl", JsonHelpers.CreateString(AddonBaseUrl));
            obj.SetNamedValue("addedAt", JsonHelpers.CreateNumber(AddedAt));
            return obj;
        }
    }
}
