# Backlog: ideas and bugs

Feature ideas and bug reports with their status. Pick work from here; one item (or one small group)
per session. When you finish one, set its status and add the change to the area doc's change log.

**Status:** `idea` (needs a design discussion with the user first) · `ready` (design agreed, can be built) ·
`in progress` · `done` (date, then remove it after a while) · `bug` (confirmed or reported problem) · `verify` (built, not yet checked).
**Size:** S (an hour or two), M (one session), L (several sessions or a new component).

## Launcher

Architecture note: many of the ideas below are "do something else with this result". Build those on one
**result actions** mechanism (below) instead of adding a new key combination per feature, and keep each
source of results as its own provider. The launcher should stay a thin core (query, ranking, UI, actions)
with providers and actions around it.

### Foundations

- **Result actions** · `done` 2026-11-27. Ctrl+K (or → at the end of the query) opens the selected result's actions in
  place of the results, filterable by typing, with submenus; Enter runs the default. Providers build `ResultAction`s on
  demand, with shared sets in `ResultActions.cs`; Ctrl+Enter, Shift, Ctrl+1…9 and `@N` are ordinary actions with
  shortcuts. See `docs/launcher.md` (Result actions). Not yet checked with the real keyboard and mouse (only through the pipe).
- **Launcher supervisor: come back after a crash** · `done` 2026-11-27 (all three layers below).
  Layers, cheapest first:
  1. `done` 2026-11-27. In the launcher: log UI-thread errors and keep running; on a background crash or a burst of UI
     errors, start a replacement before exiting (not within the first 60 s). Covers managed .NET crashes.
  2. `done` 2026-11-27. `RegisterApplicationRestart` for native crashes and hangs. **No effect on this PC**, because Windows
     Error Reporting is disabled here; it only helps where it's on.
  3. `done` 2026-11-27, as a **watcher** (`supervisor/`, see `docs/launcher.md`): restarts after any crash or kill, ends and
     restarts a hung launcher (Windows' "not responding" for three checks 2 s apart). The hotkeys stayed in the launcher; the
     design below, where the supervisor owns them and replays a key, was considered and not built. Original idea:
     A tiny native supervisor (Rust, like the collector, about 1–2 MB). GlazeWM starts it instead of the launcher. It owns the
     global hotkeys (Alt+Space, Alt+1…9) and forwards each press over the existing pipe. It starts the launcher itself (detached,
     no inherited handles) and waits on its process handle, so it uses no CPU and restarts the launcher the moment it exits.
     On a key press it expects a reply within about 300 ms. If the launcher is gone or hung, it kills or starts it and replays
     the key once the pipe is up, so the press isn't lost. An intentional Quit (exit code 0) isn't restarted until the next
     key press. Repeated crashes back off instead of looping.
  The launcher keeps registering its own hotkey when it runs without the supervisor (development, or people who don't want it).
  Considered, not recommended: binding Alt+Space in GlazeWM to `shell-exec GlazeLauncher.exe --toggle`. It starts a .NET process on every press,
  which adds noticeable delay, and it only works while GlazeWM runs.
- **Launcher settings file** · `done` 2026-11-27 (plumbing only). `%LOCALAPPDATA%\GlazeLauncher\settings.json` with
  `version`, `hotkey`, `aliases` and `indexedFolders` (`Settings.cs`). Loaded at start, reloaded on hand edits, saved
  atomically; nothing reads the values yet. Aliases, the shortcut folder and the hotkey build on it.
- **Window backend without GlazeWM** · `idea` · M. The launcher already starts and finds apps without GlazeWM.
  Put window listing and focusing behind one interface with two backends: GlazeWM IPC, and plain Win32
  (enumerate top-level windows, restore if minimized, bring to front). Decide per feature what it means
  without workspaces (for example, `@N` and GlazeWM commands hidden; focusing an open window restores it).
  Useful for people who don't run GlazeWM.

### Actions on results

- **App → Open file location** · `done` 2026-11-27. Selects the app's target exe in Explorer; windows have it too. Not
  offered for packaged apps, whose files sit in `WindowsApps`, which Explorer can't open.
- **Window → Close / End task** · `done` 2026-11-27. Close (Ctrl+W) sends a normal close through GlazeWM; End task kills
  the process after a second Enter in the action list and has no shortcut.
- **Window → Move to workspace** · `done` 2026-11-27. A submenu with slots 1–9 of the monitor you're on (what's on each,
  Ctrl+N, Shift+Enter to stay); apps have the same as "Open on workspace".
- **Action list: closer filter matching** · `idea` · S. The action filter uses the result matcher, so loose subsequence
  matches show too (`end` also lists "Close window"). Consider prefix and word-start matches only for the short action list.
- **Folder → Open terminal here** · `idea` · S. cmd, PowerShell or Windows Terminal, whichever is the default or chosen.
- **`.sln` / `.slnx` → Open with Rider, Visual Studio or VS Code** · `idea` · S–M. Detect which IDEs are installed. Depends on file results (recent files below).
- **Aliases** · `idea` · M. Extra names for a result, so `codex` finds ChatGPT. UX proposal: an "Add alias…" action on
  any app result switches the search box into alias entry; aliases are stored in the settings file, match like a
  name (exact and prefix tiers), and show as a small tag on the result. Also a way to list and remove them.

### Context: the window that was active when the launcher opened

- **Explorer in front** · `idea` · M. When the launcher opens over an Explorer window, offer "Terminal here" and
  "Duplicate this folder" (a second Explorer window on the same path, tiled next to it on the same workspace),
  showing the folder name and path. Reading the current path uses the Shell windows collection, the same API the
  planned Explorer provider needs, so build them together.

### New result sources (providers)

- **Go to workspace** · `done` 2026-11-27. A query that is only `@19` (or `@3`, using the usual one-digit rule) shows "Go to workspace N" on top; Enter switches.
- **GlazeWM cheat sheet** · `idea` · M. Read the keybindings from the live `config.yaml` (reload when the file
  changes) and make them searchable: type "move window" or "alt+shift" and see the key and what it does. Open
  questions: a prefix (`?` or `keys`) or mixed into normal results; whether Enter runs the command.
- **Shortcut folder** · `idea` · S. Index a folder the user picks (for example `C:\launcher shortcuts`) and show its items with their own badge.
- **Recent files** · `idea` · M. Start from Windows' own recent items (`%APPDATA%\Microsoft\Windows\Recent`), which needs
  no indexing service. Later perhaps per-app jump lists.
- **Paths and environment variables** · `idea` · M. Typing `%appdata%`, `C:\…` or `~` behaves like Win+R: expands the
  path, suggests child folders as you type, and opens the folder or file.
- **Explorer windows and tabs** · `idea` · M. Search already-open Explorer paths using the Shell windows collection. Investigate
  reliable tab enumeration and selection on Windows 11, with window-level results where tabs aren't available.
  [Shell windows collection](https://learn.microsoft.com/en-us/windows/win32/api/exdisp/nn-exdisp-ishellwindows) ·
  [Windows 11 File Explorer tabs](https://blogs.windows.com/windowsexperience/2022/10/18/making-the-everyday-easier-with-new-experiences-available-in-windows-11/)
- **IDE tabs (VS Code, Rider, Visual Studio)** · `idea` · L. Open editors aren't readable from outside reliably, so each IDE needs
  a small plugin that reports its open files (for example over a local named pipe). A cheaper first step: each IDE's own
  list of recent projects and solutions.
- **Browser tabs, version 2** · `idea` · L. Today's UI Automation approach needs no install, but it only sees tab titles (no URLs),
  only Chrome and Edge (no Firefox), only windows GlazeWM manages, and fullscreen and collapsed tab groups are unchecked. A browser
  extension plus a native messaging host would add URLs, Firefox, and possibly history and bookmarks, at the cost of installing
  something in each browser. Decide whether that trade is worth it before building.
- **Clipboard history** · `idea` · M–L. Search past clipboard entries and paste one into the window that was in front.
  The launcher is already resident, so it can listen for clipboard changes (`AddClipboardFormatListener`) instead of polling.
  Alternative to weigh first: read Windows' own clipboard history (Win+V, WinRT `Clipboard.GetHistoryItemsAsync`), which
  needs that history turned on but nothing stored by us. Either way: skip entries that password managers mark as excluded
  from clipboard history, start with text (images later), and either keep history in memory only or encrypt it on disk.
  Pasting means setting the clipboard, restoring the previous window and sending Ctrl+V.
- **Older ideas:** user-defined script commands (JSON), calculator, web search, per-workspace "default apps", a setting for the hotkey. All `idea`.

### Launcher bugs

- **Minimized window isn't restored** · `done` 2026-11-27. The focus path now restores it after switching workspace and waits for
  GlazeWM to see the restore. Not yet checked with a minimized floating window.
- **Minimized apps sometimes missing from results** · `bug` · S–M, needs a repro. First check which apps: apps that "minimize"
  to the tray (Signal, Discord and others can) hide their window instead, and GlazeWM drops hidden windows from its tree, so the
  launcher never sees them. If that's the cause, the fix is in the app result (Enter on the app usually brings its window back) rather than the window list.

## GlazeWM

- **Alt+N uses the wrong monitor after clicking an empty one** · `done` 2026-11-27. GlazeWM's focus follows windows, so
  clicking an empty desktop didn't change its focused monitor. The launcher now picks the monitor from the foreground window,
  or the cursor when the desktop is in front; the launcher's `@N` uses the same rule. Checked by the user with the real keys.
- **Speed of Alt+N** · `done` 2026-11-27. The launcher handles the keys itself; no PowerShell start per key press.

## Portability: other screens and a new PC

The setup should work on other screen sizes, resolutions and scaling, and be installable on a new PC without hand edits.

What already adapts (checked in the code, 2026-11-27):
- Zebar sizes are logical pixels, so they scale with Windows' display scaling. The bar is 44px tall at 100% and 66px at 150%.
- Popups are clamped to the monitor they open on (`openActivityPanel` in `src/activity-panel.ts` divides by the scale factor).
- GlazeWM gaps scale with DPI (`scale_with_dpi: true`), so the 60px top gap keeps fitting the bar.
- The launcher's workspace manager finds the monitors at runtime and rewrites the monitor anchors itself.

Items:
- **Install script for a new PC** · `idea` · M. One script that builds and installs the widget pack, collector and launcher,
  points Zebar's `settings.json` at the pack, and adds the required pieces to the GlazeWM config (keybindings, startup commands,
  the launcher ignore rule) or offers a full config to start from. Today every step is manual, and the live config isn't in Git
  at all. Probably keep a template of the GlazeWM config in the repository.
  - **Collector path (the only hard-coded path that matters).** The source `zpack.json` keeps a placeholder for the collector
    privilege, and the install script writes the real pack path into the installed copy. Nothing changes at runtime.
    Why: the bar already builds the collector path at runtime from the pack folder (`metrics-feed.ts`), but Zebar 3.3.1
    compares the requested program with the zpack `program` as an **exact string** (`check_shell_privilege` in Zebar's
    `shell_state.rs`): no path resolution and no `%VAR%` expansion. A zpack can't express "this pack's folder".
  - The storage popup's `C:\Windows\explorer.exe` could become a bare `explorer.exe` if Zebar's spawn searches the Windows
    folder (test first).
  - Already portable: the GlazeWM config uses `%USERPROFILE%`, and the launcher has no hard-coded paths.
- **Small and narrow screens** · `verify` · M. Not tested below this PC's monitors. Check: whether the bar's right side overflows
  on a narrow laptop screen (many cards plus workspace icons), and what should drop out first; whether popup content scrolls or
  gets cut off when a popup is clamped shorter than its design height (the CPU popup wants 780px); whether the CPU heatmap stays
  readable with many more logical CPUs (it draws one row per CPU in a short card).
- **Any number of monitors, adjusted automatically** · `done` 2026-11-27 for one and two monitors. The launcher's workspace
  manager (described in `docs/launcher.md`) handles up to six monitors and syncs on its own, with the merge and restore rules
  below, plus re-tiling self-maximized windows and a 30 s settle watch for windows Windows moves late. Checked by the user with
  "PC screen only", "Second screen only" and Extend. Three or more monitors are untested. The notes below are the design history.
  Before: the workspace groups (`11`–`19`, `21`–`29`) and `workspace.ps1` supported one or two monitors, threw with three,
  and only re-synced when you pressed Ctrl+Alt+R.
  Findings (2026-11-27):
  - **Zebar already adapts.** It checks the monitor list every 4 s (name, position, size, scale) and reopens every open widget
    across the current monitors (`listen_changes` in `monitor_state.rs`, `relaunch_all` in `widget_factory.rs`). The bar uses
    "all monitors", so a new monitor gets a bar within about 4 s. Not yet tested here.
  - **GlazeWM sends `monitor_added`, `monitor_updated` and `monitor_removed` events**, and each monitor has a `hardwareId` and
    `deviceName`. So a resident listener can re-sync on its own instead of waiting for Ctrl+Alt+R.
  Proposal: group *k* is `k1`–`k9`. Group 1 is the Windows primary monitor; the others follow left to right. A resident
  "workspace manager" subscribes to the monitor events and, on a change, does what Sync does now for N monitors: write the
  monitor indexes into the config, reload it, and move workspaces that are on the wrong monitor. Alt+N uses the target
  monitor's group, and picks the monitor under the cursor when the desktop is in front (this fixes the Alt+N bug above).
  **Decided (2026-11-27): the manager runs inside the launcher**, as its own module with no dependency on the launcher UI, so
  it could be split out later. The launcher already holds the GlazeWM connection and can take Alt+1…9 itself, which ends the
  PowerShell start on every key press. Because the workspace keys then depend on the launcher staying up, this goes together
  with the launcher supervisor (Launcher → Foundations).
  **Decided (2026-11-27): workspaces of a monitor that goes away merge into the same slot on the primary monitor.**
  Seen with "PC screen only": GlazeWM moved 21 and 22 onto the remaining monitor with their names, where Alt+N can't reach
  them (only `Ctrl+Alt+N`, the launcher's `@2N`, or the mouse). Rule for the manager, for any number of monitors:
  - After a monitor change, every workspace whose group has no monitor gets its windows moved to slot N of group 1
    (`22` → `12`, `35` → `15`). Group 1 is always the Windows primary, so this also covers the primary itself going away:
    the new primary becomes group 1, and its old group merges.
  - The manager remembers where each merged window came from. When that monitor comes back, windows that are still on the
    workspace they were merged into go back to their original workspace; windows the user has moved since are left alone.
  - Moving windows one by one loses the tiling layout of the merged workspace; that's accepted. Better where possible: if
    the target slot doesn't exist yet, rename the workspace instead, which keeps its layout. GlazeWM can rename with
    `update-workspace-config --workspace 22 --name 12` (already used by `workspace.ps1` Initialize).
  - Seen 2026-11-27: before the screen was turned off, the left (non-primary) screen had 11–19 and the right (primary) had
    21–29, the wrong way round. Nothing re-synced after an earlier display change. After reconnecting, the primary had 1x
    again. The manager must enforce "primary = group 1" on every monitor change, not only when Ctrl+Alt+R is pressed.
  - Still to check: how GlazeWM itself places existing workspaces on `monitor_added`, before the manager relies on it.
  Open questions: whether the config must declare every group's workspaces up front (test what GlazeWM does with workspaces
  bound to a monitor that isn't there), and whether a monitor should keep its group by `hardwareId` across docking.

## Zebar: storage

- **Disk read and write rates per drive** · `done` 2026-11-27. The bar supports per-drive Live rates
  with fixed-width readouts, Activity marks, or None. The Storage popup shows exact speeds for all
  listed drives. See [Zebar storage](zebar.md#storage-card-and-popup-srcdrivestsx-srcstorage-paneltsx).
- **Drive meter track contrast** · `done` 2026-11-27. Brightened the bar's meter tracks so partially filled
  meters show their full height next to a nearly full drive.
- **Disk activity history** · `done` 2026-11-27. Show all visible drives in the Storage popup with a shared
  scale and history window, a full-width tile for one drive, and scrolling when the grid overflows.
- **Space map, TreeSize-style** · `idea` · L. A separate helper program (not the collector: scanning terabytes is heavy and runs on
  demand) that scans a drive, caches the result and shows where the space goes at a glance. Scan options to weigh: reading the NTFS
  master file table directly (very fast, needs admin) or ordinary directory listing (no admin, slower).
  Discussed with Codex (2026-11-27), not yet agreed in full:
  - **Leave external USB drives alone for now** (the user's decision: they're touchy as it is). Only internal drives on an explicit
    allowlist of volume IDs are scanned or watched, even if Windows gives a USB drive a different letter.
  - **The helper runs only on demand.** "Analyze" on an allowed drive in the Storage popup starts the Rust helper in the background
    for that drive only. Opening the popup or starting Windows never launches it.
  - **Results are saved** (SQLite was proposed). Later visits show them at once with a "Last updated" time; Refresh starts a new scan.
  - **While the analyzer is open,** the helper can watch the selected drive and batch changes, then exit once it's closed.
  - **Measure before adding complexity.** Time to the first useful breakdown, cached navigation speed, and disk activity during a
    scan decide whether NTFS journal-based updates are worth it.

## Zebar: network

- **Network widget** · `idea` · M–L. Collector side: per-interface byte counters, connection state and type (Wi-Fi or cable) from the
  Windows interface table, which is cheap to read every second. Bar: one card with a connection icon (Wi-Fi, cable or offline) and
  upload and download sparklines for total traffic; it could replace the current Wi-Fi SSID item. Popup: per-interface rates, IP and
  SSID, and accumulated totals. To decide: which interfaces count by default (physical ones that are up; hide Hyper-V, WSL and VPN
  adapters unless chosen in Settings), and whether totals survive restarts (needs a small file, like the timer).

## Zebar: settings

- **More settings** · `idea` · S each: temperature unit, CPU card style (heatmap or sparkline), number of process rows.
