using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LanCommanderDirect
{
    /// <summary>What the plugin knows about one installed game: where it is and which archive it came from.</summary>
    public class LcInstallRecord
    {
        [JsonProperty("gameId")] public string GameId { get; set; }
        [JsonProperty("installDir")] public string InstallDir { get; set; }
        [JsonProperty("archiveId")] public Guid? ArchiveId { get; set; }
        [JsonProperty("archiveVersion")] public string ArchiveVersion { get; set; }
        [JsonProperty("installedOn")] public DateTime InstalledOn { get; set; }
    }

    /// <summary>
    /// Tracks installed games in one file (installs.json) in the plugin's own data folder, so nothing is written
    /// into the game folders. It is used to recognise a game folder again (scan) and to know which archive is
    /// installed (update check).
    /// </summary>
    static class LcInstallRegistry
    {
        static readonly object gate = new object();
        static string path;
        static Dictionary<string, LcInstallRecord> cache;

        /// <summary>Sets the file the registry lives in. Safe to call more than once.</summary>
        public static void Init(string filePath)
        {
            lock (gate)
            {
                if (path == filePath) return;
                path = filePath;
                cache = null;
            }
        }

        static Dictionary<string, LcInstallRecord> Load()
        {
            if (cache != null) return cache;

            cache = new Dictionary<string, LcInstallRecord>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (path != null && File.Exists(path))
                {
                    var d = JsonConvert.DeserializeObject<Dictionary<string, LcInstallRecord>>(File.ReadAllText(path));
                    if (d != null)
                        foreach (var kv in d) cache[kv.Key] = kv.Value;
                }
            }
            catch { /* a damaged file just means games are detected again by name */ }
            return cache;
        }

        static void Save()
        {
            try
            {
                if (path == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(cache, Formatting.Indented));
            }
            catch { /* tracking must never break an install */ }
        }

        public static LcInstallRecord Get(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return null;
            lock (gate)
            {
                LcInstallRecord r;
                return Load().TryGetValue(gameId, out r) ? r : null;
            }
        }

        public static void Set(LcInstallRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.GameId)) return;
            lock (gate)
            {
                Load()[record.GameId] = record;
                Save();
            }
        }

        public static void Remove(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return;
            lock (gate)
            {
                if (Load().Remove(gameId)) Save();
            }
        }
    }

    /// <summary>A media file from the server that has been downloaded into the plugin's cache.</summary>
    public class LcCachedFile
    {
        [JsonProperty("id")] public Guid Id { get; set; }
        [JsonProperty("path")] public string Path { get; set; }
    }

    /// <summary>
    /// What the server said about one game at the last library update. Saved to server-state.json so the plugin can
    /// apply it to games Playnite already has (Playnite itself only reads library metadata for new games).
    /// </summary>
    public class LcServerEntry
    {
        [JsonProperty("gameId")] public string GameId { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("releasedOn")] public DateTime? ReleasedOn { get; set; }
        [JsonProperty("singleplayer")] public bool Singleplayer { get; set; }

        [JsonProperty("developers")] public List<string> Developers { get; set; }
        [JsonProperty("publishers")] public List<string> Publishers { get; set; }
        [JsonProperty("genres")] public List<string> Genres { get; set; }
        [JsonProperty("tags")] public List<string> Tags { get; set; }

        [JsonProperty("icon")] public LcCachedFile Icon { get; set; }
        [JsonProperty("cover")] public LcCachedFile Cover { get; set; }
        [JsonProperty("background")] public LcCachedFile Background { get; set; }
        [JsonProperty("logo")] public LcCachedFile Logo { get; set; }
        [JsonProperty("trailer")] public LcCachedFile Trailer { get; set; }
        [JsonProperty("microTrailer")] public LcCachedFile MicroTrailer { get; set; }

        [JsonProperty("latestArchiveId")] public Guid? LatestArchiveId { get; set; }
        [JsonProperty("latestArchiveVersion")] public string LatestArchiveVersion { get; set; }
    }
}
