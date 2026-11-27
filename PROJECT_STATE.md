# Hyprland-style Windows desktop: GlazeWM + Zebar

Shared state for anyone (person or agent) working in this folder. Read it before
starting; add to it when you finish a change. See "How to update this file" at the end.

Last updated: 2026-11-27

## Goal

A Hyprland-like tiling desktop on Windows 11: GlazeWM tiles the windows, a
custom Zebar top bar shows workspaces (with app icons), drive free space, CPU, RAM
and GPU graphs, and system status, and a custom workspace-aware launcher
(**GlazeLauncher**, WPF / .NET 10) opens on Alt+Space.

**Performance comes first.** The widgets use **Solid**, not React, on purpose:
it has no virtual DOM and updates with fine-grained signals. Graphs draw to
`<canvas>`, and all sampling happens in one small native Rust collector shared by
every bar, not in the webviews. Keep it that way: no React or other heavy
frameworks or chart libraries, and no per-widget polling loops that duplicate the collector.

## Where things live

| What | Path | Notes |
|---|---|---|
| GlazeWM config (live) | `%USERPROFILE%\.glzr\glazewm\config.yaml` | Edited in place; GlazeWM reads it from here |
| Workspace helper script | `%USERPROFILE%\.glzr\glazewm\scripts\workspace.ps1` | Called by keybindings and at startup |
| Zebar settings | `%USERPROFILE%\.glzr\zebar\settings.json` | Picks which widget pack starts |
| Installed widget pack | `%USERPROFILE%\.glzr\zebar\cpu-activity\` | Built output + collector exe; Zebar loads from here |
| Widget settings | `%USERPROFILE%\.glzr\zebar\cpu-activity\settings.json` | Written by the collector when Settings changes; missing = defaults |
| Widget pack source | `C:\codex\glazewm\zebar-cpu\` | Solid + TypeScript + Vite, plus a Rust collector (`native/main.rs`, `native/processes.rs`, `native/gpu.rs`, `native/settings.rs`) |
| GlazeWM installer | `C:\codex\glazewm\glazewm-v3.10.1.exe` | Version in use: GlazeWM 3.10.1 |
| Launcher source | `C:\codex\glazewm\launcher\` | WPF, .NET 10 (`GlazeLauncher.csproj`) |
| Installed launcher | `%USERPROFILE%\.glzr\launcher\` | Published output; GlazeWM starts it from here |
| Launcher data | `%LOCALAPPDATA%\GlazeLauncher\` | `launcher.log`, `usage.json` (frecency), `snapshot.png` (debug) |

Versions: GlazeWM 3.10.1, Zebar 3.3.1 (npm `zebar@3.3.1`), `glazewm@1.7.0` JS client, Solid 1.9, Vite 7, .NET SDK 10.0.115 (desktop runtime 10.0.15).

The `launcher/` and `zebar-cpu/` sources share one Git repository rooted at `C:\codex\glazewm`.
The live config is not in this folder or in Git. Before a risky edit, copy the
file next to itself with a dated suffix, as earlier edits did (see "Backups").

## GlazeWM configuration changes

Starting point: a fresh default config for 3.10.1, created 2026-11-22. The older
May 2026 setup, which used YASB instead of Zebar, was archived to
`%USERPROFILE%\.glzr\glazewm-backup-20261122-193910\` (it has its own `CHANGELOG.md`).
It is not used now.

Unchanged from the defaults: gaps (inner 20px; outer 60px top and 20px on the other
sides, which leaves room for the 44px bar), border colours, `hide_method: cloak`,
and window rules (Zebar windows are ignored by default).

### Two workspace groups, one per monitor

- Workspaces `11`–`19` are bound to the primary monitor and `21`–`29` to the secondary one.
- The bindings use YAML anchors so the script can swap them:
  `bind_to_monitor: &primary_monitor 1` and `bind_to_monitor: &secondary_monitor 0`.
- History: the first version (10:53) used `10`–`19` / `20`–`29` with a `0` key. At
  11:01 it was cut to nine per monitor, `11`–`19` / `21`–`29`, and the `alt+0` bindings were removed.

### Keybindings (changed or added)

| Keys | Action |
|---|---|
| `alt+1`…`alt+9` | `workspace.ps1 -Action Focus -Digit N`: focus workspace `1N` on the primary monitor, or `2N` if the secondary monitor has focus |
| `alt+shift+1`…`9` | `workspace.ps1 -Action Move -Digit N`: move the focused window to that workspace, then focus it |
| `ctrl+alt+1`…`9` | `focus --workspace 2N` directly, to reach the secondary group when undocked |
| `ctrl+alt+r` | `workspace.ps1 -Action Sync`: re-read the display layout and put workspaces back on the right monitors (shows a message box on error) |
| `alt+s` / `alt+a` | Changed to `focus --next/prev-active-workspace-on-monitor` (cycles only on the current display) |
| `alt+space` | **Not bound in GlazeWM.** GlazeLauncher registers it itself (a GlazeWM binding would swallow it) |
| `alt+c` | `wm-cycle-focus` (tiling → floating → fullscreen), moved here from `alt+space` |

The default `focus --workspace N` and `move --workspace N` bindings were replaced by the script calls above.
All script calls use `shell-exec --hide-window powershell.exe -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File ...`.

### Startup

`startup_commands` now runs `shell-exec zebar`, then `workspace.ps1 -Action Initialize`, then
`shell-exec %USERPROFILE%/.glzr/launcher/GlazeLauncher.exe`.

Window rules: an extra `ignore` rule for `window_process: GlazeLauncher`, so GlazeWM never tiles the launcher popup.

### `scripts/workspace.ps1`

- Actions: `Focus`, `Move`, `Sync`, `Initialize`; `-Digit` takes 1–9.
- Picks base 10 or 20 by checking whether the focused GlazeWM monitor is the Windows
  primary screen (`System.Windows.Forms.Screen`). With only one monitor it always uses base 10.
- `Sync`/`Initialize` with two monitors: rewrites the `primary_monitor`/`secondary_monitor`
  anchor indexes in `config.yaml` (atomic replace, with rollback if `wm-reload-config`
  fails), then moves any workspace on the wrong monitor with `move-workspace --direction`,
  which keeps its windows and layout. The two monitors must share an edge.
- `Initialize` with one monitor: if GlazeWM started that monitor on workspace `21`, it
  renames it to `11` and binds it to monitor 1.
- Requires Windows and GlazeWM to agree on 1 or 2 displays; otherwise it throws ("wait after docking, then Ctrl+Alt+R").

## Zebar: the `cpu-activity` widget pack

`settings.json` startup changed from `glzr-io.starter` / `with-glazewm` to
pack `cpu-activity`, widget `bar`, preset `default` (backup: `settings.before-cpu-activity-20261127-111335.json`).

### Widgets (`zpack.json`)

| Widget | HTML | Window | Purpose |
|---|---|---|---|
| `bar` | `dist/bar.html` | 100% × 44px, top-left, all monitors, `zOrder: normal` | The status bar |
| `cpu-activity` | `dist/panel.html` | 576 × 780, top-right, `top_most`, focused | CPU details pop-up |
| `ram-activity` | `dist/ram.html` | 576 × 620, top-right, `top_most`, focused | RAM details pop-up |
| `gpu-activity` | `dist/gpu.html` | 576 × 700, top-right, `top_most`, focused | GPU details pop-up |
| `settings` | `dist/settings.html` | 576 × 660, top-right, `top_most`, focused | Settings pop-up, opened from the gear in any popup |
| `storage-activity` | `dist/storage.html` | 576 × 470, top-right, `top_most`, focused | Storage details pop-up; may launch `explorer.exe` with a drive root |

The bar opens the panels with `startWidget` on the bar's own monitor
(`src/activity-panel.ts`, offset −12px / 50px, size clamped to the monitor). The
heights in code come from `panelHeights` in `activity-panel.ts`, not from the zpack
presets, so change both together. A panel closes on Esc, on blur, with its × button,
or when its bar button is clicked again.

**Toggle behaviour** (`usePanelToggle` / `useCloseActivityPanel` in `activity-panel.ts`):
- The bar and its panels share an origin, so they talk over a `BroadcastChannel('cpu-activity-panels')`.
  A panel posts `closed` when it closes; the bar posts `close` to ask one to close.
- Pressing the mouse on the bar takes focus from the panel, which closes it on blur
  *before* the click arrives. So the bar reads the state on `pointerdown`, and a panel
  that closed in the last 600 ms counts as open: the click then does not reopen it.
- Clicking a different metric while a panel is open opens the new one, and the old one closes on blur.
- Each bar tracks only the panels it opened itself, so with one bar per monitor, a click on the other monitor's bar still opens a panel there.

The bar's `privileges.shellCommands` allows exactly one program, the collector exe, with no
arguments. **The installed `zpack.json` uses the absolute path**
`C:\Users\mihae\.glzr\zebar\cpu-activity\zebar-cpu-collector.exe`, **while the source
`zebar-cpu/zpack.json` has the bare name `zebar-cpu-collector.exe`.** Keep the absolute
path when you deploy (or check that the relative one works first).

### Bar layout (`src/bar.tsx`)

Left: a brand mark (four dots), then the workspace buttons. Right, in order (fixed by the "widget reorder" change):
paused indicator and binding-mode buttons (shown only when active) → Wi-Fi SSID → battery →
**storage card** → **RAM** (sparkline + % ) → **CPU** (heatmap + %) → **GPU** (green sparkline + %) → tiling-direction toggle (↔/↕) →
weather (°C) → input language → clock (`yyyy-MM-dd HH:mm`).

- **Input language** is shortened to the first part of the layout code, in upper case (e.g. `en-US` → `EN`). This is the "short language labels" change.
- Clicking a workspace runs `focus --workspace <name>`. Clicking the tiling toggle runs `toggle-tiling-direction`.
- **GlazeWM command strings: never quote arguments.** GlazeWM keeps the quotes as part of the value, so `focus --workspace "12"` looks for a workspace named `"12"` and fails. Pass names bare. (The bar used `JSON.stringify` here until 2026-11-27, which broke workspace clicks and the binding-mode button.)

### Storage card and popup (`src/drives.tsx`, `src/storage-panel.tsx`)

- **Bar card**: one framed card, like RAM/CPU/GPU, with a mini bar chart: one small
  vertical bar per drive, filled to its **used** %, with the letter underneath. It has **no
  text label** (decided 2026-11-27): you can see the state at a glance, and the details are in the popup.
  The hover tooltip lists `X: N free of M (P% used)` for every drive. Clicking toggles the popup.
- **Storage popup** (`storage-activity`, 576 × 470): total free and total capacity,
  the fullest drive, and one row per drive (letter badge, volume label, file system,
  "N free of M", a horizontal used bar, used %). **Clicking a row opens that drive in File
  Explorer** and closes the popup.
- **History of this design:** the first version (2026-11-27) put six loose `C | 947 GB` items on the bar.
  It looked out of place next to the framed cards, the thin bar read like a text cursor, and it was
  the widest group on the bar. It was replaced with the card and popup above.
- **Units:** binary, with GB/TB labels to match Explorer. Below 10 GB one decimal, below
  1 TB whole GB, from 1 TB up TB with one decimal (`formatDriveSize`).
- **Colours:** blue normally, amber at ≥ 85% used, red at ≥ 95% used, on the card bars, the row bars and the letters.
- **Data:** Zebar's built-in `disk` provider with `refreshInterval: 60000` (`useDrives()`). It's used
  in both the bar and the popup and doesn't touch the collector, because free space changes slowly.
- **Opening Explorer:** the popup calls `shellSpawn('C:\Windows\explorer.exe', ['D:\'])`. The
  widget's zpack privilege allows only that program with `argsRegex` `^[A-Z]:\\$` (JSON: `"^[A-Z]:\\\\$"`) (one drive
  root). Checked: the `explorer.exe` that Zebar starts hands off to Windows' COM Explorer
  factory (`explorer.exe /factory,… -Embedding`, parent svchost) and exits. So unlike the
  collector, it does **not** keep a copy of Zebar's port, and Zebar restarted cleanly with a
  drive window open.
- This PC: C 3.6 TB, D 1.8 TB, E 3.6 TB, F 16 TB (external), G 7.3 TB (98% used, shows red), H 7.3 TB.

### Settings (`src/settings.ts`, `src/settings-panel.tsx`, `native/settings.rs`)

- **Why our own popup:** Zebar 3.3.1's pack editor (Settings → Custom packs → pack) can't hold
  custom options. A widget's config is only placement presets, privileges and caching (`WidgetConfig` in
  `node_modules/zebar/dist/index.d.ts`).
