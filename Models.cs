using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LanCommanderDirect
{
    /// <summary>User-editable settings, stored as config.json in the plugin's data folder.</summary>
    public class LcConfig
    {
        public string ServerUrl { get; set; } = "http://192.168.1.104:1337";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";

        /// <summary>Folder games get installed into. Empty = %USERPROFILE%\Games\LANCommander</summary>
        public string InstallRoot { get; set; } = "";

        /// <summary>Only needed for non-zip archives. Full path to 7z.exe. Empty = auto-detect.</summary>
        public string SevenZipPath { get; set; } = "";

        /// <summary>Report play sessions (start/stop) to the server.</summary>
        public bool TrackPlayTime { get; set; } = true;

        /// <summary>Download and install new archives automatically when the server has a newer one. Off = update from the game menu.</summary>
        public bool AutoUpdate { get; set; } = false;

        /// <summary>Treat the server as the source of truth: overwrite description, developers, images... on every library update.</summary>
        public bool SyncMetadata { get; set; } = true;

        /// <summary>Show a popup when a game has been installed or updated (or an update failed).</summary>
        public bool PopupNotifications { get; set; } = true;

        /// <summary>Show the popup over everything, including running borderless/windowed games. Off = only inside Playnite's window.</summary>
        public bool PopupOverGames { get; set; } = true;

        /// <summary>Play a sound when the popup appears (only if a sound file is chosen).</summary>
        public bool PopupSound { get; set; } = true;

        /// <summary>Sound file (wav, mp3, wma...) played with the popup. Empty = silent.</summary>
        public string PopupSoundPath { get; set; } = "";

        /// <summary>Sound volume, 0-100.</summary>
        public int PopupSoundVolume { get; set; } = 70;

        /// <summary>Download trailer / micro trailer videos from the server into Extra Metadata.</summary>
        public bool SyncVideos { get; set; } = true;
    }

    public class LcNamed
    {
        [JsonProperty("name")] public string Name { get; set; }
    }

    /// <summary>
    /// Media types seen on the server: 0 = icon, 1 = cover, 2 = background, 4 = logo. Videos are recognised by
    /// their MIME type (the server's own "Video" type is also checked), so the exact number does not matter.
    /// </summary>
    public class LcMedia
    {
        [JsonProperty("name")] public string Name { get; set; }

        [JsonProperty("id")] public Guid Id { get; set; }
        [JsonProperty("fileId")] public Guid FileId { get; set; }
        [JsonProperty("type")] public int Type { get; set; }
        [JsonProperty("mimeType")] public string MimeType { get; set; }

        public bool IsVideo
        {
            get { return (MimeType ?? "").StartsWith("video/", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>A video whose name contains "micro" is the micro trailer, any other video is the trailer.</summary>
        public bool IsMicro
        {
            get { return (Name ?? "").IndexOf("micro", StringComparison.OrdinalIgnoreCase) >= 0; }
        }
    }

    public class LcArchive
    {
        [JsonProperty("id")] public Guid Id { get; set; }
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("compressedSize")] public long CompressedSize { get; set; }
        [JsonProperty("uncompressedSize")] public long UncompressedSize { get; set; }
        [JsonProperty("createdOn")] public DateTime? CreatedOn { get; set; }
    }

    /// <summary>
    /// A game as returned by /api/Games (light record) or /api/Games/{id} (full record,
    /// including archives, media, developers...).
    /// </summary>
    public class LcGame
    {
        [JsonProperty("id")] public Guid Id { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("releasedOn")] public DateTime? ReleasedOn { get; set; }
        [JsonProperty("singleplayer")] public bool Singleplayer { get; set; }

        [JsonProperty("archives")] public List<LcArchive> Archives { get; set; }
        [JsonProperty("media")] public List<LcMedia> Media { get; set; }
        [JsonProperty("developers")] public List<LcNamed> Developers { get; set; }
        [JsonProperty("publishers")] public List<LcNamed> Publishers { get; set; }
        [JsonProperty("genres")] public List<LcNamed> Genres { get; set; }
        [JsonProperty("tags")] public List<LcNamed> Tags { get; set; }

        /// <summary>Launch actions defined on the server. Shape not documented, so kept as raw JSON.</summary>
        [JsonProperty("actions")] public List<JObject> Actions { get; set; }

        /// <summary>The newest uploaded archive, or null if there is none.</summary>
        public LcArchive LatestArchive()
        {
            if (Archives == null) return null;
            LcArchive best = null;
            foreach (var a in Archives)
                if (best == null || (a.CreatedOn ?? DateTime.MinValue) > (best.CreatedOn ?? DateTime.MinValue))
                    best = a;
            return best;
        }
    }
}
