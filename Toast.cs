using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Playnite.SDK;

namespace LanCommanderDirect
{
    /// <summary>
    /// A small popup that never takes focus and ignores mouse and controller input, fades in and out and removes itself.
    ///
    /// Over games (default): an always-on-top window in the bottom-right corner of the primary screen, so it also shows
    /// while a game is running. It appears over windowed and borderless games, but Windows does not allow a normal
    /// window to draw over a game in exclusive fullscreen.
    ///
    /// Inside Playnite only: drawn over Playnite's own window (Desktop and Fullscreen mode); nothing is shown while
    /// Playnite is hidden or minimized.
    ///
    /// The entry in Playnite's notification panel is always added separately.
    /// </summary>
    static class LcToast
    {
        static readonly ILogger logger = LogManager.GetLogger();
        static readonly List<Window> open = new List<Window>();

        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x00000020;   // clicks go straight through
        const int WS_EX_TOOLWINDOW = 0x00000080;    // not in Alt+Tab
        const int WS_EX_NOACTIVATE = 0x08000000;    // never becomes the active window

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public static void Show(IPlayniteAPI api, string title, string text, bool error, bool overGames)
        {
            try
            {
                api.MainView.UIDispatcher.BeginInvoke(new Action(() => ShowCore(api, title, text, error, overGames)));
            }
            catch (Exception e)
            {
                logger.Warn(e, "LANCommander: could not show the popup");
            }
        }

        static void ShowCore(IPlayniteAPI api, string title, string text, bool error, bool overGames)
        {
            try
            {
                Window owner = null;
                if (!overGames)
                {
                    try { owner = api.Dialogs.GetCurrentAppWindow(); } catch { }
                    if (owner == null && Application.Current != null) owner = Application.Current.MainWindow;
                    if (owner == null || !owner.IsVisible || owner.WindowState == WindowState.Minimized) return;
                }

                var accent = error ? Color.FromRgb(0xE0, 0x4F, 0x4F) : Color.FromRgb(0x3D, 0x8B, 0xFF);

                var panel = new StackPanel();
                panel.Children.Add(new TextBlock
                {
                    Text = title,
                    FontWeight = FontWeights.Bold,
                    FontSize = 16,
                    Foreground = Brushes.White
                });
                panel.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });

                var border = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x1F, 0x26)),
                    BorderBrush = new SolidColorBrush(accent),
                    BorderThickness = new Thickness(4, 0, 0, 0),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(16, 12, 16, 12),
                    Child = panel
                };

                var win = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Focusable = false,
                    IsHitTestVisible = false,
                    Topmost = overGames,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    MinWidth = 280,
                    MaxWidth = 460,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Opacity = 0,
                    Content = border
                };
                if (owner != null) win.Owner = owner;

                // no focus, no click capture, no Alt+Tab entry: it must never get in the way of a game
                win.SourceInitialized += (s, e) =>
                {
                    try
                    {
                        var handle = new WindowInteropHelper(win).Handle;
                        SetWindowLong(handle, GWL_EXSTYLE,
                            GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
                    }
                    catch { }
                };

                win.Show();
                win.UpdateLayout();

                // stack above any popup that is still showing
                double offset = 0;
                foreach (var w in open) offset += w.ActualHeight + 8;

                if (owner == null)
                {
                    // bottom-right of the primary screen's usable area (device-independent units)
                    var area = SystemParameters.WorkArea;
                    win.Left = area.Right - win.ActualWidth - 24;
                    win.Top = area.Bottom - win.ActualHeight - 24 - offset;
                }
                else
                {
                    // bottom-right of Playnite's window (handles display scaling)
                    var source = PresentationSource.FromVisual(owner);
                    var toDip = source != null ? source.CompositionTarget.TransformFromDevice : Matrix.Identity;
                    var topLeft = toDip.Transform(owner.PointToScreen(new Point(0, 0)));
                    win.Left = topLeft.X + owner.ActualWidth - win.ActualWidth - 24;
                    win.Top = topLeft.Y + owner.ActualHeight - win.ActualHeight - 24 - offset;
                }

                open.Add(win);
                win.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400));
                    fade.Completed += (s2, e2) =>
                    {
                        open.Remove(win);
                        try { win.Close(); } catch { }
                    };
                    win.BeginAnimation(UIElement.OpacityProperty, fade);
                };
                timer.Start();
            }
            catch (Exception e)
            {
                logger.Warn(e, "LANCommander: could not show the popup");
            }
        }
    }
}
