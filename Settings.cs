using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Playnite.SDK;

namespace LanCommanderDirect
{
    /// <summary>
    /// Backs the settings page (Add-ons -> Libraries -> LANCommander (Direct) -> Settings).
    /// The values live in config.json, the same file the plugin reads everywhere else.
    /// </summary>
    public class LcSettings : ObservableObject, ISettings
    {
        readonly LanCommanderLibrary plugin;
        LcConfig backup;

        string serverUrl = "";
        string username = "";
        string password = "";
        string installRoot = "";
        string sevenZipPath = "";
        bool trackPlayTime = true;
        bool autoUpdate = false;
        bool syncMetadata = true;
        bool syncVideos = true;
        bool popupNotifications = true;
        bool popupOverGames = true;
        bool popupSound = true;
        string popupSoundPath = "";
        string popupSoundFailPath = "";
        string popupSoundAvailablePath = "";
        int popupSoundVolume = 70;

        public string ServerUrl { get { return serverUrl; } set { SetValue(ref serverUrl, value); } }
        public string Username { get { return username; } set { SetValue(ref username, value); } }
        public string Password { get { return password; } set { SetValue(ref password, value); } }
        public string InstallRoot { get { return installRoot; } set { SetValue(ref installRoot, value); } }
        public string SevenZipPath { get { return sevenZipPath; } set { SetValue(ref sevenZipPath, value); } }
        public bool TrackPlayTime { get { return trackPlayTime; } set { SetValue(ref trackPlayTime, value); } }
        public bool AutoUpdate { get { return autoUpdate; } set { SetValue(ref autoUpdate, value); } }
        public bool SyncMetadata { get { return syncMetadata; } set { SetValue(ref syncMetadata, value); } }
        public bool SyncVideos { get { return syncVideos; } set { SetValue(ref syncVideos, value); } }
        public bool PopupNotifications { get { return popupNotifications; } set { SetValue(ref popupNotifications, value); } }
        public bool PopupOverGames { get { return popupOverGames; } set { SetValue(ref popupOverGames, value); } }
        public bool PopupSound { get { return popupSound; } set { SetValue(ref popupSound, value); } }
        public string PopupSoundPath { get { return popupSoundPath; } set { SetValue(ref popupSoundPath, value); } }
        public string PopupSoundFailPath { get { return popupSoundFailPath; } set { SetValue(ref popupSoundFailPath, value); } }
        public string PopupSoundAvailablePath { get { return popupSoundAvailablePath; } set { SetValue(ref popupSoundAvailablePath, value); } }
        public int PopupSoundVolume { get { return popupSoundVolume; } set { SetValue(ref popupSoundVolume, value); } }

        public LcSettings(LanCommanderLibrary plugin)
        {
            this.plugin = plugin;
            Apply(plugin.ReadRawConfig());
        }

        void Apply(LcConfig c)
        {
            ServerUrl = c.ServerUrl ?? "";
            Username = c.Username ?? "";
            Password = c.Password ?? "";
            InstallRoot = c.InstallRoot ?? "";
            SevenZipPath = c.SevenZipPath ?? "";
            TrackPlayTime = c.TrackPlayTime;
            AutoUpdate = c.AutoUpdate;
            SyncMetadata = c.SyncMetadata;
            SyncVideos = c.SyncVideos;
            PopupNotifications = c.PopupNotifications;
            PopupOverGames = c.PopupOverGames;
            PopupSound = c.PopupSound;
            PopupSoundPath = c.PopupSoundPath ?? "";
            PopupSoundFailPath = c.PopupSoundFailPath ?? "";
            PopupSoundAvailablePath = c.PopupSoundAvailablePath ?? "";
            PopupSoundVolume = Math.Max(0, Math.Min(100, c.PopupSoundVolume));
        }

