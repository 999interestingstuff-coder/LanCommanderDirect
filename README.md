# LANCommander Direct + Playnite

This version is intentionally split into two responsibilities:

- **LANCommander Direct**: game library import, metadata, installation/uninstallation, launch integration, and LANCommander play-session reporting.
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

## Playtime backup (0.2.2)

Playnite's main menu has a **LANCommander** section with two items:

- **Export playtime backup...** saves the playtime of every game in your library (not only LANCommander games) to a JSON file.
- **Import playtime backup (only raises playtime)...** reads such a file, for example on a new PC. A game's playtime is only
  ever raised to the saved value, never lowered, so time played since the backup is kept.

Games from a library are matched by library and the library's own game id, which stays the same on every PC.
Games added by hand are matched by name, and only when exactly one game in the library has that name.
The server is not involved.

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
