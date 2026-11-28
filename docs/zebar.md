# Zebar: the `cpu-activity` widget pack

Read this when you work on the bar or any popup (CPU, RAM, GPU, storage, clock, settings).
If the change also touches the Rust collector (`zebar-cpu/native/`), read
[`zebar-collector.md`](zebar-collector.md) too. Ideas and bugs are in [`backlog.md`](backlog.md).

Source: `C:\codex\glazewm\zebar-cpu\` (Solid + TypeScript + Vite). Installed: `%USERPROFILE%\.glzr\zebar\cpu-activity\`.

`%USERPROFILE%\.glzr\zebar\settings.json` startup changed from `glzr-io.starter` / `with-glazewm` to
pack `cpu-activity`, widget `bar`, preset `default` (backup: `settings.before-cpu-activity-20261127-111335.json`).

## Widgets (`zpack.json`)

| Widget | HTML | Window | Purpose |
|---|---|---|---|
| `bar` | `dist/bar.html` | 100% × 38px by default, top-left, all monitors, `zOrder: normal` | The status bar |
| `cpu-activity` | `dist/panel.html` | 576 × 780, top-right, `top_most`, focused | CPU details pop-up |
| `ram-activity` | `dist/ram.html` | 576 × 620, top-right, `top_most`, focused | RAM details pop-up |
| `gpu-activity` | `dist/gpu.html` | 576 × 700, top-right, `top_most`, focused | GPU details pop-up |
| `settings` | `dist/settings.html` | 576 × 660, top-right, `top_most`, focused | Settings pop-up, opened from the gear in any popup |
| `storage-activity` | `dist/storage.html` | 576 × 470, top-right, `top_most`, focused | Storage details pop-up; may launch `explorer.exe` with a drive root |
| `network-activity` | `dist/network.html` | 576 × 470, top-right, `top_most`, focused | Network rates and adapter history |
| `clock` | `dist/clock.html` | 384 × 492, top-right, `top_most`, focused | Month calendar and persistent timer |

The bar opens the panels with `startWidget` on the bar's own monitor
(`src/activity-panel.ts`, offset −12px and 6px below the current bar, size clamped to the monitor). The
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

**Lesson from the storage popup:** in popups, guard every value that comes from a provider
(with `<Show>`) until its first output arrives. A render that throws leaves the transparent window blank.

## Bar layout (`src/bar.tsx`)

Left: a brand mark (four dots), then the workspace buttons. Default order on the right:
paused indicator and binding-mode buttons (shown only when active) → battery →
**storage card** → **RAM** (sparkline + % ) → **CPU** (heatmap + %) → **GPU** (green sparkline + %) → tiling-direction toggle (↔/↕) →
weather (°C) → input language → **network** (connection icon and live rates) → clock (default `yyyy-MM-dd HH:mm`).

- **Settings → Bar layout** lists items in their left-to-right order. Move Storage, RAM, CPU, GPU,
  tiling direction, Weather, Input language, or Network with the arrows. Visibility checkboxes keep
  each hidden item's place in the order. The clock stays at the far right.
- **Weather and Input language** use the bar's chamfered frame. Weather shows a thermometer and the
  temperature in °C. The Input language row in **Settings → Bar layout** offers **Code only**,
  **Code and flag** (the default), or **Flag only**. Flag only uses a slightly larger flag.
  The flag lookup uses the keyboard layout's explicit region or the locale's default region for
  a bare language code. The flags are bundled locally with their MIT license.
  [Flag Icons](https://github.com/lipis/flag-icons/tree/v7.3.2)
- Clicking a workspace runs `focus --workspace <name>`. Clicking the tiling toggle runs `toggle-tiling-direction`.
- **GlazeWM command strings: never quote arguments** (see PROJECT_STATE). The bar used `JSON.stringify` here until 2026-11-27, which broke workspace clicks and the binding-mode button.
- The bar theme lives in `src/bar.css` and `src/bar-icon.tsx`: chamfered frames, consistent outline
  icons, and subtle per-widget tints. RAM is teal, GPU green, CPU blue, Storage ice blue, and Network
  violet with distinct download and upload arrows. On narrow screens the middle cards scroll
  horizontally. Workspace controls have their own scroll area and the clock stays visible.
- **Height:** Settings → Bar height sizes the native window to 34–52 logical pixels
  (38 by default). Each bar keeps its monitor's width and sizes its controls and canvases to fit.
  GlazeWM's top gap controls space reserved for tiling and is set to 54px in this install.
  [Zebar widget window permissions](https://github.com/glzr-io/zebar/blob/v3.3.1/packages/desktop/capabilities/widget.json)

## Workspace app icons

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

## CPU, RAM and GPU (front end)

Data comes from the collector; see [`zebar-collector.md`](zebar-collector.md) for endpoints and sampling.

- **Feed** (`src/metrics-feed.ts`): polls once a second with POST, because Zebar's
  service worker caches cross-origin GETs. If the poll fails, the bar spawns the collector
  through `shellSpawn`, at most once every 6 s. Status is `starting` → `live` → `unavailable`, and the UI shows `—` when not live.
- **Bar**: RAM has a compact teal sparkline. The CPU heatmap uses spaced square cells from cold blue
  to hot red. Adjacent logical processors are grouped to fit the height. Each cell keeps the highest
  load in its group and time bucket. The GPU button is a green sparkline of the
  selected adapter's load plus %; its tooltip shows the GPU name and temperature. It is hidden if the collector reports no GPUs.
- **CPU panel** (`src/panel.tsx`): total, busiest core, an all-core heatmap with a colour
  key, a graph of the selected core over the history window with its peak, a clickable grid of logical processors,
  and **Top processes · last N min** (N = the history setting): Process, PID, **Now**, **Avg** and **Peak**, top 8. It's
  sorted by Avg by default; click a column header to re-sort. It refreshes every 2 s while open. The
  point is that a process that spiked for 30 s and then stopped still ranks high on Avg and Peak.
- **RAM panel** (`src/ram-panel.tsx`): usage %, a history graph, used/available/total
  in GiB/MiB, and the top 10 processes by memory.
- **GPU panel** (`src/gpu-panel.tsx`): adapter tabs (when there's more than one GPU), then load, dedicated memory (used / total with a meter), shared memory and temperature. Below that, a graph for the selected engine (Overall by default) with its peak, a clickable engine row, and the top 8 processes (GPU % and dedicated memory).
- **History length:** widgets ask for just their window with `/history?…&limit=<seconds>` and `/cpu/processes?window=<seconds>`. When the
  window grows, the feed refetches the whole window (`session = 0`). The heatmap merges long windows into
  at most 120 columns, keeping each column's **peak**, so short spikes stay visible.

## Storage card and popup (`src/drives.tsx`, `src/storage-panel.tsx`)

- **Bar card**: one framed card, like RAM/CPU/GPU, with a circular capacity meter around each
  drive letter. The arc shows **used** capacity and the contrasting track shows the free portion.
  Each drive can show **Live rates** (stacked R/W with fixed-width values and units beside the ring),
  **Activity marks** (cyan read above pink write beside the ring, bright with activity), or
  **None** (capacity only, the default).
  The hover tooltip lists free space for each drive and explains the marks when shown. Clicking toggles the popup.
- **Storage popup** (`storage-activity`, 576 × 470): a compact summary strip and a two-column grid
  of all visible drives, with navy panels and chamfered corners. Each tile shows capacity, live
  speeds, and its own cyan read and pink write history chart. The charts use the same history
  window and throughput scale, shown once above the grid. One drive spans both columns and gets
  a taller chart. Six drives fit at the default size. The grid scrolls for more drives or shorter
  windows, and stacks in one column at narrow widths. **Clicking a tile opens that drive in File
  Explorer** and closes the popup.
- **History of this design:** the first version (2026-11-27) put six loose `C | 947 GB` items on the bar.
  It looked out of place next to the framed cards, the thin bar read like a text cursor, and it was
  the widest group on the bar. It was replaced with the card and popup above.
- **Units:** capacity uses binary GB/TB labels to match Explorer. Below 10 GB one decimal, below
  1 TB whole GB, from 1 TB up TB with one decimal (`formatDriveSize`). Throughput uses decimal
  B/s through TB/s, with up to three significant digits, `0 B/s` for idle and `—` for unavailable.
- **Colours:** the Storage card's icon, frame and normal arcs use an ice-blue accent over a navy
  gradient, with slate-blue tracks. Read and write labels stay cyan and pink at idle. Bar rings
  and popup capacity meters turn amber at ≥ 85% used and red at ≥ 95% used.
- **Data:** both views get capacity from Zebar's built-in `disk` provider every 60 s and read/write
  rates from the one-second collector feed. The popup requests the selected history window using
  `view=drives` and plots gaps when a reading is missing.
- **Opening Explorer:** the popup calls `shellSpawn('C:\Windows\explorer.exe', ['D:\'])`. The
  widget's zpack privilege allows only that program with `argsRegex` `^[A-Z]:\\$` (JSON: `"^[A-Z]:\\\\$"`) (one drive
  root). Checked: the `explorer.exe` that Zebar starts hands off to Windows' COM Explorer
  factory (`explorer.exe /factory,… -Embedding`, parent svchost) and exits. So unlike the
  collector, it does **not** keep a copy of Zebar's port, and Zebar restarted cleanly with a
  drive window open.
- This PC: C 3.6 TB, D 1.8 TB, E 3.6 TB, F 16 TB (external), G 7.3 TB (98% used, shows red), H 7.3 TB.

## Network (`src/network.tsx`, `src/network-panel.tsx`)

- The Network card defaults to the last position before the clock. It shows a Wi-Fi, Ethernet, or
  disconnected icon plus fixed-width download and upload rates in bits per second. The default physical
  interface sets the icon when known, with Ethernet taking
  precedence when the default route does not match an active physical adapter. The Wi-Fi SSID is in the tooltip.
- The popup shows aggregate rates and one tile per active physical adapter. Each tile shows the local IP,
  the Wi-Fi SSID when available, current rates and a history graph. Download is sky blue and upload is
  amber. The popup shares its frame, chart rendering and history setting with Storage.
- The collector samples traffic and maintains the history. Zebar's existing `network` provider supplies
  the default interface, local IP addresses and the SSID. Network is enabled by default in Settings.

## Clock, calendar and timer (`src/clock-panel.tsx`, `src/clock.ts`, `src/timer.ts`; collector side in `native/timer.rs`)

- The far-right clock button opens on the bar's monitor with Calendar selected. The popup shows local time with seconds, a locale-aware month grid with Today and month navigation, a Timer tab, and a link to shared Settings.
- The default ISO date is centered above the time on the bar. Timer progress sits under the separate countdown.
- Timer offers immediate 5, 10 and 15 minute presets and a custom duration in hours/minutes (up to 999 h 59 min). There is one active timer across all bars. The digits count down while the progress line fills from left to right as time elapses. Cancel from the Timer tab.
- At expiry, the clock briefly pulses when motion is enabled and the elapsed line turns amber. The collector plays a Windows chime about every four seconds until dismissal. Turn off **Repeat timer chime until dismissed** in shared Settings under **Clock** for a single chime. Click a clock button or Dismiss timer in the Timer tab to silence and clear it.
- `/history` carries timer state to each bar on its existing feed. The open popup reads `/timer` once and listens for actions over a BroadcastChannel. The display uses Zebar's date provider for ticks and does not send per-second timer requests. Persistence is described in the collector doc.

## Settings (`src/settings.ts`, `src/settings-panel.tsx`; storage in `native/settings.rs`)

- **Why our own popup:** Zebar 3.3.1's pack editor (Settings → Custom packs → pack) can't hold
  custom options. A widget's config is only placement presets, privileges and caching (`WidgetConfig` in
  `node_modules/zebar/dist/index.d.ts`).
