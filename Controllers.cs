using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace LanCommanderDirect
{
    static class LcPaths
    {
        public static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.');
            return string.IsNullOrEmpty(name) ? "game" : name;
        }

        public static bool IsUnder(string path, string root)
        {
            var p = Path.GetFullPath(path).TrimEnd('\\') + "\\";
            var r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase) && p.Length > r.Length;
        }

        public static string FindSevenZip(LcConfig cfg)
        {
            if (!string.IsNullOrWhiteSpace(cfg.SevenZipPath) && File.Exists(cfg.SevenZipPath))
                return cfg.SevenZipPath;

            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;

            throw new FileNotFoundException(
                "This archive is not a zip, so 7-Zip is needed. Install 7-Zip or set SevenZipPath (7z.exe) in the LANCommander config.json.");
        }
    }

    static class LcExtract
    {
        static readonly ILogger logger = LogManager.GetLogger();

        public static bool IsZip(string path)
        {
            var b = new byte[2];
            using (var fs = File.OpenRead(path))
                if (fs.Read(b, 0, 2) < 2) return false;
            return b[0] == 0x50 && b[1] == 0x4B;   // "PK"
        }

        /// <summary>Extracts with the built-in zip support, guarding against paths that escape the target folder.</summary>
        public static void ExtractZip(string zipPath, string destDir, Action<double> progress, CancellationToken ct)
        {
            var destFull = Path.GetFullPath(destDir).TrimEnd('\\') + "\\";

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                long total = Math.Max(1, zip.Entries.Sum(e => e.Length));
                long done = 0;

                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();

                    var name = entry.FullName.Replace('/', '\\');
                    var target = Path.GetFullPath(Path.Combine(destDir, name));
                    if (!target.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Archive contains an unsafe path: " + entry.FullName);

                    if (name.EndsWith("\\") || string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);

                    done += entry.Length;
                    if (progress != null) progress(done * 100.0 / total);
                }
            }
        }

        public static void Extract7z(string sevenZip, string archive, string destDir, CancellationToken ct)
        {
            if (Path.GetFileName(sevenZip).Equals("7zFM.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SevenZipPath points at 7zFM.exe (the file manager). Use 7z.exe.");

            var psi = new ProcessStartInfo
            {
                FileName = sevenZip,
                Arguments = "x \"" + archive + "\" -o\"" + destDir + "\" -y",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var output = new StringBuilder();
            using (var proc = new Process { StartInfo = psi })
            {
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                while (!proc.WaitForExit(500))
                {
                    if (ct.IsCancellationRequested)
                    {
                        try { proc.Kill(); } catch { }
                        throw new OperationCanceledException();
                    }
                }
                proc.WaitForExit();

                string text;
                lock (output) text = output.ToString();
                logger.Info("LANCommander: 7-Zip exit code " + proc.ExitCode + ", output:\n" + text);

                if (proc.ExitCode > 1)
                    throw new InvalidOperationException("7-Zip failed (exit code " + proc.ExitCode + "): " +
                        (text.Length > 800 ? text.Substring(text.Length - 800) : text));
            }
        }
    }

    /// <summary>Progress state for a background install. Reports to a callback, at most every two seconds.</summary>
    static class LcFormat
    {
        public static string Bytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int i = 0;
            while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
            return value.ToString(i == 0 ? "0" : "0.00") + " " + units[i];
        }
    }

    class LcProgress
    {
        readonly Action<LcProgress> onChange;
        DateTime lastReport = DateTime.MinValue;
        string text;
        double current;

        public LcProgress(CancellationToken token, Action<LcProgress> onChange)
        {
            CancelToken = token;
            this.onChange = onChange;
        }

        public CancellationToken CancelToken { get; private set; }
        public double ProgressMaxValue { get; set; }
        public long BytesDone { get; set; }
        public long BytesTotal { get; set; }

        public string Text
        {
            get { return text; }
            set { text = value; Report(true); }
        }

        public double CurrentProgressValue
        {
            get { return current; }
            set { current = value; Report(false); }
        }

        void Report(bool force)
        {
            var now = DateTime.UtcNow;
            if (!force && (now - lastReport).TotalSeconds < 2) return;
            lastReport = now;
            if (onChange != null) onChange(this);
        }
    }

    /// <summary>Background job bookkeeping and the shared progress notification used by installs and updates.</summary>
    static class LcJobs
    {
        static readonly ILogger logger = LogManager.GetLogger();
        static readonly System.Collections.Generic.HashSet<Guid> running = new System.Collections.Generic.HashSet<Guid>();

        /// <summary>False if an install or update is already running for this game.</summary>
        public static bool TryBegin(Guid gameId) { lock (running) return running.Add(gameId); }
        public static void End(Guid gameId) { lock (running) running.Remove(gameId); }

        public static LcProgress MakeProgress(IPlayniteAPI api, string noteId, CancellationToken ct)
        {
            int lastBucket = -1;
            return new LcProgress(ct, p =>
            {
                var percent = Math.Round(p.CurrentProgressValue);
                var message = (p.Text ?? "").TrimEnd('.') + " " + percent + "%" +
                              (p.BytesTotal > 0 ? " (" + LcFormat.Bytes(p.BytesDone) + " of " + LcFormat.Bytes(p.BytesTotal) + ")" : "");

                // also write every 10% to extensions.log, so progress can be checked even if the notification doesn't refresh
                var bucket = (int)(percent / 10);
                if (bucket != lastBucket)
                {
                    lastBucket = bucket;
                    logger.Info("LANCommander: " + message);
                }

                // notifications are replaced by removing the old one first, and are changed on the UI thread
                api.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        api.Notifications.Remove(noteId);
                        api.Notifications.Add(new NotificationMessage(noteId, message, NotificationType.Info));
                    }
                    catch (Exception e) { logger.Warn(e, "LANCommander: could not update the progress notification"); }
                }));
            });
        }
    }

    /// <summary>Download / extract / layout steps shared by install and update.</summary>
    static class LcArchiveOps
    {
        static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>Download next to the install folder so a multi-GB archive doesn't fill the system drive's temp folder.</summary>
        public static string DownloadDir(LcConfig cfg)
        {
            var tmpDir = Path.Combine(cfg.InstallRoot, ".lc-downloads");
            try { Directory.CreateDirectory(tmpDir); }
            catch
            {
                tmpDir = Path.Combine(Path.GetTempPath(), "LanCommanderDirect");
                Directory.CreateDirectory(tmpDir);
            }
            return tmpDir;
        }

        public static void Download(LcClient client, Guid id, LcArchive archive, string file, string gameName, LcProgress p, CancellationToken ct)
        {
            p.Text = "Downloading " + gameName + "...";
            p.ProgressMaxValue = 100;
            p.CurrentProgressValue = 0;

            var hint = archive.CompressedSize;
            Task.Run(() => client.DownloadGameAsync(id, file, hint, (done, total) =>
            {
                p.BytesDone = done;
                p.BytesTotal = total;
                if (total > 0) p.CurrentProgressValue = done * 100.0 / total;
            }, ct)).GetAwaiter().GetResult();

            var size = new FileInfo(file).Length;
            logger.Info("LANCommander: downloaded " + size + " bytes to " + file);
            if (size < 4096)
                throw new InvalidOperationException("Download is only " + size + " bytes, not a game archive: " + File.ReadAllText(file));
        }

        public static void Extract(LcConfig cfg, string file, string destDir, string gameName, LcProgress p, CancellationToken ct)
        {
            Directory.CreateDirectory(destDir);

            p.BytesDone = 0;
            p.BytesTotal = 0;
            p.Text = "Extracting " + gameName + "...";
            p.CurrentProgressValue = 0;

            if (LcExtract.IsZip(file))
                LcExtract.ExtractZip(file, destDir, pct => p.CurrentProgressValue = pct, ct);
            else
                LcExtract.Extract7z(LcPaths.FindSevenZip(cfg), file, destDir, ct);

            if (!Directory.EnumerateFileSystemEntries(destDir).Any())
                throw new InvalidOperationException("The archive extracted nothing.");
        }

        /// <summary>
        /// Archives that wrap everything in one folder would end up as ...\Game\Game\. Lift the wrapper's
        /// contents up one level instead: rename the wrapper out of the way, remove the (now empty) outer
        /// folder and give the wrapper the outer folder's name. Renames stay on one drive, so this is instant.
        /// Returns the folder the game ended up in.
        /// </summary>
        public static string Flatten(string dir)
        {
            var files = Directory.GetFiles(dir);
            var subdirs = Directory.GetDirectories(dir);
            if (files.Length != 0 || subdirs.Length != 1)
                return dir;

            var wrapper = subdirs[0].TrimEnd('\\');
            var temp = dir + ".lc-flatten-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                Directory.Move(wrapper, temp);
                Directory.Delete(dir);          // empty now
                Directory.Move(temp, dir);
            }
            catch (Exception e)
            {
                logger.Warn(e, "LANCommander: could not flatten the wrapper folder, keeping the original layout");
                try
                {
                    if (Directory.Exists(temp))
                    {
                        // put the game back where it was and install from inside the wrapper, as before
                        Directory.CreateDirectory(dir);
                        var restored = Path.Combine(dir, Path.GetFileName(wrapper));
                        if (!Directory.Exists(restored)) Directory.Move(temp, restored);
                        dir = restored;
                    }
                    else
                    {
                        var left = Directory.GetDirectories(dir);
                        if (left.Length == 1) dir = left[0].TrimEnd('\\');
                    }
                }
                catch (Exception e2)
                {
                    logger.Warn(e2, "LANCommander: could not restore the original layout either");
                }
            }
            return dir;
        }

        /// <summary>Moves everything from src into dst, replacing files that already exist and keeping files that only exist in dst.</summary>
        public static void MergeInto(string src, string dst, CancellationToken ct)
        {
            Directory.CreateDirectory(dst);

            foreach (var file in Directory.GetFiles(src))
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(dst, Path.GetFileName(file));
                if (File.Exists(target))
                {
                    File.SetAttributes(target, FileAttributes.Normal);
                    File.Delete(target);
                }
                File.Move(file, target);
            }

            foreach (var sub in Directory.GetDirectories(src))
                MergeInto(sub, Path.Combine(dst, Path.GetFileName(sub.TrimEnd('\\'))), ct);
        }

        /// <summary>
        /// Downloads the newest archive and extracts it over the game's current folder. Files the game wrote
        /// itself (saves, configs) stay unless the archive contains a file with the same path.
        /// </summary>
        public static LcGame Update(LcConfig cfg, Game game, LcProgress p)
        {
            var id = Guid.Parse(game.GameId);
            var ct = p.CancelToken;
            var installDir = (game.InstallDirectory ?? "").TrimEnd('\\');
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                throw new InvalidOperationException("The install folder no longer exists: " + installDir);

            using (var client = new LcClient(cfg))
            {
                var detail = Task.Run(() => client.GetGameAsync(id, ct)).GetAwaiter().GetResult();
                var archive = detail.LatestArchive();
                if (archive == null)
                    throw new InvalidOperationException("This game has no archive uploaded on the LANCommander server.");

                var tmpDir = DownloadDir(cfg);
                var file = Path.Combine(tmpDir, id + ".update.archive");
                var staging = Path.Combine(tmpDir, id + ".update");

                try
                {
                    if (Directory.Exists(staging)) Directory.Delete(staging, true);

                    Download(client, id, archive, file, game.Name, p, ct);
                    Extract(cfg, file, staging, game.Name, p, ct);

                    // an archive with a single wrapper folder is merged from inside that folder
                    var source = staging;
                    if (Directory.GetFiles(staging).Length == 0 && Directory.GetDirectories(staging).Length == 1)
                        source = Directory.GetDirectories(staging)[0];

                    p.Text = "Installing update for " + game.Name + "...";
                    p.CurrentProgressValue = 100;
                    MergeInto(source, installDir, ct);
                }
                finally
                {
                    try { if (File.Exists(file)) File.Delete(file); } catch { }
                    try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
                }

                LcInstallRegistry.Set(new LcInstallRecord
                {
                    GameId = id.ToString(),
                    InstallDir = installDir,
                    ArchiveId = archive.Id,
                    ArchiveVersion = archive.Version,
                    InstalledOn = DateTime.UtcNow
                });
                return detail;
            }
        }
    }

    /// <summary>Works out the Play action for an installed game folder.</summary>
    static class LcLaunch
    {
        static readonly ILogger logger = LogManager.GetLogger();

        internal static string Str(JObject o, params string[] names)
        {
            foreach (var n in names)
            {
                var t = o.GetValue(n, StringComparison.OrdinalIgnoreCase);
                if (t != null && t.Type != JTokenType.Null)
                {
                    var s = t.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            return null;
        }

        internal static bool Flag(JObject o, params string[] names)
        {
            foreach (var n in names)
            {
                var t = o.GetValue(n, StringComparison.OrdinalIgnoreCase);
                bool b;
                if (t != null && bool.TryParse(t.ToString(), out b) && b) return true;
            }
            return false;
        }

        /// <summary>Tries the launch action the server defines. Returns the exe's full path, or null.</summary>
        internal static string ServerExe(string installDir, LcGame info, out string arguments, out string workingDirRel)
        {
            arguments = null;
            workingDirRel = null;
            if (info == null || info.Actions == null) return null;

            var candidates = info.Actions.Where(a => a != null && Str(a, "path") != null).ToList();
            if (candidates.Count == 0) return null;

            var chosen = candidates.FirstOrDefault(a => Flag(a, "primaryAction", "isPrimaryAction", "primary")) ?? candidates[0];

            var raw = Str(chosen, "path").Replace('/', '\\');
            var expanded = Regex.Replace(raw, @"\{InstallDir\}", installDir.Replace("$", "$$"), RegexOptions.IgnoreCase);
            if (!Path.IsPathRooted(expanded))
                expanded = Path.Combine(installDir, expanded.TrimStart('\\'));

            if (!File.Exists(expanded))
            {
                // the folder layout can differ from the server's path (for example a wrapper folder that was lifted
                // up on install), so look for the same file name inside the install folder
                var name = Path.GetFileName(expanded);
                var found = string.IsNullOrEmpty(name) ? null :
                    Directory.EnumerateFiles(installDir, name, SearchOption.AllDirectories)
                        .OrderBy(f => f.Count(c => c == '\\'))
                        .FirstOrDefault();
                if (found == null) return null;
                expanded = found;
            }

            arguments = Str(chosen, "arguments", "args");

            var wdRaw = Str(chosen, "workingDirectory", "workingDir");
            if (wdRaw != null)
            {
                var wd = Regex.Replace(wdRaw.Replace('/', '\\'), @"\{InstallDir\}", installDir.Replace("$", "$$"), RegexOptions.IgnoreCase);
                if (!Path.IsPathRooted(wd))
                    wd = Path.Combine(installDir, wd.TrimStart('\\'));
                wd = Path.GetFullPath(wd).TrimEnd('\\');
                if (Directory.Exists(wd) && wd.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                    workingDirRel = wd.Substring(installDir.Length).TrimStart('\\');
            }

            return Path.GetFullPath(expanded);
        }

        internal static string GuessExe(string installDir)
        {
            var skip = new[] { "unins", "crash", "redist", "vc_", "vcredist", "dxsetup", "dotnet", "notification_helper", "uninstall" };
            return Directory.EnumerateFiles(installDir, "*.exe", SearchOption.AllDirectories)
                .Where(f => !skip.Any(s => Path.GetFileName(f).IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(f => f.Count(c => c == '\\'))          // shallowest first
                .ThenByDescending(f => new FileInfo(f).Length)  // then largest
                .FirstOrDefault();
        }

        /// <summary>The Play action for a game folder: the server's launch action if it matches, otherwise a guessed exe. Null if no exe.</summary>
        public static GameAction BuildPlayAction(string installDir, LcGame info)
        {
            string arguments, serverWorkDir;
            var exe = ServerExe(installDir, info, out arguments, out serverWorkDir);
            if (exe != null)
                logger.Info("LANCommander: using the server's launch action: " + exe +
                            (string.IsNullOrWhiteSpace(arguments) ? "" : " with arguments: " + arguments));
            else
            {
                exe = GuessExe(installDir);
                if (exe != null) logger.Info("LANCommander: no usable server action, guessed exe: " + exe);
            }

            if (exe == null) return null;

            var rel = exe.Substring(installDir.Length).TrimStart('\\');
            var dirRel = Path.GetDirectoryName(rel) ?? "";

            var action = new GameAction
            {
                Name = "Play",
                Type = GameActionType.File,
                Path = "{InstallDir}\\" + rel,
                WorkingDir = serverWorkDir != null
                    ? (serverWorkDir.Length > 0 ? "{InstallDir}\\" + serverWorkDir : "{InstallDir}")
                    : (dirRel.Length > 0 ? "{InstallDir}\\" + dirRel : "{InstallDir}"),
                IsPlayAction = true
            };
            if (!string.IsNullOrWhiteSpace(arguments))
                action.Arguments = arguments;
            return action;
        }
    }

    public class LcInstallController : InstallController
    {
        static readonly ILogger logger = LogManager.GetLogger();

        readonly LanCommanderLibrary plugin;
        readonly IPlayniteAPI api;

        public LcInstallController(Game game, LanCommanderLibrary plugin, IPlayniteAPI api) : base(game)
        {
            Name = "Install from LANCommander";
            this.plugin = plugin;
            this.api = api;
        }

        CancellationTokenSource cts;

        /// <summary>
        /// Starts the download and extraction in the background and returns at once, so Playnite stays usable.
        /// The game shows as "installing" until it is done. Progress is shown in the notification panel, and
        /// cancelling the install in Playnite cancels the download.
        /// </summary>
        public override void Install(InstallActionArgs args)
        {
            var cfg = plugin.LoadConfig();
            if (cfg == null)
            {
                api.Dialogs.ShowErrorMessage("LANCommander is not configured yet. Open the add-on settings and enter your server details.", "LANCommander");
                return;
            }

            cts = new CancellationTokenSource();
            var gameName = Game.Name;
            var noteId = "lc-install-" + Game.Id;
            var progress = LcJobs.MakeProgress(api, noteId, cts.Token);

            Task.Run(() =>
            {
                string installDir = null;
                LcGame info = null;
                Exception error = null;
                bool cancelled = false;

                try { installDir = DoInstall(cfg, progress, out info); }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception e) { error = e; }

                api.MainView.UIDispatcher.Invoke(() =>
                {
                    if (cancelled)
                    {
                        api.Notifications.Remove(noteId);
                        return;
                    }

                    if (error != null)
                    {
                        logger.Error(error, "LANCommander install failed");
                        api.Notifications.Remove(noteId);
                        api.Notifications.Add(new NotificationMessage(noteId, gameName + ": install failed: " + error.Message, NotificationType.Error));
                        api.Dialogs.ShowErrorMessage(error.Message, "LANCommander install failed");
                        return;
                    }

                    ApplyPlayAction(installDir, info);
                    InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData { InstallDirectory = installDir }));
                    api.Notifications.Remove(noteId);
                    api.Notifications.Add(new NotificationMessage(noteId, gameName + " is installed.", NotificationType.Info));
                    if (cfg.PopupNotifications) LcToast.Show(api, Game, "Installed", false, cfg.PopupOverGames);
                });
            });
        }

        /// <summary>Called by Playnite when the install is cancelled.</summary>
        public override void Dispose()
        {
            try { if (cts != null) cts.Cancel(); } catch { }
        }

        string DoInstall(LcConfig cfg, LcProgress p, out LcGame info)
        {
            var id = Guid.Parse(Game.GameId);
            var ct = p.CancelToken;

            using (var client = new LcClient(cfg))
            {
                var detail = Task.Run(() => client.GetGameAsync(id, ct)).GetAwaiter().GetResult();
                info = detail;

                // launch actions can be missing from the detail response; the manifest has them
                try
                {
                    var manifest = Task.Run(() => client.GetManifestAsync(id, ct)).GetAwaiter().GetResult();
                    if ((detail.Actions == null || detail.Actions.Count == 0) &&
                        manifest != null && manifest.Actions != null && manifest.Actions.Count > 0)
                        detail.Actions = manifest.Actions;
                }
                catch (Exception e)
                {
                    logger.Warn(e, "LANCommander: could not read the game manifest");
                }

                var archive = detail.LatestArchive();
                if (archive == null)
                    throw new InvalidOperationException("This game has no archive uploaded on the LANCommander server yet.");

                var dir = Path.GetFullPath(Path.Combine(cfg.InstallRoot, LcPaths.Sanitize(Game.Name))).TrimEnd('\\');
                var file = Path.Combine(LcArchiveOps.DownloadDir(cfg), id + ".archive");

                bool createdDir = false;
                try
                {
                    LcArchiveOps.Download(client, id, archive, file, Game.Name, p, ct);

                    createdDir = !Directory.Exists(dir);
                    Directory.CreateDirectory(dir);

                    LcArchiveOps.Extract(cfg, file, dir, Game.Name, p, ct);
                }
                catch
                {
                    // don't leave a half-installed folder behind
                    try { if (createdDir && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                    throw;
                }
                finally
                {
                    try { if (File.Exists(file)) File.Delete(file); } catch { }
                }

                dir = LcArchiveOps.Flatten(dir);

                // remember where this game is and which archive it is, so a newer upload can be recognised as an update
                LcInstallRegistry.Set(new LcInstallRecord
                {
                    GameId = id.ToString(),
                    InstallDir = dir,
                    ArchiveId = archive.Id,
                    ArchiveVersion = archive.Version,
                    InstalledOn = DateTime.UtcNow
                });

                return dir;
            }
        }

        void ApplyPlayAction(string installDir, LcGame info)
        {
            var action = LcLaunch.BuildPlayAction(installDir, info);
            if (action == null)
            {
                api.Dialogs.ShowMessage("Installed, but no .exe was found. Add a play action to the game manually.", "LANCommander");
                return;
            }

            Game.GameActions = new ObservableCollection<GameAction> { action };
            api.Database.Games.Update(Game);
        }
    }

    public class LcUninstallController : UninstallController
    {
        static readonly ILogger logger = LogManager.GetLogger();

        readonly LanCommanderLibrary plugin;
        readonly IPlayniteAPI api;

        public LcUninstallController(Game game, LanCommanderLibrary plugin, IPlayniteAPI api) : base(game)
        {
            Name = "Uninstall (delete game folder)";
            this.plugin = plugin;
            this.api = api;
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            var cfg = plugin.LoadConfig();
            var dir = Game.InstallDirectory;

            // only ever delete inside our own install root
            if (cfg != null && !string.IsNullOrEmpty(dir) && Directory.Exists(dir) && LcPaths.IsUnder(dir, cfg.InstallRoot))
            {
                try
                {
                    Directory.Delete(dir, true);

                    // remove the wrapper folder too if we installed from a subfolder and it is now empty
                    var parent = Path.GetDirectoryName(dir.TrimEnd('\\'));
                    if (!string.IsNullOrEmpty(parent) && LcPaths.IsUnder(parent, cfg.InstallRoot) &&
                        Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                    {
                        Directory.Delete(parent);
                    }
                }
                catch (Exception e)
                {
                    logger.Error(e, "LANCommander uninstall failed");
                    api.Dialogs.ShowErrorMessage("Could not delete the game folder: " + e.Message, "LANCommander");
                    return;
                }
            }
            else if (!string.IsNullOrEmpty(dir))
            {
                api.Dialogs.ShowMessage(
                    "The game folder is outside the LANCommander install root, so it was not deleted. It is now marked as uninstalled in Playnite.",
                    "LANCommander");
            }

            LcInstallRegistry.Remove(Game.GameId);
            InvokeOnUninstalled(new GameUninstalledEventArgs());
        }
    }
}
