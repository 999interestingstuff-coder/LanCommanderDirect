using System;
using System.Collections.Generic;
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
        }

        // ---------- config ----------

        string ConfigPath => Path.Combine(GetPluginUserDataPath(), "config.json");

        /// <summary>Which variants (e.g. a VR build) belong to which base game. Written on every library update.</summary>
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

        /// <summary>Config for working out folder paths only: no notifications, install root filled in with its default.</summary>
        LcConfig ConfigForPaths()
        {
            var cfg = ReadRawConfig();
            if (string.IsNullOrWhiteSpace(cfg.InstallRoot))
            {
                cfg.InstallRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "LANCommander");
            }
            return cfg;
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

            try
            {
                return Task.Run(() => FetchAsync(cfg, installed, mediaDir, args.CancelToken)).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                logger.Error(e, "LANCommander import failed");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "lc-import-error", "LANCommander import failed: " + e.Message, NotificationType.Error));
                return new List<GameMetadata>();
            }
        }

        async Task<List<GameMetadata>> FetchAsync(LcConfig cfg, Dictionary<string, string> installed, string mediaDir, CancellationToken ct)
        {
            Directory.CreateDirectory(mediaDir);
            var result = new List<GameMetadata>();
            var logos = new Dictionary<string, string>();   // server game id -> cached logo file

            // games tagged "variant-of:<base title>" are not imported as games of their own;
            // they become extra launch options of the base game
            var variantGames = new List<Tuple<LcGame, string>>();
            var variantMap = new Dictionary<string, List<LcVariant>>();

            using (var client = new LcClient(cfg))
            {
                var games = await client.GetGamesAsync(ct).ConfigureAwait(false);

                foreach (var item in games)
                {
                    // the list only has light records; the detail call adds media, developers, genres...
                    var g = item;
                    try
                    {
                        var detail = await client.GetGameAsync(item.Id, ct).ConfigureAwait(false);
                        if (detail != null) g = detail;
                    }
                    catch (Exception e)
                    {
                        logger.Warn(e, "Could not load LANCommander details for " + item.Id);
                    }

                    var variantOf = LcVariants.BaseTitle(g);
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

                    // images: type 0 = icon, 1 = cover, 2 = background
                    var icon = await CacheMediaAsync(client, FirstOfType(g, 0), mediaDir, ct).ConfigureAwait(false);
                    if (icon != null) meta.Icon = new MetadataFile(icon);

                    var cover = await CacheMediaAsync(client, FirstOfType(g, 1), mediaDir, ct).ConfigureAwait(false);
                    if (cover != null) meta.CoverImage = new MetadataFile(cover);

                    var bg = await CacheMediaAsync(client, FirstOfType(g, 2), mediaDir, ct).ConfigureAwait(false);
                    if (bg != null) meta.BackgroundImage = new MetadataFile(bg);

                    // type 4 = logo; Playnite has no logo field, so it is copied into ExtraMetadata after the import
                    var logo = await CacheMediaAsync(client, FirstOfType(g, 4), mediaDir, ct).ConfigureAwait(false);
                    if (logo != null && logo.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        logos[gameId] = logo;

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
                }
            }

            try
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(mediaDir), "logos.json"), JsonConvert.SerializeObject(logos));
            }
            catch (Exception e) { logger.Warn(e, "Could not write logos.json"); }

            try
            {
                LcVariants.KeepResolved(variantMap, LcVariants.Load(VariantsPath));
                LcVariants.Save(VariantsPath, variantMap);
            }
            catch (Exception e) { logger.Warn(e, "Could not write variants.json"); }

            return result;
        }

        // ---------- logos (Extra Metadata layout: ExtraMetadata\games\<Playnite game id>\Logo.png) ----------

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            SyncLogos();
            SyncVariantActions();
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            SyncLogos();
            SyncVariantActions();
        }

        void SyncLogos()
        {
            try
            {
                var mapPath = Path.Combine(GetPluginUserDataPath(), "logos.json");
                if (!File.Exists(mapPath)) return;

                var map = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(mapPath));
                if (map == null || map.Count == 0) return;

                var root = Path.Combine(PlayniteApi.Paths.ConfigurationPath, "ExtraMetadata", "games");

                foreach (var game in PlayniteApi.Database.Games.Where(g => g.PluginId == Id).ToList())
                {
                    string src;
                    if (string.IsNullOrEmpty(game.GameId) || !map.TryGetValue(game.GameId, out src) || !File.Exists(src))
                        continue;

                    var dir = Path.Combine(root, game.Id.ToString());
                    var dest = Path.Combine(dir, "Logo.png");
                    if (File.Exists(dest)) continue;   // never overwrite a logo that is already there

                    Directory.CreateDirectory(dir);
                    File.Copy(src, dest);
                    logger.Info("LANCommander: placed logo for " + game.Name);
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "LANCommander: logo sync failed");
            }
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

        /// <summary>Playnite fetches image URLs without auth headers, so cache them locally first.</summary>
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

                var ext = SniffImageExtension(tmp);
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

        static string SniffImageExtension(string path)
        {
            var b = new byte[12];
            using (var fs = File.OpenRead(path))
                fs.Read(b, 0, b.Length);

            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
            if (b[0] == 0xFF && b[1] == 0xD8) return ".jpg";
            if (b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01 && b[3] == 0x00) return ".ico";
            if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return ".gif";
            if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46) return ".webp";
            return ".png";
        }

        // ---------- variants (separate full copies such as a VR build, shown as extra launch options) ----------

        /// <summary>Makes sure every installed base game has a launch option for each of its variants.</summary>
        void SyncVariantActions()
        {
            try
            {
                var map = LcVariants.Load(VariantsPath);
                if (map.Count == 0) return;

                var cfg = ConfigForPaths();
                var games = PlayniteApi.Database.Games
                    .Where(g => g.PluginId == Id && g.IsInstalled && !string.IsNullOrEmpty(g.GameId))
                    .ToList();

                PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                {
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
                var map = LcVariants.Load(VariantsPath);
                List<LcVariant> list;
                if (map.TryGetValue(game.GameId, out list))
                    LcVariants.EnsureActions(PlayniteApi, game, ConfigForPaths(), list);
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
                var map = LcVariants.Load(VariantsPath);
                List<LcVariant> list;
                if (!map.TryGetValue(game.GameId, out list)) return;

                var cfg = ConfigForPaths();
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

        /// <summary>
        /// The variant started in the current session, per Playnite game id.
        /// Used to report the play session to the variant's own server entry.
        /// </summary>
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

            using (var client = new LcClient(cfg))
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

            using (var client = new LcClient(cfg))
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

        // ---------- playtime backup (main menu) ----------

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                Description = "Export playtime backup...",
                MenuSection = "@LANCommander",
                Action = a => ExportPlaytimeBackup()
            };
            yield return new MainMenuItem
            {
                Description = "Import playtime backup (only raises playtime)...",
                MenuSection = "@LANCommander",
                Action = a => ImportPlaytimeBackup()
            };
        }

        void ExportPlaytimeBackup()
        {
            try
            {
                var path = PlayniteApi.Dialogs.SaveFile("Playtime backup|*.json");
                if (string.IsNullOrEmpty(path)) return;
                if (string.IsNullOrEmpty(Path.GetExtension(path))) path += ".json";

                var count = LcPlaytime.Export(PlayniteApi, path);
                PlayniteApi.Dialogs.ShowMessage("Saved the playtime of " + count + " game(s) to:\n" + path, "LANCommander");
            }
            catch (Exception e)
            {
                logger.Error(e, "LANCommander: playtime export failed");
                PlayniteApi.Dialogs.ShowErrorMessage("Could not save the playtime backup: " + e.Message, "LANCommander");
            }
        }

        void ImportPlaytimeBackup()
        {
            try
            {
                var path = PlayniteApi.Dialogs.SelectFile("Playtime backup|*.json");
                if (string.IsNullOrEmpty(path)) return;

                var r = LcPlaytime.Import(PlayniteApi, path);
                PlayniteApi.Dialogs.ShowMessage(
                    "Playtime backup applied.\n\n" +
                    "Raised: " + r.Raised + " game(s)\n" +
                    "Already the same or higher: " + r.Unchanged + "\n" +
                    "Not found in this library: " + r.NoMatch + "\n" +
                    "(" + r.Total + " game(s) in the file)",
                    "LANCommander");
            }
            catch (Exception e)
            {
                logger.Error(e, "LANCommander: playtime import failed");
                PlayniteApi.Dialogs.ShowErrorMessage("Could not read the playtime backup: " + e.Message, "LANCommander");
            }
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
