using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Nuvio.Diagnostics;

namespace Nuvio.Playback
{
    /// <summary>
    /// Seekable HTTP stream for media sources that need request headers. It requests bounded byte
    /// ranges, so playback does not buffer an entire video in memory. Servers that ignore Range are
    /// still streamed from the beginning when the player seeks.
    /// </summary>
    internal sealed class HttpRangeStream : Stream
    {
        // Larger sequential ranges reduce request/round-trip stalls on high-bitrate Xbox playback.
        // The response body is streamed, so this does not buffer the whole range in memory.
        private const int RangeSize = 4 * 1024 * 1024;
        private readonly HttpClient _client;
        private readonly Uri _uri;
        private readonly SemaphoreSlim _readGate = new SemaphoreSlim(1, 1);
        private HttpResponseMessage _response;
        private Stream _responseStream;
        private long _position;
        private long _responsePosition;
        private long _responseEnd;
        private bool _disposed;

        private HttpRangeStream(HttpClient client, Uri uri, long length, string contentType)
        {
            _client = client;
            _uri = uri;
            // length < 0 means the server never reported one: Length reports
            // long.MaxValue so the stream stays seekable/readable and WMF can
            // still play it; the end is signalled by a 0-byte read instead.
            _unknownLength = length < 0;
            _length = _unknownLength ? long.MaxValue : length;
            ContentType = string.IsNullOrWhiteSpace(contentType) ||
                contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
                ? GuessContentType(uri) : contentType;
        }

        public string ContentType { get; }
        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length => _length;
        private readonly long _length;
        private readonly bool _unknownLength;

        public override long Position
        {
            get { return _position; }
            set { Seek(value, SeekOrigin.Begin); }
        }

