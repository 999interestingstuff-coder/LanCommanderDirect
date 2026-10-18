using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace LanCommanderDirect
{
    /// <summary>
    /// Small file written into every installed game folder. It lets the plugin recognise a game folder
    /// again later (scan) and know which archive is installed (update check).
    /// </summary>
    public class LcInstallMarker
    {
        public const string FileName = ".lc-game.json";

        [JsonProperty("gameId")] public string GameId { get; set; }
        [JsonProperty("archiveId")] public Guid? ArchiveId { get; set; }
        [JsonProperty("archiveVersion")] public string ArchiveVersion { get; set; }
        [JsonProperty("installedOn")] public DateTime InstalledOn { get; set; }

        public static LcInstallMarker Read(string dir)
        {
            try
            {
                var path = Path.Combine(dir, FileName);
                if (!File.Exists(path)) return null;
                var m = JsonConvert.DeserializeObject<LcInstallMarker>(File.ReadAllText(path));
                return m != null && !string.IsNullOrEmpty(m.GameId) ? m : null;
            }
            catch { return null; }
        }

        public static void Write(string dir, LcInstallMarker marker)
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, FileName), JsonConvert.SerializeObject(marker, Formatting.Indented));
            }
            catch { /* a read-only folder must not break an install */ }
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
