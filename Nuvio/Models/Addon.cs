using System.Collections.Generic;
using Nuvio.Core;
using Windows.Data.Json;

namespace Nuvio.Models
{
    /// <summary>One extra a catalog accepts, e.g. {name:'genre'} / {name:'skip'} / {name:'search'}.</summary>
    internal sealed class CatalogExtraEntry
    {
        public string Name { get; set; }
        public bool IsRequired { get; set; }
        public List<string> Options { get; set; }

        public static CatalogExtraEntry FromJson(IJsonValue value)
        {
            var obj = JsonHelpers.GetObject(value);
            if (obj == null) return null;
            return new CatalogExtraEntry
            {
                Name = JsonHelpers.GetString(obj, "name"),
                IsRequired = JsonHelpers.GetBool(obj, "isRequired") ?? false,
                Options = JsonHelpers.GetStringList(obj, "options"),
            };
        }

        public JsonObject ToJson()
        {
            var obj = new JsonObject();
            if (Name != null) obj.SetNamedValue("name", JsonHelpers.CreateString(Name));
            obj.SetNamedValue("isRequired", JsonHelpers.CreateBool(IsRequired));
            if (Options != null)
            {
                var array = new JsonArray();
                foreach (var option in Options) array.Add(JsonHelpers.CreateString(option));
                obj.SetNamedValue("options", array);
            }
            return obj;
        }
    }

    /// <summary>A catalog listed by an addon manifest.</summary>
    internal sealed class Catalog
    {
        public string Type { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string PosterShape { get; set; }
        public bool HideType { get; set; }
        public List<CatalogExtraEntry> Extra { get; set; }
        /// <summary>Legacy manifest form: extraSupported / extraRequired names.</summary>
        public List<string> ExtraSupported { get; set; }
        public List<string> ExtraRequired { get; set; }

        public static Catalog FromJson(IJsonValue value)
        {
            var obj = JsonHelpers.GetObject(value);
            if (obj == null) return null;
            List<CatalogExtraEntry> extra = null;
            var extraArray = JsonHelpers.GetArray(obj, "extra");
            if (extraArray != null)
            {
                extra = new List<CatalogExtraEntry>();
                foreach (var item in extraArray)
                {
                    var entry = CatalogExtraEntry.FromJson(item);
                    if (entry != null) extra.Add(entry);
                }
            }
            return new Catalog
            {
                Type = JsonHelpers.GetString(obj, "type"),
                Id = JsonHelpers.GetString(obj, "id"),
                Name = JsonHelpers.GetString(obj, "name"),
                PosterShape = JsonHelpers.GetString(obj, "posterShape"),
                HideType = JsonHelpers.GetBool(obj, "hideType") ?? false,
                Extra = extra,
                ExtraSupported = JsonHelpers.GetStringList(obj, "extraSupported"),
                ExtraRequired = JsonHelpers.GetStringList(obj, "extraRequired"),
            };
        }

        public JsonObject ToJson()
        {
            var obj = new JsonObject();
            if (Type != null) obj.SetNamedValue("type", JsonHelpers.CreateString(Type));
            if (Id != null) obj.SetNamedValue("id", JsonHelpers.CreateString(Id));
            if (Name != null) obj.SetNamedValue("name", JsonHelpers.CreateString(Name));
            if (PosterShape != null) obj.SetNamedValue("posterShape", JsonHelpers.CreateString(PosterShape));
            if (HideType) obj.SetNamedValue("hideType", JsonHelpers.CreateBool(true));
            if (Extra != null)
            {
                var array = new JsonArray();
                foreach (var entry in Extra) array.Add(entry.ToJson());
                obj.SetNamedValue("extra", array);
            }
            return obj;
        }
    }

    /// <summary>One entry of a manifest's resources[] — a plain string or {name, types[], idPrefixes[]}.</summary>
    internal sealed class ManifestResource
    {
        public string Name { get; set; }
        public List<string> Types { get; set; }
        public List<string> IdPrefixes { get; set; }

        public static ManifestResource FromJson(IJsonValue value)
        {
            if (value == null) return null;
            if (value.ValueType == JsonValueType.String) return new ManifestResource { Name = value.GetString() };
            var obj = JsonHelpers.GetObject(value);
            if (obj == null) return null;
            return new ManifestResource
            {
                Name = JsonHelpers.GetString(obj, "name"),
                Types = JsonHelpers.GetStringList(obj, "types"),
                IdPrefixes = JsonHelpers.GetStringList(obj, "idPrefixes"),
            };
        }

        public IJsonValue ToJson()
        {
            if ((Types == null || Types.Count == 0) && (IdPrefixes == null || IdPrefixes.Count == 0))
            {
                return JsonHelpers.CreateString(Name);
            }
            var obj = new JsonObject();
            if (Name != null) obj.SetNamedValue("name", JsonHelpers.CreateString(Name));
            if (Types != null)
            {
                var array = new JsonArray();
                foreach (var type in Types) array.Add(JsonHelpers.CreateString(type));
                obj.SetNamedValue("types", array);
            }
            if (IdPrefixes != null)
            {
                var array = new JsonArray();
                foreach (var prefix in IdPrefixes) array.Add(JsonHelpers.CreateString(prefix));
                obj.SetNamedValue("idPrefixes", array);
            }
            return obj;
        }
    }

    /// <summary>A Stremio addon manifest.</summary>
    internal sealed class AddonManifest
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public string Logo { get; set; }
        public List<ManifestResource> Resources { get; set; }
        public List<string> Types { get; set; }
        public List<string> IdPrefixes { get; set; }
        public List<Catalog> Catalogs { get; set; }
        public bool BehaviorHintsConfigurable { get; set; }

