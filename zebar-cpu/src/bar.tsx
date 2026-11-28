import { createEffect, createMemo, createSignal, For, onCleanup, Show, type JSX } from 'solid-js';
import { render } from 'solid-js/web';
import { createProvider } from 'zebar';
import { usePanelToggle, type PanelName } from './activity-panel';
import { BarIcon } from './bar-icon';
import { useBarWindowHeight } from './bar-window';
import { formatClock, useClockTime } from './clock';
import { describeDrive, describeDriveActivity, DriveBars, useDrives } from './drives';
import { BarHeatmap } from './heatmap';
import { HistoryGraph } from './history-graph';
import { keyboardFlag } from './keyboard-flag';
import { memoryPercent, useMetricsFeed } from './metrics-feed';
import { adapterSsid, formatNetworkRate, mainNetworkAdapter, NetworkBarRate, NetworkIcon, networkKindLabel, useNetworkDetails } from './network';
import { type BarItem, type BarLayoutItem, driveActivityMode, formatWindow, gpuIndex, showsDrive, useSettings } from './settings';
import { clearTimer, elapsedPercent, isOverdue, timerChanged, timerLabel } from './timer';
import { appsInWorkspace, watchWorkspaceApps, type WorkspaceApp } from './workspace-apps';
import './widget.css';
import './bar.css';
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
  // Zebar on Windows includes the locale's terminating NUL.
  const keyboardLayout = createMemo(() => inputLanguage()?.layout.split('\0', 1)[0].trim().replaceAll('_', '-'));
  const [charge, setCharge] = createSignal(battery.output);
  const [forecast, setForecast] = createSignal(weather.output);
  const [panelError, setPanelError] = createSignal<PanelName>();
  const [timerError, setTimerError] = createSignal(false);
  const [acknowledging, setAcknowledging] = createSignal(false);
  const { settings, sync } = useSettings();
  useBarWindowHeight(() => settings().barHeight);
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
  const clockParts = createMemo(() => {
    const text = clockLabel();
    const combined = /^(\d{4}-\d{2}-\d{2}) (.+)$/.exec(text);
    return combined ? { date: combined[1], time: combined[2] } : { time: text };
  });
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

  const renderBarItem: Record<BarLayoutItem, () => JSX.Element> = {
    storage: () => (
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
          <BarIcon name="storage" />
          <DriveBars drives={drives()} rates={driveRates()} mode={activityMode} />
        </button>
      </Show>
    ),
    ram: () => (
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
          <BarIcon name="ram" />
          <HistoryGraph values={ramValues()} length={historySeconds()} tone="ram" compact />
          <b class="metric-value">{ramLabel()}</b>
        </button>
      </Show>
    ),
    cpu: () => (
      <Show when={shows('cpu')}>
        <button
          class="metric-button cpu-button"
          aria-label={status() === 'live' ? `CPU ${cpuLabel()}. Open CPU activity` : 'Open CPU activity'}
          aria-haspopup="dialog"
          title={panelError() === 'cpu-activity' ? 'CPU activity could not open. Click to retry.'
            : status() === 'unavailable' ? 'CPU data unavailable. Click for details.'
            : `CPU load, ${windowLabel()}. Heatmap rows group logical processors by peak load when needed. Click for details.`}
          onPointerDown={() => panels.press('cpu-activity')}
          onClick={() => void togglePanel('cpu-activity')}
        >
          <BarIcon name="cpu" />
          <BarHeatmap samples={status() === 'live' ? samples() : []} length={historySeconds()} />
          <b class="metric-value">{cpuLabel()}</b>
        </button>
      </Show>
    ),
    gpu: () => (
      <Show when={shows('gpu') && (status() !== 'live' || gpus().length)}>
        <button
          class="metric-button gpu-button"
          aria-label={gpuUsage() === undefined ? 'Open GPU activity' : `GPU ${gpuLabel()}. Open GPU activity`}
          aria-haspopup="dialog"
          title={gpuTitle()}
          onPointerDown={() => panels.press('gpu-activity')}
          onClick={() => void togglePanel('gpu-activity')}
        >
          <BarIcon name="gpu" />
          <HistoryGraph values={gpuValues()} length={historySeconds()} tone="gpu" compact />
          <b class="metric-value">{gpuLabel()}</b>
        </button>
      </Show>
    ),
    tilingDirection: () => (
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
    ),
    weather: () => (
      <Show when={shows('weather') && forecast()}>
        {value => (
          <span class="bar-status-card weather" title={`Weather: ${value().status.replaceAll('_', ' ')}`}>
            <BarIcon name="thermometer" />
            {Math.round(value().celsiusTemp)}°C
          </span>
        )}
      </Show>
    ),
    language: () => (
      <Show when={shows('language') && keyboardLayout()}>
        {layout => {
          const flag = createMemo(() => settings().languageDisplay === 'code' ? undefined : keyboardFlag(layout()));
          const flagOnly = () => settings().languageDisplay === 'flag' && !!flag();
          return (
            <span class="bar-status-card input-language" classList={{ 'flag-only': flagOnly() }}
              title={`Input language: ${layout()}`}>
              <Show when={!flagOnly()}><span>{layout().split('-', 1)[0].toUpperCase()}</span></Show>
              <Show when={flag()}>
                {src => <img class="keyboard-flag" src={src()} alt={flagOnly() ? layout() : ''} />}
              </Show>
            </span>
          );
        }}
      </Show>
    ),
    network: () => (
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
    ),
  };

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
        <div class="bar-metrics" onWheel={event => {
          const scroller = event.currentTarget;
          if (!event.deltaX && scroller.scrollWidth > scroller.clientWidth) {
            scroller.scrollLeft += event.deltaY * (event.deltaMode === WheelEvent.DOM_DELTA_LINE ? 12 : 1);
          }
        }}>
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
                <BarIcon name="battery" />{Math.round(value().chargePercent)}%
              </span>
            )}
          </Show>
          <For each={settings().barOrder}>{item => renderBarItem[item]()}</For>
        </div>
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
          <BarIcon name="clock" />
          <time dateTime={new Date(now()).toISOString()}>
            <Show when={clockParts().date}>{date => <span class="clock-date">{date()}</span>}</Show>
            <span class="clock-time">{clockParts().time}</span>
          </time>
          <Show when={activeTimer()}>
            {timer => (
              <span class="clock-timer">
                <BarIcon name="timer" />
                <span class="clock-countdown">{countdown()}</span>
                <span class="clock-progress" aria-hidden="true"
                  style={{ '--time-elapsed': `${elapsedPercent(timer(), now())}%` }} />
              </span>
            )}
          </Show>
        </button>
      </div>
    </main>
  );
}

render(() => <Bar />, document.getElementById('root')!);
