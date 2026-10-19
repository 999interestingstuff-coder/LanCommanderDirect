# LANCommander Direct + Playnite

This version is intentionally split into two responsibilities:

- **LANCommander Direct**: game library import, metadata, installation/uninstallation, update detection, launch integration, and LANCommander play-session reporting.
- **Ludusavi + Ludusavi for Playnite**: all save backup/restore handling.

The LANCommander plugin no longer downloads, extracts, creates, or uploads LANCommander save archives and no longer uses LANCommander `savePaths`.

## Playtime behavior

Playnite remains the local source of truth for playtime. When a LANCommander game starts, this plugin calls LANCommander's play-session start endpoint. When Playnite reports the game stopped, the plugin calls LANCommander's play-session end endpoint and logs Playnite's `ElapsedSeconds` for that session.

LANCommander's public PlaySessions API is session-based; this plugin does not invent a private/unsupported endpoint for setting an absolute Playnite total. If your particular LANCommander server build exposes a separate endpoint for importing an absolute playtime total, that endpoint can be added later.

## Save behavior

Install both:

1. Ludusavi itself.
2. The official `ludusavi-playnite` Playnite extension.

Enable automatic restore before play and automatic backup after play in the Ludusavi Playnite extension. The extension uses Ludusavi's manifest to find save locations for thousands of games.

Official projects:

- https://github.com/mtkennerly/ludusavi
- https://github.com/mtkennerly/ludusavi-playnite

## Building

The project targets .NET Framework 4.6.2 and Playnite SDK 6.11. Build it on a Windows machine with the required .NET Framework targeting/build tools installed.

After building, copy the generated extension files into your Playnite Extensions folder. For a normal installed Playnite installation this is normally `%AppData%\\Playnite\\Extensions\\<folder>`.

## Important migration note

Older versions of this plugin stored LANCommander save-sync state in `saves.json` and could have local save-backup ZIPs under the plugin data directory. They are no longer used. You may delete those old files after confirming Ludusavi is working.

## Install folder layout (0.2.3)

If a game's archive has a single top-level folder inside it, the plugin now lifts that folder's contents up one
level, so a game lands in `InstallRoot\Game name\` and not `InstallRoot\Game name\Game name\`.
Games installed with an older version keep their old layout until they are uninstalled and installed again.

## Launch actions from the server (0.2.4)

If a game has a launch action on the LANCommander server, install copies its path, arguments and working directory into
Playnite's Play action (the primary action, or the first one). If the exact path from the server does not exist after
install, the same file name is searched for inside the install folder, so a lifted-up wrapper folder doesn't lose the
arguments. Games without a server action still get the guessed exe.

## Background installs (0.2.5)

Install no longer opens a blocking progress window. The download and extraction run in the background, the game shows
as "installing", and progress is shown in Playnite's notification panel (bell icon). Cancelling the install in Playnite
cancels the download. The downloaded archive is kept in `.lc-downloads` inside the install folder, so a large game
doesn't fill up the system drive's temp folder, and it is deleted afterwards.

## Progress notifications (0.2.7)

The install progress notification is now replaced by removing the previous one and adding a new one, on Playnite's UI
thread, with the downloaded and total size. Progress is also written to extensions.log every 10%, so it can be checked
there if a notification doesn't refresh.

## Detecting installed games and updates (0.3.0)

Nothing is written into the game folders. The plugin keeps its own record of installs (`installs.json` in the plugin's data
folder: game id, folder and archive id). After a library update, and on startup, it scans the install folder:

- a game with a record whose folder still exists is matched by id, even if Playnite lost the install state;
- any other game is matched by name: a folder in the install folder named like the game (and containing an `.exe`).

Matched games are marked installed and get a Play action (the server's launch action if there is one, otherwise a guessed exe).
There is also **LANCommander > Scan install folder for installed games** in Playnite's main menu. Games found by name are assumed
to be on the newest archive.

When the newest archive on the server differs from the one recorded for the game, a notification lists the games. Update one from the
game's right-click menu (**LANCommander > Update**), or turn on **auto-update** in the settings. An update downloads the newest
archive and extracts it **over** the existing folder: files that are in the archive are replaced, files the game wrote itself
(saves, configs) are left alone. Updates run one at a time.

## Server as the source of truth (0.3.0)

On every library update the plugin keeps a snapshot of what the server says (`server-state.json`) and applies it to games
Playnite already has:

- `Logo.png`, `VideoTrailer.mp4` and `VideoMicrotrailer.mp4` are copied into Extra Metadata (`ExtraMetadata\games\<id>\`) whenever the server's file is new or the target is missing.
- Title, description, version, release date, developers, publishers, genres and the icon / cover / background are overwritten
  from the server. Tags and features are only added to, so your own tags are kept. Turn this off with **Server is the source of truth** in the settings.

Videos are added in LANCommander as media of type **Video**. A video whose **Name** contains `micro` is the micro trailer; any
other video is the trailer. Videos are recognised by their MIME type, and should be mp4.

## Popups (0.3.6)

When a game has been installed or updated, a popup styled after Steam's "Download Complete" toast slides in at the bottom-right
(in addition to the entry in Playnite's notification panel): a dark panel, the game's icon on the left, a small grey header
("Download Complete" for an install, "Update Complete" for an update, "Update Failed" with the error underneath if an update
failed), the game's name in white on one line, and a grey line below it. It fades out after a few seconds. It never takes
focus, ignores mouse and controller input and is not listed in Alt+Tab.

- **Over games** (default): an always-on-top popup in the bottom-right of the primary screen, so you are told while a game is
  running. It shows over windowed and borderless games; Windows does not let a normal window draw over a game in exclusive
  fullscreen. It also shows if Playnite is minimized.
- **Inside Playnite only**: turn off *Show that popup over everything...* and it is drawn over Playnite's own window (Desktop
  and Fullscreen mode) instead. Nothing is shown while Playnite is hidden or minimized.

Turn popups off completely with *Show a popup when a game has been installed or updated*. No Windows notifications are used.

The popup also has a shine that sweeps across the card as it appears, a thin progress line along the bottom that shrinks while
it is showing, and it slides out to the right when it goes away.

**Sound:** in the plugin's settings, tick *Play a sound with the popup*, press *Browse...* and pick a sound file (wav, mp3, wma,
m4a, aac), set the volume with the slider and press *Test* to hear it. *Clear* removes the file (silent). The file is played from
where it is, so don't move or delete it afterwards.

## Downloads (0.4.0)

- **Resume.** An unfinished download is kept when you cancel the install in Playnite, or when the connection drops. Install
  (or update) the game again and it continues where it stopped instead of starting over. A dropped connection is also retried
  automatically a few times. (If the server doesn't support continuing, the download starts from the beginning.) Unfinished
  downloads of an old version, or older than two weeks, are cleaned up. They live in `.lc-downloads` inside the install folder.
- **Speed and time left** are shown in the progress notification while downloading.
- **Queue.** Only one install or update downloads at a time; the others wait ("waiting for another download to finish") and
  start in order. Cancelling a waiting install removes it from the queue.
- **Free space check.** Before downloading, the plugin checks that the download and the extracted game fit on the drive(s)
  and stops with a message telling you how much is needed if they don't.
- **Update Available popup.** When a game on the server has a newer archive and auto-update is off, you get the same popup
  (at most three, once per game per Playnite session) telling you to update it from the game's menu.
- **Failed installs** now show the red popup too (the error dialog only appears when popups are turned off).