- **Opening it:** the gear button in the header of every popup (`src/panel-header.tsx`, shared by all four
  popups). The popup you came from closes on blur. Settings closes on Esc, on blur or with ×.
- **Options** (all detected at runtime, nothing specific to this PC):

  | Setting | Values | Default | Effect |
  |---|---|---|---|
  | `historyMinutes` | 1, 5, 10, 15 | 1 | Window for every graph, heatmap, "N ago" label and the CPU process Avg/Peak |
  | `bar.storage/ram/cpu/gpu` | booleans | all `true` | Which cards the bar shows. At least one must stay, because the cards are the only way into the popups (enforced in the UI and the collector) |
  | `drives` | letters or `null` | `null` = all | Drives in the storage card **and** popup. Selecting every drive saves `null`, so drives added later show up |
  | `gpuAdapter` | adapter name or `null` | `null` = most video memory | GPU the bar's GPU card follows. The GPU popup also opens on that tab |

- **Storage:** the collector owns `settings.json` (next to its exe, i.e. the pack folder) and serves
  `POST /settings` → `{ version, settings }` and `POST /settings/save` (JSON body, ≤ 8 KB) →
  the same. Saves are cleaned up (unknown history value → 1, invalid letters dropped, all bar items off →
  all on) and written atomically (temp file + rename). Bad JSON → 400; a wrong Origin → 403, like every endpoint.