        public LcConfig ToConfig()
        {
            return new LcConfig
            {
                ServerUrl = (ServerUrl ?? "").Trim(),
                Username = (Username ?? "").Trim(),
                Password = Password ?? "",
                InstallRoot = (InstallRoot ?? "").Trim(),
                SevenZipPath = (SevenZipPath ?? "").Trim(),
                TrackPlayTime = TrackPlayTime,
                AutoUpdate = AutoUpdate,
                SyncMetadata = SyncMetadata,
                SyncVideos = SyncVideos,
                PopupNotifications = PopupNotifications,
                PopupOverGames = PopupOverGames,
                PopupSound = PopupSound,
                PopupSoundPath = (PopupSoundPath ?? "").Trim(),
                PopupSoundFailPath = (PopupSoundFailPath ?? "").Trim(),
                PopupSoundAvailablePath = (PopupSoundAvailablePath ?? "").Trim(),
                PopupSoundVolume = PopupSoundVolume
            };
        }

        public void BeginEdit()
        {
            // reload from disk in case config.json was edited by hand since startup
            Apply(plugin.ReadRawConfig());
            backup = ToConfig();
        }

        public void CancelEdit()
        {
            if (backup != null) Apply(backup);
        }

        public void EndEdit()
        {
            plugin.WriteConfig(ToConfig());
            try { plugin.PlayniteApi.Notifications.Remove("lc-config"); } catch { }
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();

            Uri uri;
            if (string.IsNullOrWhiteSpace(ServerUrl) ||
                !Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                errors.Add("Server URL must look like http://192.168.1.104:1337");

            if (string.IsNullOrWhiteSpace(Username))
                errors.Add("Username is empty.");
            if (string.IsNullOrEmpty(Password))
                errors.Add("Password is empty.");

            return errors.Count == 0;
        }
    }

    /// <summary>Settings page built in code, so the project needs no XAML compilation.</summary>
    public class LcSettingsView : UserControl
    {
        public LcSettingsView(LcSettings settings, IPlayniteAPI api)
        {
            DataContext = settings;

            var panel = new StackPanel { Margin = new Thickness(20) };

            panel.Children.Add(Label("Server URL (for example http://192.168.1.104:1337)"));
            panel.Children.Add(Box("ServerUrl"));

            panel.Children.Add(Label("Username"));
            panel.Children.Add(Box("Username"));

            panel.Children.Add(Label("Password"));
            var pb = new PasswordBox { Margin = new Thickness(0, 0, 0, 12), Password = settings.Password ?? "" };
            pb.PasswordChanged += (s, e) => { if (settings.Password != pb.Password) settings.Password = pb.Password; };
            settings.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "Password" && pb.Password != (settings.Password ?? ""))
                    pb.Password = settings.Password ?? "";
            };
            panel.Children.Add(pb);

            panel.Children.Add(Label("Install folder (empty = Games\\LANCommander in your user folder)"));
            var rootRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var browse = new Button { Content = "Browse...", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
            DockPanel.SetDock(browse, Dock.Right);
            browse.Click += (s, e) =>
            {
                var folder = api.Dialogs.SelectFolder();
                if (!string.IsNullOrEmpty(folder)) settings.InstallRoot = folder;
            };
            rootRow.Children.Add(browse);
            rootRow.Children.Add(Box("InstallRoot", 0));
            panel.Children.Add(rootRow);

            panel.Children.Add(Label("7z.exe path (only needed for archives that are not zip; empty = auto-detect)"));
            panel.Children.Add(Box("SevenZipPath"));

            var timeBox = new CheckBox
            {
                Content = "Report play sessions to LANCommander",
                Margin = new Thickness(0, 0, 0, 12)
            };
            timeBox.SetBinding(CheckBox.IsCheckedProperty, new Binding("TrackPlayTime") { Mode = BindingMode.TwoWay });
            panel.Children.Add(timeBox);

            panel.Children.Add(Check("PopupNotifications", "Show a popup when a game has been installed or updated"));
            panel.Children.Add(Check("PopupOverGames", "Show that popup over everything, including running borderless/windowed games (off = only inside Playnite's window)"));
            panel.Children.Add(Check("PopupSound", "Play a sound with the popup (choose the file below; no file = silent)"));
            AddSoundRow(panel, settings, api, "Sound when an install, update or verification finishes (wav, mp3, wma...)", "PopupSoundPath", () => settings.PopupSoundPath);
            AddSoundRow(panel, settings, api, "Sound when something fails (failed install/update, server unreachable)", "PopupSoundFailPath", () => settings.PopupSoundFailPath);
            AddSoundRow(panel, settings, api, "Sound when an update is available", "PopupSoundAvailablePath", () => settings.PopupSoundAvailablePath);

            panel.Children.Add(Label("Popup sound volume"));
            var volume = new Slider { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 0, 0, 12) };
            volume.SetBinding(Slider.ValueProperty, new Binding("PopupSoundVolume") { Mode = BindingMode.TwoWay });
            panel.Children.Add(volume);

