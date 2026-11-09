using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Nuvio.Core;
using Nuvio.Diagnostics;
using Windows.Data.Json;
using Windows.Storage;

namespace Nuvio.Services
{
    /// <summary>
    /// ApplicationData access. Small scalars live in LocalSettings; all collections and
    /// credentials are JSON files in LocalFolder/nuvio/ (architecture §9.2 — the per-value
    /// size limit of LocalSettings forbids a literal localStorage port).
    /// Profile scoping is a folder, not a key prefix: profileKey(name) → profiles/p{N}/{name}.json.
    /// </summary>
    internal sealed class LocalStore
    {
        private const string RootDir = "nuvio";
        private const string Alphanumeric = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        public int ActiveProfile
        {
            get
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (values.ContainsKey("activeProfile") && values["activeProfile"] is int index && index >= 1) return index;
                return 1;
            }
        }

        public void SetActiveProfile(int index)
        {
            ApplicationData.Current.LocalSettings.Values["activeProfile"] = index;
        }

        public string ProfileKey(string name)
        {
            return "profiles/p" + ActiveProfile.ToString() + "/" + name + ".json";
        }

        public Task<JsonValue> ReadProfileJsonAsync(string name)
        {
            return ReadJsonFileAsync(ProfileKey(name));
        }

        public Task WriteProfileJsonAsync(string name, JsonValue value)
        {
            return WriteJsonFileAsync(ProfileKey(name), value);
        }

        public async Task<JsonValue> ReadJsonFileAsync(string relativePath)
        {
            try
            {
                SplitPath(relativePath, out var dir, out var fileName);
                var folder = await GetFolderAsync(dir).ConfigureAwait(false);
                var file = await folder.GetFileAsync(fileName).AsTask().ConfigureAwait(false);
                var text = await FileIO.ReadTextAsync(file).AsTask().ConfigureAwait(false);
                return JsonHelpers.Parse(text);
            }
            catch (Exception ex)
            {
                // Missing file is the normal "nothing stored yet" case; anything else is logged.
                if (!(ex is System.IO.FileNotFoundException)) Log.Error("Reading " + relativePath + " failed", ex);
                return null;
            }
        }

        public async Task WriteJsonFileAsync(string relativePath, JsonValue value)
        {
            try
            {
                SplitPath(relativePath, out var dir, out var fileName);
                var folder = await GetFolderAsync(dir).ConfigureAwait(false);
                if (value == null)
                {
                    try
                    {
                        var existing = await folder.GetFileAsync(fileName).AsTask().ConfigureAwait(false);
                        await existing.DeleteAsync().AsTask().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Already gone.
                    }
                    return;
                }
                var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting).AsTask().ConfigureAwait(false);
                await FileIO.WriteTextAsync(file, value.Stringify()).AsTask().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Writing " + relativePath + " failed", ex);
            }
        }

        private async Task<StorageFolder> GetFolderAsync(string relativeDir)
        {
            var folder = ApplicationData.Current.LocalFolder;
            if (string.IsNullOrEmpty(relativeDir)) return folder;
            foreach (var part in relativeDir.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                folder = await folder.CreateFolderAsync(part, CreationCollisionOption.OpenIfExists).AsTask().ConfigureAwait(false);
            }
            return folder;
        }

        private static void SplitPath(string relativePath, out string dir, out string fileName)
        {
            var path = RootDir + "/" + relativePath;
            var slash = path.LastIndexOf('/');
            dir = path.Substring(0, slash);
            fileName = path.Substring(slash + 1);
        }

        public T GetSetting<T>(string key, T fallback)
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            if (!values.ContainsKey(key)) return fallback;
            try
            {
                return (T)values[key];
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        public void SetSetting(string key, object value)
        {
            if (value == null) ApplicationData.Current.LocalSettings.Values.Remove(key);
            else ApplicationData.Current.LocalSettings.Values[key] = value;
        }

        /// <summary>syncClientId: 'nuvio-uwp-' + 32 chars, generated once if absent.</summary>
        public string SyncClientId()
        {
            var existing = GetSetting<string>("syncClientId", null);
            if (!string.IsNullOrEmpty(existing)) return existing;
            var id = "nuvio-uwp-" + RandomAlphanumeric(32);
            SetSetting("syncClientId", id);
            return id;
        }

        /// <summary>jellyfinDeviceId: 'nuvio-' + 24 hex chars, generated once if absent.</summary>
        public string JellyfinDeviceId()
        {
            var existing = GetSetting<string>("jellyfinDeviceId", null);
            if (!string.IsNullOrEmpty(existing)) return existing;
            var bytes = new byte[12];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var hex = new StringBuilder(24);
            foreach (var b in bytes) hex.Append(b.ToString("x2"));
            var id = "nuvio-" + hex.ToString();
            SetSetting("jellyfinDeviceId", id);
            return id;
        }

        private static string RandomAlphanumeric(int length)
        {
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var chars = new char[length];
            for (var i = 0; i < length; i++) chars[i] = Alphanumeric[bytes[i] % Alphanumeric.Length];
            return new string(chars);
        }
    }
}
