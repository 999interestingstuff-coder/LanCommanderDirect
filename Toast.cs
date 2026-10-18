using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Playnite.SDK;

namespace LanCommanderDirect
{
    /// <summary>
    /// A small popup drawn inside Playnite (bottom-right of its window), in both Desktop and Fullscreen mode.
    /// It never takes focus and ignores mouse and controller input, fades in and out, and removes itself.
    /// If Playnite's window is hidden or minimized nothing is shown; the entry in the notification panel stays.
    /// </summary>
    static class LcToast
    {
        static readonly ILogger logger = LogManager.GetLogger();
        static readonly List<Window> open = new List<Window>();

        public static void Show(IPlayniteAPI api, string title, string text, bool error = false)
        {
            try
            {
                api.MainView.UIDispatcher.BeginInvoke(new Action(() => ShowCore(api, title, text, error)));
            }
            catch (Exception e)
            {
                logger.Warn(e, "LANCommander: could not show the popup");
            }
        }

        static void ShowCore(IPlayniteAPI api, string title, string text, bool error)
        {
            try
            {
                Window owner = null;
                try { owner = api.Dialogs.GetCurrentAppWindow(); } catch { }
                if (owner == null && Application.Current != null) owner = Application.Current.MainWindow;
                if (owner == null || !owner.IsVisible || owner.WindowState == WindowState.Minimized) return;

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
                    SizeToContent = SizeToContent.WidthAndHeight,
                    MinWidth = 280,
                    MaxWidth = 460,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Owner = owner,
                    Opacity = 0,
                    Content = border
                };

                win.Show();
                win.UpdateLayout();

                // Playnite's window position in device-independent units (handles display scaling)
                var source = PresentationSource.FromVisual(owner);
                var toDip = source != null ? source.CompositionTarget.TransformFromDevice : Matrix.Identity;
                var topLeft = toDip.Transform(owner.PointToScreen(new Point(0, 0)));

                // stack above any popup that is still showing
                double offset = 0;
                foreach (var w in open) offset += w.ActualHeight + 8;

                win.Left = topLeft.X + owner.ActualWidth - win.ActualWidth - 24;
                win.Top = topLeft.Y + owner.ActualHeight - win.ActualHeight - 24 - offset;

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