        public static async Task<HttpRangeStream> CreateAsync(Uri uri, IDictionary<string, string> headers, CancellationToken token)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = true };
            var client = new HttpClient(handler);
            try
            {
                AddDefaultHeaders(client, headers);
                long? length = null;
                string contentType = null;

                // A failed HEAD must not kill playback: some origins reject HEAD
                // outright while serving GET fine.
                try
                {
                    using (var head = new HttpRequestMessage(HttpMethod.Head, uri))
                    using (var response = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, token))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            length = response.Content.Headers.ContentLength;
                            contentType = response.Content.Headers.ContentType == null ? null : response.Content.Headers.ContentType.MediaType;
                        }
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    Log.Write("Media HEAD request failed for host " + uri.Host + "; continuing with a range probe (" + ex.Message + ")");
                }

                if (!length.HasValue || length.Value <= 0)
                {
                    // A 0-0 range probe is only a hint. Some servers answer it with
                    // 403/405 while serving the media itself, so never throw here:
                    // an unsuccessful probe just means the length stays unknown.
                    try
                    {
                        using (var probe = new HttpRequestMessage(HttpMethod.Get, uri))
                        {
                            probe.Headers.Range = new RangeHeaderValue(0, 0);
                            using (var response = await client.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, token))
                            {
                                if (response.IsSuccessStatusCode)
                                {
                                    var range = response.Content.Headers.ContentRange;
                                    length = range != null && range.Length.HasValue
                                        ? range.Length
                                        : response.Content.Headers.ContentLength;
                                    if (string.IsNullOrWhiteSpace(contentType) && response.Content.Headers.ContentType != null)
                                        contentType = response.Content.Headers.ContentType.MediaType;
                                }
                                else
                                {
                                    Log.Write("Media range probe for host " + uri.Host + " returned HTTP " + (int)response.StatusCode + "; continuing without a known length");
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        Log.Write("Media range probe failed for host " + uri.Host + "; continuing without a known length (" + ex.Message + ")");
                    }
                }

                if (!length.HasValue || length.Value <= 0)
                {
                    Log.Write("The server did not report the media length for host " + uri.Host + "; streaming with an unknown length");
                    length = -1;
                }

                return new HttpRangeStream(client, uri, length.Value, contentType);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private static void AddDefaultHeaders(HttpClient client, IDictionary<string, string> headers)
        {
            var hasUserAgent = false;
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    if (string.IsNullOrWhiteSpace(header.Key) ||
                        header.Key.Equals("Range", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) hasUserAgent = true;

                    if (!client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value ?? string.Empty))
                        throw new InvalidOperationException("The media request header '" + header.Key + "' cannot be applied by the native HTTP stack.");
                }
            }
            // Some origins (and CDNs) reject requests with no User-Agent at all.
            if (!hasUserAgent) client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "NuvioUWP");
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HttpRangeStream));
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();
            if (count == 0 || _position >= Length) return 0;

            await _readGate.WaitAsync(cancellationToken);
            try
            {
                if (_position >= Length) return 0;
                if (_responseStream == null || _position != _responsePosition || _position >= _responseEnd)
                    await OpenRangeAsync(cancellationToken);

                var available = (int)Math.Min(count, Math.Min(int.MaxValue, _responseEnd - _position));
                if (available <= 0) return 0;
                var read = await _responseStream.ReadAsync(buffer, offset, available, cancellationToken);
                _position += read;
                _responsePosition += read;
                return read;
            }
            finally
            {
                _readGate.Release();
            }
        }

        private async Task OpenRangeAsync(CancellationToken token)
        {
            CloseResponse();
            var start = _position;
            var end = Math.Min(Length - 1, start + RangeSize - 1);
            var request = new HttpRequestMessage(HttpMethod.Get, _uri);
            request.Headers.Range = new RangeHeaderValue(start, end);
            HttpResponseMessage response = null;

            try
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                request.Dispose();
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // Past the end of a stream whose length was never reported:
                    // signal end-of-stream instead of failing the read.
                    _responseEnd = _position;
                    _responsePosition = _position;
                    response.Dispose();
                    return;
                }
                response.EnsureSuccessStatusCode();

                var stream = await response.Content.ReadAsStreamAsync();
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var contentRange = response.Content.Headers.ContentRange;
                    var actualStart = contentRange != null && contentRange.From.HasValue ? contentRange.From.Value : start;
                    if (actualStart != start)
                    {
                        stream.Dispose();
                        response.Dispose();
                        throw new IOException("The server returned a byte range at the wrong offset.");
                    }
                    var responseLength = response.Content.Headers.ContentLength;
                    _responseEnd = Math.Min(Length, start + (responseLength.HasValue ? responseLength.Value : end - start + 1));
                    _responsePosition = start;
                }
                else
                {
                    // Some origins ignore Range and return 200. Consume forward without keeping the
                    // whole response in memory; seeks may re-request from byte zero.
                    if (start > 0)
                        await SkipAsync(stream, start, token);
                    _responseEnd = Length;
                    _responsePosition = start;
                }

                _response = response;
                _responseStream = stream;
            }
            catch
            {
                request.Dispose();
                response?.Dispose();
                throw;
            }
        }

        private static async Task SkipAsync(Stream stream, long bytes, CancellationToken token)
        {
            var scratch = new byte[32 * 1024];
            while (bytes > 0)
            {
                token.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(scratch, 0, (int)Math.Min(scratch.Length, bytes), token);
                if (read <= 0) throw new EndOfStreamException("The server ended the media before the requested seek position.");
                bytes -= read;
            }
        }

        private static string GuessContentType(Uri uri)
        {
            switch (Path.GetExtension(uri.AbsolutePath).ToLowerInvariant())
            {
                case ".mp4": case ".m4v": return "video/mp4";
                case ".mkv": return "video/x-matroska";
                case ".webm": return "video/webm";
                case ".mov": return "video/quicktime";
                case ".ts": return "video/mp2t";
                case ".avi": return "video/x-msvideo";
                case ".mp3": return "audio/mpeg";
                case ".m4a": return "audio/mp4";
                default: return "application/octet-stream";
            }
        }

        private void CloseResponse()
        {
            try { _responseStream?.Dispose(); } catch { }
            try { _response?.Dispose(); } catch { }
            _responseStream = null;
            _response = null;
            _responsePosition = _position;
            _responseEnd = _position;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HttpRangeStream));
            long target;
            switch (origin)
            {
                case SeekOrigin.Begin: target = offset; break;
                case SeekOrigin.Current: target = _position + offset; break;
                case SeekOrigin.End:
                    if (_unknownLength) throw new NotSupportedException("The media length is unknown, so seeking from the end is not supported.");
                    target = Length + offset;
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(origin));
            }
            if (target < 0 || target > Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (target != _position)
            {
                _position = target;
                CloseResponse();
            }
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                CloseResponse();
                _client.Dispose();
                _readGate.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
