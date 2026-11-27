import { createEffect, createMemo, createSignal, For, onCleanup, Show } from 'solid-js';
import { render } from 'solid-js/web';
import { createProvider } from 'zebar';
import { usePanelToggle, type PanelName } from './activity-panel';
import { describeDrive, DriveBars, useDrives } from './drives';
import { Heatmap } from './heatmap';
import { HistoryGraph } from './history-graph';
import { memoryPercent, useMetricsFeed } from './metrics-feed';
import { type BarItem, formatWindow, gpuIndex, showsDrive, useSettings } from './settings';
import { appsInWorkspace, watchWorkspaceApps, type WorkspaceApp } from './workspace-apps';
import './widget.css';
import './workspace-icons.css';

function Bar() {
  const glazewm = createProvider({ type: 'glazewm' });
  const date = createProvider({ type: 'date', formatting: 'yyyy-MM-dd HH:mm' });
  const keyboard = createProvider({ type: 'keyboard', refreshInterval: 1000 });
  const network = createProvider({ type: 'network' });
  const battery = createProvider({ type: 'battery' });
  const weather = createProvider({ type: 'weather' });
  const [wm, setWm] = createSignal(glazewm.output);
  const [workspaceApps, setWorkspaceApps] = createSignal<Map<string, WorkspaceApp[]> | null>(null);
  const [clock, setClock] = createSignal(date.output);
  const [inputLanguage, setInputLanguage] = createSignal(keyboard.output);
  const [connection, setConnection] = createSignal(network.output);
  const [charge, setCharge] = createSignal(battery.output);
  const [forecast, setForecast] = createSignal(weather.output);
  const [panelError, setPanelError] = createSignal<PanelName>();
  const { settings, sync } = useSettings();
  const historySeconds = () => settings().historyMinutes * 60;
  const { samples, status, gpus, settingsVersion } = useMetricsFeed(true, historySeconds);
  createEffect(() => sync(settingsVersion()));
  const panels = usePanelToggle();
  const allDrives = useDrives();
  const drives = createMemo(() => allDrives().filter(drive => showsDrive(settings(), drive.letter)));
  const shows = (item: BarItem) => settings().bar[item];
  const windowLabel = () => `last ${formatWindow(historySeconds())}`;

  glazewm.onOutput(setWm);
  onCleanup(watchWorkspaceApps(setWorkspaceApps));
  date.onOutput(setClock);
  keyboard.onOutput(setInputLanguage);
  network.onOutput(setConnection);
  battery.onOutput(setCharge);
  weather.onOutput(setForecast);

  const latest = () => samples().at(-1);
  const cpuLabel = () => status() === 'live' ? `${latest()?.total}%` : '—';
  const ramValues = createMemo(() =>
    status() === 'live' ? samples().map(sample => memoryPercent(sample.memory)) : [],
  );
  const ramUsage = () => ramValues().at(-1);
  const ramLabel = () => {
    const usage = ramUsage();
    return usage === undefined ? '—' : `${Math.round(usage)}%`;
  };

  // The GPU chosen in settings, or by default the one with the most video memory.
  const gpu = createMemo(() => gpuIndex(settings(), gpus()));
  const gpuValues = createMemo(() =>
    status() === 'live' ? samples().map(sample => sample.gpu?.[gpu()]?.usage) : [],
  );
  const gpuUsage = () => gpuValues().at(-1);
  const gpuLabel = () => {
    const usage = gpuUsage();
    return usage === undefined ? '—' : `${usage}%`;
  };
  const gpuTitle = () => {
    if (panelError() === 'gpu-activity') return 'GPU activity could not open. Click to retry.';
    if (status() === 'unavailable' || (status() === 'live' && gpuUsage() === undefined)) {
      return 'GPU data unavailable. Click for details.';
    }
    const temperature = samples().at(-1)?.gpu?.[gpu()]?.temperature;
    const name = gpus()[gpu()]?.name ?? 'GPU';
    return `${name}${temperature === undefined ? '' : ` · ${Math.round(temperature)}°C`}, ${windowLabel()}. Click for details.`;
  };

  async function togglePanel(name: PanelName) {
    setPanelError(undefined);
    try {
      await panels.toggle(name);
    } catch (error) {
      console.error(`Could not open ${name}`, error);
      setPanelError(name);
    }
  }

  return (
    <main class="bar" aria-label="Desktop status bar">
      <nav class="workspaces" aria-label="Workspaces">
        <span class="brand" aria-hidden="true"><i /><i /><i /><i /></span>
        <For each={wm()?.currentWorkspaces}>
          {workspace => {
            const apps = createMemo(() => workspaceApps()?.get(workspace.id) ?? appsInWorkspace(workspace));
            const label = workspace.displayName ?? workspace.name;
            const description = () => `Workspace ${label}: ${apps().map(app => app.label).join(', ') || 'empty'}`;

            return (
              <button
                class="workspace"
                classList={{ displayed: workspace.isDisplayed, focused: workspace.hasFocus }}
                aria-current={workspace.isDisplayed ? 'true' : undefined}
                aria-label={description()}
                title={description()}
                // GlazeWM keeps quotes as part of the name, so pass it bare.
                onClick={() => void wm()?.runCommand(`focus --workspace ${workspace.name}`)}
              >
                {label}
                <Show when={apps().length}>
                  <span class="workspace-apps" aria-hidden="true">
                    <For each={apps()}>
                      {app => (
                        <Show when={app.glyph} fallback={<span class="workspace-app-initials">{app.initials}</span>}>
                          {glyph => <span class={`workspace-app-icon ${glyph()}`} />}
                        </Show>
                      )}
                    </For>
                  </span>
                </Show>
              </button>
            );
          }}
        </For>
      </nav>
      <div class="bar-status">
        <Show when={wm()?.isPaused}>
          <button class="bar-control" onClick={() => void wm()?.runCommand('wm-toggle-pause')}>Paused</button>
        </Show>
        <For each={wm()?.bindingModes}>
          {mode => (
            <button class="bar-control" onClick={() => void wm()?.runCommand(`wm-disable-binding-mode --name ${mode.name}`)}>
              {mode.displayName ?? mode.name}
            </button>
          )}
        </For>
        <Show when={connection()?.defaultGateway?.ssid}>
          {ssid => <span class="bar-stat network" title={ssid()}>{ssid()}</span>}
        </Show>
        <Show when={charge()}>
          {value => (
            <span class="bar-stat battery" title={`Battery ${value().state}`}>
              <span>BAT</span> {Math.round(value().chargePercent)}%
            </span>
          )}
        </Show>
        <Show when={shows('storage') && drives().length}>
          <button
            class="metric-button storage-button"
            aria-label={`Storage. ${drives().map(describeDrive).join('. ')}. Open storage details`}
            aria-haspopup="dialog"
            title={panelError() === 'storage-activity' ? 'Storage details could not open. Click to retry.'
              : `${drives().map(describeDrive).join('\n')}\nClick for details.`}
            onPointerDown={() => panels.press('storage-activity')}
            onClick={() => void togglePanel('storage-activity')}
          >
            <DriveBars drives={drives()} />
          </button>
        </Show>
        <Show when={shows('ram')}>
          <button
            class="metric-button ram-button"
            aria-label={ramUsage() === undefined ? 'Open RAM activity' : `RAM ${ramLabel()}. Open RAM activity`}
            aria-haspopup="dialog"
            title={panelError() === 'ram-activity' ? 'RAM activity could not open. Click to retry.'
              : status() === 'unavailable' ? 'RAM data unavailable. Click for details.'
              : `RAM usage, ${windowLabel()}. Click for details.`}
            onPointerDown={() => panels.press('ram-activity')}
            onClick={() => void togglePanel('ram-activity')}
          >
            <HistoryGraph values={ramValues()} length={historySeconds()} tone="ram" compact />
            <span class="metric-summary">
              <span class="ram-icon" aria-hidden="true" />
              <b>{ramLabel()}</b>
            </span>
          </button>
        </Show>
        <Show when={shows('cpu')}>
          <button
            class="metric-button"
            aria-label={status() === 'live' ? `CPU ${cpuLabel()}. Open CPU activity` : 'Open CPU activity'}
            aria-haspopup="dialog"
            title={panelError() === 'cpu-activity' ? 'CPU activity could not open. Click to retry.'
              : status() === 'unavailable' ? 'CPU data unavailable. Click for details.'
              : `CPU load by logical processor, ${windowLabel()}. Click for details.`}
            onPointerDown={() => panels.press('cpu-activity')}
            onClick={() => void togglePanel('cpu-activity')}
          >
            <Heatmap samples={status() === 'live' ? samples() : []} length={historySeconds()} />
            <span class="metric-summary">
              <span class="cpu-icon" aria-hidden="true" />
              <b>{cpuLabel()}</b>
            </span>
          </button>
        </Show>
        <Show when={shows('gpu') && (status() !== 'live' || gpus().length)}>
          <button
            class="metric-button gpu-button"
            aria-label={gpuUsage() === undefined ? 'Open GPU activity' : `GPU ${gpuLabel()}. Open GPU activity`}
            aria-haspopup="dialog"
            title={gpuTitle()}
            onPointerDown={() => panels.press('gpu-activity')}
            onClick={() => void togglePanel('gpu-activity')}
          >
            <HistoryGraph values={gpuValues()} length={historySeconds()} tone="gpu" compact />
            <span class="metric-summary">
              <span class="gpu-icon" aria-hidden="true" />
              <b>{gpuLabel()}</b>
            </span>
          </button>
        </Show>
        <Show when={wm()}>
          {state => (
            <button
              class="bar-control layout-control"
              aria-label={`Tiling ${state().tilingDirection}. Switch tiling direction`}
              title={`Tiling ${state().tilingDirection}`}
              onClick={() => void state().runCommand('toggle-tiling-direction')}
            >
              {state().tilingDirection === 'horizontal' ? '↔' : '↕'}
            </button>
          )}
        </Show>
        <Show when={forecast()}>
          {value => (
            <span class="bar-stat weather" title={value().status.replaceAll('_', ' ')}>
              {Math.round(value().celsiusTemp)}°C
            </span>
          )}
        </Show>
        <Show when={inputLanguage()?.layout}>
          {layout => (
            <span class="bar-stat input-language" title={`Input language: ${layout()}`}>
              {layout().split(/[-_]/, 1)[0].toUpperCase()}
            </span>
          )}
        </Show>
        <Show when={clock()}>
          {value => <time class="clock" dateTime={value().iso ?? undefined}>{value().formatted}</time>}
        </Show>
      </div>
    </main>
  );
}

render(() => <Bar />, document.getElementById('root')!);
