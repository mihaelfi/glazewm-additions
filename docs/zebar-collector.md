# Zebar collector (`zebar-cpu/native/`)

Read this when you change sampling, endpoints, settings storage or the timer back end.
Front-end widgets are described in [`zebar.md`](zebar.md); changes to the collector are logged there too.

A windowless Rust HTTP server, built as `zebar-cpu-collector.exe`, shared by every bar. All sampling
happens here, not in the webviews. Files: `main.rs` (server, sampling loop, endpoints), `processes.rs`,
`gpu.rs`, `drive_io.rs`, `network.rs`, `pdh.rs`, `settings.rs`, `timer.rs`, `state_file.rs`.

## Server

- Listens on `127.0.0.1:6128`. Uses `sysinfo` for total CPU, per-core CPU and RAM, and the `windows` crate for processes and the GPU.
- Only answers requests with `Origin: http://127.0.0.1:6124` (Zebar) and the matching `Host`; anything else gets 403.
- Exits after 20 s with no clients. If the port is already in use it exits quietly, so several bars share one collector.
- **Relaunches itself on start.** Zebar's `shellSpawn` creates child processes with handle inheritance on, so the collector
  used to inherit Zebar's own server socket (`127.0.0.1:6124`). That socket outlived Zebar, and a restarted Zebar couldn't
  serve its widgets: the bar showed as a grey strip with a scrollbar. Now, when started with no arguments, the collector
  starts a copy of itself with `--detached` (via `CreateProcessW`, inheritance off) and exits. Only the `--detached` copy
  serves. `--detached` and `--probe` are the only accepted arguments.
- **Debugging:** `zebar-cpu-collector.exe --probe > probe.json` prints the adapters, one sample and the process list without needing the bar.
- **Cost:** the whole collector uses about 0.2% of one core and 18 MB without process sampling, about 0.7% of one core with it.

## Endpoints (all POST, because Zebar's service worker caches cross-origin GETs)

- `/history?session=&after=&limit=<seconds>` → `{ session, settingsVersion, timer, gpus, driveRates, network, samples }`:
  new historical samples since `after` and the latest rate snapshot by drive letter.
  With `view=drives`, each sample contains only `{ sequence, driveRates }`. Other callers receive
  the regular CPU, memory, and GPU sample shape, or `{ sequence, rates }` for `view=network`.
- `/memory/processes` → top 10 processes by working set, from the latest 1 s sample.
- `/cpu/processes?window=<seconds>` → `{ window, processes: [{ name, pid, current, average, peak }] }`: the top 40 by average.
  Values are % of **total** CPU (Task Manager style); `window` is the number of seconds covered.
- `/gpu/processes` → top 10 GPU processes per adapter, refreshed at most every 3 s.
- `/settings` → `{ version, settings }`; `/settings/save` (JSON body, ≤ 8 KB) → the same.
- `/timer`, `/timer/start?seconds=`, `/timer/clear?deadline=` (see Timer).

## Sampling

- Total and per-logical-CPU usage plus RAM (used/available/total) every second. The collector always keeps
  15 min (900 samples, and the same for per-process CPU); widgets ask for just their window.
- **Per-process sampling** (`processes.rs`): once a second, a single
  `NtQuerySystemInformation(SystemProcessInformation)` call (the one Task Manager uses) reads
  CPU time, working set and image name for every process. The struct is read at fixed
  x64 offsets (see the constants). The CPU share is the change in CPU time divided by wall time × logical CPUs.
  Each second stores the processes that used at least 0.05%. Processes are keyed by (PID, creation time), so a
  reused PID isn't merged. Names are kept while a process runs or is still in the window.
- **Why not sysinfo for processes:** sysinfo opens every process and calls `GetProcessTimes` /
  `GetProcessMemoryInfo` on each (about 360 processes here). That took the collector from 0.2% to
  1.3% of one core. The single call costs 0.7% of one core (about 0.03% of this 24-thread
  CPU), mostly Windows building the list.

### GPU (`gpu.rs`)

Uses the same sources as Task Manager, so it works for any vendor:

- **Adapters:** DXGI provides names and dedicated/shared memory totals. Software adapters and Microsoft Basic Render are skipped, and so are DXGI entries that have no performance counters (on this PC DXGI lists the RTX 4090 twice). They're sorted by dedicated memory, so adapter 0 is the discrete card. On this PC that's the RTX 4090, then the AMD Radeon iGPU.
- **Load:** the `\GPU Engine(*)\Utilization Percentage` counter is summed over processes for each engine. Adapter load is the busiest engine, as in Task Manager. Engines are grouped into 3D / Copy / Video Decode / Video Encode / Compute (`Compute*` and `Cuda`).
- **Memory:** `\GPU Adapter Memory(*)\Dedicated Usage` and `Shared Usage`.
- **Temperature:** `D3DKMTQueryAdapterInfo(KMTQAITYPE_ADAPTERPERFDATA)`, reported in tenths of a degree.
- **Per process:** the busiest engine for each PID plus `\GPU Process Memory(*)\Dedicated Usage`. Names come from a Toolhelp snapshot, which also names protected processes such as dwm.exe.
- **Gotcha:** counter instance names use **upper-case** hex LUIDs (`luid_0x00000000_0x000160AD`). PowerShell's `Get-Counter` shows them in lower case, which is misleading.
- **dwm.exe** reports about 17 GB of "dedicated" memory. That is the real counter value (Task Manager shows the same), not a bug.
- **Cost:** about 2 ms per sample.

### Disk throughput (`drive_io.rs`)

- Reads `LogicalDisk(*)` read/write bytes per second from Windows Performance Data Helper on the
  existing one-second tick. The GPU and disk samplers share the PDH query and wildcard-reading code
  in `pdh.rs`.
- Reports only drive-letter instances as `{ read, write }` in `driveRates`. A missing instance or
  `null` direction means the counter was unavailable. A zero means the counter measured no traffic.
- Keeps the last 15 minutes in the existing in-memory sample ring. The Storage popup requests
  the current history window, then receives updates by sequence. The bar still reads the latest
  snapshot from the top-level response. The frontend joins disk data to capacity by drive letter.

### Network throughput (`network.rs`)

- Reads interface octet counters once per collector tick and divides the delta by measured elapsed
  time. The sampler keeps active hardware Wi-Fi and Ethernet interfaces, skipping filters, tunnels,
  and other virtual adapters. A new link or counter reset makes the affected rate unavailable until
  the next good sample. [Windows interface counters](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/ns-netioapi-mib_if_row2)
- Rates are bytes per second, keyed by interface GUID. The `network` snapshot includes per-adapter
  rates and the total used by the bar. History goes in the same in-memory sample ring as disk I/O.

## Settings storage (`settings.rs`)

- The collector owns `settings.json` next to its exe (the pack folder). Missing = defaults.
- Saves are cleaned up (unknown history value → 1, invalid letters dropped, all bar items off → all on) and written
  atomically (temp file + rename, `state_file.rs`). Bad JSON → 400; a wrong Origin → 403, like every endpoint.
- Every `/history` response carries `settingsVersion` so bars refetch after a change.
- `driveActivity` maps drive letters to their bar mode. The default is empty. Saving removes invalid
  letters and capacity-only entries. A hidden drive keeps its saved mode.
- Older settings files get `bar.network: true` by default.

## Timer (`timer.rs`)

- Atomically writes the absolute Unix-millisecond deadline and duration to `timer.json` beside its executable. After
  restart it restores the active timer, including overdue alarms. The saved alarm timestamp prevents a single chime
  from playing twice. Repeating alarms resume if still overdue and repeat is enabled. Dismissal persists as `null`.
- Plays the Windows chime from the existing one-second tick (about every four seconds while repeating).
- `/timer/start` rejects a second timer, and `/timer/clear` checks the deadline so stale actions cannot clear a timer with a different deadline.

## Deploying a new exe

Stop `zebar-cpu-collector` (the exe is locked while it runs) and copy in a **retry loop**: the bar restarts it
within a few hundred ms, so a single stop-then-copy can fail with "file in use".

## Known issues and backups

- Disk history is in memory and starts filling after each collector restart.
- Volumes with no usable Windows logical disk counter show unavailable rates.
- Live collector backups are listed in [Zebar](zebar.md#backups).

## Change log

### 2026-11-27
- **Added network throughput history.** `network.rs` reads Windows interface counters, `main.rs`
  serves live rates and history, `settings.rs` migrates the bar visibility option, and `Cargo.toml`
  enables the existing Windows bindings for IP Helper. The isolated collector matched the active
  physical Windows adapter and verified its rates, totals, retained history, and older settings.
  The deployed collector returned live network history after Zebar restarted.
- **Retained disk activity history.** `main.rs` stores drive rates in the existing sample ring and
  serves disk-only samples on `/history?view=drives`. `drive_io.rs` returns an owned snapshot each tick.
  The isolated collector verified sample limits, incremental reads, and compatibility with the
  regular metrics feed. The deployed collector returned retained samples for all six local drives.
- **Added logical disk throughput.** `drive_io.rs` samples two counters, `pdh.rs` extracts the existing
  PDH code from `gpu.rs`, and `main.rs` carries current rates in `/history`. `settings.rs` persists
  and validates per-drive modes. The collector retains the latest rates. An isolated collector
  measured a test write and verified older settings and restart persistence. The deployed collector
  returned all six drive letters alongside CPU and GPU data.