- **Live updates:** every `/history` response carries `settingsVersion`. The bar passes it to
  `useSettings().sync`, which refetches when it changes, so changes apply within about 1 s. Popups fetch settings
  once when they open.
- **History length:** the collector always keeps 15 min (900 samples, and the same for per-process CPU). Widgets ask for
  just their window with `/history?…&limit=<seconds>` and `/cpu/processes?window=<seconds>`. When the
  window grows, the feed refetches the whole window (`session = 0`). The heatmap merges long windows into
  at most 120 columns, keeping each column's **peak**, so short spikes stay visible.

### Workspace app icons

- Each workspace button shows its number and then one icon per distinct app in that workspace, sorted by name.
- `src/workspace-apps.ts` maps process names to Nerd Font glyph classes (`knownApps`):
  ChatGPT, Claude, Discord, Docker Desktop, Explorer, Firefox, FxSound, Chrome, Jellyfin, Edge,
  Excel, Outlook (`outlook`/`olk`), PowerPoint, Word, Notepad, PyCharm, ShareX, Signal, Spotify,
  Steam, VS Code, Windows Terminal.
- An unknown app gets two-letter initials in a small pill instead.
- Live updates: it runs its own `WmClient` and subscribes to window, workspace, monitor
  and pause events, waiting 50 ms to batch bursts, then calls `queryWorkspaces`. If that
  connection drops it falls back to the Zebar glazewm provider's data.
