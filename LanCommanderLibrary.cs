using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace LanCommanderDirect
{
    public class LanCommanderLibrary : LibraryPlugin
    {
        static readonly ILogger logger = LogManager.GetLogger();

        public override Guid Id { get; } = Guid.Parse("7d2f4c1a-9b3e-4f60-8a15-c4e7d9b02f36");
        public override string Name => "LANCommander (Direct)";

        public LanCommanderLibrary(IPlayniteAPI api) : base(api)
        {
            Properties = new LibraryPluginProperties { HasSettings = true };
            LcInstallRegistry.Init(Path.Combine(GetPluginUserDataPath(), "installs.json"));
        }

        // ---------- config ----------

        string ConfigPath => Path.Combine(GetPluginUserDataPath(), "config.json");

        /// <summary>Which variants (e.g. a VR build) belong to which base game. Rewritten on every library update.</summary>
        string VariantsPath => Path.Combine(GetPluginUserDataPath(), "variants.json");

        /// <summary>Reads config.json without any checks or notifications (used by the settings page).</summary>
        public LcConfig ReadRawConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var c = JsonConvert.DeserializeObject<LcConfig>(File.ReadAllText(ConfigPath));
                    if (c != null) return c;
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "Could not read LANCommander config.json");
            }
            return new LcConfig();
        }

        public void WriteConfig(LcConfig cfg)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(cfg, Formatting.Indented));
        }

        // ---------- settings page ----------

        LcSettings settingsInstance;

        LcSettings GetOrCreateSettings()
        {
            return settingsInstance ?? (settingsInstance = new LcSettings(this));
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return GetOrCreateSettings();
        }

        public override System.Windows.Controls.UserControl GetSettingsView(bool firstRunSettings)
        {
            return new LcSettingsView(GetOrCreateSettings(), PlayniteApi);
        }

        /// <summary>Same as LoadConfig, but never creates files or shows notifications. Null if not filled in yet.</summary>
        public LcConfig LoadConfigQuiet()
        {
            var cfg = ReadRawConfig();
            if (string.IsNullOrWhiteSpace(cfg.ServerUrl) || string.IsNullOrWhiteSpace(cfg.Username) || string.IsNullOrWhiteSpace(cfg.Password))
                return null;

            if (string.IsNullOrWhiteSpace(cfg.InstallRoot))
                cfg.InstallRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "LANCommander");
            return cfg;
        }

        /// <summary>Loads config.json (creating a template on first run). Returns null if not filled in yet.</summary>
        public LcConfig LoadConfig()
        {
            var path = ConfigPath;
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(new LcConfig(), Formatting.Indented));
            }

            LcConfig cfg = null;
            try { cfg = JsonConvert.DeserializeObject<LcConfig>(File.ReadAllText(path)); }
            catch (Exception e) { logger.Error(e, "Could not read LANCommander config"); }

            if (cfg == null || string.IsNullOrWhiteSpace(cfg.ServerUrl) ||
                string.IsNullOrWhiteSpace(cfg.Username) || string.IsNullOrWhiteSpace(cfg.Password))
            {
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "lc-config",
                    "LANCommander: enter your server URL, username and password under Add-ons > Libraries > LANCommander (Direct) > Settings, then update the library again.",
                    NotificationType.Info));
                return null;
            }

            if (string.IsNullOrWhiteSpace(cfg.InstallRoot))
            {
                cfg.InstallRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "LANCommander");
            }
            return cfg;
        }

        // ---------- server snapshot (server-state.json) ----------

        string StatePath { get { return Path.Combine(GetPluginUserDataPath(), "server-state.json"); } }
        string AppliedPath { get { return Path.Combine(GetPluginUserDataPath(), "applied.json"); } }

        public Dictionary<string, LcServerEntry> LoadState()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    var d = JsonConvert.DeserializeObject<Dictionary<string, LcServerEntry>>(File.ReadAllText(StatePath));
                    if (d != null) return d;
                }
            }
            catch (Exception e) { logger.Warn(e, "LANCommander: could not read server-state.json"); }
            return new Dictionary<string, LcServerEntry>();
        }

        /// <summary>Which server media file was last applied to which Playnite game ("gameId|kind" -> media id).</summary>
        Dictionary<string, string> LoadApplied()
        {
            try
            {
                if (File.Exists(AppliedPath))
                {
                    var d = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(AppliedPath));
                    if (d != null) return d;
                }
            }
            catch (Exception e) { logger.Warn(e, "LANCommander: could not read applied.json"); }
            return new Dictionary<string, string>();
        }

        void SaveApplied(Dictionary<string, string> applied)
        {
            try { File.WriteAllText(AppliedPath, JsonConvert.SerializeObject(applied)); }
            catch (Exception e) { logger.Warn(e, "LANCommander: could not write applied.json"); }
        }

        // ---------- library import ----------

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var cfg = LoadConfig();
            if (cfg == null)
                return new List<GameMetadata>();

            // snapshot of games we already installed, so a library update never flips them back to "not installed"
            var installed = new Dictionary<string, string>();
            foreach (var g in PlayniteApi.Database.Games)
            {
                if (g.PluginId == Id && g.IsInstalled && !string.IsNullOrEmpty(g.GameId))
                    installed[g.GameId] = g.InstallDirectory;
            }

            var mediaDir = Path.Combine(GetPluginUserDataPath(), "media");
            var previous = LoadState();

            try
            {
                var games = Task.Run(() => FetchAsync(cfg, installed, previous, mediaDir, args.CancelToken)).GetAwaiter().GetResult();
                NoteServerOk(cfg);
                return games;
            }
            catch (LcServerUnreachableException e)
            {
                // not an error of the plugin: the server is off or out of reach. Installed games keep working and nothing is removed.
                logger.Warn(e, "LANCommander server unreachable during the library update");
                try { PlayniteApi.Notifications.Remove("lc-import-error"); } catch { }
                NoteServerUnreachable(cfg, true);
                return new List<GameMetadata>();
            }
            catch (Exception e)
            {
                logger.Error(e, "LANCommander import failed");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "lc-import-error", "LANCommander import failed: " + e.Message, NotificationType.Error));
                return new List<GameMetadata>();
            }
        }

        static LcCachedFile Cached(LcMedia media, string path)
        {
            return media == null || path == null ? null : new LcCachedFile { Id = media.Id, Path = path };
        }

        static List<string> Names(List<LcNamed> list)
        {
            if (list == null) return null;
            return list.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.Name.Trim()).ToList();
        }

        async Task<List<GameMetadata>> FetchAsync(LcConfig cfg, Dictionary<string, string> installed,
            Dictionary<string, LcServerEntry> previous, string mediaDir, CancellationToken ct)
        {
            Directory.CreateDirectory(mediaDir);
            var result = new List<GameMetadata>();
            var state = new Dictionary<string, LcServerEntry>();

            // server games that are variants of another game (e.g. "Portal 2 (VR)"): not imported as games of their own,
            // they become extra launch options of the base game
            var variantGames = new List<Tuple<LcGame, string>>();
            var variantMap = new Dictionary<string, List<LcVariant>>();

            using (var client = new LcClient(cfg))
            {
                var games = await client.GetGamesAsync(ct).ConfigureAwait(false);

                // the list only has light records; the detail call adds media, developers, genres, tags...
                var loaded = new List<Tuple<LcGame, bool>>();
                foreach (var item in games)
                {
                    var g = item;
                    bool detailOk = false;
                    try
                    {
                        var detail = await client.GetGameAsync(item.Id, ct).ConfigureAwait(false);
                        if (detail != null) { g = detail; detailOk = true; }
                    }
                    catch (Exception e)
                    {
                        logger.Warn(e, "Could not load LANCommander details for " + item.Id);
                    }
                    loaded.Add(Tuple.Create(g, detailOk));
                }

                // every title on the server, so "Portal 2 (VR)" can be recognised as a variant of "Portal 2"
                var allTitles = loaded.Where(x => !string.IsNullOrWhiteSpace(x.Item1.Title)).Select(x => x.Item1.Title.Trim()).ToList();

                foreach (var entryPair in loaded)
                {
                    var g = entryPair.Item1;
                    bool detailOk = entryPair.Item2;

                    var variantOf = LcVariants.BaseTitle(g, allTitles);
                    if (variantOf != null && string.Equals(variantOf, (g.Title ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                        variantOf = null;   // a game can't be a variant of itself
                    if (variantOf != null)
                    {
                        variantGames.Add(Tuple.Create(g, variantOf));
                        continue;
                    }

                    var gameId = g.Id.ToString();
                    var meta = new GameMetadata
                    {
                        GameId = gameId,
                        Name = !string.IsNullOrWhiteSpace(g.Title) ? g.Title : "Game " + gameId,
                        Source = new MetadataNameProperty("LANCommander"),
                        Description = g.Description,
                        Version = g.Version,
                        IsInstalled = false,
                        Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") }
                    };

                    string dir;
                    if (installed.TryGetValue(gameId, out dir))
                    {
                        meta.IsInstalled = true;
                        meta.InstallDirectory = dir;
                    }

                    if (g.ReleasedOn.HasValue)
                        meta.ReleaseDate = new ReleaseDate(g.ReleasedOn.Value);

                    meta.Developers = ToProps(g.Developers);
                    meta.Publishers = ToProps(g.Publishers);
                    meta.Genres = ToProps(g.Genres);
                    meta.Tags = ToProps(g.Tags);
                    if (g.Singleplayer)
                        meta.Features = new HashSet<MetadataProperty> { new MetadataNameProperty("Single Player") };

                    // if the detail call failed, keep what the last good update knew instead of recording an empty game
                    LcServerEntry old;
                    if (!detailOk && previous.TryGetValue(gameId, out old))
                    {
                        state[gameId] = old;
                        result.Add(meta);
                        continue;
                    }

                    var entry = new LcServerEntry
                    {
                        GameId = gameId,
                        Title = g.Title,
                        Description = g.Description,
                        Version = g.Version,
                        ReleasedOn = g.ReleasedOn,
                        Singleplayer = g.Singleplayer,
                        Developers = Names(g.Developers),
                        Publishers = Names(g.Publishers),
                        Genres = Names(g.Genres),
                        Tags = Names(g.Tags)
                    };

                    var latest = g.LatestArchive();
                    if (latest != null)
                    {
                        entry.LatestArchiveId = latest.Id;
                        entry.LatestArchiveVersion = latest.Version;
                    }

                    // images: type 0 = icon, 1 = cover, 2 = background
                    var iconM = FirstOfType(g, 0);
                    var icon = await CacheMediaAsync(client, iconM, mediaDir, ct).ConfigureAwait(false);
                    if (icon != null) { meta.Icon = new MetadataFile(icon); entry.Icon = Cached(iconM, icon); }

                    var coverM = FirstOfType(g, 1);
                    var cover = await CacheMediaAsync(client, coverM, mediaDir, ct).ConfigureAwait(false);
                    if (cover != null) { meta.CoverImage = new MetadataFile(cover); entry.Cover = Cached(coverM, cover); }

                    var bgM = FirstOfType(g, 2);
                    var bg = await CacheMediaAsync(client, bgM, mediaDir, ct).ConfigureAwait(false);
                    if (bg != null) { meta.BackgroundImage = new MetadataFile(bg); entry.Background = Cached(bgM, bg); }

                    // type 4 = logo; Playnite has no logo field, so it is copied into Extra Metadata after the import
                    var logoM = FirstOfType(g, 4);
                    var logo = await CacheMediaAsync(client, logoM, mediaDir, ct).ConfigureAwait(false);
                    if (logo != null && logo.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        entry.Logo = Cached(logoM, logo);

                    // videos: a video whose name contains "micro" is the micro trailer, any other video is the trailer
                    if (cfg.SyncVideos && g.Media != null)
                    {
                        var videos = g.Media.Where(m => m.IsVideo).ToList();
                        var microM = videos.FirstOrDefault(m => m.IsMicro);
                        var trailerM = videos.FirstOrDefault(m => !m.IsMicro);

                        var trailer = await CacheMediaAsync(client, trailerM, mediaDir, ct).ConfigureAwait(false);
                        if (trailer != null) entry.Trailer = Cached(trailerM, trailer);

                        var micro = await CacheMediaAsync(client, microM, mediaDir, ct).ConfigureAwait(false);
                        if (micro != null) entry.MicroTrailer = Cached(microM, micro);
                    }

                    state[gameId] = entry;
                    result.Add(meta);
                }

                // link every variant to its base game (matched by title)
                foreach (var pair in variantGames)
                {
                    var vg = pair.Item1;
                    var baseMeta = result.FirstOrDefault(m => string.Equals(m.Name, pair.Item2, StringComparison.OrdinalIgnoreCase));
                    if (baseMeta == null)
                    {
                        logger.Warn("LANCommander: variant '" + vg.Title + "' is tagged for '" + pair.Item2 + "', but no game with that title exists");
                        continue;
                    }

                    var actions = vg.Actions;
                    if (actions == null || actions.Count == 0)
                    {
                        try
                        {
                            var manifest = await client.GetManifestAsync(vg.Id, ct).ConfigureAwait(false);
                            if (manifest != null) actions = manifest.Actions;
                        }
                        catch (Exception e)
                        {
                            logger.Warn(e, "LANCommander: could not read the manifest of variant " + vg.Title);
                        }
                    }

                    List<LcVariant> list;
                    if (!variantMap.TryGetValue(baseMeta.GameId, out list))
                        variantMap[baseMeta.GameId] = list = new List<LcVariant>();
                    list.Add(LcVariants.Build(vg, baseMeta.GameId, actions));
                    logger.Info("LANCommander: '" + vg.Title + "' is a variant of '" + baseMeta.Name + "' and will not be imported as its own game");
                }
            }

            try
            {
                File.WriteAllText(StatePath, JsonConvert.SerializeObject(state, Formatting.Indented));
            }
            catch (Exception e) { logger.Warn(e, "Could not write server-state.json"); }

            try
            {
                LcVariants.KeepResolved(variantMap, LcVariants.Load(VariantsPath));
                LcVariants.Save(VariantsPath, variantMap);
            }
            catch (Exception e) { logger.Warn(e, "Could not write variants.json"); }

            return result;
        }

        // ---------- after a library update (and on startup) ----------

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            LcInstallRegistry.Init(Path.Combine(GetPluginUserDataPath(), "installs.json"));
            RunPostSync();

            // tell the user early if the server can't be reached (never with no credentials entered: LoadConfigQuiet is null then)
            var cfg = LoadConfigQuiet();
            if (cfg != null)
                Task.Run(() => PingServer(cfg, true));
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            RunPostSync();
        }

        void RunPostSync()
        {
            var cfg = LoadConfigQuiet();
            if (cfg == null) return;

            try { ApplyServerState(cfg); }
            catch (Exception e) { logger.Warn(e, "LANCommander: applying server metadata failed"); }

            try { ScanInstalled(cfg); }
            catch (Exception e) { logger.Warn(e, "LANCommander: scanning for installed games failed"); }

            SyncVariantActions();

            try { CheckUpdates(cfg, false); }
            catch (Exception e) { logger.Warn(e, "LANCommander: update check failed"); }
        }

        // ---------- server is the source of truth: logos, videos and Playnite fields ----------

        void ApplyServerState(LcConfig cfg)
        {
            var state = LoadState();
            if (state.Count == 0) return;

            var applied = LoadApplied();
            bool dirty = false;
            var root = Path.Combine(PlayniteApi.Paths.ConfigurationPath, "ExtraMetadata", "games");
            var changed = new List<Game>();

            foreach (var game in PlayniteApi.Database.Games.Where(g => g.PluginId == Id).ToList())
            {
                LcServerEntry e;
                if (string.IsNullOrEmpty(game.GameId) || !state.TryGetValue(game.GameId, out e))
                    continue;

                // Extra Metadata layout: ExtraMetadata\games\<Playnite game id>\Logo.png, VideoTrailer.mp4, VideoMicrotrailer.mp4
                var dir = Path.Combine(root, game.Id.ToString());
                CopyExtra(game, e.Logo, dir, "Logo.png", applied, ref dirty);
                if (cfg.SyncVideos)
                {
                    CopyExtra(game, e.Trailer, dir, "VideoTrailer.mp4", applied, ref dirty);
                    CopyExtra(game, e.MicroTrailer, dir, "VideoMicrotrailer.mp4", applied, ref dirty);
                }

                if (cfg.SyncMetadata && ApplyFields(game, e, applied, ref dirty))
                    changed.Add(game);
            }

            if (changed.Count > 0)
                PlayniteApi.Database.Games.Update(changed);
            if (dirty)
                SaveApplied(applied);
        }

        /// <summary>Copies a cached server file into Extra Metadata when the server's file is new or the target is missing.</summary>
        void CopyExtra(Game game, LcCachedFile file, string dir, string targetName, Dictionary<string, string> applied, ref bool dirty)
        {
            try
            {
                if (file == null || string.IsNullOrEmpty(file.Path) || !File.Exists(file.Path)) return;

                var dest = Path.Combine(dir, targetName);
                var key = game.Id + "|" + targetName;
                string last;
                applied.TryGetValue(key, out last);
                if (File.Exists(dest) && last == file.Id.ToString()) return;

                Directory.CreateDirectory(dir);
                File.Copy(file.Path, dest, true);
                applied[key] = file.Id.ToString();
                dirty = true;
                logger.Info("LANCommander: placed " + targetName + " for " + game.Name);
            }
            catch (Exception e)
            {
                logger.Warn(e, "LANCommander: could not place " + targetName + " for " + game.Name);
            }
        }

        /// <summary>Overwrites the Playnite game's fields with the server's. Returns true if anything changed.</summary>
        bool ApplyFields(Game game, LcServerEntry e, Dictionary<string, string> applied, ref bool dirty)
        {
            bool changed = false;
            var db = PlayniteApi.Database;

            if (!string.IsNullOrWhiteSpace(e.Title) && game.Name != e.Title) { game.Name = e.Title; changed = true; }
            if (!string.IsNullOrWhiteSpace(e.Description) && game.Description != e.Description) { game.Description = e.Description; changed = true; }
            if (!string.IsNullOrWhiteSpace(e.Version) && game.Version != e.Version) { game.Version = e.Version; changed = true; }

            if (e.ReleasedOn.HasValue && e.ReleasedOn.Value.Year > 1900)
            {
                var rd = new ReleaseDate(e.ReleasedOn.Value);
                if (!game.ReleaseDate.HasValue || !game.ReleaseDate.Value.Equals(rd)) { game.ReleaseDate = rd; changed = true; }
            }

            // developers, publishers and genres follow the server; tags and features are only added to, so your own tags survive
            changed |= SyncIds(game.DeveloperIds, v => game.DeveloperIds = v, db.Companies, e.Developers, true);
            changed |= SyncIds(game.PublisherIds, v => game.PublisherIds = v, db.Companies, e.Publishers, true);
            changed |= SyncIds(game.GenreIds, v => game.GenreIds = v, db.Genres, e.Genres, true);
            changed |= SyncIds(game.TagIds, v => game.TagIds = v, db.Tags, e.Tags, false);
            if (e.Singleplayer)
                changed |= SyncIds(game.FeatureIds, v => game.FeatureIds = v, db.Features, new List<string> { "Single Player" }, false);

            changed |= SyncImage(game, e.Icon, "icon", () => game.Icon, v => game.Icon = v, applied, ref dirty);
            changed |= SyncImage(game, e.Cover, "cover", () => game.CoverImage, v => game.CoverImage = v, applied, ref dirty);
            changed |= SyncImage(game, e.Background, "background", () => game.BackgroundImage, v => game.BackgroundImage = v, applied, ref dirty);

            return changed;
        }

        bool SyncIds<T>(List<Guid> current, Action<List<Guid>> set, IItemCollection<T> collection, List<string> names, bool replace)
            where T : DatabaseObject
        {
            if (names == null || names.Count == 0) return false;

            var wanted = new List<Guid>();
            foreach (var name in names)
            {
                var item = collection.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) ?? collection.Add(name);
                wanted.Add(item.Id);
            }

            var cur = current ?? new List<Guid>();
            var result = replace ? wanted : cur.Union(wanted).ToList();
            if (new HashSet<Guid>(result).SetEquals(cur)) return false;

            set(result);
            return true;
        }

        bool SyncImage(Game game, LcCachedFile file, string kind, Func<string> get, Action<string> set,
            Dictionary<string, string> applied, ref bool dirty)
        {
            if (file == null || string.IsNullOrEmpty(file.Path) || !File.Exists(file.Path)) return false;

            var key = game.Id + "|" + kind;
            string last;
            applied.TryGetValue(key, out last);
            if (last == file.Id.ToString() && !string.IsNullOrEmpty(get())) return false;

            try
            {
                var newId = PlayniteApi.Database.AddFile(file.Path, game.Id);
                var old = get();
                if (!string.IsNullOrEmpty(old))
                {
                    try { PlayniteApi.Database.RemoveFile(old); } catch { }
                }
                set(newId);
                applied[key] = file.Id.ToString();
                dirty = true;
                return true;
            }
            catch (Exception e)
            {
                logger.Warn(e, "LANCommander: could not set the " + kind + " image for " + game.Name);
                return false;
            }
        }

        // ---------- detect games that are already installed in the install folder ----------

        class DetectedGame
        {
            public Game Game;
            public string Dir;
            public GameAction Action;
        }

        /// <summary>
        /// Looks through the install folder for game folders Playnite doesn't know are installed. A folder is
        /// recognised from the plugin's install records, or (for games installed by hand or on another PC) by its name.
        /// Returns how many games were found; the library is updated in the background.
        /// </summary>
        public int ScanInstalled(LcConfig cfg)
        {
            var state = LoadState();
            var games = PlayniteApi.Database.Games.Where(g => g.PluginId == Id).ToList();

            // installed games the plugin has no record of yet: assume they match the newest archive
            foreach (var g in games.Where(x => x.IsInstalled && !string.IsNullOrEmpty(x.InstallDirectory) && !string.IsNullOrEmpty(x.GameId)))
            {
                try
                {
                    LcServerEntry e;
                    if (LcInstallRegistry.Get(g.GameId) != null) continue;
                    if (!state.TryGetValue(g.GameId, out e) || !e.LatestArchiveId.HasValue) continue;

                    LcInstallRegistry.Set(new LcInstallRecord
                    {
                        GameId = g.GameId,
                        InstallDir = g.InstallDirectory,
                        ArchiveId = e.LatestArchiveId,
                        ArchiveVersion = e.LatestArchiveVersion,
                        InstalledOn = DateTime.UtcNow
                    });
                }
                catch (Exception ex) { logger.Warn(ex, "LANCommander: could not record the install of " + g.Name); }
            }

            var missing = games.Where(g => !g.IsInstalled && !string.IsNullOrEmpty(g.GameId)).ToList();
            if (missing.Count == 0)
                return 0;

            var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // games the plugin installed before (for example after Playnite lost the install state): the record says where they are
            foreach (var g in missing)
            {
                var r = LcInstallRegistry.Get(g.GameId);
                if (r != null && !string.IsNullOrEmpty(r.InstallDir) && Directory.Exists(r.InstallDir))
                    byId[g.GameId] = r.InstallDir;
            }

            // everything else: a folder in the install folder named like the game
            if (!string.IsNullOrWhiteSpace(cfg.InstallRoot) && Directory.Exists(cfg.InstallRoot))
            {
                var wanted = new HashSet<string>(
                    missing.Where(g => !byId.ContainsKey(g.GameId)).Select(g => LcPaths.Sanitize(g.Name)),
                    StringComparer.OrdinalIgnoreCase);

                if (wanted.Count > 0)
                {
                    foreach (var dir in Directory.GetDirectories(cfg.InstallRoot))
                    {
                        var folder = Path.GetFileName(dir.TrimEnd('\\'));
                        if (folder.StartsWith(".") || !wanted.Contains(folder)) continue;

                        // only trust a folder that really contains a program
                        var candidate = dir;
                        if (SafeFiles(dir).Length == 0)
                        {
                            var subs = SafeDirectories(dir);
                            if (subs.Length == 1) candidate = subs[0];   // older "Game\Game\" layout
                        }
                        if (HasExe(candidate)) byName[folder] = candidate;
                    }
                }
            }

            var found = new List<DetectedGame>();
            foreach (var g in missing)
            {
                string dir;
                if (!byId.TryGetValue(g.GameId, out dir) && !byName.TryGetValue(LcPaths.Sanitize(g.Name), out dir))
                    continue;
                found.Add(new DetectedGame { Game = g, Dir = dir.TrimEnd('\\') });
            }

            if (found.Count == 0) return 0;

            Task.Run(() => FinishDetection(cfg, found, state));
            return found.Count;
        }

        static string[] SafeDirectories(string dir)
        {
            try { return Directory.GetDirectories(dir); } catch { return new string[0]; }
        }

        static string[] SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir); } catch { return new string[0]; }
        }

        static bool HasExe(string dir)
        {
            try { return Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories).Any(); }
            catch { return false; }
        }

        void FinishDetection(LcConfig cfg, List<DetectedGame> found, Dictionary<string, LcServerEntry> state)
        {
            try
            {
                using (var client = new LcClient(cfg))
                {
                    foreach (var d in found)
                    {
                        LcGame info = null;
                        try
                        {
                            var id = Guid.Parse(d.Game.GameId);
                            info = client.GetGameAsync(id, CancellationToken.None).GetAwaiter().GetResult();
                            try
                            {
                                var manifest = client.GetManifestAsync(id, CancellationToken.None).GetAwaiter().GetResult();
                                if ((info.Actions == null || info.Actions.Count == 0) && manifest != null && manifest.Actions != null && manifest.Actions.Count > 0)
                                    info.Actions = manifest.Actions;
                            }
                            catch (Exception e) { logger.Warn(e, "LANCommander: could not read the manifest for " + d.Game.Name); }
                        }
                        catch (Exception e)
                        {
                            logger.Warn(e, "LANCommander: could not load server details for " + d.Game.Name + ", guessing the exe instead");
                        }

                        try { d.Action = LcLaunch.BuildPlayAction(d.Dir, info); }
                        catch (Exception e) { logger.Warn(e, "LANCommander: could not work out a play action for " + d.Game.Name); }

                        // a folder found by name is assumed to hold the newest archive; an existing record is kept
                        var existing = LcInstallRegistry.Get(d.Game.GameId);
                        if (existing == null || existing.InstallDir != d.Dir)
                        {
                            var latest = info != null ? info.LatestArchive() : null;
                            LcServerEntry e;
                            state.TryGetValue(d.Game.GameId, out e);
                            LcInstallRegistry.Set(new LcInstallRecord
                            {
                                GameId = d.Game.GameId,
                                InstallDir = d.Dir,
                                ArchiveId = existing != null && existing.ArchiveId.HasValue ? existing.ArchiveId
                                    : (latest != null ? latest.Id : (e != null ? e.LatestArchiveId : null)),
                                ArchiveVersion = existing != null && existing.ArchiveId.HasValue ? existing.ArchiveVersion
                                    : (latest != null ? latest.Version : (e != null ? e.LatestArchiveVersion : null)),
                                InstalledOn = DateTime.UtcNow
                            });
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "LANCommander: detection of installed games hit an error");
            }

            PlayniteApi.MainView.UIDispatcher.Invoke(() =>
            {
                var updated = new List<Game>();
                foreach (var d in found)
                {
                    var g = d.Game;
                    if (g.IsInstalled) continue;   // installed in the meantime

                    g.InstallDirectory = d.Dir;
                    g.IsInstalled = true;

                    if (d.Action != null)
                    {
                        if (g.GameActions == null)
                            g.GameActions = new ObservableCollection<GameAction>();
                        if (!g.GameActions.Any(a => a.IsPlayAction))
                            g.GameActions.Add(d.Action);
                    }
                    updated.Add(g);
                }

                if (updated.Count == 0) return;

                PlayniteApi.Database.Games.Update(updated);
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "lc-detected",
                    "LANCommander: found " + updated.Count + " installed game(s) in the install folder: " +
                    string.Join(", ", updated.Select(x => x.Name).Take(8)) + (updated.Count > 8 ? ", ..." : ""),
                    NotificationType.Info));
            });

            SyncVariantActions();
        }

        // ---------- updates ----------

        /// <summary>Installed games whose installed archive differs from the newest one on the server (as of the last library update).</summary>
        public List<Game> FindOutdatedGames()
        {
            var state = LoadState();
            var outdated = new List<Game>();

            foreach (var g in PlayniteApi.Database.Games.Where(x => x.PluginId == Id && x.IsInstalled))
            {
                if (string.IsNullOrEmpty(g.GameId) || string.IsNullOrEmpty(g.InstallDirectory) || !Directory.Exists(g.InstallDirectory))
                    continue;

                LcServerEntry e;
                if (!state.TryGetValue(g.GameId, out e) || !e.LatestArchiveId.HasValue) continue;

                var m = LcInstallRegistry.Get(g.GameId);
                if (m == null || !m.ArchiveId.HasValue) continue;

                if (m.ArchiveId.Value != e.LatestArchiveId.Value)
                    outdated.Add(g);
            }
            return outdated;
        }

        readonly HashSet<Guid> popupAnnounced = new HashSet<Guid>();

        /// <summary>Tells the user which games have updates and, if auto-update is on, starts them. Returns the games found.</summary>
        public List<Game> CheckUpdates(LcConfig cfg, bool interactive)
        {
            var outdated = FindOutdatedGames();

            if (outdated.Count == 0)
            {
                PlayniteApi.Notifications.Remove("lc-updates");
                return outdated;
            }

            var names = string.Join(", ", outdated.Select(x => x.Name).Take(8)) + (outdated.Count > 8 ? ", ..." : "");

            if (cfg.AutoUpdate)
            {
                PlayniteApi.Notifications.Remove("lc-updates");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "lc-updates", "LANCommander: updating " + outdated.Count + " game(s): " + names, NotificationType.Info));
                LcUpdater.Enqueue(PlayniteApi, this, outdated);
            }
            else
            {
                PlayniteApi.Notifications.Remove("lc-updates");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "lc-updates",
                    "LANCommander: updates available for " + outdated.Count + " game(s): " + names +
                    ". Right-click the game > LANCommander > Update.",
                    NotificationType.Info));

                // one popup per game (at most three) the first time this session a game is found outdated
                if (!interactive && cfg.PopupNotifications)
                {
                    foreach (var g in outdated.Where(x => popupAnnounced.Add(x.Id)).Take(3))
                        LcToast.Show(PlayniteApi, g, LcToastKind.UpdateAvailable, null, cfg);
                }
            }
            return outdated;
        }

        static HashSet<MetadataProperty> ToProps(List<LcNamed> list)
        {
            if (list == null) return null;
            var props = list.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Name))
                            .Select(x => (MetadataProperty)new MetadataNameProperty(x.Name))
                            .ToList();
            return props.Count == 0 ? null : new HashSet<MetadataProperty>(props);
        }

        static LcMedia FirstOfType(LcGame g, int type)
        {
            return g.Media == null ? null : g.Media.FirstOrDefault(m => m.Type == type);
        }

        /// <summary>Playnite fetches image URLs without auth headers, so cache them locally first (videos are cached the same way).</summary>
        static async Task<string> CacheMediaAsync(LcClient client, LcMedia media, string mediaDir, CancellationToken ct)
        {
            if (media == null) return null;

            // already cached (any extension)?
            var existing = Directory.GetFiles(mediaDir, media.Id + ".*")
                .FirstOrDefault(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && new FileInfo(f).Length > 0);
            if (existing != null) return existing;

            var tmp = Path.Combine(mediaDir, media.Id + ".tmp");
            try
            {
                await client.DownloadMediaAsync(media.Id, tmp, ct).ConfigureAwait(false);

                var ext = SniffExtension(tmp);
                var final = Path.Combine(mediaDir, media.Id + ext);
                if (File.Exists(final)) File.Delete(final);
                File.Move(tmp, final);
                return final;
            }
            catch (Exception e)
            {
                logger.Warn(e, "Could not download LANCommander media " + media.Id);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return null;
            }
        }

        static string SniffExtension(string path)
        {
            var b = new byte[12];
            using (var fs = File.OpenRead(path))
                fs.Read(b, 0, b.Length);

            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
            if (b[0] == 0xFF && b[1] == 0xD8) return ".jpg";
            if (b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01 && b[3] == 0x00) return ".ico";
            if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return ".gif";
            if (b[4] == 0x66 && b[5] == 0x74 && b[6] == 0x79 && b[7] == 0x70) return ".mp4";          // "ftyp"
            if (b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3) return ".webm";
            if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46) return ".webp";
            return ".png";
        }

        // ---------- server reachable or not ----------

        /// <summary>The server answered: clears the "unreachable" notice and says so once if it was marked unreachable. Does nothing if nothing is configured.</summary>
        public void NoteServerOk(LcConfig cfg)
        {
            if (cfg == null) return;
            if (!LcServerStatus.SetOnline()) return;

            try { PlayniteApi.Notifications.Remove("lc-offline"); } catch { }
            if (cfg.PopupNotifications)
                LcToast.Show(PlayniteApi, null, LcToastKind.ServerOnline, null, cfg);
        }

        /// <summary>
        /// The server could not be reached. Shows one notice in the panel and, when it has just changed from reachable and
        /// announce is set, the popup. Does nothing if nothing is configured, and a wrong password never counts as unreachable.
        /// </summary>
        public void NoteServerUnreachable(LcConfig cfg, bool announce)
        {
            if (cfg == null) return;
            var changed = LcServerStatus.SetOffline();

            try
            {
                PlayniteApi.Notifications.Remove("lc-offline");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "lc-offline",
                    "LANCommander: can't reach the server at " + cfg.ServerUrl.TrimEnd('/') + ". Installed games still launch. " +
                    "Installing, updating and library updates need the server; they work again once it is back (check it from the LANCommander menu).",
                    NotificationType.Error));
            }
            catch { }

            if (changed && announce && cfg.PopupNotifications)
                LcToast.Show(PlayniteApi, null, LcToastKind.ServerOffline, "Installed games still work", cfg);
        }

        void PingServer(LcConfig cfg, bool announce)
        {
            try
            {
                using (var client = new LcClient(cfg, 8))
                    Task.Run(() => client.PingAsync(CancellationToken.None)).GetAwaiter().GetResult();
                NoteServerOk(cfg);
            }
            catch (LcServerUnreachableException e)
            {
                logger.Warn(e, "LANCommander server unreachable");
                NoteServerUnreachable(cfg, announce);
            }
            catch (Exception e)
            {
                // the server answered (for example it refused the login), so it is reachable
                logger.Warn(e, "LANCommander server check");
                NoteServerOk(cfg);
            }
        }

        void CheckConnectionFromMenu()
        {
            var cfg = LoadConfig();
            if (cfg == null) return;

            string message = null;
            bool ok = false;
            PlayniteApi.Dialogs.ActivateGlobalProgress(p =>
            {
                try
                {
                    using (var client = new LcClient(cfg, 10))
                        Task.Run(() => client.PingAsync(p.CancelToken)).GetAwaiter().GetResult();
                    ok = true;
                    NoteServerOk(cfg);
                }
                catch (OperationCanceledException) { }
                catch (LcServerUnreachableException e)
                {
                    message = e.Message;
                    NoteServerUnreachable(cfg, false);
                }
                catch (Exception e)
                {
                    message = "The server answered, but: " + e.Message;
                    NoteServerOk(cfg);
                }
            }, new GlobalProgressOptions("Checking the LANCommander server...", true) { IsIndeterminate = true });

            if (ok)
                PlayniteApi.Dialogs.ShowMessage("Connected to " + cfg.ServerUrl.TrimEnd('/') + " and logged in.", "LANCommander");
            else if (message != null)
                PlayniteApi.Dialogs.ShowErrorMessage(message, "LANCommander");
        }

        // ---------- variants (separate full copies such as a VR build, shown as extra launch options) ----------

        /// <summary>
        /// Removes variants that were imported as games of their own earlier (Playnite never deletes a game that stops
        /// showing up in an import) and makes sure every installed base game has a launch option for each variant.
        /// </summary>
        void SyncVariantActions()
        {
            try
            {
                var map = LcVariants.Load(VariantsPath);
                if (map.Count == 0) return;

                var cfg = LoadConfigQuiet();
                if (cfg == null) return;

                var variantIds = new HashSet<string>(
                    map.Values.SelectMany(l => l).Select(v => v.VariantId), StringComparer.OrdinalIgnoreCase);

                PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    var stale = PlayniteApi.Database.Games
                        .Where(g => g.PluginId == Id && !string.IsNullOrEmpty(g.GameId) && variantIds.Contains(g.GameId))
                        .ToList();
                    if (stale.Count > 0)
                    {
                        logger.Info("LANCommander: removing " + stale.Count + " standalone entries that are variants of another game");
                        PlayniteApi.Database.Games.Remove(stale);
                    }

                    var games = PlayniteApi.Database.Games
                        .Where(g => g.PluginId == Id && g.IsInstalled && !string.IsNullOrEmpty(g.GameId))
                        .ToList();
                    foreach (var game in games)
                    {
                        List<LcVariant> list;
                        if (map.TryGetValue(game.GameId, out list))
                            LcVariants.EnsureActions(PlayniteApi, game, cfg, list);
                    }
                });
            }
            catch (Exception e)
            {
                logger.Warn(e, "LANCommander: variant sync failed");
            }
        }

        /// <summary>Right after the base game is installed (its Play action was just rebuilt), add the variant launch options again.</summary>
        public override void OnGameInstalled(OnGameInstalledEventArgs args)
        {
            var game = args.Game;
            if (game == null || game.PluginId != Id || string.IsNullOrEmpty(game.GameId)) return;

            try
            {
                var cfg = LoadConfigQuiet();
                var map = LcVariants.Load(VariantsPath);
                List<LcVariant> list;
                if (cfg != null && map.TryGetValue(game.GameId, out list))
                    LcVariants.EnsureActions(PlayniteApi, game, cfg, list);
            }
            catch (Exception e)
            {
                logger.Warn(e, "LANCommander: could not add variant launch options for " + game.Name);
            }
        }

        /// <summary>Uninstalling the base game also removes the variant copies and their launch options.</summary>
        public override void OnGameUninstalled(OnGameUninstalledEventArgs args)
        {
            var game = args.Game;
            if (game == null || game.PluginId != Id || string.IsNullOrEmpty(game.GameId)) return;

            try
            {
                var cfg = LoadConfigQuiet();
                var map = LcVariants.Load(VariantsPath);
                List<LcVariant> list;
                if (cfg == null || !map.TryGetValue(game.GameId, out list)) return;

                foreach (var v in list)
                {
                    var dir = LcVariants.Dir(cfg, v);

                    // only ever delete inside our own install root
                    if (Directory.Exists(dir) && LcPaths.IsUnder(dir, cfg.InstallRoot))
                    {
                        try { Directory.Delete(dir, true); }
                        catch (Exception e)
                        {
                            logger.Error(e, "LANCommander: could not delete " + dir);
                            PlayniteApi.Notifications.Add(new NotificationMessage(
                                "lc-variant-del-" + v.VariantId,
                                "LANCommander: could not delete the folder of " + v.Title + ": " + e.Message,
                                NotificationType.Error));
                        }
                    }

                    v.Resolved = false;   // the exe is gone, so the next launch has to install it again
                }

                LcVariants.Save(VariantsPath, map);
                LcVariants.RemoveActions(PlayniteApi, game, list);
            }
            catch (Exception e)
            {
                logger.Warn(e, "LANCommander: variant cleanup failed for " + game.Name);
            }
        }

        /// <summary>The variant started in the current session, per Playnite game id. Used to report the session to the variant's own server entry.</summary>
        readonly Dictionary<Guid, LcVariant> activeVariants = new Dictionary<Guid, LcVariant>();
        readonly object activeLock = new object();

        /// <summary>
        /// Called while Playnite is starting a variant's launch option. If the variant isn't on disk yet it is
        /// downloaded and extracted first. This runs before Playnite starts the exe, behind a progress dialog.
        /// </summary>
        void PrepareVariant(LcConfig cfg, Game game, LcVariant v, GameAction source, Dictionary<string, List<LcVariant>> map)
        {
            if (LcVariants.IsInstalled(cfg, v)) return;

            string error = null;

            PlayniteApi.Dialogs.ActivateGlobalProgress(a =>
            {
                try
                {
                    a.ProgressMaxValue = 100;

                    var p = new LcProgress(a.CancelToken, pr =>
                    {
                        a.Text = (pr.Text ?? "").TrimEnd('.') + " " + Math.Round(pr.CurrentProgressValue) + "%" +
                                 (pr.BytesTotal > 0 ? " (" + LcFormat.Bytes(pr.BytesDone) + " of " + LcFormat.Bytes(pr.BytesTotal) + ")" : "");
                        a.CurrentProgressValue = pr.CurrentProgressValue;
                    });

                    LcGame info;
                    var dir = LcVariantInstaller.Install(cfg, v, p, out info);

                    if (!LcVariants.Resolve(dir, info, v))
                        throw new InvalidOperationException("The download finished, but no .exe was found in " + dir + ".");
                }
                catch (OperationCanceledException) { error = "The install of " + v.Title + " was cancelled."; }
                catch (Exception e)
                {
                    logger.Error(e, "LANCommander: variant install failed for " + v.Title);
                    error = e.Message;
                }
            }, new GlobalProgressOptions("Installing " + v.Title + " (first launch)...", true) { IsIndeterminate = false });

            if (error != null)
            {
                PlayniteApi.Dialogs.ShowErrorMessage(error, "LANCommander: " + v.Title);
                return;
            }

            // remember the real exe location, and point the launch option at it
            try { LcVariants.Save(VariantsPath, map); }
            catch (Exception e) { logger.Warn(e, "LANCommander: could not save variants.json"); }

            var fresh = LcVariants.BuildAction(cfg, v);
            Action<GameAction> apply = act =>
            {
                act.Path = fresh.Path;
                act.WorkingDir = fresh.WorkingDir;
                act.Arguments = fresh.Arguments;
            };

            var stored = game.GameActions == null ? null :
                game.GameActions.FirstOrDefault(x => string.Equals(x.Name, v.Title, StringComparison.OrdinalIgnoreCase));
            if (stored != null)
            {
                apply(stored);
                PlayniteApi.Database.Games.Update(game);
            }
            if (source != null && !ReferenceEquals(source, stored))
                apply(source);
        }

        // ---------- play sessions ----------

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            var game = args.Game;
            if (game == null || game.PluginId != Id) return;

            var cfg = LoadConfig();
            if (cfg == null) return;

            // is this one of the variant launch options (for example "Portal 2 (VR)")?
            LcVariant variant = null;
            try
            {
                var map = LcVariants.Load(VariantsPath);
                variant = LcVariants.Match(map, game, args.SourceAction);
                if (variant != null)
                    PrepareVariant(cfg, game, variant, args.SourceAction, map);
            }
            catch (Exception e)
            {
                logger.Error(e, "LANCommander: could not prepare the variant for " + game.Name);
            }

            lock (activeLock)
            {
                if (variant != null) activeVariants[game.Id] = variant;
                else activeVariants.Remove(game.Id);
            }

            if (!cfg.TrackPlayTime) return;

            // a variant reports its session to its own entry on the server
            var serverId = variant != null ? variant.VariantId : game.GameId;

            PlayniteApi.Dialogs.ActivateGlobalProgress(p =>
            {
                try { BeforePlay(cfg, game, serverId, p.CancelToken); }
                catch (LcServerUnreachableException e) { logger.Warn(e, "LANCommander: server unreachable, play session not reported for " + game.Name); NoteServerUnreachable(cfg, false); }
                catch (Exception e) { ReportSyncProblem(game, "before playing", e); }
            }, new GlobalProgressOptions("Starting LANCommander play session for " + game.Name + "...", false) { IsIndeterminate = true });
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            var game = args.Game;
            if (game == null || game.PluginId != Id) return;

            LcVariant variant;
            lock (activeLock)
            {
                activeVariants.TryGetValue(game.Id, out variant);
                activeVariants.Remove(game.Id);
            }

            var cfg = LoadConfig();
            if (cfg == null || !cfg.TrackPlayTime) return;

            var serverId = variant != null ? variant.VariantId : game.GameId;
            var elapsedSeconds = (long)args.ElapsedSeconds;
            PlayniteApi.Dialogs.ActivateGlobalProgress(p =>
            {
                try { AfterPlay(cfg, game, serverId, elapsedSeconds, p.CancelToken); }
                catch (LcServerUnreachableException e) { logger.Warn(e, "LANCommander: server unreachable, play session end not reported for " + game.Name); NoteServerUnreachable(cfg, false); }
                catch (Exception e) { ReportSyncProblem(game, "after playing", e); }
            }, new GlobalProgressOptions("Saving LANCommander play session for " + game.Name + "...", false) { IsIndeterminate = true });
        }

        void ReportSyncProblem(Game game, string when, Exception e)
        {
            logger.Error(e, "LANCommander playtime sync failed " + when + " for " + game.Name);
            PlayniteApi.Notifications.Add(new NotificationMessage(
                "lc-playtime-" + game.Id,
                "LANCommander: could not sync playtime for " + game.Name + " " + when + ": " + e.Message,
                NotificationType.Error));
        }

        void BeforePlay(LcConfig cfg, Game game, string serverGameId, CancellationToken ct)
        {
            var id = Guid.Parse(serverGameId);

            // a short wait: a switched-off server must not hold the game back for long
            using (var client = new LcClient(cfg, 5))
            {
                try
                {
                    Task.Run(() => client.ReportPlaySessionAsync(id, true, ct)).GetAwaiter().GetResult();
                    logger.Info("LANCommander: started play session for " + game.Name);
                }
                catch (Exception e)
                {
                    logger.Warn(e, "LANCommander: could not start the play session for " + game.Name);
                    throw;
                }
            }
        }

        void AfterPlay(LcConfig cfg, Game game, string serverGameId, long elapsedSeconds, CancellationToken ct)
        {
            var id = Guid.Parse(serverGameId);

            using (var client = new LcClient(cfg, 5))
            {
                try
                {
                    // Playnite is the source of truth for the local session duration.
                    // LANCommander's public PlaySessions endpoint records the session
                    // server-side when it is started/ended; the elapsed value is logged
                    // here for verification and troubleshooting.
                    Task.Run(() => client.ReportPlaySessionAsync(id, false, ct)).GetAwaiter().GetResult();
                    logger.Info("LANCommander: ended play session for " + game.Name + ". Playnite session duration: " + elapsedSeconds + " seconds.");
                }
                catch (Exception e)
                {
                    logger.Warn(e, "LANCommander: could not end the play session for " + game.Name);
                    throw;
                }
            }
        }

        // ---------- menus ----------

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var games = args.Games
                .Where(g => g.PluginId == Id && g.IsInstalled && !string.IsNullOrEmpty(g.InstallDirectory))
                .ToList();
            if (games.Count == 0)
                yield break;

            yield return new GameMenuItem
            {
                Description = "Update (extract newest archive over the install)",
                MenuSection = "LANCommander",
                Action = a => LcUpdater.Enqueue(PlayniteApi, this, games)
            };
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                Description = "Scan install folder for installed games",
                MenuSection = "@LANCommander",
                Action = a => ScanFromMenu()
            };
            yield return new MainMenuItem
            {
                Description = "Check server connection",
                MenuSection = "@LANCommander",
                Action = a => CheckConnectionFromMenu()
            };
            yield return new MainMenuItem
            {
                Description = "Check for game updates",
                MenuSection = "@LANCommander",
                Action = a => CheckUpdatesFromMenu()
            };
        }

        void ScanFromMenu()
        {
            var cfg = LoadConfig();
            if (cfg == null) return;

            try
            {
                var count = ScanInstalled(cfg);
                PlayniteApi.Dialogs.ShowMessage(
                    count > 0
                        ? "Found " + count + " installed game(s) in " + cfg.InstallRoot + ". They are being added in the background."
                        : "No new installed games found in " + cfg.InstallRoot + ". Games must be in a folder named like the game, or have been installed by this plugin.",
                    "LANCommander");
            }
            catch (Exception e)
            {
                logger.Error(e, "LANCommander: scan failed");
                PlayniteApi.Dialogs.ShowErrorMessage("Scan failed: " + e.Message, "LANCommander");
            }
        }

        void CheckUpdatesFromMenu()
        {
            var cfg = LoadConfig();
            if (cfg == null) return;

            var outdated = CheckUpdates(cfg, true);
            if (outdated.Count == 0)
                PlayniteApi.Dialogs.ShowMessage("All installed LANCommander games match the newest archive as of the last library update. Update the library first to pick up newly uploaded archives.", "LANCommander");
        }

        // ---------- install / uninstall ----------

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
                yield break;

            yield return new LcInstallController(args.Game, this, PlayniteApi);
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
                yield break;

            yield return new LcUninstallController(args.Game, this, PlayniteApi);
        }
    }
}
