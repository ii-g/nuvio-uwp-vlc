using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Nuvio.Diagnostics;

namespace Nuvio.Core
{
    /// <summary>An HTTP failure, mirroring http.js HttpError(message, status, retryAfter).</summary>
    internal sealed class HttpError : Exception
    {
        public HttpError(string message, int status, double? retryAfterSeconds = null)
            : base(message)
        {
            Status = status;
            RetryAfterSeconds = retryAfterSeconds;
        }

        public int Status { get; }
        /// <summary>Seconds the server asked us to wait (Retry-After), if it said.</summary>
        public double? RetryAfterSeconds { get; }
    }

    /// <summary>Raw HTTP result: status, body text, and parsed JSON (null when empty or not JSON).</summary>
    internal sealed class RawResult
    {
        public int Status { get; set; }
        public string Text { get; set; }
        public JsonValue Data { get; set; }
    }

    /// <summary>
    /// Shared HttpClient: 400-entry TTL cache, in-flight de-duplication, 16 MB response cap.
    /// Ports core/http.js (getJson / requestJson / getText). There is no CORS in native code,
    /// so every service calls this directly. Header values are never logged.
    /// </summary>
    internal static class HttpService
    {
        private const int MaxCacheEntries = 400;
        private const long MaxBytes = 16 * 1024 * 1024;

        private static readonly HttpClient Client = CreateClient();
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, CacheEntry> Cache = new Dictionary<string, CacheEntry>();
        private static readonly Queue<string> CacheOrder = new Queue<string>();
        private static readonly Dictionary<string, Task<JsonValue>> InflightJson = new Dictionary<string, Task<JsonValue>>();

        private sealed class CacheEntry
        {
            public DateTimeOffset Expires;
            public JsonValue Value;
        }

        private sealed class ResponseText
        {
            public int Status;
            public string Text;
            public double? RetryAfterSeconds;
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient();
            var version = Package.Current.Id.Version;
            client.DefaultRequestHeaders.UserAgent.TryParseAdd(
                string.Format("NuvioUWP/{0}.{1}.{2}", version.Major, version.Minor, version.Build));
            return client;
        }

        /// <summary>GETs JSON, cached for ttlMs (0 = no cache). Default TTL 5 minutes.</summary>
        public static Task<JsonValue> GetJsonAsync(string url, int ttlMs = 300000, int timeoutMs = 20000, IDictionary<string, string> headers = null)
        {
            lock (Sync)
            {
                if (ttlMs > 0)
                {
                    CacheEntry hit;
                    if (Cache.TryGetValue(url, out hit) && hit.Expires > DateTimeOffset.UtcNow)
                    {
                        return Task.FromResult(hit.Value);
                    }
                }
                Task<JsonValue> running;
                if (InflightJson.TryGetValue(url, out running)) return running;
                var task = FetchJsonAsync(url, ttlMs, timeoutMs, headers);
                InflightJson[url] = task;
                return task;
            }
        }

        private static async Task<JsonValue> FetchJsonAsync(string url, int ttlMs, int timeoutMs, IDictionary<string, string> headers)
        {
            try
            {
                var value = await RequestJsonAsync(url, "GET", null, timeoutMs, headers).ConfigureAwait(false);
                if (ttlMs > 0 && value != null)
                {
                    lock (Sync)
                    {
                        if (!Cache.ContainsKey(url))
                        {
                            CacheOrder.Enqueue(url);
                            while (CacheOrder.Count > MaxCacheEntries)
                            {
                                Cache.Remove(CacheOrder.Dequeue());
                            }
                        }
                        Cache[url] = new CacheEntry { Expires = DateTimeOffset.UtcNow.AddMilliseconds(ttlMs), Value = value };
                    }
                }
                return value;
            }
            finally
            {
                lock (Sync)
                {
                    InflightJson.Remove(url);
                }
            }
        }

        public static void Invalidate(string prefix)
        {
            lock (Sync)
            {
                var doomed = new List<string>();
                foreach (var key in Cache.Keys)
                {
                    if (key.StartsWith(prefix, StringComparison.Ordinal)) doomed.Add(key);
                }
                foreach (var key in doomed) Cache.Remove(key);
                if (doomed.Count > 0)
                {
                    var kept = new Queue<string>();
                    foreach (var key in CacheOrder)
                    {
                        if (Cache.ContainsKey(key)) kept.Enqueue(key);
                    }
                    CacheOrder.Clear();
                    foreach (var key in kept) CacheOrder.Enqueue(key);
                }
            }
        }

        /// <summary>Any method; body is JSON-encoded. Not cached.</summary>
        public static async Task<JsonValue> RequestJsonAsync(string url, string method = "GET", string body = null, int timeoutMs = 20000, IDictionary<string, string> headers = null)
        {
            using (var request = new HttpRequestMessage(new HttpMethod(method), url))
            {
                if (headers != null)
                {
                    foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                }
                if (body != null)
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }
                var response = await SendAsync(request, timeoutMs).ConfigureAwait(false);
                if (string.IsNullOrEmpty(response.Text)) return null;
                try
                {
                    return JsonValue.Parse(response.Text);
                }
                catch (Exception)
                {
                    throw new HttpError("Invalid response from " + new Uri(url).Host, response.Status);
                }
            }
        }

