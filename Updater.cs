using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace LanCommanderDirect
{
    /// <summary>
    /// Runs game updates one at a time in the background (a queue, so auto-update of several games doesn't
    /// download them all at once). Progress shows in the notification panel like an install.
    /// </summary>
    static class LcUpdater
    {
        static readonly ILogger logger = LogManager.GetLogger();
        static readonly object gate = new object();
        static readonly Queue<Game> queue = new Queue<Game>();
        static readonly HashSet<Guid> queued = new HashSet<Guid>();
        static bool busy;

        public static void Enqueue(IPlayniteAPI api, LanCommanderLibrary plugin, IEnumerable<Game> games)
        {
            lock (gate)
            {
                foreach (var g in games)
                    if (g != null && queued.Add(g.Id))
                        queue.Enqueue(g);

                if (busy) return;
                busy = true;
            }
            Next(api, plugin);
        }

        static void Next(IPlayniteAPI api, LanCommanderLibrary plugin)
        {
            Game game;
            lock (gate)
            {
                if (queue.Count == 0)
                {
                    busy = false;
                    return;
                }
                game = queue.Dequeue();
                queued.Remove(game.Id);
            }

            Run(api, plugin, game, () => Next(api, plugin));
        }

        static void Run(IPlayniteAPI api, LanCommanderLibrary plugin, Game game, Action done)
        {
            var cfg = plugin.LoadConfig();
            if (cfg == null || string.IsNullOrEmpty(game.GameId))
            {
                done();
                return;
            }

            if (!LcJobs.TryBegin(game.Id))
            {
                // already installing or updating
                done();
                return;
            }

            var noteId = "lc-update-" + game.Id;
            var name = game.Name;
            var progress = LcJobs.MakeProgress(api, noteId, CancellationToken.None);

            Task.Run(() =>
            {
                LcGame info = null;
                Exception error = null;

                try { info = LcArchiveOps.Update(cfg, game, progress); }
                catch (Exception e) { error = e; }

                try
                {
                    api.MainView.UIDispatcher.Invoke(() =>
                    {
                        api.Notifications.Remove(noteId);

                        if (error != null)
                        {
                            logger.Error(error, "LANCommander update failed for " + name);
                            api.Notifications.Add(new NotificationMessage(noteId, name + ": update failed: " + error.Message, NotificationType.Error));
                            if (cfg.PopupNotifications) LcToast.Show(api, game, LcToastKind.UpdateFailed, error.Message, cfg.PopupOverGames);
                            return;
                        }

                        if (info != null && !string.IsNullOrWhiteSpace(info.Version))
                        {
                            game.Version = info.Version;
                            api.Database.Games.Update(game);
                        }
                        api.Notifications.Add(new NotificationMessage(noteId, name + " is updated.", NotificationType.Info));
                        if (cfg.PopupNotifications) LcToast.Show(api, game, LcToastKind.Updated, null, cfg.PopupOverGames);
                    });
                }
                catch (Exception e)
                {
                    logger.Warn(e, "LANCommander: could not report the update result for " + name);
                }
                finally
                {
                    LcJobs.End(game.Id);
                    done();
                }
            });
        }
    }
}
