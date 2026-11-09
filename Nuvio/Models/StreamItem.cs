using System.Collections.Generic;
using Nuvio.Core;
using Windows.Data.Json;

namespace Nuvio.Models
{
    /// <summary>A subtitle offered by an addon for a stream.</summary>
    internal sealed class SubtitleRef
    {
        public string Url { get; set; }
        public string Lang { get; set; }
        public string Id { get; set; }
        public string AddonName { get; set; }
    }

    /// <summary>behaviorHints on a stream (proxy headers, filename, size, binge group).</summary>
    internal sealed class StreamBehaviorHints
    {
        public string Filename { get; set; }
        public long? VideoSize { get; set; }
        public string VideoHash { get; set; }
        public string BingeGroup { get; set; }
        /// <summary>proxyHeaders.request — string values only.</summary>
        public Dictionary<string, string> ProxyRequestHeaders { get; set; }
    }

    /// <summary>Jellyfin/Emby origin of a stream (only set for Jellyfin streams).</summary>
    internal sealed class JellyfinRef
    {
        public string Server { get; set; }
        public string ItemId { get; set; }
        public string MediaSourceId { get; set; }
    }

    /// <summary>A playable (or torrent/external) stream result from an addon.</summary>
    internal sealed class StreamItem
    {
        public string Url { get; set; }
        public string Name { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string InfoHash { get; set; }
        public int? FileIdx { get; set; }
        /// <summary>Tracker URLs.</summary>
        public List<string> Sources { get; set; }
        public string YtId { get; set; }
        public string ExternalUrl { get; set; }
        public List<SubtitleRef> Subtitles { get; set; }
        public StreamBehaviorHints BehaviorHints { get; set; }
        /// <summary>Attached by the caller, not from the server.</summary>
        public string AddonUrl { get; set; }
        /// <summary>Attached by the caller, not from the server.</summary>
        public string AddonName { get; set; }
        public JellyfinRef Jellyfin { get; set; }

        public static StreamItem FromJson(JsonValue value)
        {
            var obj = JsonHelpers.GetObject(value);
            if (obj == null) return null;
            return new StreamItem
            {
                Url = JsonHelpers.GetString(obj, "url"),
                Name = JsonHelpers.GetString(obj, "name"),
                Title = JsonHelpers.GetString(obj, "title"),
                Description = JsonHelpers.GetString(obj, "description"),
                InfoHash = JsonHelpers.GetString(obj, "infoHash"),
                FileIdx = JsonHelpers.GetInt(obj, "fileIdx"),
                Sources = JsonHelpers.GetStringList(obj, "sources"),
                YtId = JsonHelpers.GetString(obj, "ytId"),
                ExternalUrl = JsonHelpers.GetString(obj, "externalUrl"),
                Subtitles = ParseSubtitles(JsonHelpers.GetArray(obj, "subtitles")),
                BehaviorHints = ParseHints(JsonHelpers.GetObject(obj, "behaviorHints")),
                Jellyfin = ParseJellyfin(JsonHelpers.GetObject(obj, "jellyfin")),
            };
        }

        private static List<SubtitleRef> ParseSubtitles(JsonArray array)
        {
            if (array == null) return null;
            var result = new List<SubtitleRef>();
            foreach (var item in array)
            {
                var obj = JsonHelpers.GetObject(item);
                if (obj == null) continue;
                result.Add(new SubtitleRef
                {
                    Url = JsonHelpers.GetString(obj, "url"),
                    Lang = JsonHelpers.GetString(obj, "lang"),
                    Id = JsonHelpers.GetString(obj, "id"),
                    AddonName = JsonHelpers.GetString(obj, "addonName"),
                });
            }
            return result;
        }

        private static StreamBehaviorHints ParseHints(JsonObject obj)
        {
            if (obj == null) return null;
            Dictionary<string, string> proxy = null;
            var proxyHeaders = JsonHelpers.GetObject(obj, "proxyHeaders");
            var request = proxyHeaders != null ? JsonHelpers.GetObject(proxyHeaders, "request") : null;
            if (request != null)
            {
                proxy = new Dictionary<string, string>();
                foreach (var pair in request)
                {
                    var text = JsonHelpers.GetString(pair.Value);
                    if (text != null) proxy[pair.Key] = text;
                }
            }
            return new StreamBehaviorHints
            {
                Filename = JsonHelpers.GetString(obj, "filename"),
                VideoSize = JsonHelpers.GetLong(obj, "videoSize"),
                VideoHash = JsonHelpers.GetString(obj, "videoHash"),
                BingeGroup = JsonHelpers.GetString(obj, "bingeGroup"),
                ProxyRequestHeaders = proxy,
            };
        }

        private static JellyfinRef ParseJellyfin(JsonObject obj)
        {
            if (obj == null) return null;
            return new JellyfinRef
            {
                Server = JsonHelpers.GetString(obj, "server"),
                ItemId = JsonHelpers.GetString(obj, "itemId"),
                MediaSourceId = JsonHelpers.GetString(obj, "mediaSourceId"),
            };
        }

        public JsonObject ToJson()
        {
            var obj = new JsonObject();
            if (Url != null) obj.SetNamedValue("url", JsonHelpers.CreateString(Url));
            if (Name != null) obj.SetNamedValue("name", JsonHelpers.CreateString(Name));
            if (Title != null) obj.SetNamedValue("title", JsonHelpers.CreateString(Title));
            if (Description != null) obj.SetNamedValue("description", JsonHelpers.CreateString(Description));
            if (InfoHash != null) obj.SetNamedValue("infoHash", JsonHelpers.CreateString(InfoHash));
            if (FileIdx.HasValue) obj.SetNamedValue("fileIdx", JsonHelpers.CreateNumber(FileIdx.Value));
            if (YtId != null) obj.SetNamedValue("ytId", JsonHelpers.CreateString(YtId));
            if (ExternalUrl != null) obj.SetNamedValue("externalUrl", JsonHelpers.CreateString(ExternalUrl));
            if (BehaviorHints != null)
            {
                var hints = new JsonObject();
                if (BehaviorHints.Filename != null) hints.SetNamedValue("filename", JsonHelpers.CreateString(BehaviorHints.Filename));
                if (BehaviorHints.VideoSize.HasValue) hints.SetNamedValue("videoSize", JsonHelpers.CreateNumber(BehaviorHints.VideoSize.Value));
                if (BehaviorHints.VideoHash != null) hints.SetNamedValue("videoHash", JsonHelpers.CreateString(BehaviorHints.VideoHash));
                if (BehaviorHints.BingeGroup != null) hints.SetNamedValue("bingeGroup", JsonHelpers.CreateString(BehaviorHints.BingeGroup));
                obj.SetNamedValue("behaviorHints", hints);
            }
            if (Jellyfin != null)
            {
                var jf = new JsonObject();
                if (Jellyfin.Server != null) jf.SetNamedValue("server", JsonHelpers.CreateString(Jellyfin.Server));
                if (Jellyfin.ItemId != null) jf.SetNamedValue("itemId", JsonHelpers.CreateString(Jellyfin.ItemId));
                if (Jellyfin.MediaSourceId != null) jf.SetNamedValue("mediaSourceId", JsonHelpers.CreateString(Jellyfin.MediaSourceId));
                obj.SetNamedValue("jellyfin", jf);
            }
            return obj;
        }
    }
}
