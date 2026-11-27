# Zebar collector (`zebar-cpu/native/`)

Read this when you change sampling, endpoints, settings storage or the timer back end.
Front-end widgets are described in [`zebar.md`](zebar.md); changes to the collector are logged there too.

A windowless Rust HTTP server, built as `zebar-cpu-collector.exe`, shared by every bar. All sampling
happens here, not in the webviews. Files: `main.rs` (server, sampling loop, endpoints), `processes.rs`,
`gpu.rs`, `settings.rs`, `timer.rs`, `state_file.rs`.

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

- `/history?session=&after=&limit=<seconds>` → `{ session, settingsVersion, timer, gpus, samples }`: new samples since `after`.
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

## Settings storage (`settings.rs`)

- The collector owns `settings.json` next to its exe (the pack folder). Missing = defaults.
- Saves are cleaned up (unknown history value → 1, invalid letters dropped, all bar items off → all on) and written
  atomically (temp file + rename, `state_file.rs`). Bad JSON → 400; a wrong Origin → 403, like every endpoint.
- Every `/history` response carries `settingsVersion` so bars refetch after a change.

## Timer (`timer.rs`)

- Atomically writes the absolute Unix-millisecond deadline and duration to `timer.json` beside its executable. After
  restart it restores the active timer, including overdue alarms. The saved alarm timestamp prevents a single chime
  from playing twice. Repeating alarms resume if still overdue and repeat is enabled. Dismissal persists as `null`.
- Plays the Windows chime from the existing one-second tick (about every four seconds while repeating).
- `/timer/start` rejects a second timer, and `/timer/clear` checks the deadline so stale actions cannot clear a timer with a different deadline.

## Deploying a new exe

Stop `zebar-cpu-collector` (the exe is locked while it runs) and copy in a **retry loop**: the bar restarts it
within a few hundred ms, so a single stop-then-copy can fail with "file in use".
