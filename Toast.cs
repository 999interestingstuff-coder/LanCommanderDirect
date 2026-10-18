using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace LanCommanderDirect
{
    /// <summary>A Windows popup (tray balloon / toast) that does not block Playnite and goes away on its own.</summary>
    static class LcToast
    {
        public static void Show(string title, string text, bool error = false)
        {
            try
            {
                // NotifyIcon wants its own thread with a message pump; the icon is removed again once the popup is gone
                var thread = new Thread(() =>
                {
                    try
                    {
                        using (var icon = new NotifyIcon())
                        {
                            icon.Icon = error ? SystemIcons.Error : SystemIcons.Information;
                            icon.BalloonTipTitle = title;
                            icon.BalloonTipText = text;
                            icon.BalloonTipIcon = error ? ToolTipIcon.Error : ToolTipIcon.Info;
                            icon.Visible = true;
                            icon.ShowBalloonTip(8000);

                            for (int i = 0; i < 100; i++)
                            {
                                Application.DoEvents();
                                Thread.Sleep(100);
                            }
                            icon.Visible = false;
                        }
                    }
                    catch { /* a popup is a nicety, never a failure */ }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
            }
            catch { }
        }
    }
}