        public static AddonManifest FromJson(IJsonValue value)
        {
            var obj = JsonHelpers.GetObject(value);
            if (obj == null) return null;
            List<ManifestResource> resources = null;
            var resourcesArray = JsonHelpers.GetArray(obj, "resources");
            if (resourcesArray != null)
            {
                resources = new List<ManifestResource>();
                foreach (var item in resourcesArray)
                {
                    var resource = ManifestResource.FromJson(item);
                    if (resource != null) resources.Add(resource);
                }
            }
            List<Catalog> catalogs = null;
            var catalogsArray = JsonHelpers.GetArray(obj, "catalogs");
            if (catalogsArray != null)
            {
                catalogs = new List<Catalog>();
                foreach (var item in catalogsArray)
                {
                    var catalog = Catalog.FromJson(item);
                    if (catalog != null) catalogs.Add(catalog);
                }
            }
            var hints = JsonHelpers.GetObject(obj, "behaviorHints");
            return new AddonManifest
            {
                Id = JsonHelpers.GetString(obj, "id"),
                Name = JsonHelpers.GetString(obj, "name"),
                Version = JsonHelpers.GetString(obj, "version"),
                Description = JsonHelpers.GetString(obj, "description"),
                Logo = JsonHelpers.GetString(obj, "logo"),
                Resources = resources ?? new List<ManifestResource>(),
                Types = JsonHelpers.GetStringList(obj, "types") ?? new List<string>(),
                IdPrefixes = JsonHelpers.GetStringList(obj, "idPrefixes"),
                Catalogs = catalogs ?? new List<Catalog>(),
                BehaviorHintsConfigurable = hints != null && (JsonHelpers.GetBool(hints, "configurable") ?? false),
            };
        }

        public JsonObject ToJson()
        {
            var obj = new JsonObject();
            if (Id != null) obj.SetNamedValue("id", JsonHelpers.CreateString(Id));
            if (Name != null) obj.SetNamedValue("name", JsonHelpers.CreateString(Name));
            if (Version != null) obj.SetNamedValue("version", JsonHelpers.CreateString(Version));
            if (Description != null) obj.SetNamedValue("description", JsonHelpers.CreateString(Description));
            if (Logo != null) obj.SetNamedValue("logo", JsonHelpers.CreateString(Logo));
            var resources = new JsonArray();
            if (Resources != null)
            {
                foreach (var resource in Resources) resources.Add(resource.ToJson());
            }
            obj.SetNamedValue("resources", resources);
            if (Types != null)
            {
                var array = new JsonArray();
                foreach (var type in Types) array.Add(JsonHelpers.CreateString(type));
                obj.SetNamedValue("types", array);
            }
            if (IdPrefixes != null)
            {
                var array = new JsonArray();
                foreach (var prefix in IdPrefixes) array.Add(JsonHelpers.CreateString(prefix));
                obj.SetNamedValue("idPrefixes", array);
            }
            var catalogs = new JsonArray();
            if (Catalogs != null)
            {
                foreach (var catalog in Catalogs) catalogs.Add(catalog.ToJson());
            }
            obj.SetNamedValue("catalogs", catalogs);
            if (BehaviorHintsConfigurable)
            {
                var hints = new JsonObject();
                hints.SetNamedValue("configurable", JsonHelpers.CreateBool(true));
                obj.SetNamedValue("behaviorHints", hints);
            }
            return obj;
        }
    }

    /// <summary>An installed addon: { transportUrl, manifest, enabled, refreshedAt, error? }.</summary>
    internal sealed class Addon
    {
        /// <summary>The manifest URL.</summary>
        public string TransportUrl { get; set; }
        public AddonManifest Manifest { get; set; }
        /// <summary>Absent = enabled.</summary>
        public bool? Enabled { get; set; }
        /// <summary>Manifest fetch time, ms epoch.</summary>
        public long RefreshedAt { get; set; }
        public string Error { get; set; }

        /// <summary>Built-in providers (Trakt/Jellyfin, M7) answer in code instead of over HTTP. Not serialised.</summary>
        public Core.IAddonProvider Handler { get; set; }

        public bool IsEnabled
        {
            get { return Enabled != false && Manifest != null; }
        }

        public static Addon FromJson(IJsonValue value)
        {
            var obj = JsonHelpers.GetObject(value);
            if (obj == null) return null;
            return new Addon
            {
                TransportUrl = JsonHelpers.GetString(obj, "transportUrl"),
                Manifest = AddonManifest.FromJson(JsonHelpers.GetObject(obj, "manifest")),
                Enabled = JsonHelpers.GetBool(obj, "enabled"),
                RefreshedAt = JsonHelpers.GetLong(obj, "refreshedAt") ?? 0,
                Error = JsonHelpers.GetString(obj, "error"),
            };
        }

        public JsonObject ToJson()
        {
            var obj = new JsonObject();
            if (TransportUrl != null) obj.SetNamedValue("transportUrl", JsonHelpers.CreateString(TransportUrl));
            if (Manifest != null) obj.SetNamedValue("manifest", Manifest.ToJson());
            else obj.SetNamedValue("manifest", JsonValue.CreateNullValue());
            if (Enabled.HasValue) obj.SetNamedValue("enabled", JsonHelpers.CreateBool(Enabled.Value));
            obj.SetNamedValue("refreshedAt", JsonHelpers.CreateNumber(RefreshedAt));
            if (Error != null) obj.SetNamedValue("error", JsonHelpers.CreateString(Error));
            return obj;
        }
    }
}
