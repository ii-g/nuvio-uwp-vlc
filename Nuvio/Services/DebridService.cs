using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Nuvio.Core;
using Nuvio.Models;
using Windows.Data.Json;

namespace Nuvio.Services
{
    /// <summary>A debrid failure. Codes: missing | not_cached | invalid_key | error.</summary>
    internal sealed class DebridError : Exception
    {
        public DebridError(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    /// <summary>The user's configured debrid account: provider id + API key.</summary>
    internal sealed class DebridAccount
    {
        public string Provider { get; set; }
        public string ApiKey { get; set; }
    }

    /// <summary>A torrent resolved to a direct playable link.</summary>
    internal sealed class DebridResolved
    {
        public string Url { get; set; }
        public string Filename { get; set; }
        public long? Size { get; set; }
    }

    /// <summary>Static info about one supported debrid provider.</summary>
    internal sealed class DebridProviderInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Short { get; set; }
        public string KeyHelp { get; set; }
    }

    /// <summary>
    /// Turns torrent results into direct links through the user's debrid service.
    /// Ported from web/js/services/debrid.js. Nothing is downloaded on this device.
    /// No stream URL, API key, auth header or token is ever logged.
    /// </summary>
    internal sealed class DebridService
    {
        public static readonly IReadOnlyList<DebridProviderInfo> Providers = new DebridProviderInfo[]
        {
            new DebridProviderInfo { Id = "torbox", Name = "TorBox", Short = "TB", KeyHelp = "torbox.app/settings → API Key" },
            new DebridProviderInfo { Id = "realdebrid", Name = "Real-Debrid", Short = "RD", KeyHelp = "real-debrid.com/apitoken" },
            new DebridProviderInfo { Id = "premiumize", Name = "Premiumize", Short = "PM", KeyHelp = "premiumize.me/account → API Key" },
        };

        private static readonly Regex VideoExtensions = new Regex(
            @"\.(mkv|mp4|m4v|avi|mov|wmv|webm|ts|m2ts|mpg|mpeg|flv|ogv|3gp)$",
            RegexOptions.IgnoreCase);
        private const int CacheTtlMs = 15 * 60 * 1000;
        private const int TimeoutMs = 30000;
        private const string FormContentType = "application/x-www-form-urlencoded";

        private readonly AppSettings _settings;
        private readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>();
        private readonly object _cacheSync = new object();

        public event EventHandler DebridChanged;

        public DebridService(AppSettings settings)
        {
            _settings = settings;
        }

        private sealed class CacheEntry
        {
            public DateTimeOffset At;
            public DebridResolved Result;
        }

        /// <summary>What the resolver wants from a torrent's file list.</summary>
        private sealed class FileWant
        {
            public string Filename;
            public int? FileIdx;
            public int? Season;
            public int? Episode;
        }

        private sealed class MultipartBody
        {
            public string Body;
            public string ContentType;
        }

        private static DebridProviderInfo FindProvider(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var provider in Providers)
            {
                if (string.Equals(provider.Id, id, StringComparison.Ordinal)) return provider;
            }
            return null;
        }

        /// <summary>The stored account, only when the provider is known and a key is set.</summary>
        public DebridAccount DebridAccount()
        {
            var stored = JsonHelpers.GetObject(_settings.GetValue("debrid"));
            if (stored == null) return null;
            var provider = JsonHelpers.GetString(stored, "provider");
            var apiKey = JsonHelpers.GetString(stored, "apiKey");
            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(apiKey)) return null;
            if (FindProvider(provider) == null) return null;
            return new DebridAccount { Provider = provider, ApiKey = apiKey };
        }

        public string DebridName()
        {
            var account = DebridAccount();
            if (account == null) return null;
            var info = FindProvider(account.Provider);
            return info != null ? info.Name : null;
        }

        public string DebridShort()
        {
            var account = DebridAccount();
            if (account == null) return null;
            var info = FindProvider(account.Provider);
            return info != null ? info.Short : null;
        }

