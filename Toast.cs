using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace LanCommanderDirect
{
    enum LcToastKind
    {
        Installed,
        Updated,
        UpdateFailed
    }

    /// <summary>
    /// A small popup styled after Steam's "Download Complete" toast: dark panel, the game's square icon on the left,
    /// a small grey header with an icon ("Download Complete"), the game's name in white on one line (cut off with "..."
    /// if it is too long) and a grey line below it. It slides in from the right, fades out and removes itself.
    /// It never takes focus, ignores mouse and controller input and is not listed in Alt+Tab.
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

        // Steam's toast colours
        static readonly Color PanelColor = Color.FromArgb(0xF5, 0x17, 0x1D, 0x25);
        static readonly Color EdgeColor = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
        static readonly Color GreyText = Color.FromRgb(0x8F, 0x98, 0xA0);
        static readonly Color ErrorColor = Color.FromRgb(0xE0, 0x6C, 0x6C);

        /// <summary>Shows a popup for a game. detail is only used for UpdateFailed (the error text).</summary>
        public static void Show(IPlayniteAPI api, Game game, LcToastKind kind, string detail, bool overGames)
        {
            try
            {
                api.MainView.UIDispatcher.BeginInvoke(new Action(() => ShowCore(api, game, kind, detail, overGames)));
            }
            catch (Exception e)
            {
                logger.Warn(e, "LANCommander: could not show the popup");
            }
        }

        /// <summary>The game's icon from Playnite's database, or null if it has none or it can't be read.</summary>
        static ImageSource LoadIcon(IPlayniteAPI api, Game game)
        {
            try
            {
                if (game == null || string.IsNullOrEmpty(game.Icon)) return null;

                var path = api.Database.GetFullFilePath(game.Icon);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.DecodePixelWidth = 128;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The small header glyph: a download arrow, or a cross for a failure. Drawn as a path so no icon font is needed.</summary>
        static System.Windows.Shapes.Path HeaderGlyph(bool error)
        {
            return new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(error ? "M3,3 L11,11 M11,3 L3,11" : "M7,1 L7,9 M3,6 L7,10 L11,6 M1,13 L13,13"),
                Stroke = new SolidColorBrush(error ? ErrorColor : GreyText),
                StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 14,
                Height = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            };
        }

        static void ShowCore(IPlayniteAPI api, Game game, LcToastKind kind, string detail, bool overGames)
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

                bool error = kind == LcToastKind.UpdateFailed;
                string header, line;
                switch (kind)
                {
                    case LcToastKind.Installed: header = "Download Complete"; line = "Your game is ready to play"; break;
                    case LcToastKind.Updated: header = "Update Complete"; line = "Your game is up to date"; break;
                    default: header = "Update Failed"; line = string.IsNullOrWhiteSpace(detail) ? "Something went wrong" : detail; break;
                }

                var grey = new SolidColorBrush(GreyText);

                // header row: glyph + "Download Complete"
                var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
                headerRow.Children.Add(HeaderGlyph(error));
                headerRow.Children.Add(new TextBlock
                {
                    Text = header,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(error ? ErrorColor : GreyText),
                    VerticalAlignment = VerticalAlignment.Center
                });

                var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                texts.Children.Add(headerRow);
                texts.Children.Add(new TextBlock
                {
                    Text = game != null && !string.IsNullOrWhiteSpace(game.Name) ? game.Name : "LANCommander",
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 3, 0, 0)
                });
                texts.Children.Add(new TextBlock
                {
                    Text = line,
                    FontSize = 13,
                    Foreground = grey,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 1, 0, 0)
                });

                var panel = new DockPanel();
                var iconSource = LoadIcon(api, game);
                if (iconSource != null)
                {
                    var image = new Image
                    {
                        Source = iconSource,
                        Width = 56,
                        Height = 56,
                        Stretch = Stretch.UniformToFill,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 14, 0)
                    };
                    RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                    DockPanel.SetDock(image, Dock.Left);
                    panel.Children.Add(image);
                }
                panel.Children.Add(texts);

                var border = new Border
                {
                    Background = new SolidColorBrush(PanelColor),
                    BorderBrush = new SolidColorBrush(EdgeColor),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(14, 12, 18, 12),
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
                    Width = 360,
                    SizeToContent = SizeToContent.Height,
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

                double finalLeft, finalTop;
                if (owner == null)
                {
                    // bottom-right of the primary screen's usable area (device-independent units)
                    var area = SystemParameters.WorkArea;
                    finalLeft = area.Right - win.ActualWidth - 20;
                    finalTop = area.Bottom - win.ActualHeight - 20 - offset;
                }
                else
                {
                    // bottom-right of Playnite's window (handles display scaling)
                    var source = PresentationSource.FromVisual(owner);
                    var toDip = source != null ? source.CompositionTarget.TransformFromDevice : Matrix.Identity;
                    var topLeft = toDip.Transform(owner.PointToScreen(new Point(0, 0)));
                    finalLeft = topLeft.X + owner.ActualWidth - win.ActualWidth - 20;
                    finalTop = topLeft.Y + owner.ActualHeight - win.ActualHeight - 20 - offset;
                }

                win.Top = finalTop;
                win.Left = finalLeft + 48;

                open.Add(win);

                // slide in from the right while fading in, like Steam's toast
                var slide = new DoubleAnimation(finalLeft + 48, finalLeft, TimeSpan.FromMilliseconds(280))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                win.BeginAnimation(Window.LeftProperty, slide);
                win.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(error ? 8 : 6) };
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
