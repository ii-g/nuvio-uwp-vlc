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
    /// <summary>One cast member from TMDB (photo, name, character).</summary>
    internal sealed class TmdbCastMember
    {
        public string Name { get; set; }
        public string Character { get; set; }
        public string Photo { get; set; }

        /// <summary>Initials for the photo placeholder (web details.js initials()).</summary>
        public string Initials
        {
            get
            {
                var words = (Name ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var result = string.Empty;
                foreach (var word in words.Take(2)) result += char.ToUpperInvariant(word[0]);
                return result;
            }
        }
    }

    /// <summary>TMDB enrichment for a title: artwork, cast, similar titles.</summary>
    internal sealed class TmdbExtras
    {
        public string Logo { get; set; }
        public string Background { get; set; }
        public List<TmdbCastMember> Cast { get; set; } = new List<TmdbCastMember>();
        public List<TmdbSimilarItem> Similar { get; set; } = new List<TmdbSimilarItem>();
    }

    /// <summary>A "More Like This" pick: the display meta plus its TMDB id/kind for opening it.</summary>
    internal sealed class TmdbSimilarItem
    {
        public Meta Meta { get; set; }
        public int TmdbId { get; set; }
        public string Kind { get; set; }
    }

    /// <summary>
    /// Optional TMDB enrichment for the details screen: cast with photos, "More like this",
    /// and a logo or backdrop when the addon has none. Needs a TMDB key (Settings &gt;
    /// Integrations in M7, or the built-in key from config.js); either a v3 API key or a
    /// v4 read access token works. Ported from services/tmdb.js.
    /// </summary>
    internal sealed class TmdbService
    {
        private const string Api = "https://api.themoviedb.org/3";
        private const string Images = "https://image.tmdb.org/t/p/";
        private const int CacheMs = 6 * 60 * 60 * 1000;

        private readonly AppSettings _settings;

        public TmdbService(AppSettings settings)
        {
            _settings = settings;
        }

        /// <summary>The key from settings (the built-in config.js key is empty, so without a
        /// user key this returns empty and the extras section is simply absent).</summary>
        public string TmdbKey()
        {
            return (_settings.GetString("tmdbApiKey", "") ?? string.Empty).Trim();
        }

        private async Task<JsonObject> RequestAsync(string path, IDictionary<string, string> parameters)
        {
            var key = TmdbKey();
            var query = new List<string>();
            if (parameters != null)
            {
                foreach (var pair in parameters)
                {
                    query.Add(Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value ?? string.Empty));
                }
            }
            Dictionary<string, string> headers = null;
            if (key.StartsWith("eyJ", StringComparison.Ordinal))
            {
                headers = new Dictionary<string, string> { { "Authorization", "Bearer " + key } };
            }
            else
            {
                query.Add("api_key=" + Uri.EscapeDataString(key));
            }
            var url = Api + path + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
            var data = await HttpService.GetJsonAsync(url, CacheMs, 15000, headers).ConfigureAwait(false);
            return JsonHelpers.GetObject(data);
        }

        private static string Image(string size, string path)
        {
            return string.IsNullOrEmpty(path) ? null : Images + size + path;
        }

        /// <summary>TMDB extras for an addon meta with an IMDb id, or null if there's no key or no match.</summary>
        public async Task<TmdbExtras> GetExtrasAsync(Meta meta)
        {
            if (meta == null) return null;
            var imdbId = (meta.Id ?? string.Empty).Split(':')[0];
            if (string.IsNullOrEmpty(TmdbKey()) || !IsImdbId(imdbId)) return null;
            JsonObject found;
            try
            {
                found = await RequestAsync("/find/" + Uri.EscapeDataString(imdbId),
                    new Dictionary<string, string> { { "external_source", "imdb_id" } }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("TMDB find failed", ex);
                return null;
            }
            if (found == null) return null;
            var preferTv = meta.Type != "movie";
            var movie = FirstResult(JsonHelpers.GetArray(found, "movie_results"));
            var tv = FirstResult(JsonHelpers.GetArray(found, "tv_results"));
            var hit = preferTv ? (tv ?? movie) : (movie ?? tv);
            if (hit == null) return null;
            var kind = hit == movie ? "movie" : "tv";
            var hitId = JsonHelpers.GetInt(hit, "id");
            if (!hitId.HasValue) return null;
            JsonObject details;
            try
            {
                details = await RequestAsync("/" + kind + "/" + hitId.Value,
                    new Dictionary<string, string>
                    {
                        { "append_to_response", "credits,recommendations,images" },
                        { "include_image_language", "en,null" },
                    }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("TMDB details failed", ex);
                return null;
            }
            if (details == null) return null;
            var extras = new TmdbExtras();
            var logos = JsonHelpers.GetArray(JsonHelpers.GetObject(details, "images"), "logos");
            JsonObject logo = null;
            if (logos != null)
            {
                foreach (var item in logos)
                {
                    var logoObj = JsonHelpers.GetObject(item);
                    if (logoObj == null) continue;
                    if (logo == null) logo = logoObj;
                    if (JsonHelpers.GetString(logoObj, "iso_639_1") == "en")
                    {
                        logo = logoObj;
                        break;
                    }
                }
            }
            if (logo != null) extras.Logo = Image("w500", JsonHelpers.GetString(logo, "file_path"));
            extras.Background = Image("w1280", JsonHelpers.GetString(details, "backdrop_path"));
            var cast = JsonHelpers.GetArray(JsonHelpers.GetObject(details, "credits"), "cast");
            if (cast != null)
            {
                foreach (var item in cast.Take(16))
                {
                    var member = JsonHelpers.GetObject(item);
                    if (member == null) continue;
                    extras.Cast.Add(new TmdbCastMember
                    {
                        Name = JsonHelpers.GetString(member, "name"),
                        Character = JsonHelpers.GetString(member, "character") ?? string.Empty,
                        Photo = Image("w185", JsonHelpers.GetString(member, "profile_path")),
                    });
                }
            }
            var recommendations = JsonHelpers.GetArray(JsonHelpers.GetObject(details, "recommendations"), "results");
            if (recommendations != null)
            {
                foreach (var item in recommendations.Take(20))
                {
                    var result = JsonHelpers.GetObject(item);
                    if (result == null) continue;
                    var resultId = JsonHelpers.GetInt(result, "id");
                    if (!resultId.HasValue) continue;
                    var release = JsonHelpers.GetString(result, "release_date") ?? JsonHelpers.GetString(result, "first_air_date") ?? string.Empty;
                    extras.Similar.Add(new TmdbSimilarItem
                    {
                        TmdbId = resultId.Value,
                        Kind = kind,
                        Meta = new Meta
                        {
                            Id = "tmdb:" + kind + ":" + resultId.Value,
                            Type = kind == "movie" ? "movie" : "series",
                            Name = JsonHelpers.GetString(result, "title") ?? JsonHelpers.GetString(result, "name") ?? string.Empty,
                            Poster = Image("w342", JsonHelpers.GetString(result, "poster_path")),
                            Background = Image("w1280", JsonHelpers.GetString(result, "backdrop_path")),
                            ReleaseInfo = release.Length > 4 ? release.Substring(0, 4) : release,
                        },
                    });
                }
            }
            return extras;
        }

        /// <summary>The IMDb id addons use for a TMDB title (for opening a "More like this" pick).</summary>
        public async Task<string> GetImdbIdAsync(string kind, int tmdbId)
        {
            var ids = await RequestAsync("/" + kind + "/" + tmdbId + "/external_ids", null).ConfigureAwait(false);
            return ids != null ? JsonHelpers.GetString(ids, "imdb_id") : null;
        }

        private static bool IsImdbId(string value)
        {
            if (string.IsNullOrEmpty(value) || !value.StartsWith("tt", StringComparison.Ordinal)) return false;
            for (var i = 2; i < value.Length; i++)
            {
                if (!char.IsDigit(value[i])) return false;
            }
            return value.Length > 2;
        }

        private static JsonObject FirstResult(JsonArray array)
        {
            if (array == null || array.Count == 0) return null;
            return JsonHelpers.GetObject(array[0]);
        }
    }
}
