# GlazeLauncher: workspace-aware app launcher

Read this when you work on the launcher (`launcher/`) or its supervisor (`supervisor/`). Ideas and bugs are in
[`backlog.md`](backlog.md#launcher). New "do something with this result" features are result actions (see Result actions).

A resident WPF (.NET 10) app in `C:\codex\glazewm\launcher\`, installed to `%USERPROFILE%\.glzr\launcher\`,
data in `%LOCALAPPDATA%\GlazeLauncher\`. It replaces PowerToys Run on Alt+Space
(PowerToys Run is turned off; Command Palette stays on Win+Alt+Space) and knows about GlazeWM's monitors,
workspaces and windows. Same performance rule as the bar: it stays resident with its window pre-created,
gets pushed updates from GlazeWM instead of polling, and does shell work on one background STA thread.

## Using it

- **Alt+Space** toggles it. It opens centred on the monitor GlazeWM has focused, a fifth of the way down. Esc, or clicking elsewhere, closes it.
  The content fades and slides into place over 125 ms. The selected row has a lavender accent that fades in over 85 ms. Both effects respect Windows' animation setting. Typing, navigation, closing and opening a result respond immediately.
- **↑ ↓ and Tab / Shift+Tab wrap around** in the results and the action list: ↑ on the top row goes to the last, ↓ on
  the last goes to the top. Page Up / Page Down move 8 rows and stop at the ends.
- An **empty query** lists recent items for the current workspace, then every open window (other workspaces first) as a window switcher.
- Typing searches **apps** (Start menu and Store apps, from `shell:AppsFolder`), **open windows** (by title or process),
  **open Chrome and Edge tabs**, **folders**, and **commands** (tiling direction, floating, fullscreen, redraw, reload config, pause, workspace Sync, re-index, quit).
  The app index skips packaged entries missing from the current user's installed package inventory, Windows Store stubs, and direct Store product links.
  Matching ranks exact > prefix or whole word > word start > initials (`vsc` → Visual Studio Code) > substring > subsequence.
  Within one match tier, a concrete open window on another workspace comes first, then open windows on the current workspace, then app and command results. Provider priority and a bounded frecency boost refine the order within each group.
  The matching characters are highlighted in result titles, including non-adjacent fuzzy matches. When a window only matches by process name, the highlight is in its subtitle. The `@N` workspace token is separate from the text match.
- **Apps:** Enter opens it. If it is already open, Enter focuses its window instead; pressing it again cycles through that app's windows.
  Ctrl+Enter always opens a new window. The badge `● 12 21` shows the workspaces where it's open.
- **Windows:** Enter switches to the window's workspace if hidden and focuses the selected window. A minimized window is restored first, to the state it had before (tiling or floating). Ctrl+Enter brings it to the current workspace.
- **Go to a workspace:** a query that is only `@N` shows **Go to workspace N** at the top; Enter switches to it. The same one-digit/two-digit rule as below applies. The usual list stays below it, so ↓ still picks a window or app to send there.
- **Browser tabs:** Search by tab title. A tab result shows the browser and workspace. Enter switches to that workspace and selects the existing tab. Chrome and Edge use Windows UI Automation without an extension or a change to browser settings.
- **Folders:** Search for Home, Desktop, Downloads, Documents, Pictures, Music, or Videos and press Enter to open the folder in File Explorer. Results show the Windows icon and the current user's registered path, including redirected locations. Used folders can appear in recents.
- **Files and folders anywhere** (from the GlazeIndex service, [`indexer.md`](indexer.md)): from 3 letters, the index's
  best 12 files and folders on the internal drives show below apps that match by name or word start (they rank as
  substring matches, above loose subsequence matches). The subtitle has the size (a folder's total) and the containing
  folder. Several words may match across the path: `dragon tooltips` finds `D:\Dragon Age Mods\Detailed Tooltips-248`.
  Folders come first; `node_modules`, `.git`, `AppData`, `Windows` and package caches are pushed down. Results arrive a
  few ms after the keystroke without blocking typing; the previous query's still-matching results stay meanwhile. Known
  folders (Downloads, …) are left to the folder provider. Recently opened files and folders show on the home screen.
  Without the service the launcher works as before (one log line, then it retries every 15 s).
- **Send to a workspace:** add `@N` to the query, or press **Ctrl+1…9** on a result. One digit means slot N in the group of
  the monitor you were on when you opened the launcher (`@3` → `13` on the primary monitor, `23` on the next one), the same
  rule as Alt+N; two digits (`@23`) are taken literally. The launcher also opens on that monitor. By default your view follows the window; hold **Shift** to stay where you are
  (Hyprland's "silent"). The chip at the right of the search box shows the target (`→ ws 13`).
- **Aliases:** extra names for apps, folders and commands, so `codex` finds ChatGPT. Add one with Ctrl+K › **Add alias…**
  (see Result actions). An alias matches exactly or by prefix only (`codex`, `cod`; not `dex`), ranks like a name match of
  the same tier, and works with `@N` (`codex @3`). A name match of the same quality wins over the alias. When the alias is
  what found the result, it shows as a small tag after the title (`ChatGPT [codex]`) with the typed part highlighted.
  Windows and browser tabs can't have aliases (they come and go; an app alias covers its windows).
- **Store suggestions never show.** Only apps installed for this user are results; Windows Search's Store ads for apps you
  don't have (Netflix and the like) are filtered out with the Store stubs (checked 2026-11-27: `netflix` finds nothing).

## Result actions

Every result has a list of actions. **Enter** runs the first (the default). **Ctrl+K**, or **→** with the caret at the end
of the query, opens the list for the selected result in place of the results; the search box shows the result as a chip
(`[icon] Claude › Move to workspace`) and filters the list as you type. Enter runs the selected action, → or Enter opens a
submenu (`›`), ← (empty filter) or Esc goes back a level, and Esc on the results closes the launcher. The footer under
the results is built from the selected result's actions: the default, Ctrl+K, and every action with a shortcut.

| Result | Actions (shortcut) |
|---|---|
| App | Focus open window / Focus next open window, or Open (↵) · Open new window (Ctrl+↵, when open) · Open on workspace › (Ctrl+1–9, +Shift stay) · Run as administrator (Ctrl+Shift+↵; programs and desktop Store apps, a muted row for UWP apps) · Open file location (Win32 apps only) |
| Window | Focus window (↵) · Bring to this workspace (Ctrl+↵, when elsewhere) · Move to workspace › (Ctrl+1–9, +Shift stay) · Open file location · Close window (Ctrl+W) · End task |
| With `@N` in the query | App: Open on workspace N (↵, Shift+↵ stay); window: Move to workspace N (↵, Shift+↵ stay) |
| Folder, command | Their default action (Open in File Explorer / Run) |
| Indexed folder | Open (↵) · Open Terminal here (Ctrl+↵) · … as administrator (Ctrl+Shift+↵) · Open PowerShell 7 here · … as administrator · Open Command Prompt here · … as administrator · Open file location · Copy path |
| Indexed file | Open (↵) · Open file location (Ctrl+↵) · Run as administrator (Ctrl+Shift+↵; `.exe`, `.bat`, `.cmd`, `.com`, `.msi`) · Open Terminal / PowerShell 7 / Command Prompt in its folder, each also as administrator · Copy path |
| App, folder, command | Also: Add alias… (text entry) · Aliases › (when it has any; Enter on one removes it) |
| Go to workspace, browser tab | One default action each |

- **Shortcuts work without opening the list:** a Ctrl chord is looked up in the selected result's actions and their
  submenus, so Ctrl+3 runs "Move to workspace 13". Shift added to a shortcut runs the action's "stay here" variant.
  A Ctrl+Enter or Shift+Enter that matches nothing falls back to the default (as before).
- **Move / Open on workspace ›** lists slots 1–9 of the monitor you're on, with what's on each (`chrome, Signal`, `empty`,
  `current`).
- **Close window** asks the window to close (GlazeWM `close`, or `WM_CLOSE` without GlazeWM), so the app may ask to save.
- **End task** kills the window's process. It has no shortcut: the first Enter in the list turns the row into "Press ↵
  again to end X.exe (unsaved work is lost)", the second runs it, and any other key cancels. It refuses the launcher's own
  process; elevated processes fail (logged).
- **Open file location** runs `explorer /select,` on the app's target exe or the window's exe. It isn't offered for
  packaged apps (their files are in `WindowsApps`, which Explorer can't open).
- **Run as administrator** (Ctrl+Shift+↵, the Start menu's key) starts a new elevated instance, also when the app is
  already open. It is offered exactly where the Start menu offers it: Win32 entries whose target is an `.exe`, `.bat`,
  `.cmd` or `.msc` (not `.url`, `.pdf`, Steam links), and packaged apps with `System.AppUserModel.HostEnvironment` = 2
  (desktop-bridge apps: Terminal, Notepad, Paint, ChatGPT, Claude…). UWP apps (Calculator, Settings) show a muted
  "not possible for this Store app" row that keeps the shortcut, so Ctrl+Shift+↵ does nothing there instead of an ordinary
  launch; the footer skips inert actions. Both rules matched the shell's own verb list for all 295 AppsFolder entries
  (2026-11-28) and cost one property read per packaged app at index time. `Actions.RunAsAdmin` runs ShellExecute `runas`
  on `shell:AppsFolder\<id>` (the shortcut with its arguments); a Win32 entry whose item refuses the verb falls back to its
  target exe. It runs off the UI thread, because ShellExecute returns only once the UAC prompt is answered and the
  supervisor ends a UI that is unresponsive for about 11 s. Frecency is recorded like a normal launch. A cancelled UAC prompt
  is never an error: on the AppsFolder item ShellExecute reports success either way (nothing is logged), and on the exe
  fallback error 1223 is logged as `UAC prompt cancelled`.
- **Add alias…** turns the search box into a text field (`ChatGPT › Add alias…`). The row under it follows the text: `Add
  alias "codex"  for ChatGPT`, `Move alias "codex" here  from Notepad` when another result has it, or a muted line saying why
  it can't be saved (already this result's alias, contains `@`, longer than 40 characters, or `settings.json` has a JSON
  error). Enter saves it and shows the results for the new alias, so you see it work. Aliases are stored lower case with
  spaces collapsed.
- **Aliases ›** lists the result's aliases as "Remove alias …" rows; Enter removes one and the list stays open (it closes
  when the last one is gone).
- **Terminals in a folder** (`Actions.OpenShell`): Windows Terminal when its `wt.exe` alias exists (otherwise PowerShell
  takes the Terminal rows), PowerShell 7 when `pwsh.exe` is on PATH (otherwise Windows PowerShell), and cmd. Normally the
  folder is the working directory (`wt -d .`, nothing to quote); elevated starts ignore that, so then the folder goes on the
  command line (`wt -d "D:\\"`, `pwsh -NoExit -Command "Set-Location -LiteralPath '…'"`, `cmd /k cd /d "…"`). Elevated
  starts run off the UI thread, like Run as administrator; a cancelled UAC prompt is logged, not an error.
  Folders and files share the rows (`ActionKits.ShellActions`): every shell has a normal row and an "… as administrator"
  row, so typing `admin` in Ctrl+K lists all elevated ones; Shift+↵ on a normal row also runs its elevated twin. Files open
  the shell in the file's own folder, so two `GlazeIndex.exe` results open in their two different folders.
- Close, End task, Open file location and the alias actions don't count towards frecency; workspace actions record usage for the target workspace.
- **Text-entry levels and keep-open actions** are general: a `ResultAction` with `Input` builds its rows from the typed text,
  `ActionFlags.KeepOpen` runs an action without hiding the launcher (then searches `ThenQuery`, or rebuilds the list), and
  `ActionFlags.Disabled` marks an informational row.

## Settings file

`%LOCALAPPDATA%\GlazeLauncher\settings.json` (`Settings.cs`). `aliases` is in use (`Aliases.cs`); `hotkey` and
`indexedFolders` are read but not used yet.

```json
{
  "version": 1,
  "hotkey": "Alt+Space",
  "aliases": { "codex": "app:<AppsFolder ID>" },
  "indexedFolders": [ { "path": "C:/launcher shortcuts", "badge": "Shortcut" } ]
}
```

- Alias targets are a result's usage key (`app:…`, `folder:…`, `cmd:<title>`), the same IDs `usage.json` uses. Hand
  edits may use any case; `AliasBook` normalizes. An alias whose target is gone (an uninstalled app) is ignored.
- Written with defaults when missing. Reading allows comments and trailing commas. Keys this build doesn't know are kept
  on save (`[JsonExtensionData]`).
- Edits by hand are picked up by a file watcher (300 ms debounce; the launcher's own writes are ignored). A file with a
  JSON error is logged and left alone: the last good settings stay, and `SettingsStore.Update` refuses to save until it's fixed.
- Saves are atomic (temp file, then replace).

## Workspace manager

`WorkspaceManager.cs` (no dependency on the launcher UI). The launcher owns the workspace keys while GlazeWM is
connected, and releases them when it isn't, so Alt+digits stay free for other apps without GlazeWM.

- **Groups:** each monitor owns nine workspaces. Group 1 (`11`–`19`) is always the Windows primary monitor; the others follow
  left to right, then top to bottom (`21`–`29` … `61`–`69`). `WorkspaceGroups.Assign` computes this from GlazeWM's monitors.
- **The monitor you're on** (`WorkspaceGroups.TargetMonitor`): the monitor holding the foreground window, or the one under
  the cursor when the desktop or taskbar is in front. GlazeWM's own focused monitor doesn't change when you click an empty
  desktop, because its focus follows windows; that used to send Alt+N to the wrong screen.
- **Keys:** Alt+N focuses slot N of that monitor's group. Alt+Shift+N moves the foreground window there and follows it (with
  the desktop in front it only switches). Ctrl+Alt+R re-syncs. Each is a `RegisterHotKey`, so no process starts per key press.
- **Sync** runs when the launcher connects, after `monitor_*` events (1.2 s debounce, and only once Windows and GlazeWM report
  the same number of monitors, retried for up to 10 s), after `user_config_changed`, and on Ctrl+Alt+R or the Sync command:
  1. Writes each group's GlazeWM monitor index into the `&groupN_monitor` anchors in `config.yaml` (groups without a monitor
     point at the primary) and reloads the config if anything changed. Atomic replace; the old file is restored if GlazeWM
     rejects the new one.
  2. **Merges** workspaces whose group has no monitor into the same slot of group 1 (`22` → `12`). If that slot isn't
     active, the workspace is renamed (`update-workspace-config --name`), which keeps its layout; otherwise its windows move.
  3. **Restores** merged windows when their monitor is back: windows still on the workspace they were merged into go back;
     windows the user moved since stay. A merged workspace that holds only those windows is renamed back.
     The memory is `%LOCALAPPDATA%\GlazeLauncher\workspace-merges.json` (window ID → origin and target), so it survives a
     launcher restart, but not a GlazeWM restart (window IDs change).
  4. Moves active workspaces that sit on another group's monitor to their own with `move-workspace --direction` (whole
     workspace, layout kept), one neighbouring monitor per step.
  5. **Re-tiles windows the display change maximized.** Some apps maximize themselves when the screen setup changes (seen
     with Chrome), and GlazeWM takes a maximize as fullscreen, so the window covers its workspace and new windows tile behind
     it. Window states are captured at the first `monitor_*` event of a burst (the client's snapshot is still from before
     it); windows that were tiling then and are fullscreen now get `set-tiling`. If an app maximized itself before GlazeWM
     reported the monitor change, this misses it (Alt+F twice fixes it by hand).
  6. **Removes workspaces left empty by a merge.** GlazeWM only deletes an empty workspace when it's switched away from, so
     the manager visits each one and then returns to the workspace you were on.
  7. `wm-redraw`, then a **settle watch** for 30 s (checks at 1, 2, 3, 5, 8, 13, 20 and 30 s): after a display change,
     Windows and some apps (Jellyfin, a Qt app) move windows back to where they were on that monitor setup, sometimes long
     after GlazeWM has laid them out, and GlazeWM doesn't put them back. Each check compares `GetWindowRect` of tiled
     windows on displayed workspaces with GlazeWM's position (16 px tolerance for the invisible borders), and looks for
     uncloaked windows on hidden workspaces; if anything is off it logs it and runs `wm-redraw`. The watch runs after every
     sync that saw a display change or was forced (connect, config reload, Ctrl+Alt+R); a newer watch replaces an older one.
- GlazeWM can stop answering IPC for a few seconds during a display change; a sync that times out retries after 2 s.
- A `monitor_updated` event that doesn't change any monitor's position or size doesn't trigger a sync.

## How it works

| File | Role |
|---|---|
| `App.xaml.cs` | Single instance (mutex `Local\GlazeLauncher.Instance`), Alt+Space `RegisterHotKey` (retried every 5 s while another program holds it), named-pipe server `GlazeLauncher`, the detached relaunch, and crash recovery |
| `WorkspaceManager.cs` | Workspace groups, the monitor you're on, Alt+N / Alt+Shift+N, and syncing workspaces to monitors (see Workspace manager) |
| `GlazeClient.cs` | WebSocket client for GlazeWM IPC `ws://127.0.0.1:6123`: `sub --events …`, `query monitors`, `command [--id X] …`. On any event it re-queries the tree (80 ms debounce), forwards `window_managed`, and raises `EventReceived` and `ConnectionChanged`. `WmSnapshot.TargetMonitor` is the monitor a one-digit `@N` refers to. `WmWindow` keeps id, handle, title, process, workspace and focus, not the window state; `WindowStateAsync` queries one window's state on demand |
| `ShellIndex.cs` | Refreshes the app catalog and known folders on the background STA thread. Checks Store package status while indexing apps and re-indexes on open if the index is older than 2 min. Marks each `AppEntry` as packaged and whether it can run as administrator (`CanElevate`). |
| `KnownFolder.cs` | Resolves the standard per-user folder IDs with `SHGetKnownFolderPath` and publishes the folders Windows can resolve |
| `Search.cs` | Query parsing (`@N`), fuzzy match tiers and character positions, merged ranking, shared result model (`MatchedAlias` for the tag), `WindowIdentity` |
| `SearchProviders.cs` | `ISearchProvider` contract and the compiled-in app, window, folder, command and go-to-workspace providers. A `Pinned` result is shown above all ranked results. `SearchContext.AliasHits` holds the query's alias matches (computed once per search); `WithAlias` picks the better of name and alias match |
| `ResultActions.cs` | `ResultAction` (title, glyph, callback, optional shortcut, submenu, flags, Shift variant), `KeyChord`, `ActionContext` (snapshot + resolved `@N`), and `ActionKits`: the shared app, window and workspace-slot action sets providers compose |
| `Settings.cs` | `LauncherSettings` and `SettingsStore`: load, watch, atomic save of `settings.json` |
| `Aliases.cs` | `AliasBook`: alias index built lazily from the current settings (no events or locks), exact/prefix matching per query, add, move and remove through `SettingsStore.Update` |
| `BrowserTabSearchProvider.cs` | Refreshes the Chrome and Edge tab cache on a background thread using only their accessibility tab strips. Selects a tab by its runtime ID within the original browser window. |
| `Actions.cs` | What actions do: `Run` (records frecency, logs failures), launch, `LaunchOn`, `RunAsAdmin`, `FocusOrCycle`, `MoveWindow`, `CloseWindow`, `EndTask`, `ShowInExplorer`, commands. Open app and window results share a focus path that runs `focus --workspace N` for hidden workspaces, restores a minimized window (Win32 `SW_RESTORE`, then waits up to 600 ms for GlazeWM to stop reporting it as minimized, because focusing it earlier leaves GlazeWM's focus on the previous window), then runs `focus --container-id`. After an `@N` launch, the next matching `window_managed` window (within 15 s) gets `move --workspace N` and uses the same focus path if following. |
| `IconCache.cs` | Windows Shell icons (`IShellItemImageFactory`, 48px, alpha kept) loaded on the STA thread. Known folders use `SHGetKnownFolderItem` first, and the cache warms folder icons before app icons at startup. |
| `UsageStore.cs` | Frecency (half-life 10 days), global and per workspace, saved to `usage.json` |
| `MainWindow.xaml(.cs)` | UI (bar palette `#151b30` / `#b6acff`, DWM rounded corners), keys, placement, and the action list (levels, filter, confirm, shortcut lookup). `ActionRow` is one row of the list |
| `HighlightedTextBlock.cs` | Renders matching characters as WPF runs and keeps Unicode text elements intact |
| `WindowRescue.cs` | Uncloaks windows a dead GlazeWM left hidden (see below) |
| `IndexClient.cs` | Client for GlazeIndex on `\\.\pipe\GlazeIndex` (`search<TAB>limit<TAB>text`, JSON reply); 150 ms connect timeout, 15 s back-off when the service is down |
| `FileSearchProvider.cs` | Files and folders from GlazeIndex: background query (only the newest in flight), cached reply, `Changed` → `MainWindow.RefreshResults`; recents for the home screen; `ResultItem.FixedTitleMatches` carries the service's highlight |
| `Native.cs`, `Infrastructure.cs` | Win32 interop; the shared background STA worker |

- **Matching a window to an app.** An app's IDs are its AppsFolder ID (an AUMID or a path), its exe name, its name with spaces removed, and for packaged apps the family name plus a small alias table (`PackagedProcessAliases`: Terminal, Notepad, Calculator, Paint). A window's IDs are its process name, its process's packaged AUMID (`GetApplicationUserModelId`), and its window AUMID (`PKEY_AppUserModel_ID`). They match if the two sets share any ID. Squirrel apps (Discord) point at `Update.exe`, which is ignored; the name match covers them.
- **Adding a provider.** Register it in `App.xaml.cs`. Search reads a cached snapshot and returns `ProviderResult` entries with a stable ID, match, optional hosting `WmWindow`, display fields, and an `Actions` function. That function is only called for the selected result (footer, Enter, a shortcut, the list), so a search allocates no actions; reuse `ActionKits` where it fits and `ActionKits.Single` for one action. The engine ranks results centrally and the UI renders the supplied icon, text and key hints. Providers with `IRefreshableSearchProvider` update their cache asynchronously when the launcher opens. The browser provider uses the hosting window for workspace ranking and `Actions.FocusWindow` before selecting the tab. The tab cache holds titles and runtime IDs in memory, and frecency stores one key per browser.
- **Launching** uses ShellExecute on `shell:AppsFolder\<id>`, which works for Win32 and Store apps alike.
- **Rescuing windows after GlazeWM dies** (`WindowRescue.cs`). GlazeWM hides the windows of workspaces that aren't
  displayed by shell-cloaking them (`IApplicationView::SetCloak(1, 2)`). When GlazeWM dies, its watcher shows them and puts
  them back on the taskbar but never uncloaks them, and a new GlazeWM skips cloaked windows. They end up on the taskbar
  but invisible, in no workspace and missing from the launcher (seen 2026-11-28 with ChatGPT, Notepad, Signal, Steam and
  Task Manager after GlazeWM died at 22:51 the night before). The launcher keeps the handles from GlazeWM's last snapshot
  when the IPC connection drops. It uncloaks them (`SetCloak(1, 0)`, the same undocumented shell call GlazeWM uses):
  - 3 s after the drop, if no `glazewm` process is running (a dropped connection to a live GlazeWM is left alone);
  - when a GlazeWM connects whose tree doesn't include them; it then adopts them like newly opened windows.

  It only touches windows that still exist, are cloaked by the shell (`DWMWA_CLOAKED` = 2), and are on the current virtual
  desktop (`IVirtualDesktopManager`; windows on other desktops are shell-cloaked too). The handles are in memory only, so a
  launcher restart while GlazeWM is down forgets them. The log line is `uncloaked N window(s) GlazeWM left hidden (…)`.
- **Detached relaunch:** started without `--detached`, the exe relaunches itself through ShellExecute (no handle inheritance) and exits. That's the same protection the Zebar collector has against keeping a parent's sockets alive.
- **Supervisor** (`supervisor/main.rs`, Rust, `GlazeSupervisor.exe`, about 180 KB and 1 MB private memory). GlazeWM starts
  it instead of the launcher. It relaunches itself without inherited handles, keeps one instance (mutex
  `Local\GlazeLauncher.Supervisor`), and starts `GlazeLauncher.exe --detached --supervised` from its own folder, then waits
  on the process handle:
  - exit code 0 (Quit, or `--quit` before a deploy): the supervisor exits too;
  - any other exit (crash, killed): restart at once; repeats within 2 min wait 2 s, 10 s, then 60 s;
  - every 2 s, `IsHungAppWindow` on the launcher's top-level windows (Windows' "not responding": no message processing
    for 5 s); three hung checks in a row end the process (exit code `0xDEAD`) and it restarts. A hang is caught about
    11 s after it starts.
  - A launcher already running when the supervisor starts is asked to quit over the pipe (ended after 5 s if it doesn't) and
    replaced by a supervised one.
  - The hotkeys stay in the launcher (decided 2026-11-27: the supervisor doesn't own them, so key presses never pass
    through it and Windows' foreground rules are unaffected). A key pressed during the few seconds of a hang is lost.
  - Log: `%LOCALAPPDATA%\GlazeLauncher\supervisor.log` (removed at start when over 256 KB).
- **Crash recovery inside the launcher** (`InstallCrashHandling` in `App.xaml.cs`). Under the supervisor (`--supervised`)
  the launcher only logs and exits; it starts no replacement and doesn't register Windows' restart, because a second
  restarter would race the supervisor. Without the supervisor (started by hand) it recovers on its own as follows:
  - An error on the UI thread is logged and skipped. Five within 10 s means the UI is stuck failing, so the launcher starts a
    replacement and exits.
  - An error on a background thread ends the process (.NET can't stop that); the handler logs it and starts a replacement first.
  - The replacement runs with `--detached --restart`: it waits up to 10 s for the old instance's mutex instead of sending it
    "toggle", and exits quietly if another instance keeps it.
  - No replacement if the launcher failed within 60 s of starting, so a broken build can't restart in a loop.
  - `RegisterApplicationRestart("--detached --restart")` asks Windows to restart it after native crashes and hangs as well.
    **Windows Error Reporting is disabled on this PC** (`HKLM\…\Windows Error Reporting\Disabled = 1`), so that layer does
    nothing here; it helps on PCs where it's on. The `--restart` rule keeps the two from ending up with two copies.
  - Doesn't cover native crashes, hangs or being killed on this PC; the supervisor does.
- **Command line:** `GlazeLauncher.exe --toggle|--show|--hide|--reindex|--sync|--quit` forwards the command to the running instance over the pipe. With no argument a second start toggles.
- **Debug pipe commands** (send them with a `NamedPipeClientStream` to `GlazeLauncher` opened with `PipeDirection.Out`,
  since the server is in-only and `InOut` gets "access denied"; not available as exe flags): `query:<text>` types a query,
  `enter`, `enter:ctrl` or `enter:shift` runs what Enter would (nothing while hidden), `actions` opens the
  action list (Ctrl+K), `back` goes back a level, `key:<chord>` presses a key through the normal key handling (`key:ctrl+shift+3`,
  `key:ctrl+w`, `key:right`, `key:escape`; while the list is open, `query:` sets its filter), and `snapshot` renders the window to `%LOCALAPPDATA%\GlazeLauncher\snapshot.png`.
  `ws:focus:N` and `ws:move:N` run the Alt+N and Alt+Shift+N actions (they use the current foreground window, so make
  sure the right window is in front). For crash-recovery tests: `debug:throw` raises an error on the UI thread, and `debug:crash` crashes a background thread
  (the launcher really ends; a replacement starts only after the first 60 s). `debug:hang` blocks the UI thread for 30 s,
  to test the supervisor's hang detection. `debug:rescue:<hex handle>[,…]` runs the window rescue on those handles.
- **Rendering is software-only** (`RenderMode.SoftwareOnly`), and concurrent GC is off. GPU rendering loaded the NVIDIA driver into the process: about 113 → 77 MB private memory after a search. Working set is about 170 MB, mostly shared WPF DLL pages.
- **Without GlazeWM** the launcher still starts and searches apps (noticed 2026-11-27, not designed for). What works and what should happen in that mode is in the backlog.

## Build and deploy

```powershell
cd C:\codex\glazewm\launcher
dotnet publish -c Release -r win-x64 --self-contained false -o publish   # ReadyToRun, framework-dependent
cd ..\supervisor
cargo build --release --offline                                          # only if supervisor/ changed
& $env:USERPROFILE\.glzr\launcher\GlazeLauncher.exe --quit                # exit code 0: the supervisor exits too
Copy-Item ..\launcher\publish\* $env:USERPROFILE\.glzr\launcher -Force
Copy-Item target\release\GlazeSupervisor.exe $env:USERPROFILE\.glzr\launcher -Force   # only if it changed
```

Back up the installed folder first as `.glzr\launcher\before-<change>-<timestamp>\`. Then start the **supervisor** again
(it starts the launcher). From an agent's shell, have GlazeWM start it, so it runs outside the Claude app's package:
send `command shell-exec %USERPROFILE%/.glzr/launcher/GlazeSupervisor.exe` over GlazeWM IPC. Don't use `Stop-Process`
on the launcher to deploy: a non-zero exit makes the supervisor restart it straight away, with the exe still locked.

**Testing gotcha (agents):** the Claude desktop app is an MSIX package. Processes started from its shell
run inside that package, so their writes to `%LOCALAPPDATA%` are redirected to
`%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\…`, and reads from that shell see the
redirected copy. A launcher you start from the shell therefore logs to a different `launcher.log`
than the real instance GlazeWM starts. Check the real instance with read-only window checks or ask the user. The computer-use
screenshot tool can't target the popup (it has no taskbar or Start entry); use the `snapshot` pipe command instead.

## Known issues and unverified

- **PowerToys Run must stay off** (it was turned off on 2026-11-27). If it is turned back on, it takes Alt+Space again:
  `RegisterHotKey` fails with error 1409, and the launcher keeps retrying every 5 s.
- Not yet checked: Ctrl+1…9 from the keyboard, and `@N` launches of apps that start slowly or
  hand off to another process (only Notepad silent → ws 23 was tested). Store apps whose process name doesn't match their package
  may need an entry in `PackagedProcessAliases`.
- Browser tab search depends on the tab controls Chrome and Edge expose through accessibility. Fullscreen and collapsed tab groups have not been checked.
- Minimized windows are sometimes missing from results (needs a repro). Tracked in the backlog.
- **Aliases and result actions (Ctrl+K)** were confirmed by the user with the real keyboard on 2026-11-28. Not yet checked:
  hand edits of `aliases` in `settings.json`, a command alias, and Shift-click on action rows.
- **The workspace keys need the launcher.** The supervisor restarts it after any crash at once and after a hang in about
  11 s; keys pressed in between do nothing. If the supervisor itself isn't running (Quit, or it was never started), a
  crash leaves the keys dead until GlazeWM is restarted or the supervisor is started again.
- Workspace manager, checked by the user on 2026-11-27: the real keys, the snappier Alt+N, "PC screen only", "Second screen
  only" (the primary going away) and back to Extend, with merge and restore. Not yet checked: three or more monitors, and
  merges where the target slot already has windows on a real display change (the "moved" path; only seen in the first test).
- The settle watch can only fix windows GlazeWM manages; an app that moves itself more than 30 s after a display change
  needs Ctrl+Alt+R.
- **The window rescue was checked end to end on the GlazeWM-exit path** (2026-11-28): `Stop-Process glazewm` with
  Windows Terminal and Docker Desktop on a hidden workspace; both were uncloaked 3 s later and the next GlazeWM adopted them.
  Not yet checked: the reconnect path (a new GlazeWM up within 3 s of the old one dying).
- **Run as administrator was confirmed by the user** (2026-11-28) with the real keyboard: approving and cancelling UAC for
  PowerShell 7 (Win32), Terminal and WhatsApp (packaged). `runas` on `shell:AppsFolder\<id>` worked for all of them; the exe
  fallback never ran. **A cancelled prompt logs nothing:** no `UAC prompt cancelled` line appeared, so ShellExecute on an
  AppsFolder item apparently returns success and the elevation (or cancel) happens later in the shell. The 1223 branch is
  then only reached through the exe fallback. Harmless (nothing is logged as an error), but the log can't tell a cancel from
  a launch. Not checked: whether GlazeWM, which runs unelevated, can tile or move the elevated window (Windows blocks
  input from lower-integrity processes).

**Testing tip:** a throwaway WinForms window (`[System.Windows.Forms.Application]::Run($form)` from a background PowerShell)
is a safe target for window tests. To see a `snapshot`, have GlazeWM copy it out of the real `%LOCALAPPDATA%`
(`command shell-exec --hide-window cmd.exe /c copy /y %LOCALAPPDATA%\GlazeLauncher\snapshot.png <folder>`), because the
agent's shell only sees its redirected copy.

## Backups

- `.glzr\launcher\before-workspace-focus-20261127-150241\`: before the workspace focus fix
- `.glzr\launcher\before-motion-and-matches-20261127-152447\`: before the animation and highlight update
- `.glzr\launcher\before-provider-ranking-20261127-154402\`: before the provider and ranking update
- `.glzr\launcher\before-store-filter-20261127-160536\`: before the Store placeholder filter
- `.glzr\launcher\before-known-folders-20261127-161208\`: before the known-folder provider
- `.glzr\launcher\before-folder-icons-20261127-161907\`: before the Windows folder icon update
- `.glzr\launcher\before-browser-tabs-20261127-162737\`: before extension-free browser tab search
- `.glzr\launcher\before-minimized-and-goto-20261127-181846\`: before minimized-window restore and the go-to-workspace result
- `.glzr\launcher\before-crash-recovery-20261127-182352\`: before crash recovery
- `.glzr\launcher\before-workspace-manager-20261127-185520\`: before the workspace manager
- `.glzr\launcher\before-supervisor-20261127-192822\`: before the supervisor
- `.glzr\launcher\before-result-actions-20261127-194948\`: before result actions and the settings file
- `.glzr\launcher\before-aliases-20261127-224525\`: before aliases
- `.glzr\launcher\before-window-rescue-20261128-100413\`: before the window rescue
- `.glzr\launcher\before-file-search-20261128-110655\`: before file and folder results
- `.glzr\launcher\before-run-as-admin-20261128-104516\`: before Run as administrator
- `.glzr\launcher\before-list-wrap-20261128-112255\`: before ↑ ↓ wrap-around
- `.glzr\launcher\before-file-shells-20261128-120346\`: before shells in a file's folder

## Change log

Newest first.

### 2026-11-28
- **Terminal, PowerShell and Command Prompt in a file's folder, each also as administrator** (user request: two
  `GlazeIndex.exe` results, one in Program Files and one in the build folder, each needs a terminal in its own folder).
  `ResultActions.cs`: new `ShellActions` builds the shell rows for both folders and files; every elevated variant is now a
  row of its own (before, PowerShell and cmd had it only as Shift+↵), and folder rows read "Open Terminal here". Installed
  through the supervisor. Checked through the pipe with snapshots: `glazeindex.exe` lists both copies; Ctrl+K on the build
  copy shows the six shell rows with its folder, `admin` filters to the four elevated rows; folder shortcuts unchanged.
  Not checked: opening the shells (elevated or not) with the real keyboard.
- **↑ ↓ and Tab wrap around** in the results and the action list (user request). `MainWindow.xaml.cs`: `Move` takes
  `wrap`; Page Up / Page Down still stop at the ends. Installed through the supervisor (this build also carries the file
  search changes below; GlazeIndex wasn't running, so it logged `file search is off for now`). Checked through the pipe
  with snapshots: ↑ on the first `command prompt` result selects the last, ↓ returns to the first, ↑ in Ctrl+K selects
  "Add alias…".
- **Added file and folder results from GlazeIndex** (the new file index service, `docs/indexer.md`). New `IndexClient.cs`
  and `FileSearchProvider.cs`; `ActionKits.Folder` / `File` (`ResultActions.cs`); `Actions.OpenPath`, `OpenShell`
  (Terminal / PowerShell / cmd here, also elevated), `RunFileAsAdmin`, `CopyText`; `TryRunAs` takes arguments and a
  working directory; `ResultItem.FixedTitleMatches`; `MainWindow.RefreshResults`. Checked through the pipe with the service
  running: `tooltips` and `dragon tooltips` (snapshots). Not yet checked: the terminal actions and Copy path with the
  real keyboard, and file recents on the home screen.
- **Added Run as administrator to app results** (Ctrl+Shift+↵). `ShellIndex.cs`: `AppEntry.IsPackaged` and
  `CanRunAsAdmin` (`CanElevate`: program extension for Win32, `HostEnvironment` = 2 for packaged). `ResultActions.cs`:
  `KeyChord.CtrlShiftEnter`, the action in `ActionKits.App` (a disabled row for UWP apps). `Actions.cs`: `RunAsAdmin`
  (`runas` on the AppsFolder item, exe fallback, off the UI thread, error 1223 logged as a cancel). `MainWindow.xaml.cs`:
  the footer skips inert actions and inert rows show no shortcut. Published and installed through the supervisor. Checked
  through the pipe, then confirmed by the user with real UAC prompts (approve and cancel) on PowerShell 7, Terminal and
  WhatsApp; a cancel on an AppsFolder item logs nothing (see Known issues).
- **The user confirmed aliases and Ctrl+K with the real keyboard.** Known issues updated.
- **Windows a dead GlazeWM left hidden come back.** Reported: ChatGPT was open and on the taskbar, but the launcher only
  offered to launch it. Cause: the user turned GlazeWM off at 22:51 on 2026-11-27 while ChatGPT, Notepad, Signal, Steam and Task Manager were on hidden workspaces. GlazeWM's watcher doesn't
  uncloak, and the GlazeWM started at 09:55 skipped the cloaked windows. Uncloaked those five by hand, and GlazeWM adopted
  them. New `WindowRescue.cs` does this automatically (see How it works); `App.xaml.cs` creates it and adds the
  `debug:rescue:` pipe command. The Settings app's frame window is also shell-cloaked and outside GlazeWM; it was left alone.
  Tested end to end by killing GlazeWM: two hidden windows uncloaked after 3 s, adopted by the next GlazeWM (started
  through the launcher's own search, since an agent's shell would start it inside the Claude app's package). GlazeWM's
  watcher crashed during its cleanup in that test (access violation in `combase.dll`).

### 2026-11-27
- **Added aliases.** Design agreed with the user first: exact and prefix matching only; apps, folders and commands; a tag
  when the alias matched, and per-result removal. New `Aliases.cs` (`AliasBook`). `Search.cs`: `ResultItem.MatchedAlias`
  and `AliasText`, `SearchEngine` takes the `AliasBook`. `SearchProviders.cs`: `SearchContext.AliasHits` and `WithAlias`,
  used by the app, folder and command providers. `ResultActions.cs`: `ResultAction.Input`, `InputPlaceholder` and
  `ThenQuery`, flags `KeepOpen` and `Disabled`, `IsInert`, and `ActionKits.Aliases` / `WithAliases`. `MainWindow.xaml(.cs)`:
  `ActionLevel` can rebuild itself and be a text-entry level, keep-open actions, the alias tag, muted inert rows.
  `Settings.cs`: `SettingsStore.IsBroken`. `App.xaml.cs` builds the `AliasBook` and describes alias targets for "Move".
  Published and installed through the supervisor; the user's `codex` → ChatGPT alias was added during testing and kept.
  - Checked through the pipe with snapshots: Add alias on ChatGPT (the app's ID is `OpenAI.Codex_…!App`), `codex` finds it with
    the tag, `cod` ranks it first, `dex` doesn't find it; the duplicate and `a@b` rows are inert; a second alias `gpt`, then
    Aliases › showed both and removing `gpt` kept the list open with `codex`; on Downloads, `codex` offered "Move … from
    ChatGPT"; `dl` on Downloads worked and removing it closed the submenu; `codex @3` defaulted to Open on workspace 13;
    the alias survived a restart (`settings: loaded (1 aliases …)`).
- **Added result actions and the settings file.** New `ResultActions.cs` (`ResultAction`, `KeyChord`, `ActionContext`,
  `ActionKits` with app, window and workspace-slot actions) and `Settings.cs` (`LauncherSettings`, `SettingsStore`).
  `ResultItem` (`Search.cs`) lost `Activate`, `FooterText` and `SupportsWorkspaceTarget` and gained `Actions`; every
  provider (`SearchProviders.cs`, `BrowserTabSearchProvider.cs`) now supplies actions, and footers are built from them.
  `Actions.cs` replaced `ActivateOptions` with explicit methods and added Close, End task and Open file location
  (`Native.cs`: `PostMessage`, `WM_CLOSE`). `MainWindow.xaml(.cs)` shows the action list in place of the results with a
  chip in the search box, submenus, filtering, the End task confirmation and the shortcut lookup; `App.xaml` adds
  `DangerBrush`. `App.xaml.cs` loads and watches the settings and adds the `actions`, `back` and `key:` pipe commands.
  Design agreed with the user first (Ctrl+K and → open the list; same window; providers own their actions).
  Published and installed through the supervisor.
  - Checked through the pipe on throwaway windows: filter + submenu; Ctrl+Shift+7 moved a window to 27 and stayed on 22;
    Ctrl+3 moved one to 23 and followed; Ctrl+Enter brought one from 27 to 22; Ctrl+W closed one normally (its close
    handler ran); End task armed on the first Enter (process alive) and killed it on the second (no close handler).
    Open file location on Chrome opened Explorer in its Application folder, and Ctrl+W closed that Explorer window.
    → opened the actions and then the submenu, ← ← went back to the results with the query and selection restored, Ctrl+K
    then Esc Esc closed the launcher. `notepad @7` made "Move to workspace 27" the default with a Shift "stay here" variant.
    `netflix` finds nothing (Store ads stay filtered).
  - Settings checked with a harness on a temp file (defaults written, hand edit with comments loaded, broken JSON kept the
    last good settings and blocked saving, unknown keys kept, own writes not reloaded). The installed launcher wrote the
    default file on its first start.
  - Fixed during testing: the pipe's `enter` acted on the stale selection while the launcher was hidden.
  - Not yet checked: see Known issues.
- **Added GlazeSupervisor**, a small Rust watcher in the new `supervisor/` folder, installed next to the launcher. GlazeWM now
  starts it, and it starts the launcher with `--supervised`, restarts it after a crash or a kill (with backoff), ends and
  restarts it when Windows reports it hung, and exits when the launcher quits normally. In supervised mode, `App.xaml.cs`
  starts no replacement of its own and doesn't register Windows' restart. New `debug:hang` pipe command.
  - Chose the watcher over a supervisor that owns the hotkeys (the user agreed): no extra hop on key presses and no
    foreground-rights hand-off.
  - Checked with the supervisor started by GlazeWM: `debug:crash` restarted at once (`0xE0434352`); `Stop-Process` restarted
    after the 2 s backoff; `debug:hang` was reported not responding three times 2 s apart and ended about 11 s into the hang,
    then restarted after the 10 s backoff; Quit made the supervisor exit; starting the supervisor while an unsupervised
    launcher ran replaced it with a supervised one. The supervised launcher registered all hotkeys and synced.
- **Added the workspace manager and took over the workspace keys.** New `WorkspaceManager.cs` (groups for up to six
  monitors, target monitor from the foreground window or cursor, Alt+N, Alt+Shift+N, automatic sync with merge and restore).
  `App.xaml.cs` registers Alt+1…9, Alt+Shift+1…9 and Ctrl+Alt+R while GlazeWM is connected, and adds `--sync` and the
  `ws:focus:N` / `ws:move:N` pipe commands. `GlazeClient.cs` raises `EventReceived` and `ConnectionChanged`, subscribes to
  `user_config_changed`, and `WmSnapshot.ResolveWorkspace` uses the group rule and `TargetMonitor`. `MainWindow.xaml.cs` opens
  on the monitor you're on. `Native.cs` adds monitor and class-name helpers. The Sync command now runs in the launcher.
  GlazeWM config changes are in [`glazewm.md`](glazewm.md). Published and installed.
  - "PC screen only" test by the user: 21 merged into 11 (windows moved) and 22 into 12 (renamed). Two problems found and
    fixed: Chrome on 19 had maximized itself during the display change, so GlazeWM treated it as fullscreen and Signal tiled
    behind it (now re-tiled by the sync); and the emptied 21 stayed on the bar (now removed by the sync). Neither fix has been
    through a real display change yet. `WmWindow` now carries GlazeWM's window state.
  - Extend after that: 11 → 21 and 12 → 22 were renamed back (restore works). Then "Second screen only": the first sync timed
    out while GlazeWM was busy; a later one merged 21 → 11 and 22 → 12 by rename. GlazeWM's layout was right, but the windows
    sat where Windows had moved them (about two thirds of the screen). Added the timeout retry and redraws at 2 s and 5 s.
  - Back to Extend: Jellyfin (22, left screen in GlazeWM) was physically on the right screen more than a minute later, so
    something moved it after the redraws. `wm-redraw` put it back. Replaced the fixed redraws with the 30 s settle watch.
    Checked with a throwaway window pushed out of its tile 1.5 s after a sync: the +2 s check logged it and put it back.
  - Final round by the user (19:18 one screen, 19:19 back to two): merged by rename both times and renamed back, and the user
    confirmed the windows landed right. No settle check had to step in on that run.
  - Checked: the sync at connect and after the config reload found the anchors and changed nothing (the layout was already
    right). Through the pipe, Alt+8 and Alt+4 went to 28 and 24 with Jellyfin (left screen) in front, which matches
    Windows' foreground window. Alt+Shift+7 on a throwaway window moved it from 22 to 27 and followed it.
  - Not yet checked: see Known issues.
- **Added crash recovery.** `App.xaml.cs` logs UI-thread errors and keeps running, replaces itself after five within 10 s,
  logs background-thread crashes and starts a replacement (`--detached --restart`) before the process ends, and registers
  with Windows' application restart. `Native.cs` adds `RegisterApplicationRestart`. Debug pipe commands `debug:throw` and
  `debug:crash` exist for testing. Published and installed.
  - Checked on the installed launcher: one UI error left it running; a background crash after 67 s was replaced within about
    a second; a burst of five UI errors after 66 s was replaced; a crash 5 s into a replacement was not restarted (loop guard).
  - Found that Windows Error Reporting is disabled on this PC, so Windows' own restart never fires here. The first version
    relied on it and left the launcher down after a crash; the self-replacement was added because of that.
  - Fixed during testing: the uptime check first measured from the error instead of from process start.
- **Restore minimized windows, and go to a workspace with a bare `@N`.** `Actions.cs` restores a minimized window after switching
  to its workspace and waits for GlazeWM to see the restore before `focus --container-id` (`Native.cs` adds `IsIconic` and
  `ShowWindow`; `GlazeClient.cs` adds `WindowStateAsync`). New `WorkspaceSearchProvider` in `SearchProviders.cs`, registered in
  `App.xaml.cs`, shows "Go to workspace N" when the query is only `@N`; `ProviderResult.Pinned` puts it above ranked results
  (`Search.cs`). Published and installed.
  - Checked with a throwaway test window: minimized on ws 23 and picked from ws 22, then from ws 23 itself. Both times it came
    back tiled, in the foreground, and focused in GlazeWM. Without the wait, Windows showed it but GlazeWM's focus stayed on
    the previous window. `@19` + Enter switched to ws 19; a snapshot of `@3` on ws 19 showed "Go to workspace 13" on top.
  - Not checked: a minimized floating window, and an app that minimizes to the tray.
- **Added extension-free Chrome and Edge tab search.** `BrowserTabSearchProvider.cs` reads only tab titles and IDs from each GlazeWM-managed browser's Windows UI Automation tab strip. `SearchProviders.cs`, `Search.cs`, `App.xaml.cs`, and `MainWindow.xaml.cs` refresh provider data asynchronously on open. `Actions.cs` logs activation failures by usage key. Published and installed the build. Verified inactive tab selection and foreground focus on a hidden workspace in Chrome and in Edge with both horizontal and vertical tabs. Closed the isolated browser test windows and cleaned up their profiles.
- **Use Windows icons for launcher folders.** `Native.cs` and `IconCache.cs` request each folder's icon by Known Folder ID on the shared background worker, fall back to the resolved folder path, and deliver cached images to all waiting results. `App.xaml.cs`, `SearchProviders.cs`, `Search.cs`, and `MainWindow.xaml.cs` pass the folder ID, warm the icons, and show the generic outline while the Windows image is unavailable. Published and installed the build. Verified distinct Windows icons for Downloads, Documents, Pictures, Desktop, Music, and Videos, and the standard Windows folder icon for Home.
- **Added current-user folders to launcher search.** `KnownFolder.cs` resolves seven standard Windows folder IDs for the current account. `ShellIndex.cs` (formerly `AppIndex.cs`) caches them alongside the app catalog. `SearchProviders.cs`, `Search.cs`, `App.xaml.cs`, `Actions.cs`, and `MainWindow.xaml.cs` add the folder provider, shell activation, and result handling. Published and installed the build. Checked the Downloads, Documents, and Pictures results and verified that selecting Downloads opened its registered path in File Explorer.
- **Filtered Store placeholders out of GlazeLauncher.** `launcher/AppIndex.cs` checks the current user's main and optional packages once per background index rebuild. It excludes missing packages, packages marked as stubs on supported Windows versions, and direct Store product links. If Windows denies or fails the package query, the launcher logs it and keeps the shell catalog available. `GlazeLauncher.csproj` targets the Windows SDK projection for the package APIs. Deployed the launcher and both WinRT assemblies. Six stubs went away on this PC, reducing the index from 267 to 261. Checked that Power Automate's stub disappears and that Microsoft Store, Calculator, and Visual Studio Code remain searchable.
- **Added search providers and prioritized open windows.** `SearchProviders.cs` separates app, window and command results. `Search.cs` ranks a shared match tier and workspace ahead of provider priority and usage. `Actions.cs`, `App.xaml.cs` and `MainWindow.xaml.cs` use provider-owned actions and result metadata.
  - Checked ranking, recents, provider priorities, activation callbacks and highlight fields with a local harness. Published and installed the launcher. From ws 14, `cla` selected Claude's Window result above its Application row, and Enter made the existing Claude window visible and foreground on ws 11.
- **Added launcher transitions and match highlights.** `MainWindow.xaml` and `.xaml.cs` add a short opening effect and clearer selection and search focus, with motion controlled by Windows' animation setting. `Search.cs`, `HighlightedTextBlock.cs`, and `App.xaml` highlight the characters found by the existing matcher. The ranking comparison passed for 20,000 queries. Published and restarted the installed launcher.
  - Checked the opening and settled frames, the `sig` prefix, the `vsc` initials with a workspace target, and a process-only `windowsterminal` match in the subtitle.
- **Fixed switching to an open window on a hidden workspace.** GlazeWM 3.10.1's `focus --container-id` changed focus order without redrawing the old and new workspaces. `launcher/Actions.cs` now explicitly switches to a hidden workspace before focusing the selected window. The same path handles windows that the launcher moves and follows. Published and restarted the installed launcher through GlazeWM.
  - Checked Signal from ws 11 through the launcher pipe. Before the fix, GlazeWM focused ws 19 while Windows kept Signal cloaked. After deployment, Signal was visible and foreground on ws 19. Re-selecting it on ws 19 kept it visible and focused.
- **Added GlazeLauncher.** It finds apps, open windows and GlazeWM commands; focuses an app that's already open instead of starting another; launches or moves
  to a workspace with `@N` / Ctrl+1…9 (+Shift to stay); and ranks results by per-workspace frecency. GlazeWM config changes are in [`glazewm.md`](glazewm.md).
  - Checked: build and ReadyToRun publish; GlazeWM IPC connect and events; indexing (267 apps, ~400 ms); search ranking and icons via
    `snapshot`; `notepad @3` + Shift opened on ws 23 (focus was on the secondary monitor) while the view stayed on 21; a start via GlazeWM
    `shell-exec` relaunches detached; the real instance shows on the focused monitor, takes foreground and hides.
  - Fixed along the way: black result text (ListBox default foreground), Google Chrome ranking below its own windows, and memory (software rendering).
  - PowerToys Run held Alt+Space; the user turned it off, and **Alt+Space then opened the launcher** (confirmed by the user, no restart needed).