- **Opening it:** the gear button in the shared popup header (`src/panel-header.tsx`). The popup you came from closes on blur. Settings closes on Esc, on blur or with ×.
- **Options** (all detected at runtime, nothing specific to this PC):

  | Setting | Values | Default | Effect |
  |---|---|---|---|
  | `clockFormat` | Luxon format preset or custom string | `yyyy-MM-dd HH:mm` | Date and time on the bar. Settings includes a live preview and a syntax tooltip |
  | `repeatTimerChime` | boolean | `true` | Repeat the expiry chime until dismissal. Off plays it once |
  | `historyMinutes` | 1, 5, 10, 15 | 1 | Window for every graph, heatmap, "N ago" label and the CPU process Avg/Peak |
  | `barHeight` | 34–52 logical pixels | 38 | Height of the Zebar bar on every monitor |
  | `bar.storage/ram/cpu/gpu/network/weather/language` | booleans | all `true` | Which cards and indicators the bar shows. At least one of the five activity cards stays enabled for access to its popup |
  | `barOrder` | ordered item IDs | Storage, RAM, CPU, GPU, tiling direction, Weather, Input language, Network | Order of the configurable items on every bar |
  | `languageDisplay` | `code`, `codeAndFlag`, `flag` | `codeAndFlag` | How the Input language card displays the active keyboard layout |
  | `drives` | letters or `null` | `null` = all | Drives in the storage card **and** popup. Selecting every drive saves `null`, so drives added later show up |
  | `driveActivity` | drive letter → `marks` or `rates` | empty | Bar activity display per drive. Missing letters are **None**. Hiding a drive keeps its activity preference |
  | `gpuAdapter` | adapter name or `null` | `null` = most video memory | GPU the bar's GPU card follows. The GPU popup also opens on that tab |