        /// <summary>
        /// Any method with an explicit body content type (form/multipart). Not cached.
        /// Returns the status, body text and parsed JSON without throwing on HTTP error
        /// statuses; throws HttpError only on timeout/network (status 0) or oversize.
        /// </summary>
        public static async Task<RawResult> RequestRawAsync(string url, string method, string body, string contentType, int timeoutMs, IDictionary<string, string> headers)
        {
            using (var request = new HttpRequestMessage(new HttpMethod(method), url))
            {
                if (headers != null)
                {
                    foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                }
                if (body != null)
                {
                    request.Content = new StringContent(body, Encoding.UTF8, contentType ?? "application/json");
                }
                var response = await SendRawAsync(request, timeoutMs).ConfigureAwait(false);
                return new RawResult { Status = response.Status, Text = response.Text, Data = JsonHelpers.Parse(response.Text) };
            }
        }

        /// <summary>Fetches text such as a subtitle file, decoding it as UTF-8 or, failing that, Windows-1252.</summary>
        public static async Task<string> GetTextAsync(string url, int timeoutMs = 20000)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                var response = await SendAsync(request, timeoutMs).ConfigureAwait(false);
                return response.Text;
            }
        }

        private static async Task<ResponseText> SendAsync(HttpRequestMessage request, int timeoutMs)
        {
            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                HttpResponseMessage response;
                try
                {
                    response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    throw new HttpError("The request timed out", 0);
                }
                using (response)
                {
                    double? retryAfter = null;
                    var retry = response.Headers.RetryAfter;
                    if (retry != null)
                    {
                        if (retry.Delta.HasValue) retryAfter = Math.Max(0, retry.Delta.Value.TotalSeconds);
                        else if (retry.Date.HasValue) retryAfter = Math.Max(0, (retry.Date.Value - DateTimeOffset.UtcNow).TotalSeconds);
                    }
                    var status = (int)response.StatusCode;
                    var length = response.Content.Headers.ContentLength;
                    if (length.HasValue && length.Value > MaxBytes)
                    {
                        throw new HttpError("Response too large", status, retryAfter);
                    }
                    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (bytes.Length > MaxBytes)
                    {
                        throw new HttpError("Response too large", status, retryAfter);
                    }
                    var text = DecodeText(bytes);
                    if (status < 200 || status >= 300)
                    {
                        throw new HttpError(ErrorMessageFrom(text, status), status, retryAfter);
                    }
                    return new ResponseText { Status = status, Text = text, RetryAfterSeconds = retryAfter };
                }
            }
        }

        /// <summary>Like SendAsync, but HTTP error statuses are returned, not thrown,
        /// so the caller can read the status and body. Timeout/network surface as
        /// HttpError with status 0; oversize as HttpError, mirroring SendAsync.</summary>
        private static async Task<ResponseText> SendRawAsync(HttpRequestMessage request, int timeoutMs)
        {
            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                HttpResponseMessage response;
                try
                {
                    response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    throw new HttpError("The request timed out", 0);
                }
                catch (HttpRequestException)
                {
                    throw new HttpError("The request failed", 0);
                }
                using (response)
                {
                    double? retryAfter = null;
                    var retry = response.Headers.RetryAfter;
                    if (retry != null)
                    {
                        if (retry.Delta.HasValue) retryAfter = Math.Max(0, retry.Delta.Value.TotalSeconds);
                        else if (retry.Date.HasValue) retryAfter = Math.Max(0, (retry.Date.Value - DateTimeOffset.UtcNow).TotalSeconds);
                    }
                    var status = (int)response.StatusCode;
                    var length = response.Content.Headers.ContentLength;
                    if (length.HasValue && length.Value > MaxBytes)
                    {
                        throw new HttpError("Response too large", status, retryAfter);
                    }
                    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (bytes.Length > MaxBytes)
                    {
                        throw new HttpError("Response too large", status, retryAfter);
                    }
                    return new ResponseText { Status = status, Text = DecodeText(bytes), RetryAfterSeconds = retryAfter };
                }
            }
        }

        private static string ErrorMessageFrom(string text, int status)
        {
            var message = "HTTP " + status;
            if (string.IsNullOrEmpty(text)) return message;
            try
            {
                var data = JsonValue.Parse(text);
                if (data != null && data.ValueType == JsonValueType.Object)
                {
                    var obj = data.GetObject();
                    var candidate = JsonHelpers.GetString(obj, "message")
                        ?? JsonHelpers.GetString(obj, "error_description")
                        ?? JsonHelpers.GetString(obj, "msg")
                        ?? JsonHelpers.GetString(obj, "error");
                    if (!string.IsNullOrEmpty(candidate)) return candidate;
                    if (obj.ContainsKey("error"))
                    {
                        var errorValue = obj.GetNamedValue("error");
                        if (errorValue != null && errorValue.ValueType != JsonValueType.Null) return errorValue.Stringify();
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to the plain HTTP message.
            }
            return message;
        }

        public static string DecodeText(byte[] bytes)
        {
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                try
                {
                    return Encoding.GetEncoding(1252).GetString(bytes);
                }
                catch (Exception)
                {
                    return new UTF8Encoding(false, false).GetString(bytes);
                }
            }
        }
    }
}