- The icons come from `src/workspace-icons.woff2`, a **subset** of the Nerd Fonts 3.5.1
  webfont (licence in `public/workspace-icons-LICENSE.txt`), with codepoints set in
  `src/workspace-icons.css`. **To add an app you need three things: an entry in
  `knownApps`, a CSS rule with its codepoint, and the glyph in the subset font.** An
  existing glyph can be reused without touching the font.

### CPU and RAM widgets

- **Collector** (`native/main.rs` + `native/gpu.rs`, Rust, built as `zebar-cpu-collector.exe`): a windowless
  HTTP server on `127.0.0.1:6128` that uses `sysinfo` for CPU and RAM and the `windows` crate for the GPU.
  - Samples total and per-logical-CPU usage plus RAM (used/available/total) every second and keeps 60 samples.
  - `POST /history?session=&after=` returns `{ session, gpus, samples }` with the new samples.
  - `POST /memory/processes` returns the top 10 processes by working set, from the latest 1 s sample.
  - `POST /cpu/processes` returns `{ window, processes: [{ name, pid, current, average, peak }] }`: the top 40 by average. Values are % of **total** CPU (Task Manager style); `window` is the number of seconds covered (up to 60).
  - `POST /gpu/processes` returns the top 10 GPU processes per adapter, refreshed at most every 3 s.
  - **Per-process sampling** (`native/processes.rs`): once a second, a single
    `NtQuerySystemInformation(SystemProcessInformation)` call (the one Task Manager uses) reads
    CPU time, working set and image name for every process. The struct is read at fixed
    x64 offsets (see the constants). The CPU share is the change in CPU time divided by wall time × logical CPUs.
    Each second stores the processes that used at least 0.05%. Processes are keyed by (PID, creation time), so a
    reused PID isn't merged. Names are kept while a process runs or is still in the window.
  - **Why not sysinfo for processes:** sysinfo opens every process and calls `GetProcessTimes` /
    `GetProcessMemoryInfo` on each (about 360 processes here). That took the collector from 0.2% to
    1.3% of one core. The single call costs 0.7% of one core (about 0.03% of this 24-thread
    CPU), mostly Windows building the list. sysinfo is still used for total CPU, per-core CPU and RAM.
  - Only answers requests with `Origin: http://127.0.0.1:6124` (Zebar) and the matching `Host`; anything else gets 403.
  - Exits after 20 s with no clients. If the port is already in use it exits quietly, so several bars share one collector.
  - **Relaunches itself on start.** Zebar's `shellSpawn` creates child processes with handle inheritance on, so the collector used to inherit Zebar's own server socket (`127.0.0.1:6124`). That socket outlived Zebar, and a restarted Zebar couldn't serve its widgets: the bar showed as a grey strip with a scrollbar. Now, when started with no arguments, the collector starts a copy of itself with `--detached` (via `CreateProcessW`, inheritance off) and exits. Only the `--detached` copy serves. `--detached` and `--probe` are the only accepted arguments.
- **GPU sampling** (`native/gpu.rs`) uses the same sources as Task Manager, so it works for any vendor:
  - **Adapters:** DXGI provides names and dedicated/shared memory totals. Software adapters and Microsoft Basic Render are skipped, and so are DXGI entries that have no performance counters (on this PC DXGI lists the RTX 4090 twice). They're sorted by dedicated memory, so adapter 0 is the discrete card. On this PC that's the RTX 4090, then the AMD Radeon iGPU.
  - **Load:** the `\GPU Engine(*)\Utilization Percentage` counter is summed over processes for each engine. Adapter load is the busiest engine, as in Task Manager. Engines are grouped into 3D / Copy / Video Decode / Video Encode / Compute (`Compute*` and `Cuda`).
  - **Memory:** `\GPU Adapter Memory(*)\Dedicated Usage` and `Shared Usage`.
  - **Temperature:** `D3DKMTQueryAdapterInfo(KMTQAITYPE_ADAPTERPERFDATA)`, reported in tenths of a degree.
  - **Per process:** the busiest engine for each PID plus `\GPU Process Memory(*)\Dedicated Usage`. Names come from a Toolhelp snapshot, which also names protected processes such as dwm.exe.
  - **Gotcha:** counter instance names use **upper-case** hex LUIDs (`luid_0x00000000_0x000160AD`). PowerShell's `Get-Counter` shows them in lower case, which is misleading.
  - **dwm.exe** reports about 17 GB of "dedicated" memory. That is the real counter value (Task Manager shows the same), not a bug.
  - **Cost:** about 2 ms per sample. The whole collector uses about 0.2% of one core and 18 MB.
  - **Debugging:** `zebar-cpu-collector.exe --probe > probe.json` prints the adapters, one sample and the process list without needing the bar.
- **Feed** (`src/metrics-feed.ts`): polls once a second with POST, because Zebar's
  service worker caches cross-origin GETs. If the poll fails, the bar spawns the collector
  through `shellSpawn`, at most once every 6 s. Status is `starting` → `live` → `unavailable`, and the UI shows `—` when not live.