- **Live updates:** every `/history` response carries `settingsVersion`. The bar passes it to
  `useSettings().sync`, which refetches when it changes, so changes apply within about 1 s. Popups fetch settings
  once when they open.

## Build and deploy

The repo has no deploy script. These steps were worked out from the files present:

```powershell
cd C:\codex\glazewm\zebar-cpu
npm install --cache .npm-cache      # once; earlier runs used this cache flag
npm run build                       # tsc --noEmit && vite build -> dist/
cargo build --release --offline     # -> target\release\zebar-cpu-collector.exe (crates are in the local cache)
```

Then **replace** the installed `dist\` as a whole: move the old one aside as
`dist.before-<change>-<timestamp>` and copy the new `dist\` in, so all widget HTML files and
their assets stay in step. Copy `target\release\zebar-cpu-collector.exe` only if it changed
(see the collector doc: stop it and copy in a retry loop).
Keep the installed `zpack.json` (see the absolute-path note above); when you add a
widget, add it to both the installed and the source `zpack.json`. The panels load fresh each time they open. The bar
and any `zpack.json` change need a Zebar restart:

```powershell
Stop-Process -Name zebar -Force; Start-Sleep -Seconds 3; Start-Process zebar
```

**If the bar is ever a grey strip after a restart**, Zebar couldn't start its widget server
on `127.0.0.1:6124`. Check which PID owns it with
`netstat -ano | findstr "LISTENING" | findstr "127.0.0.1:6124"`. The known cause (the collector
inheriting the socket) was fixed on 2026-11-27, and a restart with the collector still running was
checked afterwards. If it comes back, look for another child process of Zebar holding the port.

**Testing note:** while driving the desktop with computer-use, popups sometimes close between steps because the tool
changes window focus, and the popups close on blur. A focus log confirmed that a real click inside a popup keeps focus.
Don't mistake this for a widget bug. The native desktop tool does not expose the Zebar bar windows.

## Known issues and unverified

- Changing bar height does not change GlazeWM's top gap. Adjust the gap separately for a different tiling margin.
- On narrow screens, Network scrolls with the other configurable cards.
- Input language falls back to its code in either flag mode when the locale has no matching bundled flag.
- Zebar provides the SSID for the default Wi-Fi interface. Other active Wi-Fi adapters still show
  traffic history and any local addresses the provider supplies.
- Browser checks confirmed six drive rings and one Live rates readout fit at 1280 × 34 px in the test layout.
  All six Live rates readouts fit at 1920 × 38 px. Check how many simultaneous readouts fit on narrow
  native bars with several workspaces.
- Check native clock popup placement and dismissal on both monitors, and listen for the chime on the desktop.
- **Not yet portable:** the installed `zpack.json` uses the absolute collector path `C:\Users\mihae\...`. Zebar matches the program as an exact string, so a relative path can't work; the planned install script writes the real path (see the backlog's install script item).
- The panel toggle has only been tested with the bar on the primary monitor. The other monitor's bar should behave the same way (each bar tracks its own panels), but that hasn't been checked.

## Backups

- `.glzr\zebar\cpu-activity\dist.before-language-display-20261128-134437` and
  `zebar-cpu-collector.exe.before-language-display-20261128-134437` hold the installed pack before
  adding the Input language choices.
- `.glzr\zebar\cpu-activity\dist.before-keyboard-flags-20261128-134048` holds the installed
  web assets before the keyboard locale fix.
- `.glzr\zebar\cpu-activity\dist.before-status-cards-20261128-133424` and
  `zebar-cpu-collector.exe.before-status-cards-20261128-133424` hold the installed pack before the
  Network ordering and framed indicators update.
- `.glzr\zebar\cpu-activity\dist.before-bar-layout-20261128-132542` and
  `zebar-cpu-collector.exe.before-bar-layout-20261128-132542` hold the installed pack before Bar layout.
- `.glzr\zebar\cpu-activity\dist.before-storage-colors-20261128-131217`: installed assets before the Storage palette change.
- `.glzr\zebar\cpu-activity\dist.before-drive-rings-20261128-130631`: installed assets before the circular capacity meters.
- `.glzr\zebar\cpu-activity\dist.before-clock-center-20261128-130008`: installed assets before centering the clock.
- `.glzr\zebar\cpu-activity\dist.before-bar-theme-20261128-123340`,
  `zebar-cpu-collector.exe.before-bar-theme-20261128-123340`, and
  `zpack.json.before-bar-theme-20261128-123340`: installed pack before the bar refresh.
- `settings.json.before-bar-height-test-20261128-123425`: before testing native resizing.
- `.glzr\zebar\cpu-activity\dist.before-network-position-20261127-212520`: installed assets before moving Network beside the clock.
- `.glzr\zebar\cpu-activity\dist.before-network-widget-20261127-212106`,
  `zebar-cpu-collector.exe.before-network-widget-20261127-212106`, and
  `zpack.json.before-network-widget-20261127-212106`: installed assets, collector, and manifest before Network.
- `.glzr\zebar\cpu-activity\dist.before-disk-history-20261127-210028` and
  `zebar-cpu-collector.exe.before-disk-history-20261127-210028`: assets and collector before disk history.
- `.glzr\zebar\cpu-activity\dist.before-drive-meter-contrast-20261127-203606`: installed assets before the bar meter contrast change.
- `.glzr\zebar\cpu-activity\dist.before-disk-activity-20261127-202906` and
  `zebar-cpu-collector.exe.before-disk-activity-20261127-202906`: installed assets and collector before disk activity.
- `.glzr\zebar\settings.before-cpu-activity-20261127-111335.json`: starter-pack Zebar settings
- `.glzr\zebar\cpu-activity\backup-before-gpu-20261127-124849\`: installed `dist\`, `zpack.json` and collector exe from before the GPU widget and toggle
- `.glzr\zebar\cpu-activity\bar.before-workspace-icons-20261127-111836.html`, `bar.before-widget-reorder-20261127-114545.html`, `bar.before-short-language-labels-20261127-120006.html`
- `zpack.before-storage-*.json`, `zpack.before-settings-*.json`, `zebar-cpu-collector.before-cpu-processes.exe`, `zebar-cpu-collector.before-settings.exe` (in the pack folder)
- `.glzr\zebar\cpu-activity\dist.before-clock-20261127-154825\`, `zpack.json.before-clock-20261127-154825`, `zebar-cpu-collector.exe.before-clock-20261127-154825`: installed pack before the clock and timer
- `.glzr\zebar\cpu-activity\dist.before-timer-repeat-20261127-155842\`, `zebar-cpu-collector.exe.before-timer-repeat-20261127-155842`: installed assets and collector before elapsed progress and the repeating chime

## Change log

Newest first. Covers the whole pack, front end and collector.

### 2026-11-28
- **Added Input language display choices** to the Bar layout settings in `src/settings-panel.tsx`,
  `src/settings.ts` and `src/widget.css`. Updated the card in `src/bar.tsx` and `src/bar.css`,
  with persistence in `native/settings.rs`. Verified all three selections across two browser
  bars, the fallback for layouts without an available flag, and reopening at the saved mode.
  Deployed the bar and collector.
- **Fixed missing Windows keyboard flags** in `src/bar.tsx` and `src/keyboard-flag.ts`.
  Stripped the null terminator from the Windows keyboard locale and added default-region lookup
  for language-only codes. Browser checks covered explicit and inferred regions. Deployed and
  verified the flag appeared beneath `HE` on the live Zebar bar.
- **Made Network movable and framed Weather and Input language.** Updated `src/bar.tsx`,
  `src/bar.css`, `src/bar-icon.tsx`, the layout settings, and the collector defaults. Added
  `src/keyboard-flag.ts` and bundled region flags. Verified both indicators between Network and
  the clock in two browser bars, Network popup access after moving, and live keyboard flag
  updates with a language-only fallback. Deployed the bar and collector.
- **Added live bar ordering and visibility** in `src/bar.tsx`, `src/settings.ts`,
  `src/settings-panel.tsx`, and `src/widget.css`, with persistence in `native/settings.rs`.
  Verified reordering, hiding and restoring indicators, and opening the reordered GPU card
  in the browser with two bars. Deployed the bar and collector and verified the installed
  collector reads the new settings with the existing layout preferences.
- **Refined the Storage bar palette** in `src/bar.css` with brighter ice-blue arcs, a matching icon
  and frame, a navy gradient, and colored read/write labels at idle. Verified capacity warning colors
  and six simultaneous Live rates readouts in the browser, then deployed the rebuilt assets.
- **Added circular drive meters** in `src/drives.tsx` and `src/bar.css`. Centered each drive letter
  in its ring and placed the per-drive activity display beside it. Verified all three activity
  modes in the browser at bar heights of 34, 38, and 50 px. Deployed the rebuilt assets.
- **Centered the time under the date** in `src/bar.css`. Verified text alignment in the browser
  and deployed the rebuilt assets.
- **Restyled the bar and added a height setting.** Added `src/bar.css`, `bar-icon.tsx` and
  `bar-window.ts`. Updated `bar.tsx`, `drives.tsx`, `heatmap.tsx`, `activity-panel.ts`,
  `clock.css`, `widget.css`, Settings and both manifests. The bar now uses horizontal drive
  meters and thermal CPU cells, with timer progress beneath the countdown. Browser checks covered
  narrow and tall layouts, 64 logical processors, clock and timer, and both drive activity modes.
  Deployed and verified both live bars resize from 38 to 50 logical pixels and back.

### 2026-11-27
- **Moved Network immediately left of the clock** in `src/bar.tsx`. Verified the position and deployed the rebuilt assets.
- **Added the Network widget.** `src/network.tsx` and `src/bar.tsx` show the link icon and compact
  rates. `network.html`, `src/network-panel.tsx`, `src/network.css`, `src/activity-panel.ts`,
  `vite.config.ts` and both widget manifests add the popup. Moved the Storage frame styles and
  throughput graph into shared components, added the Settings toggle, and used the collector's
  new network history view in `src/metrics-feed.ts`. Deployed the widget and verified the live
  history feed. Browser checks covered Wi-Fi, Ethernet, disconnected and unavailable states,
  fixed-width rates, SSID, IP, history graphs, Settings, and the existing Storage layout.
- **Added disk activity history to the Storage popup.** `src/storage-panel.tsx`, `src/storage.css` and
  `src/drive-history-graph.tsx` render the adaptive grid. `src/history-graph.tsx` shares canvas setup,
  and `src/metrics-feed.ts` requests the disk history view of the existing feed. The [collector](zebar-collector.md)
  retains the one-second samples. Browser checks covered one full-width drive, six drives without scrolling,
  eight drives with a fixed header and scrolling, narrow windows, both traces, settings changes, and Explorer.
  Deployed the pack and collector and verified live disk history for all six drives.
- **Brightened the background track on the bar's drive meters** in `src/widget.css` so partially filled
  drives show the same full height as a nearly full drive. Verified equal 20 px tracks and red fill on
  the nearly full meter in the browser. Deployed the rebuilt assets and verified the Zebar restart.
- **Added per-drive read/write indicators.** Settings → Drives selects Live rates, Activity marks, or None.
  The Storage popup shows both speeds. Changed `src/drives.tsx`, `src/bar.tsx`, `src/storage-panel.tsx`,
  `src/settings.ts`, `src/settings-panel.tsx`, `src/metrics-feed.ts` and `src/widget.css`.
  Deployed the pack and [collector](zebar-collector.md). Headless browser checks covered mixed modes, saved settings,
  stable readout width as units change, idle and unavailable values, six-drive and narrow popup layouts, and Explorer launch.
  The installed collector reported all six drive letters after the restart. Disk history graphs remain a separate idea.
- **Changed timer progress to fill left to right and added a repeat-chime setting.** Updated `src/timer.ts`, `src/bar.tsx`, `src/clock-panel.tsx`, and `src/clock.css` for elapsed progress. Added the setting to `src/clock-settings.tsx`, `src/settings-panel.tsx`, `src/settings.ts`, and `native/settings.rs`. `native/timer.rs` and `native/main.rs` use the existing collector tick for repeats and stop on dismissal or when repeat is turned off. Deployed the rebuilt pack and collector.
  - Browser checks covered elapsed fill and the saved setting. An isolated collector verified repeat scheduling, dismissal, one-shot mode, and restoration across restart with sound commands recorded in a log. Audible playback on the desktop remains open.
- **Added and deployed the clock, calendar, and timer.** `clock.html`, `src/clock-panel.tsx`, `src/clock-settings.tsx`, `src/clock.ts`, `src/timer.ts` and `src/clock.css` implement the UI. Integrated it into the bar, shared Settings, `activity-panel.ts`, `metrics-feed.ts`, `vite.config.ts` and both widget manifests. `native/timer.rs` owns timer persistence and the Windows chime. `native/main.rs`, `native/settings.rs` and `native/state_file.rs` wire it into the existing collector and settings file.
  - Verified the UI and collector builds and live settings and metrics after restarting Zebar. Browser-tested the rendered calendar, month navigation, timer presets, countdown and overdue dismissal, popup links, format preview, and syntax tooltip. An isolated collector test covered settings migration, 25-hour timer restore, expiry during downtime, one alarm across restarts, and durable dismissal. Native window placement and audible playback remain open.
- **Added a Settings popup**: history window, bar items, drives, GPU for the bar.
  - New files: `native/settings.rs`, `src/settings.ts`, `src/settings-panel.tsx`, `src/panel-header.tsx`, `settings.html`.
  - Changed: `main.rs` (`/settings`, `/settings/save`, `limit` and `window` parameters, 15 min history), `processes.rs` (`cpu_summary(window)`), `metrics-feed.ts` (history-length accessor, `settingsVersion`), `heatmap.tsx` and `history-graph.tsx` (`length` prop, peak-merged columns), `bar.tsx`, all four popups, `activity-panel.ts` (exported `openActivityPanel`, `settings: 660`), `widget.css`, `vite.config.ts`, and both zpacks (new `settings` widget).
  - Checked on screen: the gear opens Settings; choosing 5 min saved `settings.json` and switched the bar graphs live; unticking GPU hid the GPU card; unticking G: removed it from the storage card. Also checked bad JSON → 400 and a foreign Origin → 403. **Afterwards the settings were set back to the defaults** (1 min, everything shown).
- **CPU popup: top processes with Now / Avg / Peak.** The collector got a new `native/processes.rs` (a single `NtQuerySystemInformation` call per second, which also replaced sysinfo's per-process memory refresh), a `/cpu/processes` endpoint and the `Wdk_System_SystemInformation` feature. `src/panel.tsx` got a sortable table. The popup is now 780 px tall in `activity-panel.ts` and both zpacks.
  - Checked: the memory list matches `Get-Process` exactly, the CPU table renders and re-sorts on screen, and the toggle still works. The collector costs 0.7% of one core.
- **Replaced the loose drive indicators with a storage card and popup**, matching CPU/RAM/GPU. Files: `src/drives.tsx` (now `useDrives`, `DriveBars`, helpers), new `src/storage-panel.tsx` and `storage.html`, `bar.tsx`, `activity-panel.ts` (`panelHeights`), `widget.css`, `vite.config.ts`, and both `zpack.json` files (new widget with the Explorer privilege).
  - First deploy bug: the popup rendered blank because the "Fullest" cell read a drive before the provider had sent any data. Guarded with `<Show>`.
  - Checked on screen: the card renders, the popup opens and closes from the card, and clicking a row opens Explorer. Zebar also restarted cleanly while that Explorer window was open.
- Added **drive free-space indicators** to the bar (`src/drives.tsx`), left of RAM, using Zebar's `disk` provider refreshed every 60 s. (Replaced by the storage card the same day.)
- **Fixed workspace clicks on the bar.** `bar.tsx` sent `focus --workspace "12"` (quoted with `JSON.stringify`). Names are now passed bare, and the binding-mode button got the same fix. Checked: clicking 19 switched to it.
- **Fixed the grey bar after Zebar restarts.** The collector inherited Zebar's port-6124 socket and kept it after Zebar exited. The collector now relaunches itself with `--detached` and no inherited handles (`relaunch_detached` in `native/main.rs`, new `Win32_System_Threading` feature). Checked: killing Zebar while the collector kept running freed the port, and the restarted Zebar served normally.
- Added a **GPU widget**: a bar sparkline and a `gpu-activity` detail panel, plus GPU sampling in the collector (`native/gpu.rs`, new `windows` crate dependency, `/gpu/processes` endpoint, `--probe` flag). New files: `src/gpu-panel.tsx`, `gpu.html`. Changed: `metrics-feed.ts`, `history-graph.tsx` (`gpu` tone, `.sparkline` class), `bar.tsx`, `widget.css`, `vite.config.ts`, and both `zpack.json` files.
- **Bar buttons now toggle their panels**: a second click closes the panel instead of reopening it (`activity-panel.ts`, BroadcastChannel and a check on pointerdown). Checked on screen for GPU, CPU and RAM, including switching between panels.
- Redeployed the full widget build. Before this, the installed `panel.html`/`ram.html` still loaded the 11:37 build and old assets were piling up. The installed `dist\` now matched `zebar-cpu\dist\` exactly. Its `dist.before-full-redeploy-*` backup was deleted later that day, when the GPU widget was deployed.
- Shortened input-language labels (`EN`), reordered the right side of the bar, and added app icons to the workspace buttons (Nerd Font subset).
- Built the `cpu-activity` pack (bar + CPU panel + RAM panel + Rust collector) and made it the startup widget instead of the starter pack.
