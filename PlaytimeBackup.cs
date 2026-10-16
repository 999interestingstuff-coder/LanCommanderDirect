using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace LanCommanderDirect
{
    /// <summary>One game's playtime in a backup file.</summary>
    public class PlaytimeEntry
    {
        /// <summary>The Playnite library the game came from (empty for games added by hand).</summary>
        [JsonProperty("pluginId")] public Guid PluginId { get; set; }

        /// <summary>The library's own id for the game (for this plugin, the LANCommander game id).</summary>
        [JsonProperty("gameId")] public string GameId { get; set; }

        /// <summary>Only used to match games added by hand, and to make the file readable.</summary>
        [JsonProperty("name")] public string Name { get; set; }

        [JsonProperty("playtimeSeconds")] public long PlaytimeSeconds { get; set; }
    }

    public class PlaytimeBackupFile
    {
        [JsonProperty("version")] public int Version { get; set; } = 1;
        [JsonProperty("exportedOn")] public DateTime ExportedOn { get; set; }
        [JsonProperty("games")] public List<PlaytimeEntry> Games { get; set; } = new List<PlaytimeEntry>();
    }

    public class PlaytimeImportResult
    {
        public int Raised;
        public int Unchanged;
        public int NoMatch;
        public int Total;
    }

    /// <summary>
    /// Saves the playtime of every game to a small JSON file and reads it back later (for example on a new PC).
    /// Importing only ever raises a playtime, so it can't wipe out time you have played since the backup.
    /// </summary>
    static class LcPlaytime
    {
        static readonly ILogger logger = LogManager.GetLogger();

        public static int Export(IPlayniteAPI api, string path)
        {
            var file = new PlaytimeBackupFile { ExportedOn = DateTime.UtcNow };

            foreach (var g in api.Database.Games.Where(x => x.Playtime > 0).OrderBy(x => x.Name))
            {
                file.Games.Add(new PlaytimeEntry
                {
                    PluginId = g.PluginId,
                    GameId = g.GameId,
                    Name = g.Name,
                    PlaytimeSeconds = (long)g.Playtime
                });
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonConvert.SerializeObject(file, Formatting.Indented));
            return file.Games.Count;
        }

        public static PlaytimeImportResult Import(IPlayniteAPI api, string path)
        {
            var file = JsonConvert.DeserializeObject<PlaytimeBackupFile>(File.ReadAllText(path));
            if (file == null || file.Games == null)
                throw new InvalidOperationException("This file is not a playtime backup.");

            var games = api.Database.Games.ToList();

            // games from a library: matched by library + the library's own id (stable across PCs)
            var byLibrary = games
                .Where(g => g.PluginId != Guid.Empty && !string.IsNullOrEmpty(g.GameId))
                .GroupBy(g => g.PluginId + "|" + g.GameId)
                .ToDictionary(x => x.Key, x => x.ToList());

            // games added by hand: matched by name, but only when the name is unique
            var byName = games
                .GroupBy(g => (g.Name ?? "").Trim().ToLowerInvariant())
                .ToDictionary(x => x.Key, x => x.ToList());

            var result = new PlaytimeImportResult { Total = file.Games.Count };
            var changed = new List<Game>();

            foreach (var entry in file.Games)
            {
                List<Game> targets = null;

                if (entry.PluginId != Guid.Empty && !string.IsNullOrEmpty(entry.GameId))
                    byLibrary.TryGetValue(entry.PluginId + "|" + entry.GameId, out targets);

                if (targets == null)
                {
                    List<Game> sameName;
                    if (byName.TryGetValue((entry.Name ?? "").Trim().ToLowerInvariant(), out sameName) && sameName.Count == 1)
                        targets = sameName;
                }

                if (targets == null || targets.Count == 0)
                {
                    result.NoMatch++;
                    continue;
                }

                bool raisedAny = false;
                foreach (var g in targets)
                {
                    if ((long)g.Playtime < entry.PlaytimeSeconds)
                    {
                        logger.Info("LANCommander: playtime import raised " + g.Name + " from " + g.Playtime + " s to " + entry.PlaytimeSeconds + " s");
                        g.Playtime = (ulong)entry.PlaytimeSeconds;
                        changed.Add(g);
                        raisedAny = true;
                    }
                }

                if (raisedAny) result.Raised++; else result.Unchanged++;
            }

            if (changed.Count > 0)
                api.Database.Games.Update(changed);

            return result;
        }
    }
}