        /// <summary>Stores the account (null clears it), drops the resolve cache, emits DebridChanged.</summary>
        public void SetDebridAccount(DebridAccount account)
        {
            if (account == null)
            {
                // AppSettings.Set dereferences the value, so clear with a JSON null, not a C# null.
                _settings.Set("debrid", JsonValue.CreateNullValue());
            }
            else
            {
                var obj = new JsonObject();
                obj.SetNamedValue("provider", JsonHelpers.CreateString(account.Provider));
                obj.SetNamedValue("apiKey", JsonHelpers.CreateString(account.ApiKey));
                _settings.Set("debrid", obj);
            }
            lock (_cacheSync)
            {
                _cache.Clear();
            }
            var handler = DebridChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        // ---------- Key validation ----------

        /// <summary>True when the provider accepts the key. False on unknown provider,
        /// blank key or non-2xx; HttpError (timeout/network) propagates.</summary>
        public async Task<bool> ValidateDebridKeyAsync(string provider, string apiKey)
        {
            var key = (apiKey ?? string.Empty).Trim();
            if (key.Length == 0) return false;
            var headers = AuthHeaders(key);
            if (provider == "torbox")
            {
                var torbox = await HttpService.RequestRawAsync("https://api.torbox.app/v1/api/user/me", "GET", null, null, TimeoutMs, headers).ConfigureAwait(false);
                return IsOk(torbox);
            }
            if (provider == "realdebrid")
            {
                var realDebrid = await HttpService.RequestRawAsync("https://api.real-debrid.com/rest/1.0/user", "GET", null, null, TimeoutMs, headers).ConfigureAwait(false);
                return IsOk(realDebrid);
            }
            if (provider == "premiumize")
            {
                var premiumize = await HttpService.RequestRawAsync("https://www.premiumize.me/api/account/info", "GET", null, null, TimeoutMs, headers).ConfigureAwait(false);
                if (!IsOk(premiumize)) return false;
                var status = JsonHelpers.GetString(JsonHelpers.GetObject(premiumize.Data), "status");
                return string.Equals(status, "success", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        // ---------- Resolving ----------

        /// <summary>Resolves a torrent result to a playable link, cached 15 minutes.
        /// Throws DebridError (missing | not_cached | invalid_key | error).</summary>
        public async Task<DebridResolved> ResolveTorrentAsync(StreamItem stream, int? season, int? episode)
        {
            var account = DebridAccount();
            if (account == null) throw new DebridError("missing", "Connect a debrid service in Settings to play torrent results.");
            var fileIdx = stream != null ? stream.FileIdx : null;
            var key = account.Provider + "|"
                + (stream != null && stream.InfoHash != null ? stream.InfoHash : string.Empty) + "|"
                + (fileIdx.HasValue ? fileIdx.Value.ToString(CultureInfo.InvariantCulture) : string.Empty) + "|"
                + (season.HasValue ? season.Value.ToString(CultureInfo.InvariantCulture) : string.Empty) + "|"
                + (episode.HasValue ? episode.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
            lock (_cacheSync)
            {
                CacheEntry hit;
                if (_cache.TryGetValue(key, out hit) && (DateTimeOffset.UtcNow - hit.At).TotalMilliseconds < CacheTtlMs)
                {
                    return hit.Result;
                }
            }
            var magnet = MagnetFor(stream);
            if (magnet == null) throw new DebridError("error", "This result has no torrent to resolve.");
            var want = new FileWant
            {
                Filename = stream != null && stream.BehaviorHints != null ? stream.BehaviorHints.Filename : null,
                FileIdx = fileIdx,
                Season = season,
                Episode = episode,
            };
            var apiKey = account.ApiKey.Trim();
            DebridResolved result;
            if (account.Provider == "torbox") result = await ResolveTorboxAsync(apiKey, magnet, want).ConfigureAwait(false);
            else if (account.Provider == "realdebrid") result = await ResolveRealDebridAsync(apiKey, magnet, want).ConfigureAwait(false);
            else result = await ResolvePremiumizeAsync(apiKey, magnet, want).ConfigureAwait(false);
            lock (_cacheSync)
            {
                _cache[key] = new CacheEntry { At = DateTimeOffset.UtcNow, Result = result };
            }
            return result;
        }

        /// <summary>magnet:?xt=urn:btih:&lt;infoHash&gt; + dn + one tr per tracker source.</summary>
        public static string MagnetFor(StreamItem stream)
        {
            if (stream == null || string.IsNullOrEmpty(stream.InfoHash)) return null;
            var magnet = "magnet:?xt=urn:btih:" + stream.InfoHash;
            var filename = stream.BehaviorHints != null ? stream.BehaviorHints.Filename : null;
            if (!string.IsNullOrEmpty(filename)) magnet += "&dn=" + Uri.EscapeDataString(filename);
            if (stream.Sources != null)
            {
                foreach (var source in stream.Sources)
                {
                    if (source == null) continue;
                    var tracker = source.Trim();
                    if (tracker.Length == 0) continue;
                    if (tracker.StartsWith("dht:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (tracker.StartsWith("tracker:", StringComparison.OrdinalIgnoreCase)) tracker = tracker.Substring("tracker:".Length);
                    magnet += "&tr=" + Uri.EscapeDataString(tracker);
                }
            }
            return magnet;
        }

        private async Task<DebridResolved> ResolveTorboxAsync(string apiKey, string magnet, FileWant want)
        {
            var headers = AuthHeaders(apiKey);
            var multipart = Multipart(new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("magnet", magnet),
                new KeyValuePair<string, string>("add_only_if_cached", "true"),
                new KeyValuePair<string, string>("allow_zip", "false"),
            });
            var create = await HttpService.RequestRawAsync("https://api.torbox.app/v1/api/torrents/createtorrent", "POST", multipart.Body, multipart.ContentType, TimeoutMs, headers).ConfigureAwait(false);
            var createTop = JsonHelpers.GetObject(create.Data);
            var createData = createTop != null ? JsonHelpers.GetObject(createTop, "data") : null;
            string torrentId = null;
            if (createData != null) torrentId = JsonHelpers.GetString(createData, "torrent_id") ?? JsonHelpers.GetString(createData, "id");
            var success = createTop != null ? JsonHelpers.GetBool(createTop, "success") : null;
            if (!IsOk(create) || createTop == null || (success.HasValue && !success.Value) || torrentId == null)
            {
                if (create.Status == 401 || create.Status == 403) throw Failed(create, "add the torrent");
                var detail = DetailOf(createTop, false);
                if ((detail != null && detail.ToLowerInvariant().Contains("cache")) || create.Status == 404) throw NotCached();
                throw Failed(create, "add the torrent");
            }
            var info = await HttpService.RequestRawAsync("https://api.torbox.app/v1/api/torrents/mylist?id=" + Uri.EscapeDataString(torrentId) + "&bypass_cache=true", "GET", null, null, TimeoutMs, headers).ConfigureAwait(false);
            var infoTop = JsonHelpers.GetObject(info.Data);
            var infoData = infoTop != null ? JsonHelpers.GetObject(infoTop, "data") : null;
            var files = ToObjectList(infoData != null ? JsonHelpers.GetArray(infoData, "files") : null);
            var file = SelectFile(files, want,
                f => JsonHelpers.GetString(f, "name") ?? JsonHelpers.GetString(f, "short_name") ?? string.Empty,
                f => IsVideoMime(JsonHelpers.GetString(f, "mimetype")) || VideoExtensions.IsMatch(JsonHelpers.GetString(f, "short_name") ?? JsonHelpers.GetString(f, "name") ?? string.Empty),
                f => JsonHelpers.GetLong(f, "size") ?? 0);
            if (file == null) throw new DebridError("error", "No video file in this torrent matches.");
            var fileId = JsonHelpers.GetString(file, "id");
            if (fileId == null) throw new DebridError("error", "No video file in this torrent matches.");
            var link = await HttpService.RequestRawAsync("https://api.torbox.app/v1/api/torrents/requestdl?token=" + Uri.EscapeDataString(apiKey) + "&torrent_id=" + Uri.EscapeDataString(torrentId) + "&file_id=" + Uri.EscapeDataString(fileId) + "&zip_link=false&redirect=false&append_name=false", "GET", null, null, TimeoutMs, headers).ConfigureAwait(false);
            var url = JsonHelpers.GetString(JsonHelpers.GetObject(link.Data), "data");
            if (!IsOk(link) || string.IsNullOrEmpty(url)) throw Failed(link, "make a link");
            var size = JsonHelpers.GetLong(file, "size");
            return new DebridResolved
            {
                Url = url,
                Filename = FirstNonEmpty(JsonHelpers.GetString(file, "short_name"), JsonHelpers.GetString(file, "name"), want.Filename),
                Size = size.HasValue && size.Value != 0 ? size : null,
            };
        }

        private async Task<DebridResolved> ResolveRealDebridAsync(string apiKey, string magnet, FileWant want)
        {
            const string baseUrl = "https://api.real-debrid.com/rest/1.0";
            var headers = AuthHeaders(apiKey);
            var add = await HttpService.RequestRawAsync(baseUrl + "/torrents/addMagnet", "POST", FormField("magnet", magnet), FormContentType, TimeoutMs, headers).ConfigureAwait(false);
            var id = JsonHelpers.GetString(JsonHelpers.GetObject(add.Data), "id");
            if (!IsOk(add) || string.IsNullOrEmpty(id)) throw Failed(add, "add the torrent");
            var done = false;
            try
            {
                var before = await HttpService.RequestRawAsync(baseUrl + "/torrents/info/" + Uri.EscapeDataString(id), "GET", null, null, TimeoutMs, headers).ConfigureAwait(false);
                var files = ToObjectList(JsonHelpers.GetArray(JsonHelpers.GetObject(before.Data), "files"));
                if (files.Count == 0) throw NotCached();
                var file = SelectFile(files, want,
                    f => JsonHelpers.GetString(f, "path") ?? string.Empty,
                    f => VideoExtensions.IsMatch(JsonHelpers.GetString(f, "path") ?? string.Empty),
                    f => JsonHelpers.GetLong(f, "bytes") ?? 0);
                if (file == null) throw new DebridError("error", "No video file in this torrent matches.");
                var fileId = JsonHelpers.GetString(file, "id");
                var select = await HttpService.RequestRawAsync(baseUrl + "/torrents/selectFiles/" + Uri.EscapeDataString(id), "POST", FormField("files", fileId ?? string.Empty), FormContentType, TimeoutMs, headers).ConfigureAwait(false);
                if (!IsOk(select) && select.Status != 202) throw Failed(select, "select the file");
                var after = await HttpService.RequestRawAsync(baseUrl + "/torrents/info/" + Uri.EscapeDataString(id), "GET", null, null, TimeoutMs, headers).ConfigureAwait(false);
                var afterTop = JsonHelpers.GetObject(after.Data);
                var status = JsonHelpers.GetString(afterTop, "status");
                string link = null;
                var links = afterTop != null ? JsonHelpers.GetArray(afterTop, "links") : null;
                if (links != null)
                {
                    foreach (var item in links)
                    {
                        var candidate = JsonHelpers.GetString(item);
                        if (!string.IsNullOrEmpty(candidate)) { link = candidate; break; }
                    }
                }
                if (!string.Equals(status, "downloaded", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(link)) throw NotCached();
                var unrestrict = await HttpService.RequestRawAsync(baseUrl + "/unrestrict/link", "POST", FormField("link", link), FormContentType, TimeoutMs, headers).ConfigureAwait(false);
                var unrestrictTop = JsonHelpers.GetObject(unrestrict.Data);
                var url = JsonHelpers.GetString(unrestrictTop, "download");
                if (!IsOk(unrestrict) || string.IsNullOrEmpty(url)) throw Failed(unrestrict, "make a link");
                done = true;
                var fileBytes = JsonHelpers.GetLong(file, "bytes");
                var unrestrictSize = JsonHelpers.GetLong(unrestrictTop, "filesize");
                long? size = null;
                if (unrestrictSize.HasValue && unrestrictSize.Value != 0) size = unrestrictSize.Value;
                else if (fileBytes.HasValue && fileBytes.Value != 0) size = fileBytes.Value;
                return new DebridResolved
                {
                    Url = url,
                    Filename = FirstNonEmpty(JsonHelpers.GetString(unrestrictTop, "filename"), BaseName(JsonHelpers.GetString(file, "path") ?? string.Empty)),
                    Size = size,
                };
            }
            finally
            {
                // Don't leave half-added torrents in the user's account.
                if (!done)
                {
                    try
                    {
                        await HttpService.RequestRawAsync(baseUrl + "/torrents/delete/" + Uri.EscapeDataString(id), "DELETE", null, null, TimeoutMs, headers).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Best-effort cleanup.
                    }
                }
            }
        }

        private async Task<DebridResolved> ResolvePremiumizeAsync(string apiKey, string magnet, FileWant want)
        {
            var headers = AuthHeaders(apiKey);
            var response = await HttpService.RequestRawAsync("https://www.premiumize.me/api/transfer/directdl", "POST", FormField("src", magnet), FormContentType, TimeoutMs, headers).ConfigureAwait(false);
            var top = JsonHelpers.GetObject(response.Data);
            var status = JsonHelpers.GetString(top, "status");
            if (!IsOk(response) || top == null || !string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                var message = (JsonHelpers.GetString(top, "message") ?? string.Empty).ToLowerInvariant();
                if (message.Contains("cache") || message.Contains("not found")) throw NotCached();
                throw Failed(response, "make a link");
            }
            var content = ToObjectList(JsonHelpers.GetArray(top, "content"));
            var file = SelectFile(content, want,
                f => JsonHelpers.GetString(f, "path") ?? string.Empty,
                f => !string.IsNullOrEmpty(JsonHelpers.GetString(f, "link")) && VideoExtensions.IsMatch(JsonHelpers.GetString(f, "path") ?? string.Empty),
                f => JsonHelpers.GetLong(f, "size") ?? 0);
            if (file == null) throw NotCached();
            var size = JsonHelpers.GetLong(file, "size");
            return new DebridResolved
            {
                Url = JsonHelpers.GetString(file, "link"),
                Filename = FirstNonEmpty(BaseName(JsonHelpers.GetString(file, "path") ?? string.Empty)),
                Size = size.HasValue && size.Value != 0 ? size : null,
            };
        }

        // ---------- File choice ----------

        /// <summary>Same order as debrid.js: the addon's file name, then SxxEyy,
        /// then the file index, then the largest video. The SxxEyy branch returns
        /// null on no match instead of falling through.</summary>
        private static JsonObject SelectFile(List<JsonObject> files, FileWant want, Func<JsonObject, string> path, Func<JsonObject, bool> playable, Func<JsonObject, long> size)
        {
            var videos = new List<JsonObject>();
            foreach (var f in files)
            {
                if (f != null && playable(f)) videos.Add(f);
            }
            if (videos.Count == 0) return null;
            if (!string.IsNullOrEmpty(want.Filename))
            {
                var name = NormPath(want.Filename).ToLowerInvariant();
                var baseName = BaseName(name);
                foreach (var f in videos)
                {
                    var p = NormPath(path(f)).ToLowerInvariant();
                    if (p == name || p.EndsWith("/" + name, StringComparison.Ordinal) || BaseName(p) == baseName) return f;
                }
            }
            if (want.Season.HasValue && want.Episode.HasValue)
            {
                var s = want.Season.Value;
                var e = want.Episode.Value;
                var pattern = new Regex("(^|[^a-z0-9])(s0*" + s + "[ ._-]*e0*" + e + "|0*" + s + "x0*" + e + ")(?![0-9])", RegexOptions.IgnoreCase);
                JsonObject best = null;
                long bestSize = -1;
                foreach (var f in videos)
                {
                    if (!pattern.IsMatch(BaseName(NormPath(path(f))))) continue;
                    var fileSize = size(f);
                    if (best == null || fileSize > bestSize) { best = f; bestSize = fileSize; }
                }
                return best;
            }
            if (want.FileIdx.HasValue)
            {
                var idx = want.FileIdx.Value;
                if (idx >= 0 && idx < files.Count && files[idx] != null && playable(files[idx])) return files[idx];
            }
            JsonObject largest = null;
            long largestSize = -1;
            foreach (var f in videos)
            {
                var fileSize = size(f);
                if (largest == null || fileSize > largestSize) { largest = f; largestSize = fileSize; }
            }
            return largest;
        }

        // ---------- Small helpers ----------

        private static Dictionary<string, string> AuthHeaders(string apiKey)
        {
            return new Dictionary<string, string>
            {
                { "Accept", "application/json" },
                { "Authorization", "Bearer " + apiKey },
            };
        }

        private static bool IsOk(RawResult response)
        {
            return response != null && response.Status >= 200 && response.Status < 300;
        }

        private DebridError NotCached()
        {
            return new DebridError("not_cached", "Not cached on " + (DebridName() ?? "the debrid service") + ".");
        }

        private DebridError Failed(RawResult response, string what)
        {
            if (response != null && (response.Status == 401 || response.Status == 403))
            {
                return new DebridError("invalid_key", (DebridName() ?? "The debrid service") + " rejected your API key.");
            }
            var detail = DetailOf(response != null ? JsonHelpers.GetObject(response.Data) : null, true);
            var name = DebridName() ?? "The debrid service";
            return new DebridError("error", name + " couldn't " + what + (string.IsNullOrEmpty(detail) ? "." : ": " + detail));
        }

        private static string DetailOf(JsonObject obj, bool includeMessage)
        {
            if (obj == null) return null;
            return JsonHelpers.GetString(obj, "detail")
                ?? JsonHelpers.GetString(obj, "error")
                ?? (includeMessage ? JsonHelpers.GetString(obj, "message") : null);
        }

        private static string FormField(string key, string value)
        {
            return Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value ?? string.Empty);
        }

        private static MultipartBody Multipart(List<KeyValuePair<string, string>> fields)
        {
            var boundary = "NuvioUWP" + Guid.NewGuid().ToString("N");
            var builder = new StringBuilder();
            foreach (var field in fields)
            {
                builder.Append("--").Append(boundary).Append("\r\n");
                builder.Append("Content-Disposition: form-data; name=\"").Append(field.Key).Append("\"\r\n\r\n");
                builder.Append(field.Value).Append("\r\n");
            }
            builder.Append("--").Append(boundary).Append("--\r\n");
            return new MultipartBody { Body = builder.ToString(), ContentType = "multipart/form-data; boundary=" + boundary };
        }

        private static List<JsonObject> ToObjectList(JsonArray array)
        {
            var result = new List<JsonObject>();
            if (array == null) return result;
            foreach (var item in array) result.Add(JsonHelpers.GetObject(item));
            return result;
        }

        private static bool IsVideoMime(string mimetype)
        {
            return mimetype != null && mimetype.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormPath(string p)
        {
            var s = (p ?? string.Empty).Trim().Replace('\\', '/');
            if (s.StartsWith("/", StringComparison.Ordinal)) s = s.Substring(1);
            return s;
        }

        private static string BaseName(string p)
        {
            if (string.IsNullOrEmpty(p)) return string.Empty;
            var slash = p.LastIndexOf('/');
            return slash >= 0 ? p.Substring(slash + 1) : p;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrEmpty(value)) return value;
            }
            return null;
        }
    }
}
