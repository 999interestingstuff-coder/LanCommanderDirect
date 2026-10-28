using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace LanCommanderDirect
{
    /// <summary>
    /// A full, standalone copy of a game (for example a VR build) that lives on the server as its own game,
    /// is installed to its own folder, and shows up as an extra launch option of its base game in Playnite.
    /// Stored in variants.json in the plugin data folder, keyed by the base game's server id.
    /// </summary>
    public class LcVariant
    {
        public string BaseGameId { get; set; }
        public string VariantId { get; set; }
        public string Title { get; set; }

        /// <summary>Exe path relative to the variant's own folder. Null until known.</summary>
        public string ExeRel { get; set; }
        public string Arguments { get; set; }
        /// <summary>Working directory relative to the variant's folder. Empty string = the folder itself.</summary>
        public string WorkingDirRel { get; set; }

        /// <summary>True once the exe was located on disk after an install (the server's path can differ from the real layout).</summary>
        public bool Resolved { get; set; }
    }

    static class LcVariants
    {
        /// <summary>Tag on the server game that marks it as a variant: "variant-of:Portal 2" (title of the base game).</summary>
        public const string TagPrefix = "variant-of:";

        static readonly ILogger logger = LogManager.GetLogger();

        // ---------- detection ----------

        /// <summary>Returns the base game's title if this server game is tagged as a variant, otherwise null.</summary>
        public static string BaseTitle(LcGame g)
        {
            if (g == null || g.Tags == null) return null;
            foreach (var t in g.Tags)
            {
                if (t == null || string.IsNullOrWhiteSpace(t.Name)) continue;
                var name = t.Name.Trim();
                if (name.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var title = name.Substring(TagPrefix.Length).Trim();
                    if (title.Length > 0) return title;
                }
            }
            return null;
        }

        // ---------- storage ----------

        public static Dictionary<string, List<LcVariant>> Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var map = JsonConvert.DeserializeObject<Dictionary<string, List<LcVariant>>>(File.ReadAllText(path));
                    if (map != null) return map;
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "LANCommander: could not read variants.json");
            }
            return new Dictionary<string, List<LcVariant>>();
        }

        public static void Save(string path, Dictionary<string, List<LcVariant>> map)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonConvert.SerializeObject(map, Formatting.Indented));
        }

        /// <summary>
        /// After a library refresh, keep what an earlier install already worked out about the real exe location,
        /// instead of going back to the server's (possibly different) path.
        /// </summary>
        public static void KeepResolved(Dictionary<string, List<LcVariant>> fresh, Dictionary<string, List<LcVariant>> old)
        {
            foreach (var kv in fresh)
            {
                List<LcVariant> oldList;
                if (!old.TryGetValue(kv.Key, out oldList)) continue;

                foreach (var v in kv.Value)
                {
                    var o = oldList.FirstOrDefault(x => x.VariantId == v.VariantId);
                    if (o != null && o.Resolved)
                    {
                        v.ExeRel = o.ExeRel;
                        v.Arguments = o.Arguments;
                        v.WorkingDirRel = o.WorkingDirRel;
                        v.Resolved = true;
                    }
                }
            }
        }

        // ---------- server action parsing ----------

        public static string Str(JObject o, params string[] names)
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

        static bool Flag(JObject o, params string[] names)
        {
            foreach (var n in names)
            {
                var t = o.GetValue(n, StringComparison.OrdinalIgnoreCase);
                bool b;
                if (t != null && bool.TryParse(t.ToString(), out b) && b) return true;
            }
            return false;
        }

        /// <summary>The variant's main launch action: the primary one if flagged, otherwise the first with a path.</summary>
        public static JObject PickAction(List<JObject> actions)
        {
            if (actions == null) return null;
            var candidates = actions.Where(a => a != null && Str(a, "path") != null).ToList();
            if (candidates.Count == 0) return null;
            return candidates.FirstOrDefault(a => Flag(a, "primaryAction", "isPrimaryAction", "primary")) ?? candidates[0];
        }

        /// <summary>Turns a server path like "{InstallDir}\portal2.exe" into a path relative to the install folder.</summary>
        static string Rel(string raw)
        {
            raw = raw.Replace('/', '\\');
            raw = Regex.Replace(raw, @"\{InstallDir\}", "", RegexOptions.IgnoreCase);
            raw = raw.Trim().TrimStart('\\');
            return Path.IsPathRooted(raw) ? null : raw;
        }

        /// <summary>Builds a variant record from the server game and its launch actions (taken from the server's paths, not yet checked on disk).</summary>
        public static LcVariant Build(LcGame variantGame, string baseGameId, List<JObject> actions)
        {
            var v = new LcVariant
            {
                BaseGameId = baseGameId,
                VariantId = variantGame.Id.ToString(),
                Title = !string.IsNullOrWhiteSpace(variantGame.Title) ? variantGame.Title : "Variant " + variantGame.Id
            };

            var chosen = PickAction(actions);
            if (chosen != null)
            {
                var exe = Rel(Str(chosen, "path"));
                v.ExeRel = string.IsNullOrEmpty(exe) ? null : exe;
                v.Arguments = Str(chosen, "arguments", "args");

                var wd = Str(chosen, "workingDirectory", "workingDir");
                v.WorkingDirRel = wd == null ? null : Rel(wd);
            }
            return v;
        }

        // ---------- locations ----------

        public static string Dir(LcConfig cfg, LcVariant v)
        {
            return Path.GetFullPath(Path.Combine(cfg.InstallRoot, LcPaths.Sanitize(v.Title))).TrimEnd('\\');
        }

        public static bool IsInstalled(LcConfig cfg, LcVariant v)
        {
            if (string.IsNullOrEmpty(v.ExeRel)) return false;
            return File.Exists(Path.Combine(Dir(cfg, v), v.ExeRel));
        }

        // ---------- Playnite actions ----------

        /// <summary>The Playnite launch option for a variant. Uses absolute paths, because {InstallDir} belongs to the base game.</summary>
        public static GameAction BuildAction(LcConfig cfg, LcVariant v)
        {
            var dir = Dir(cfg, v);
            var exe = !string.IsNullOrEmpty(v.ExeRel)
                ? Path.Combine(dir, v.ExeRel)
                : Path.Combine(dir, "not-installed-yet.exe");   // placeholder, replaced after the first install

            string workDir;
            if (v.WorkingDirRel != null)
                workDir = Path.GetFullPath(Path.Combine(dir, v.WorkingDirRel)).TrimEnd('\\');
            else
                workDir = Path.GetDirectoryName(exe);

            var action = new GameAction
            {
                Name = v.Title,
                Type = GameActionType.File,
                Path = exe,
                WorkingDir = workDir,
                IsPlayAction = true
            };
            if (!string.IsNullOrWhiteSpace(v.Arguments))
                action.Arguments = v.Arguments;
            return action;
        }

        /// <summary>Adds any missing variant launch options to the base game. Existing ones are left alone.</summary>
        public static void EnsureActions(IPlayniteAPI api, Game game, LcConfig cfg, List<LcVariant> variants)
        {
            if (game.GameActions == null)
                game.GameActions = new ObservableCollection<GameAction>();

            var changed = false;
            foreach (var v in variants)
            {
                if (game.GameActions.Any(a => string.Equals(a.Name, v.Title, StringComparison.OrdinalIgnoreCase)))
                    continue;
                game.GameActions.Add(BuildAction(cfg, v));
                changed = true;
            }

            if (changed) api.Database.Games.Update(game);
        }

        public static void RemoveActions(IPlayniteAPI api, Game game, List<LcVariant> variants)
        {
            if (game.GameActions == null) return;

            var doomed = game.GameActions
                .Where(a => variants.Any(v => string.Equals(a.Name, v.Title, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (doomed.Count == 0) return;

            foreach (var a in doomed) game.GameActions.Remove(a);
            api.Database.Games.Update(game);
        }

        /// <summary>Which variant (if any) does the launch option Playnite is starting belong to?</summary>
        public static LcVariant Match(Dictionary<string, List<LcVariant>> map, Game game, GameAction action)
        {
            if (action == null || string.IsNullOrEmpty(game.GameId) || string.IsNullOrEmpty(action.Name)) return null;

            List<LcVariant> list;
            if (!map.TryGetValue(game.GameId, out list)) return null;
            return list.FirstOrDefault(v => string.Equals(v.Title, action.Name, StringComparison.OrdinalIgnoreCase));
        }

        // ---------- locating the exe after an install ----------

        static string GuessExe(string dir)
        {
            var skip = new[] { "unins", "crash", "redist", "vc_", "vcredist", "dxsetup", "dotnet", "notification_helper", "uninstall" };
            return Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories)
                .Where(f => !skip.Any(s => Path.GetFileName(f).IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(f => f.Count(c => c == '\\'))
                .ThenByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }

        static string Expand(string raw, string dir)
        {
            var expanded = Regex.Replace(raw.Replace('/', '\\'), @"\{InstallDir\}", dir.Replace("$", "$$"), RegexOptions.IgnoreCase);
            if (!Path.IsPathRooted(expanded))
                expanded = Path.Combine(dir, expanded.TrimStart('\\'));
            return expanded;
        }

        /// <summary>
        /// Finds the real exe, arguments and working directory inside the installed folder and stores them on the variant.
        /// Returns false if no exe could be found at all.
        /// </summary>
        public static bool Resolve(string dir, LcGame info, LcVariant v)
        {
            dir = Path.GetFullPath(dir).TrimEnd('\\');

            string exe = null;
            var chosen = PickAction(info != null ? info.Actions : null);

            if (chosen != null)
            {
                var expanded = Expand(Str(chosen, "path"), dir);
                if (File.Exists(expanded) && LcPaths.IsUnder(expanded, dir))
                {
                    exe = Path.GetFullPath(expanded);
                }
                else
                {
                    // the folder layout can differ from the server's path (a wrapper folder lifted on install), so search by file name
                    var name = Path.GetFileName(expanded);
                    if (!string.IsNullOrEmpty(name))
                        exe = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories)
                            .OrderBy(f => f.Count(c => c == '\\'))
                            .FirstOrDefault();
                }
            }

            if (exe == null) exe = GuessExe(dir);
            if (exe == null) return false;

            var rel = exe.Substring(dir.Length).TrimStart('\\');
            v.ExeRel = rel;

            string workRel = null;
            if (chosen != null)
            {
                v.Arguments = Str(chosen, "arguments", "args");

                var wdRaw = Str(chosen, "workingDirectory", "workingDir");
                if (wdRaw != null)
                {
                    var wd = Path.GetFullPath(Expand(wdRaw, dir)).TrimEnd('\\');
                    if (Directory.Exists(wd) && (wd.Equals(dir, StringComparison.OrdinalIgnoreCase) || LcPaths.IsUnder(wd, dir)))
                        workRel = wd.Length <= dir.Length ? "" : wd.Substring(dir.Length).TrimStart('\\');
                }
            }
            v.WorkingDirRel = workRel ?? (Path.GetDirectoryName(rel) ?? "");
            v.Resolved = true;
            return true;
        }
    }

    /// <summary>Downloads a variant's archive and extracts it into the variant's own folder.</summary>
    static class LcVariantInstaller
    {
        static readonly ILogger logger = LogManager.GetLogger();

        public static string Install(LcConfig cfg, LcVariant v, LcProgress p, out LcGame info)
        {
            var id = Guid.Parse(v.VariantId);
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
                    logger.Warn(e, "LANCommander: could not read the manifest of variant " + v.Title);
                }

                var archive = (detail.Archives ?? new List<LcArchive>())
                    .OrderByDescending(a => a.CreatedOn ?? DateTime.MinValue)
                    .FirstOrDefault();
                if (archive == null)
                    throw new InvalidOperationException(v.Title + " has no archive uploaded on the LANCommander server yet.");

                var dir = LcVariants.Dir(cfg, v);

                var tmpDir = Path.Combine(cfg.InstallRoot, ".lc-downloads");
                try { Directory.CreateDirectory(tmpDir); }
                catch
                {
                    tmpDir = Path.Combine(Path.GetTempPath(), "LanCommanderDirect");
                    Directory.CreateDirectory(tmpDir);
                }
                var file = Path.Combine(tmpDir, id + ".archive");

                bool createdDir = false;
                try
                {
                    p.Text = "Downloading " + v.Title + "...";
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
                    logger.Info("LANCommander: downloaded variant " + v.Title + ", " + size + " bytes to " + file);
                    if (size < 4096)
                        throw new InvalidOperationException("Download is only " + size + " bytes, not a game archive: " + File.ReadAllText(file));

                    createdDir = !Directory.Exists(dir);
                    Directory.CreateDirectory(dir);

                    p.BytesDone = 0;
                    p.BytesTotal = 0;
                    p.Text = "Extracting " + v.Title + "...";
                    p.CurrentProgressValue = 0;

                    if (LcExtract.IsZip(file))
                        LcExtract.ExtractZip(file, dir, pct => p.CurrentProgressValue = pct, ct);
                    else
                        LcExtract.Extract7z(LcPaths.FindSevenZip(cfg), file, dir, ct);

                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        throw new InvalidOperationException("The archive extracted nothing.");
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

                // archives that wrap everything in one folder: lift the contents up one level (same idea as the base install)
                var files = Directory.GetFiles(dir);
                var subdirs = Directory.GetDirectories(dir);
                if (files.Length == 0 && subdirs.Length == 1)
                {
                    var wrapper = subdirs[0].TrimEnd('\\');
                    var temp = dir + ".lc-flatten-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    try
                    {
                        Directory.Move(wrapper, temp);
                        Directory.Delete(dir);
                        Directory.Move(temp, dir);
                    }
                    catch (Exception e)
                    {
                        logger.Warn(e, "LANCommander: could not flatten the wrapper folder of " + v.Title + ", keeping the original layout");
                        try
                        {
                            if (Directory.Exists(temp))
                            {
                                Directory.CreateDirectory(dir);
                                var restored = Path.Combine(dir, Path.GetFileName(wrapper));
                                if (!Directory.Exists(restored)) Directory.Move(temp, restored);
                            }
                        }
                        catch (Exception e2)
                        {
                            logger.Warn(e2, "LANCommander: could not restore the original layout either");
                        }
                    }
                }

                return dir;
            }
        }
    }
}
