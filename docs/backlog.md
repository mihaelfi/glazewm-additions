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

- **Result actions** · `idea` · M. Every result offers a list of actions; Enter runs the default, and one key
  (Tab, → or Ctrl+K, to be decided) opens the list for the selected result, filterable by typing. Providers
  supply actions with a title, an optional shortcut and a callback; the existing Ctrl+Enter, Shift and
  Ctrl+1…9 behaviours become ordinary actions with shortcuts. Most items below depend on this.
- **Launcher settings file** · `idea` · S. A `settings.json` in `%LOCALAPPDATA%\GlazeLauncher\` for aliases,
  extra indexed folders and later the hotkey. Needed by aliases and the shortcut folder.
- **Window backend without GlazeWM** · `idea` · M. The launcher already starts and finds apps without GlazeWM.
  Put window listing and focusing behind one interface with two backends: GlazeWM IPC, and plain Win32
  (enumerate top-level windows, restore if minimized, bring to front). Decide per feature what it means
  without workspaces (for example, `@N` and GlazeWM commands hidden; focusing an open window restores it).
  Useful for people who don't run GlazeWM.

### Actions on results

- **App → Open file location** · `idea` · S. Show the exe or shortcut selected in Explorer.
- **Window → Close / End task** · `idea` · S. Close sends a normal close; End task kills the process and should
  ask for a second press, since it can lose unsaved work.
- **Window → Move to workspace** · `idea` · S. Already possible with `@N` and Ctrl+1…9; list it as an action so it's discoverable.
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

- **Go to workspace** · `ready` · S. A query that is only `@19` (or `@3`, using the usual one-digit rule) switches to that workspace, with no app selected.
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
- **Older ideas:** user-defined script commands (JSON), calculator, web search, per-workspace "default apps", a setting for the hotkey. All `idea`.

### Launcher bugs

- **Minimized window isn't restored** · `bug` · S. Picking a minimized window (seen with Signal) switches to its workspace but
  leaves it minimized, so the workspace looks empty. Likely cause: `GlazeClient.cs` doesn't read the window's state, and the focus
  path in `Actions.cs` never restores it. Fix: restore before focusing.
- **Minimized apps sometimes missing from results** · `bug` · S–M, needs a repro. First check which apps: apps that "minimize"
  to the tray (Signal, Discord and others can) hide their window instead, and GlazeWM drops hidden windows from its tree, so the
  launcher never sees them. If that's the cause, the fix is in the app result (Enter on the app usually brings its window back) rather than the window list.

## GlazeWM

- **Alt+N uses the wrong monitor after clicking an empty one** · `bug` · S–M. Click the empty desktop on monitor 1, press Alt+2:
  expected ws 12, got ws 22 (the previously focused monitor). Likely cause: GlazeWM's focus follows windows, and an empty desktop
  has none, so GlazeWM's focused monitor doesn't change; `workspace.ps1` asks GlazeWM. Possible fix: when the foreground window
  isn't one GlazeWM manages (the desktop), use the monitor under the cursor. The launcher's one-digit `@N` uses the same rule and
  should get the same fix.
- **Unfinished note from the user:** "make sure that the configuration is …" (cut off). Ask what was meant.
- **Speed of Alt+N** · `idea` · S, measure first. Each Alt+N starts a PowerShell process. If it feels slow, move the logic into
  something already running (the launcher) or a small native helper.

## Zebar: storage

- **Disk read and write rates per drive** · `idea` · M. The collector can read per-volume counters
  (`\LogicalDisk(*)\Disk Read Bytes/sec` and `Disk Write Bytes/sec`) with the same PDH code the GPU uses, keyed by drive letter so it
  lines up with the storage card. To discuss: what goes on the bar (keep free space only, or add an activity hint) and what the popup
  shows (read/write graphs per drive, or a drive × time heatmap like the CPU one).
- **Space map, TreeSize-style** · `idea` · L. A separate helper program (not the collector: scanning terabytes is heavy and runs on
  demand) that scans a drive, caches the result and shows where the space goes at a glance. Scan options to weigh: reading the NTFS
  master file table directly (very fast, needs admin) or ordinary directory listing (no admin, slower).

## Zebar: network

- **Network widget** · `idea` · M–L. Collector side: per-interface byte counters, connection state and type (Wi-Fi or cable) from the
  Windows interface table, which is cheap to read every second. Bar: one card with a connection icon (Wi-Fi, cable or offline) and
  upload and download sparklines for total traffic; it could replace the current Wi-Fi SSID item. Popup: per-interface rates, IP and
  SSID, and accumulated totals. To decide: which interfaces count by default (physical ones that are up; hide Hyper-V, WSL and VPN
  adapters unless chosen in Settings), and whether totals survive restarts (needs a small file, like the timer).

## Zebar: settings

- **More settings** · `idea` · S each: temperature unit, CPU card style (heatmap or sparkline), number of process rows.
