# GlazeLauncher: workspace-aware app launcher

Read this when you work on the launcher (`launcher/`). Ideas and bugs, including the planned
actions framework, are in [`backlog.md`](backlog.md#launcher).

A resident WPF (.NET 10) app in `C:\codex\glazewm\launcher\`, installed to `%USERPROFILE%\.glzr\launcher\`,
data in `%LOCALAPPDATA%\GlazeLauncher\`. It replaces PowerToys Run on Alt+Space
(PowerToys Run is turned off; Command Palette stays on Win+Alt+Space) and knows about GlazeWM's monitors,
workspaces and windows. Same performance rule as the bar: it stays resident with its window pre-created,
gets pushed updates from GlazeWM instead of polling, and does shell work on one background STA thread.

## Using it

- **Alt+Space** toggles it. It opens centred on the monitor GlazeWM has focused, a fifth of the way down. Esc, or clicking elsewhere, closes it.
  The content fades and slides into place over 125 ms. The selected row has a lavender accent that fades in over 85 ms. Both effects respect Windows' animation setting. Typing, navigation, closing and opening a result respond immediately.
- An **empty query** lists recent items for the current workspace, then every open window (other workspaces first) as a window switcher.
- Typing searches **apps** (Start menu and Store apps, from `shell:AppsFolder`), **open windows** (by title or process),
  **open Chrome and Edge tabs**, **folders**, and **commands** (tiling direction, floating, fullscreen, redraw, reload config, pause, workspace Sync, re-index, quit).
  The app index skips packaged entries missing from the current user's installed package inventory, Windows Store stubs, and direct Store product links.
  Matching ranks exact > prefix or whole word > word start > initials (`vsc` → Visual Studio Code) > substring > subsequence.
  Within one match tier, a concrete open window on another workspace comes first, then open windows on the current workspace, then app and command results. Provider priority and a bounded frecency boost refine the order within each group.
  The matching characters are highlighted in result titles, including non-adjacent fuzzy matches. When a window only matches by process name, the highlight is in its subtitle. The `@N` workspace token is separate from the text match.
- **Apps:** Enter opens it. If it is already open, Enter focuses its window instead; pressing it again cycles through that app's windows.
  Ctrl+Enter always opens a new window. The badge `● 12 21` shows the workspaces where it's open.
- **Windows:** Enter switches to the window's workspace if hidden and focuses the selected window. Ctrl+Enter brings it to the current workspace.
- **Browser tabs:** Search by tab title. A tab result shows the browser and workspace. Enter switches to that workspace and selects the existing tab. Chrome and Edge use Windows UI Automation without an extension or a change to browser settings.
- **Folders:** Search for Home, Desktop, Downloads, Documents, Pictures, Music, or Videos and press Enter to open the folder in File Explorer. Results show the Windows icon and the current user's registered path, including redirected locations. Used folders can appear in recents.
- **Send to a workspace:** add `@N` to the query, or press **Ctrl+1…9** on a result. One digit means slot N on the
  focused monitor's group (`@3` → `13` on the primary monitor, `23` on the secondary), the same rule as `workspace.ps1`;
  two digits (`@23`) are taken literally. By default your view follows the window; hold **Shift** to stay where you are
  (Hyprland's "silent"). The chip at the right of the search box shows the target (`→ ws 13`).

## How it works

| File | Role |
|---|---|
| `App.xaml.cs` | Single instance (mutex `Local\GlazeLauncher.Instance`), Alt+Space `RegisterHotKey` (retried every 5 s while another program holds it), named-pipe server `GlazeLauncher`, and the detached relaunch |
| `GlazeClient.cs` | WebSocket client for GlazeWM IPC `ws://127.0.0.1:6123`: `sub --events …`, `query monitors`, `command [--id X] …`. On any event it re-queries the tree (80 ms debounce) and forwards `window_managed`. `WmWindow` currently keeps id, handle, title, process, workspace and focus, **not the window state** (tiling/floating/minimized) |
| `ShellIndex.cs` | Refreshes the app catalog and known folders on the background STA thread. Checks Store package status while indexing apps and re-indexes on open if the index is older than 2 min. |
| `KnownFolder.cs` | Resolves the standard per-user folder IDs with `SHGetKnownFolderPath` and publishes the folders Windows can resolve |
| `Search.cs` | Query parsing (`@N`), fuzzy match tiers and character positions, merged ranking, shared result model, `WindowIdentity` |
| `SearchProviders.cs` | `ISearchProvider` contract and the compiled-in app, window, folder and command providers |
| `BrowserTabSearchProvider.cs` | Refreshes the Chrome and Edge tab cache on a background thread using only their accessibility tab strips. Selects a tab by its runtime ID within the original browser window. |
| `Actions.cs` | Launch / focus / move. Open app and window results share a focus path that runs `focus --workspace N` for hidden workspaces before `focus --container-id`. After an `@N` launch, the next matching `window_managed` window (within 15 s) gets `move --workspace N` and uses the same focus path if following. |
| `IconCache.cs` | Windows Shell icons (`IShellItemImageFactory`, 48px, alpha kept) loaded on the STA thread. Known folders use `SHGetKnownFolderItem` first, and the cache warms folder icons before app icons at startup. |
| `UsageStore.cs` | Frecency (half-life 10 days), global and per workspace, saved to `usage.json` |
| `MainWindow.xaml(.cs)` | UI (bar palette `#151b30` / `#b6acff`, DWM rounded corners), keys, placement |
| `HighlightedTextBlock.cs` | Renders matching characters as WPF runs and keeps Unicode text elements intact |
| `Native.cs`, `Infrastructure.cs` | Win32 interop; the shared background STA worker |

- **Matching a window to an app.** An app's IDs are its AppsFolder ID (an AUMID or a path), its exe name, its name with spaces removed, and for packaged apps the family name plus a small alias table (`PackagedProcessAliases`: Terminal, Notepad, Calculator, Paint). A window's IDs are its process name, its process's packaged AUMID (`GetApplicationUserModelId`), and its window AUMID (`PKEY_AppUserModel_ID`). They match if the two sets share any ID. Squirrel apps (Discord) point at `Update.exe`, which is ignored; the name match covers them.
- **Adding a provider.** Register it in `App.xaml.cs`. Search reads a cached snapshot and returns `ProviderResult` entries with a stable ID, match, optional hosting `WmWindow`, display fields, and an async activation callback. The engine ranks results centrally and the UI renders the supplied icon, text and key hints. Providers with `IRefreshableSearchProvider` update their cache asynchronously when the launcher opens. The browser provider uses the hosting window for workspace ranking and `Actions.FocusWindow` before selecting the tab. The tab cache holds titles and runtime IDs in memory, and frecency stores one key per browser.
- **Launching** uses ShellExecute on `shell:AppsFolder\<id>`, which works for Win32 and Store apps alike.
- **Detached relaunch:** started without `--detached`, the exe relaunches itself through ShellExecute (no handle inheritance) and exits. That's the same protection the Zebar collector has against keeping a parent's sockets alive.
- **Command line:** `GlazeLauncher.exe --toggle|--show|--hide|--reindex|--quit` forwards the command to the running instance over the pipe. With no argument a second start toggles.
- **Debug pipe commands** (send them with a `NamedPipeClientStream` to `GlazeLauncher`, not available as exe flags): `query:<text>` types a query,
  `enter`, `enter:ctrl` or `enter:shift` activates the selection, and `snapshot` renders the window to `%LOCALAPPDATA%\GlazeLauncher\snapshot.png`.
- **Rendering is software-only** (`RenderMode.SoftwareOnly`), and concurrent GC is off. GPU rendering loaded the NVIDIA driver into the process: about 113 → 77 MB private memory after a search. Working set is about 170 MB, mostly shared WPF DLL pages.
- **Without GlazeWM** the launcher still starts and searches apps (noticed 2026-11-27, not designed for). What works and what should happen in that mode is in the backlog.

## Build and deploy

```powershell
cd C:\codex\glazewm\launcher
dotnet publish -c Release -r win-x64 --self-contained false -o publish   # ReadyToRun, framework-dependent
GlazeLauncher.exe --quit        # from %USERPROFILE%\.glzr\launcher (or Stop-Process -Name GlazeLauncher)
Copy-Item publish\* $env:USERPROFILE\.glzr\launcher -Force
```

Back up the installed folder first as `.glzr\launcher\before-<change>-<timestamp>\`. Then start it again
(or reload GlazeWM's startup by restarting GlazeWM).

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
- Minimized windows: not restored on activation, and sometimes missing from results. Tracked in the backlog.

## Backups

- `.glzr\launcher\before-workspace-focus-20261127-150241\`: before the workspace focus fix
- `.glzr\launcher\before-motion-and-matches-20261127-152447\`: before the animation and highlight update
- `.glzr\launcher\before-provider-ranking-20261127-154402\`: before the provider and ranking update
- `.glzr\launcher\before-store-filter-20261127-160536\`: before the Store placeholder filter
- `.glzr\launcher\before-known-folders-20261127-161208\`: before the known-folder provider
- `.glzr\launcher\before-folder-icons-20261127-161907\`: before the Windows folder icon update
- `.glzr\launcher\before-browser-tabs-20261127-162737\`: before extension-free browser tab search

## Change log

Newest first.

### 2026-11-27
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
