using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace LanCommanderDirect
{
    /// <summary>
    /// Verify / repair: downloads the game's archive (resumable, queued like an install), compares every file in it with the
    /// installed copy and, after asking, puts back the files that are missing or different. Files that are not in the
    /// archive (saves, configs the game created) are never touched. Works with zip archives.
    /// Quick compares file sizes; full also compares the contents.
    /// </summary>
    static class LcVerify
    {
        static readonly ILogger logger = LogManager.GetLogger();

        class Problem
        {
            public ZipArchiveEntry Entry;
            public string Target;
            public bool Missing;
        }

        public static void Start(IPlayniteAPI api, LanCommanderLibrary plugin, List<Game> games, bool deep)
        {
            if (games.Count != 1)
            {
                api.Dialogs.ShowMessage("Select a single game to verify.", "LANCommander");
                return;
            }

            var game = games[0];
            var cfg = plugin.LoadConfig();
            if (cfg == null || string.IsNullOrEmpty(game.GameId)) return;

            var installDir = (game.InstallDirectory ?? "").TrimEnd('\\');
            if (!Directory.Exists(installDir))
            {
                api.Dialogs.ShowErrorMessage("The install folder no longer exists: " + installDir, "LANCommander");
                return;
            }

            if (!LcJobs.TryBegin(game.Id))
            {
                api.Dialogs.ShowMessage("An install, update or verification of this game is already running.", "LANCommander");
                return;
            }

            var noteId = "lc-verify-" + game.Id;
            var name = game.Name;
            var progress = LcJobs.MakeProgress(api, noteId, CancellationToken.None);

            Task.Run(() =>
            {
                string summary = null;
                Exception error = null;
                bool gotSlot = false;

                try
                {
                    LcJobs.AcquireSlot(CancellationToken.None, () => LcJobs.ShowQueued(api, noteId, name));
                    gotSlot = true;
                    summary = Run(api, cfg, game, installDir, deep, progress);
                }
                catch (Exception e) { error = e; }
                finally { if (gotSlot) LcJobs.ReleaseSlot(); }

                try
                {
                    api.MainView.UIDispatcher.Invoke(() =>
                    {
                        api.Notifications.Remove(noteId);

                        if (error is OperationCanceledException)
                            return;     // the user chose to change nothing

                        if (error != null)
                        {
                            logger.Error(error, "LANCommander verify failed for " + name);
                            if (error is LcServerUnreachableException)
                                plugin.NoteServerUnreachable(cfg, false);
                            api.Notifications.Add(new NotificationMessage(noteId, name + ": verification failed: " + error.Message, NotificationType.Error));
                            if (cfg.PopupNotifications) LcToast.Show(api, game, LcToastKind.VerifyFailed, error.Message, cfg);
                            return;
                        }

                        plugin.NoteServerOk(cfg);
                        api.Notifications.Add(new NotificationMessage(noteId, name + ": " + summary + ".", NotificationType.Info));
                        if (cfg.PopupNotifications) LcToast.Show(api, game, LcToastKind.VerifyDone, summary, cfg);
                    });
                }
                catch (Exception e)
                {
                    logger.Warn(e, "LANCommander: could not report the verify result for " + name);
                }
                finally
                {
                    LcJobs.End(game.Id);
                }
            });
        }

        static string Run(IPlayniteAPI api, LcConfig cfg, Game game, string installDir, bool deep, LcProgress p)
        {
            var id = Guid.Parse(game.GameId);
            var ct = p.CancelToken;

            using (var client = new LcClient(cfg))
            {
                var detail = Task.Run(() => client.GetGameAsync(id, ct)).GetAwaiter().GetResult();
                var archive = detail.LatestArchive();
                if (archive == null)
                    throw new InvalidOperationException("This game has no archive uploaded on the LANCommander server.");

                // the files are checked against the newest archive, so an outdated install would show everything that changed as damaged
                var record = LcInstallRegistry.Get(id.ToString());
                if (record != null && record.ArchiveId.HasValue && record.ArchiveId.Value != archive.Id)
                    throw new InvalidOperationException("A newer version of this game is on the server. Update the game first; verifying compares the files with the newest version.");

                var file = Path.Combine(LcArchiveOps.DownloadDir(cfg), id + ".verify.archive");
                try
                {
                    LcArchiveOps.CheckSpace(cfg, installDir, archive, false, file, false);
                    LcArchiveOps.Download(client, id, archive, file, game.Name, p, ct);

                    if (!LcExtract.IsZip(file))
                        throw new InvalidOperationException("Verify works with zip archives only, and this game's archive is not a zip.");

                    return Compare(api, game, installDir, file, deep, p, ct);
                }
                finally
                {
                    try { if (File.Exists(file)) File.Delete(file); } catch { }
                }
            }
        }

        /// <summary>The install lifts the contents of a single wrapper folder one level up; do the same here when matching paths.</summary>
        static string WrapperPrefix(List<ZipArchiveEntry> files)
        {
            if (files.Count == 0) return "";

            var paths = files.Select(e => e.FullName.Replace('/', '\\')).ToList();
            if (paths.Any(x => x.IndexOf('\\') < 0)) return "";

            var tops = paths.Select(x => x.Substring(0, x.IndexOf('\\'))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return tops.Count == 1 ? tops[0] + "\\" : "";
        }

        static string Compare(IPlayniteAPI api, Game game, string installDir, string zipPath, bool deep, LcProgress p, CancellationToken ct)
        {
            var destFull = Path.GetFullPath(installDir).TrimEnd('\\') + "\\";

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var files = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                var prefix = WrapperPrefix(files);

                p.BytesDone = 0;
                p.BytesTotal = 0;
                p.BytesPerSecond = 0;
                p.Text = (deep ? "Comparing the files of " : "Checking the files of ") + game.Name + "...";
                p.CurrentProgressValue = 0;

                long total = Math.Max(1, files.Sum(e => e.Length));
                long done = 0;
                var problems = new List<Problem>();

                foreach (var entry in files)
                {
                    ct.ThrowIfCancellationRequested();

                    var rel = entry.FullName.Replace('/', '\\');
                    if (prefix.Length > 0 && rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        rel = rel.Substring(prefix.Length);

                    var target = Path.GetFullPath(Path.Combine(installDir, rel));
                    if (!target.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Archive contains an unsafe path: " + entry.FullName);

                    if (!File.Exists(target))
                        problems.Add(new Problem { Entry = entry, Target = target, Missing = true });
                    else if (new FileInfo(target).Length != entry.Length)
                        problems.Add(new Problem { Entry = entry, Target = target, Missing = false });
                    else if (deep && !SameContent(entry, target, ct))
                        problems.Add(new Problem { Entry = entry, Target = target, Missing = false });

                    done += entry.Length;
                    p.CurrentProgressValue = done * 100.0 / total;
                }

                if (problems.Count == 0)
                    return "All " + files.Count + " files are intact";

                var missing = problems.Where(x => x.Missing).ToList();
                var changed = problems.Where(x => !x.Missing).ToList();
                var toRestore = Ask(api, game, missing, changed, installDir, deep);

                p.Text = "Restoring files of " + game.Name + "...";
                p.CurrentProgressValue = 0;
                long restoreTotal = Math.Max(1, toRestore.Sum(x => x.Entry.Length));
                long restored = 0;

                foreach (var problem in toRestore)
                {
                    ct.ThrowIfCancellationRequested();

                    Directory.CreateDirectory(Path.GetDirectoryName(problem.Target));
                    if (File.Exists(problem.Target))
                        File.SetAttributes(problem.Target, FileAttributes.Normal);
                    problem.Entry.ExtractToFile(problem.Target, true);

                    restored += problem.Entry.Length;
                    p.CurrentProgressValue = restored * 100.0 / restoreTotal;
                }

                var left = problems.Count - toRestore.Count;
                return toRestore.Count + " file(s) restored" + (left > 0 ? ", " + left + " left as they are" : "");
            }
        }

        /// <summary>Tells the user what was found and what can be restored. Throws OperationCanceledException if they choose to change nothing.</summary>
        static List<Problem> Ask(IPlayniteAPI api, Game game, List<Problem> missing, List<Problem> changed, string installDir, bool deep)
        {
            var examples = string.Join("\n", missing.Concat(changed).Take(8).Select(x =>
                "  " + x.Target.Substring(Math.Min(x.Target.Length, installDir.TrimEnd('\\').Length + 1))));
            var more = missing.Count + changed.Count > 8 ? "\n  ..." : "";

            string text;
            MessageBoxButton buttons;
            if (missing.Count > 0 && changed.Count > 0)
            {
                buttons = MessageBoxButton.YesNoCancel;
                text = missing.Count + " file(s) are missing and " + changed.Count + " differ from the archive" + (deep ? "" : " (in size)") + ":\n\n" +
                       examples + more + "\n\nYes = restore missing and changed files (files the game saved itself, such as settings, go back to their original state).\n" +
                       "No = restore only the missing files.\nCancel = change nothing.";
            }
            else if (missing.Count > 0)
            {
                buttons = MessageBoxButton.YesNo;
                text = missing.Count + " file(s) are missing:\n\n" + examples + more + "\n\nRestore them from the archive?";
            }
            else
            {
                buttons = MessageBoxButton.YesNo;
                text = changed.Count + " file(s) differ from the archive" + (deep ? "" : " (in size)") + ":\n\n" + examples + more +
                       "\n\nReplace them with the originals? (Settings or saves the game keeps in its own folder can show up here; replacing them resets them.)";
            }

            var answer = MessageBoxResult.Cancel;
            api.MainView.UIDispatcher.Invoke(() =>
            {
                answer = api.Dialogs.ShowMessage(text, "LANCommander: " + game.Name, buttons, MessageBoxImage.Question);
            });

            if (answer == MessageBoxResult.Yes)
                return missing.Concat(changed).ToList();
            if (answer == MessageBoxResult.No && missing.Count > 0 && changed.Count > 0)
                return missing;

            throw new OperationCanceledException();
        }

        static bool SameContent(ZipArchiveEntry entry, string path, CancellationToken ct)
        {
            try
            {
                using (var a = entry.Open())
                using (var b = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var bufferA = new byte[81920];
                    var bufferB = new byte[81920];
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        int readA = ReadFull(a, bufferA);
                        int readB = ReadFull(b, bufferB);
                        if (readA != readB) return false;
                        if (readA == 0) return true;
                        for (int i = 0; i < readA; i++)
                            if (bufferA[i] != bufferB[i]) return false;
                    }
                }
            }
            catch (IOException)
            {
                return true;    // in use by the game or unreadable: leave it alone rather than call it damaged
            }
        }

        static int ReadFull(Stream s, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = s.Read(buffer, total, buffer.Length - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }
    }
}