- **Bar**: the RAM button has a compact teal `HistoryGraph` sparkline; the CPU button has
  a `Heatmap` canvas (one row per logical CPU, 60 columns, blue→red).
- **CPU panel** (`src/panel.tsx`): total, busiest core, an all-core heatmap with a colour
  key, a graph of the selected core over the history window with its peak, a clickable grid of logical processors,
  and **Top processes · last N min** (N = the history setting): Process, PID, **Now**, **Avg** and **Peak**, top 8. It's
  sorted by Avg by default; click a column header to re-sort. It refreshes every 2 s while open. The
  point is that a process that spiked for 30 s and then stopped still ranks high on Avg and Peak.
- **RAM panel** (`src/ram-panel.tsx`): usage %, a 60 s history graph, used/available/total
  in GiB/MiB, and the top 10 processes by memory.
- **GPU bar button**: a green sparkline of adapter 0's load plus %. Its tooltip shows the GPU name and temperature. It is hidden if the collector reports no GPUs.
- **GPU panel** (`src/gpu-panel.tsx`): adapter tabs (when there's more than one GPU), then load, dedicated memory (used / total with a meter), shared memory and temperature. Below that, a 60 s graph for the selected engine (Overall by default) with its peak, a clickable engine row, and the top 8 processes (GPU % and dedicated memory).
- Theme: dark navy bar `#151b30`, accent `#b6acff`; CPU in purple tones, RAM in teal, GPU in green (`src/widget.css`, `src/history-graph.tsx`). Compact graphs use the `.sparkline` class.

### Build and deploy (from what is on disk; check before relying on it)

The repo has no deploy script. The steps below were worked out from the files present:

```powershell
cd C:\codex\glazewm\zebar-cpu
npm install --cache .npm-cache      # once; earlier runs used this cache flag
npm run build                       # tsc --noEmit && vite build -> dist/
cargo build --release --offline     # -> target\release\zebar-cpu-collector.exe (crates are in the local cache)
```

Then **replace** the installed `dist\` as a whole: move the old one aside as
`dist.before-<change>-<timestamp>` and copy the new `dist\` in, so all three HTML files and
their assets stay in step. Copy `target\release\zebar-cpu-collector.exe` only if it changed.
Keep the installed `zpack.json` (see the absolute-path note above); when you add a
widget, add it to both the installed and the source `zpack.json`. Before replacing the
collector exe, stop `zebar-cpu-collector` (the exe is locked while it runs); the bar
starts the new one within about 6 s. The panels load fresh each time they open. The bar
and any `zpack.json` change need a Zebar restart:

```powershell
Stop-Process -Name zebar -Force; Start-Sleep -Seconds 3; Start-Process zebar
```

**If the bar is ever a grey strip after a restart**, Zebar couldn't start its widget server
on `127.0.0.1:6124`. Check which PID owns it with
`netstat -ano | findstr "LISTENING" | findstr "127.0.0.1:6124"`. The known cause (the collector
inheriting the socket) was fixed on 2026-11-27, and a restart with the collector still running was
checked afterwards. If it comes back, look for another child process of Zebar holding the port.

## GlazeLauncher: workspace-aware app launcher

A resident WPF (.NET 10) app in `C:\codex\glazewm\launcher\`. It replaces PowerToys Run on Alt+Space
(PowerToys Run is turned off; Command Palette stays on Win+Alt+Space) and knows about GlazeWM's monitors, workspaces and windows. Same performance rule as the bar: it stays
resident with its window pre-created (showing it is just `Show()`), gets pushed updates from GlazeWM
instead of polling, and does shell work on one background STA thread.

### Using it

- **Alt+Space** toggles it. It opens centred on the monitor GlazeWM has focused, a fifth of the way down. Esc, or clicking elsewhere, closes it.
- An **empty query** lists recent items for the current workspace, then every open window (other workspaces first) as a window switcher.
- Typing searches **apps** (Start menu and Store apps, from `shell:AppsFolder`), **open windows** (by title or process),
  and **commands** (tiling direction, floating, fullscreen, redraw, reload config, pause, workspace Sync, re-index, quit).
  Matching ranks prefix > whole word > word start > initials (`vsc` → Visual Studio Code) > substring > subsequence,
  plus a frecency boost that is stronger for the current workspace.
- **Apps:** Enter opens it. If it is already open, Enter focuses its window instead; pressing it again cycles through that app's windows.
  Ctrl+Enter always opens a new window. The badge `● 12 21` shows the workspaces where it's open.
- **Windows:** Enter switches to the window's workspace if hidden and focuses the selected window. Ctrl+Enter brings it to the current workspace.
- **Send to a workspace:** add `@N` to the query, or press **Ctrl+1…9** on a result. One digit means slot N on the
  focused monitor's group (`@3` → `13` on the primary monitor, `23` on the secondary), the same rule as `workspace.ps1`;
  two digits (`@23`) are taken literally. By default your view follows the window; hold **Shift** to stay where you are
  (Hyprland's "silent"). The chip at the right of the search box shows the target (`→ ws 13`).

### How it works

| File | Role |
|---|---|
| `App.xaml.cs` | Single instance (mutex `Local\GlazeLauncher.Instance`), Alt+Space `RegisterHotKey` (retried every 5 s while another program holds it), named-pipe server `GlazeLauncher`, and the detached relaunch |
| `GlazeClient.cs` | WebSocket client for GlazeWM IPC `ws://127.0.0.1:6123`: `sub --events …`, `query monitors`, `command [--id X] …`. On any event it re-queries the tree (80 ms debounce) and forwards `window_managed` |
| `AppIndex.cs` | Enumerates `shell:AppsFolder` through `Shell.Application` (~270 apps in ~400 ms), re-indexed when it is opened and the index is older than 2 min |
| `Search.cs` | Query parsing (`@N`), fuzzy scoring, ranking, built-in command list, `WindowIdentity` |
| `Actions.cs` | Launch / focus / move. Open app and window results share a focus path that runs `focus --workspace N` for hidden workspaces before `focus --container-id`. After an `@N` launch, the next matching `window_managed` window (within 15 s) gets `move --workspace N` and uses the same focus path if following. |
| `IconCache.cs` | Shell icons (`IShellItemImageFactory`, 48px, alpha kept) loaded on the STA thread, all app icons warmed after indexing |
| `UsageStore.cs` | Frecency (half-life 10 days), global and per workspace, saved to `usage.json` |
| `MainWindow.xaml(.cs)` | UI (bar palette `#151b30` / `#b6acff`, DWM rounded corners), keys, placement |

- **Matching a window to an app.** An app's IDs are its AppsFolder ID (an AUMID or a path), its exe name, its name with spaces removed, and for packaged apps the family name plus a small alias table (`PackagedProcessAliases`: Terminal, Notepad, Calculator, Paint). A window's IDs are its process name, its process's packaged AUMID (`GetApplicationUserModelId`), and its window AUMID (`PKEY_AppUserModel_ID`). They match if the two sets share any ID. Squirrel apps (Discord) point at `Update.exe`, which is ignored; the name match covers them.
- **Launching** uses ShellExecute on `shell:AppsFolder\<id>`, which works for Win32 and Store apps alike.
- **Detached relaunch:** started without `--detached`, the exe relaunches itself through ShellExecute (no handle inheritance) and exits. That's the same protection the Zebar collector has against keeping a parent's sockets alive.
- **Command line:** `GlazeLauncher.exe --toggle|--show|--hide|--reindex|--quit` forwards the command to the running instance over the pipe. With no argument a second start toggles.
- **Debug pipe commands** (send them with a `NamedPipeClientStream` to `GlazeLauncher`, not available as exe flags): `query:<text>` types a query,
  `enter`, `enter:ctrl` or `enter:shift` activates the selection, and `snapshot` renders the window to `%LOCALAPPDATA%\GlazeLauncher\snapshot.png`.
- **Rendering is software-only** (`RenderMode.SoftwareOnly`), and concurrent GC is off. GPU rendering loaded the NVIDIA driver into the process: about 113 → 77 MB private memory after a search. Working set is about 170 MB, mostly shared WPF DLL pages.

### Build and deploy

```powershell
cd C:\codex\glazewm\launcher
dotnet publish -c Release -r win-x64 --self-contained false -o publish   # ReadyToRun, framework-dependent
GlazeLauncher.exe --quit        # from %USERPROFILE%\.glzr\launcher (or Stop-Process -Name GlazeLauncher)
Copy-Item publish\* $env:USERPROFILE\.glzr\launcher -Force
```

Then start it again (or reload GlazeWM's startup by restarting GlazeWM).

**Testing gotcha (agents):** the Claude desktop app is an MSIX package. Processes started from its shell
run inside that package, so their writes to `%LOCALAPPDATA%` are redirected to
`%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\…`, and reads from that shell see the
redirected copy. A launcher you start from the shell therefore logs to a different `launcher.log`
than the real instance GlazeWM starts. Check the real instance with read-only window checks or ask the user. The computer-use
screenshot tool can't target the popup (it has no taskbar or Start entry); use the `snapshot` pipe command instead.

## Backups made so far

- `.glzr\glazewm\config.before-monitor-workspaces-20261127-105357.yaml`: default config before the workspace groups
- `.glzr\glazewm\backup-before-nine-workspaces-20261127-110114\`: config + script from the 10–19 / 20–29 version
- `.glzr\glazewm\config.before-launcher-20261127-140419.yaml`: config before the launcher (alt+space was `wm-cycle-focus`)
- `.glzr\launcher\before-workspace-focus-20261127-150241\`: installed launcher files from before the workspace focus fix
- `.glzr\zebar\settings.before-cpu-activity-20261127-111335.json`: starter-pack Zebar settings
- `.glzr\zebar\cpu-activity\backup-before-gpu-20261127-124849\`: installed `dist\`, `zpack.json` and collector exe from before the GPU widget and toggle
- `.glzr\zebar\cpu-activity\bar.before-workspace-icons-20261127-111836.html`, `bar.before-widget-reorder-20261127-114545.html`, `bar.before-short-language-labels-20261127-120006.html`

## Open items and known issues

- Create the private GitHub repository and push the initial commit once GitHub authentication is verified as `mihaelfi`.
- Ideas for more settings: temperature unit, clock format, CPU card style (heatmap or sparkline), and the number of process rows.
- **Not yet portable:** the installed `zpack.json` uses the absolute collector path `C:\Users\mihae\...`. To share the pack, check whether Zebar accepts a path relative to the pack.
- **PowerToys Run must stay off** (it was turned off on 2026-11-27). If it is turned back on, it takes Alt+Space again:
  `RegisterHotKey` fails with error 1409, and the launcher keeps retrying every 5 s.
- Launcher, not yet checked: Ctrl+1…9 from the keyboard, and `@N` launches of apps that start slowly or
  hand off to another process (only Notepad silent → ws 23 was tested). Store apps whose process name doesn't match their package
  may need an entry in `PackagedProcessAliases`.
- The workspace focus fix was checked live with Signal's Application result. The Window result uses the same focus path and still needs a separate UI check.
- Launcher ideas for later: user-defined script commands (JSON), calculator, web search, per-workspace "default apps", a setting for the hotkey.
- The toggle has only been tested with the bar on the primary monitor. The other monitor's bar should behave the same way (each bar tracks its own panels), but that hasn't been checked.

## Change log

Newest first. One entry per piece of work: date, what changed, files touched, anything left unfinished.

### 2026-11-27
- **Started tracking GlazeLauncher and the Zebar widget pack in Git.** Both sources, dependency lockfiles, and project notes are tracked together on `main`. The root `.gitignore` excludes the GlazeWM installer and launcher build outputs. The existing widget `.gitignore` excludes widget build outputs and dependency caches. GitHub publishing is pending authentication as `mihaelfi`.
- **Fixed switching to an open window on a hidden workspace.** GlazeWM 3.10.1's `focus --container-id` changed focus order without redrawing the old and new workspaces. `launcher/Actions.cs` now explicitly switches to a hidden workspace before focusing the selected window. The same path handles windows that the launcher moves and follows. Published and restarted the installed launcher through GlazeWM.
  - Checked Signal from ws 11 through the launcher pipe. Before the fix, GlazeWM focused ws 19 while Windows kept Signal cloaked. After deployment, Signal was visible and foreground on ws 19. Re-selecting it on ws 19 kept it visible and focused. The desktop was returned to its original ws 29 after testing.
- **Added GlazeLauncher**, a workspace-aware launcher (WPF / .NET 10) in `launcher\`, installed to `.glzr\launcher\`.
  It finds apps, open windows and GlazeWM commands; focuses an app that's already open instead of starting another; launches or moves
  to a workspace with `@N` / Ctrl+1…9 (+Shift to stay); and ranks results by per-workspace frecency. GlazeWM config (backup
  `config.before-launcher-20261127-140419.yaml`): `wm-cycle-focus` moved from `alt+space` to `alt+c`, an `ignore` rule for
  `GlazeLauncher`, and a startup command.
  - Checked: build and ReadyToRun publish; GlazeWM IPC connect and events; indexing (267 apps, ~400 ms); search ranking and icons via
    `snapshot`; `notepad @3` + Shift opened on ws 23 (focus was on the secondary monitor) while the view stayed on 21; a start via GlazeWM
    `shell-exec` relaunches detached; the real instance shows on the focused monitor, takes foreground and hides.
  - Fixed along the way: black result text (ListBox default foreground), Google Chrome ranking below its own windows, and memory (software rendering).
  - PowerToys Run held Alt+Space; the user turned it off, and **Alt+Space then opened the launcher** (confirmed by the user, no restart needed).
- **Added a Settings popup** (see "Settings" above): history window, bar items, drives, GPU for the bar.
  - New files: `native/settings.rs`, `src/settings.ts`, `src/settings-panel.tsx`, `src/panel-header.tsx`, `settings.html`.
  - Changed: `main.rs` (`/settings`, `/settings/save`, `limit` and `window` parameters, 15 min history), `processes.rs` (`cpu_summary(window)`), `metrics-feed.ts` (history-length accessor, `settingsVersion`), `heatmap.tsx` and `history-graph.tsx` (`length` prop, peak-merged columns), `bar.tsx`, all four popups, `activity-panel.ts` (exported `openActivityPanel`, `settings: 660`), `widget.css`, `vite.config.ts`, and both zpacks (new `settings` widget).
  - Backups: `zpack.before-settings-*.json`, `zebar-cpu-collector.before-settings.exe`.
  - Checked on screen: the gear opens Settings; choosing 5 min saved `settings.json` and switched the bar graphs live; unticking GPU hid the GPU card; unticking G: removed it from the storage card. Also checked bad JSON → 400 and a foreign Origin → 403. **Afterwards the settings were set back to the defaults** (1 min, everything shown).
- **CPU popup: top processes with Now / Avg / Peak over the last minute.** The collector got a new `native/processes.rs` (a single `NtQuerySystemInformation` call per second, which also replaced sysinfo's per-process memory refresh), a `/cpu/processes` endpoint and the `Wdk_System_SystemInformation` feature. `src/panel.tsx` got a sortable table. The popup is now 780 px tall in `activity-panel.ts` and both zpacks. The old collector exe is backed up as `zebar-cpu-collector.before-cpu-processes.exe`.
  - Checked: the memory list matches `Get-Process` exactly, the CPU table renders and re-sorts on screen, and the toggle still works. The collector costs 0.7% of one core.
  - **Testing note:** while driving the desktop with computer-use, popups sometimes closed between steps because the tool changed window focus, and the popups close on blur. A focus log confirmed that a real click inside a popup keeps focus. Don't mistake this for a widget bug.
  - **To deploy a new collector exe**, stop it and copy in a retry loop. The bar restarts it within a few hundred ms, so a single stop-then-copy can fail with "file in use".
- **Replaced the loose drive indicators with a storage card and popup**, matching CPU/RAM/GPU. The card is a mini bar chart with no label; clicking it toggles the new `storage-activity` popup, where clicking a row opens the drive in Explorer. Files: `src/drives.tsx` (now `useDrives`, `DriveBars`, helpers), new `src/storage-panel.tsx` and `storage.html`, `bar.tsx`, `activity-panel.ts` (`panelHeights`), `widget.css`, `vite.config.ts`, and both `zpack.json` files (new widget with the Explorer privilege). The installed zpack was backed up as `zpack.before-storage-*.json`.
  - First deploy bug: the popup rendered blank. The "Fullest" cell read a drive before the provider had sent any data, the render threw, and the transparent window showed nothing. It's guarded with `<Show>` now. **Lesson: in popups, guard every value that comes from a provider until its first output arrives.**
  - Checked on screen: the card renders, the popup opens and closes from the card, and clicking a row opens Explorer. Zebar also restarted cleanly while that Explorer window was open.
- Added **drive free-space indicators** to the bar (`src/drives.tsx`, used in `bar.tsx`, styles in `widget.css`), left of RAM. They use Zebar's `disk` provider, refreshed every 60 s. Checked on screen: all six drives show and the values match Windows. The hover tooltip appears, but its text couldn't be read in a screenshot.
- **Fixed workspace clicks on the bar.** `bar.tsx` sent `focus --workspace "12"` (quoted with `JSON.stringify`), and GlazeWM treated the quotes as part of the name. Names are now passed bare, and the binding-mode button got the same fix. Checked: clicking 19 switched to it.
- **Fixed the grey bar after Zebar restarts.** The collector inherited Zebar's port-6124 socket and kept it after Zebar exited. The collector now relaunches itself with `--detached` and no inherited handles (`relaunch_detached` in `native/main.rs`, new `Win32_System_Threading` feature). Checked: killing Zebar while the collector kept running freed the port, and the restarted Zebar served normally.
- Added a **GPU widget**: a bar sparkline and a `gpu-activity` detail panel, plus GPU sampling in the collector (`native/gpu.rs`, new `windows` crate dependency, `/gpu/processes` endpoint, `--probe` flag). New files: `src/gpu-panel.tsx`, `gpu.html`. Changed: `metrics-feed.ts`, `history-graph.tsx` (`gpu` tone, `.sparkline` class), `bar.tsx`, `widget.css`, `vite.config.ts`, and both `zpack.json` files.
- **Bar buttons now toggle their panels**: a second click closes the panel instead of reopening it. Fixed in `activity-panel.ts` with a BroadcastChannel and a check on pointerdown.
- Checked on screen: GPU, CPU and RAM buttons open on the first click and close on the second; switching between panels works; the GPU panel fits at 700px. The pre-GPU install was backed up to `backup-before-gpu-20261127-124849\`. The older `dist.before-full-redeploy-*` folder was deleted at the same time.
- Redeployed the full widget build. Before this, the installed `panel.html`/`ram.html` (CPU/RAM pop-ups) still loaded the 11:37 build and old assets were piling up. The old folder is kept as `.glzr\zebar\cpu-activity\dist.before-full-redeploy-20261127-123759\`. The installed `dist\` now matches `zebar-cpu\dist\` exactly. The collector exe was already up to date.
- Wrote this file plus `AGENTS.md` / `CLAUDE.md`, so new sessions load this context.
- Zebar: shortened input-language labels (`EN`), reordered the right side of the bar, and added app icons to the workspace buttons (Nerd Font subset).
- Zebar: built the `cpu-activity` pack (bar + CPU panel + RAM panel + Rust collector) and made it the startup widget instead of the starter pack.
- GlazeWM: changed to nine workspaces per monitor (`11`–`19` / `21`–`29`), dropped the `0` key.
- GlazeWM: added the monitor-aware workspace groups, `workspace.ps1`, `ctrl+alt+1..9`, `ctrl+alt+r` sync, Initialize at startup, and per-monitor workspace cycling.

### 2026-11-22
- Installed GlazeWM 3.10.1 with a fresh default config and archived the old YASB-era config.

## How to update this file

- When you finish a change, add a dated entry at the top of the **Change log**, and update
  the matching section above so it describes the current state, not only the history.
- Record decisions and their reasons, gotchas, and anything left unfinished under **Open items**.
- Don't paste whole config files here. Point to the path and describe what is special about it.