            panel.Children.Add(Check("AutoUpdate", "Update games automatically when a newer archive is uploaded to the server (off = update from the game's right-click menu)"));
            panel.Children.Add(Check("SyncMetadata", "Server is the source of truth: overwrite description, developers, genres, images... from LANCommander on every library update"));
            panel.Children.Add(Check("SyncVideos", "Download trailer / micro trailer videos from the server into Extra Metadata (a video named \"micro\" is the micro trailer)"));

            var test = new Button
            {
                Content = "Test connection",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 6, 0, 0)
            };
            test.Click += async (s, e) =>
            {
                try
                {
                    using (var client = new LcClient(settings.ToConfig()))
                    {
                        var games = await client.GetGamesAsync(CancellationToken.None);
                        api.Dialogs.ShowMessage("Connected. The server has " + games.Count + " game(s).", "LANCommander");
                    }
                }
                catch (Exception ex)
                {
                    api.Dialogs.ShowErrorMessage(ex.Message, "LANCommander connection failed");
                }
            };
            panel.Children.Add(test);

            panel.Children.Add(new TextBlock
            {
                Text = "Saves are handled exclusively by the Ludusavi Playnite extension. The install folder above is also scanned for games that are already installed. Click Save in the add-ons window to keep your changes. The password is stored as plain text in config.json.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                Margin = new Thickness(0, 16, 0, 0)
            });

            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        }

        /// <summary>A label plus a path box with Browse / Clear / Test buttons for one sound.</summary>
        static void AddSoundRow(StackPanel panel, LcSettings settings, IPlayniteAPI api, string label, string bindingPath, Func<string> currentPath)
        {
            panel.Children.Add(Label(label));
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var test = new Button { Content = "Test", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
            var clear = new Button { Content = "Clear", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
            var browse = new Button { Content = "Browse...", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
            DockPanel.SetDock(test, Dock.Right);
            DockPanel.SetDock(clear, Dock.Right);
            DockPanel.SetDock(browse, Dock.Right);

            var property = typeof(LcSettings).GetProperty(bindingPath);
            browse.Click += (s, e) =>
            {
                var file = api.Dialogs.SelectFile("Audio files|*.wav;*.mp3;*.wma;*.m4a;*.aac|All files|*.*");
                if (!string.IsNullOrEmpty(file)) property.SetValue(settings, file, null);
            };
            clear.Click += (s, e) => property.SetValue(settings, "", null);
            test.Click += (s, e) => LcToast.PlaySound(currentPath(), settings.PopupSoundVolume);

            row.Children.Add(test);
            row.Children.Add(clear);
            row.Children.Add(browse);
            row.Children.Add(Box(bindingPath, 0));
            panel.Children.Add(row);
        }

        static CheckBox Check(string bindingPath, string text)
        {
            var cb = new CheckBox
            {
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                Margin = new Thickness(0, 0, 0, 12)
            };
            cb.SetBinding(CheckBox.IsCheckedProperty, new Binding(bindingPath) { Mode = BindingMode.TwoWay });
            return cb;
        }

        static TextBlock Label(string text)
        {
            return new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 4) };
        }

        static TextBox Box(string bindingPath, double bottomMargin = 12)
        {
            var tb = new TextBox { Margin = new Thickness(0, 0, 0, bottomMargin) };
            tb.SetBinding(TextBox.TextProperty, new Binding(bindingPath)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
            return tb;
        }
    }
}
