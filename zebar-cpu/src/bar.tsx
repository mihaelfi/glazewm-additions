import { createEffect, createMemo, createSignal, For, onCleanup, Show } from 'solid-js';
import { render } from 'solid-js/web';
import { createProvider } from 'zebar';
import { usePanelToggle, type PanelName } from './activity-panel';
import { formatClock, useClockTime } from './clock';
import { describeDrive, describeDriveActivity, DriveBars, useDrives } from './drives';
import { Heatmap } from './heatmap';
import { HistoryGraph } from './history-graph';
import { memoryPercent, useMetricsFeed } from './metrics-feed';
import { adapterSsid, formatNetworkRate, mainNetworkAdapter, NetworkBarRate, NetworkIcon, networkKindLabel, useNetworkDetails } from './network';
import { type BarItem, driveActivityMode, formatWindow, gpuIndex, showsDrive, useSettings } from './settings';
import { clearTimer, elapsedPercent, isOverdue, timerChanged, timerLabel } from './timer';
import { appsInWorkspace, watchWorkspaceApps, type WorkspaceApp } from './workspace-apps';
import './widget.css';
import './clock.css';
import './workspace-icons.css';

function Bar() {
  const glazewm = createProvider({ type: 'glazewm' });
  const keyboard = createProvider({ type: 'keyboard', refreshInterval: 1000 });
  const connection = useNetworkDetails();
  const battery = createProvider({ type: 'battery' });
  const weather = createProvider({ type: 'weather' });
  const [wm, setWm] = createSignal(glazewm.output);
  const [workspaceApps, setWorkspaceApps] = createSignal<Map<string, WorkspaceApp[]> | null>(null);
  const now = useClockTime();
  const [inputLanguage, setInputLanguage] = createSignal(keyboard.output);
  const [charge, setCharge] = createSignal(battery.output);
  const [forecast, setForecast] = createSignal(weather.output);
  const [panelError, setPanelError] = createSignal<PanelName>();
  const [timerError, setTimerError] = createSignal(false);
  const [acknowledging, setAcknowledging] = createSignal(false);
  const { settings, sync } = useSettings();
  const historySeconds = () => settings().historyMinutes * 60;
  const { samples, status, gpus, settingsVersion, activeTimer, driveRates, network } = useMetricsFeed(true, historySeconds);
  createEffect(() => sync(settingsVersion()));
  const panels = usePanelToggle();
  const allDrives = useDrives();
  const drives = createMemo(() => allDrives().filter(drive => showsDrive(settings(), drive.letter)));
  const activityMode = (letter: string) => driveActivityMode(settings(), letter);
  const storageLabel = () => drives().map(drive => activityMode(drive.letter) === 'none'
    ? describeDrive(drive)
    : `${describeDrive(drive)}. ${describeDriveActivity(driveRates()?.[drive.letter])}`).join('. ');
  const shows = (item: BarItem) => settings().bar[item];
  const windowLabel = () => `last ${formatWindow(historySeconds())}`;
  const networkAdapter = createMemo(() => mainNetworkAdapter(network()?.adapters ?? [], connection()));
  const networkTitle = () => {
    const adapter = networkAdapter();
    if (!adapter) return network() ? 'No active Wi-Fi or Ethernet' : 'Network data unavailable';
    return adapterSsid(adapter, connection()) ?? networkKindLabel(adapter.kind);
  };

  glazewm.onOutput(setWm);
  onCleanup(watchWorkspaceApps(setWorkspaceApps));
  keyboard.onOutput(setInputLanguage);
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

  const clockLabel = createMemo(() => formatClock(now(), settings().clockFormat));
  const countdown = createMemo(() => {
    const timer = activeTimer();
    return timer ? timerLabel(timer, now()) : '';
  });
  const overdue = createMemo(() => {
    const timer = activeTimer();
    return timer !== null && isOverdue(timer, now());
  });
  const clockAction = () => timerError() && overdue() ? 'Could not dismiss timer. Click to retry.'
    : overdue() ? 'Dismiss timer alarm'
    : panelError() === 'clock' ? 'Calendar could not open. Click to retry.' : 'Open calendar and timer';

  async function clickClock() {
    const timer = activeTimer();
    if (!timer || !isOverdue(timer, Date.now())) {
      await togglePanel('clock');
      return;
    }
    if (acknowledging()) return;
    setTimerError(false);
    setAcknowledging(true);
    try {
      await clearTimer(timer);
      timerChanged();
    } catch {
      setTimerError(true);
    } finally {
      setAcknowledging(false);
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
            aria-label={`Storage. ${storageLabel()}. Open storage details`}
            aria-haspopup="dialog"
            title={panelError() === 'storage-activity' ? 'Storage details could not open. Click to retry.'
              : `${drives().map(describeDrive).join('\n')}${drives().some(drive => activityMode(drive.letter) === 'marks')
                ? '\nActivity marks: read above, write below.' : ''}\nClick for details.`}
            onPointerDown={() => panels.press('storage-activity')}
            onClick={() => void togglePanel('storage-activity')}
          >
            <DriveBars drives={drives()} rates={driveRates()} mode={activityMode} />
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
        <Show when={shows('network')}>
          <button
            class="metric-button network-button"
            aria-haspopup="dialog"
            aria-label={`${networkTitle()}. Download ${formatNetworkRate(network()?.total.download)}. Upload ${formatNetworkRate(network()?.total.upload)}. Open network activity.`}
            title={panelError() === 'network-activity' ? 'Network activity could not open. Click to retry.' : `${networkTitle()}. Click for details.`}
            onPointerDown={() => panels.press('network-activity')}
            onClick={() => void togglePanel('network-activity')}
          >
            <NetworkIcon kind={networkAdapter()?.kind} />
            <span class="network-bar-rates">
              <NetworkBarRate direction="download" bytes={network()?.total.download} />
              <NetworkBarRate direction="upload" bytes={network()?.total.upload} />
            </span>
          </button>
        </Show>
        <button
          class="clock-button"
          classList={{ 'clock-timer-active': !!activeTimer(), 'clock-overdue': overdue() }}
          aria-haspopup={overdue() ? undefined : 'dialog'}
          aria-label={`${clockLabel()}${countdown() ? `. Timer ${countdown()}` : ''}. ${clockAction()}`}
          title={`${clockLabel()} · ${clockAction()}`}
          disabled={acknowledging()}
          onPointerDown={() => panels.press('clock')}
          onClick={() => void clickClock()}
        >
          <time dateTime={new Date(now()).toISOString()}>{clockLabel()}</time>
          <Show when={activeTimer()}>
            {timer => <>
              <span class="clock-countdown">{countdown()}</span>
              <span class="clock-progress" aria-hidden="true"
                style={{ '--time-elapsed': `${elapsedPercent(timer(), now())}%` }} />
            </>}
          </Show>
        </button>
      </div>
    </main>
  );
}

render(() => <Bar />, document.getElementById('root')!);
