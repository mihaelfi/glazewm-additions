import { createEffect, createMemo, createSignal, For, Index, onCleanup, onMount, Show } from 'solid-js';
import { render } from 'solid-js/web';
import { useCloseActivityPanel } from './activity-panel';
import { BarIcon } from './bar-icon';
import { PanelHeader } from './panel-header';
import { PanelSection } from './panel-section';
import { formatWindow, gpuIndex, useSettings } from './settings';
import { HistoryGraph } from './history-graph';
import { GPU_ENGINES, requestMetrics, useMetricsFeed, type GpuSample } from './metrics-feed';
import './widget.css';
import './activity-popup.css';
import './gpu-panel.css';

interface GpuProcess {
  adapter: number;
  pid: number;
  name: string;
  usage: number;
  dedicated: number;
}

const PROCESS_ROWS = 8;
const mebibyte = 1024 ** 2;
const gibibyte = 1024 ** 3;
const formatCapacity = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 });

function formatMemory(bytes: number | undefined) {
  if (bytes === undefined) return '—';
  if (bytes >= gibibyte) return `${formatCapacity.format(bytes / gibibyte)} GiB`;
  return `${formatCapacity.format(bytes / mebibyte)} MiB`;
}

const formatUsage = (value: number | undefined) => value === undefined ? '—' : `${value}%`;

/** A missing engine index means overall adapter load. */
const loadOf = (sample: GpuSample | undefined, index: number | undefined) =>
  index === undefined ? sample?.usage : sample?.engines[index];

function GpuPanel() {
  const close = useCloseActivityPanel();
  const { settings, loaded } = useSettings();
  const historySeconds = () => settings().historyMinutes * 60;
  const { samples, status, gpus } = useMetricsFeed(false, historySeconds);
  const [adapter, setAdapter] = createSignal(0);
  // Start on the GPU the bar follows, until the user picks a tab.
  let picked = false;
  createEffect(() => {
    const available = gpus();
    if (available.length && ((!picked && loaded()) || adapter() >= available.length)) {
      setAdapter(gpuIndex(settings(), available));
    }
  });
  const [engine, setEngine] = createSignal<number>();
  const [processes, setProcesses] = createSignal<GpuProcess[]>([]);
  const [processStatus, setProcessStatus] = createSignal<'loading' | 'live' | 'unavailable'>('loading');

  const info = () => gpus()[adapter()];
  const current = () => status() === 'live' ? samples().at(-1)?.gpu?.[adapter()] : undefined;
  const adapterHistory = createMemo(() =>
    status() === 'live' ? samples().map(sample => sample.gpu?.[adapter()]) : [],
  );
  const history = createMemo(() => adapterHistory().map(sample => loadOf(sample, engine())));
  const peak = createMemo(() => history().reduce<number | undefined>(
    (highest, value) => value === undefined ? highest : Math.max(highest ?? 0, value),
    undefined,
  ));
  const selectedLoad = () => loadOf(current(), engine());
  const historyTitle = () => {
    const selected = engine();
    return selected === undefined ? 'GPU Usage' : `${GPU_ENGINES[selected]} Usage`;
  };
  const temperature = () => {
    const value = current()?.temperature;
    return value === undefined ? '—' : `${Math.round(value)}°C`;
  };
  const dedicatedShare = () => {
    const total = info()?.dedicatedTotal;
    const used = current()?.dedicatedUsed;
    return total && used !== undefined ? Math.min(100, used / total * 100) : 0;
  };
  const visibleProcesses = createMemo(() =>
    processes().filter(process => process.adapter === adapter()).slice(0, PROCESS_ROWS),
  );
  const largestProcess = () => Math.max(1, ...visibleProcesses().map(process => process.dedicated));

  onMount(() => {
    let timer: number | undefined;
    let stopped = false;

    async function refreshProcesses() {
      try {
        const latest = await requestMetrics<GpuProcess[]>('/gpu/processes');
        if (stopped) return;
        setProcesses(latest);
        setProcessStatus('live');
      } catch {
        if (!stopped) setProcessStatus('unavailable');
      } finally {
        if (!stopped) timer = window.setTimeout(refreshProcesses, 3000);
      }
    }

    void refreshProcesses();
    onCleanup(() => {
      stopped = true;
      window.clearTimeout(timer);
    });
  });

  return (
    <main class="panel popup-frame activity-popup gpu-panel" aria-labelledby="gpu-title">
      <PanelHeader id="gpu-title" title="GPU Activity" onClose={close} icon={<BarIcon name="gpu" />} />

      <Show when={gpus().length > 1} fallback={
        <Show when={info()}>{gpu => <p class="gpu-adapter-name">{gpu().name}</p>}</Show>
      }>
        <div class="gpu-adapter-tabs" role="group" aria-label="Graphics adapter">
          <For each={gpus()}>
            {(gpu, index) => (
              <button
                type="button"
                aria-pressed={adapter() === index()}
                title={gpu.name}
                onClick={() => {
                  picked = true;
                  setAdapter(index());
                }}
              >
                {gpu.name}
              </button>
            )}
          </For>
        </div>
      </Show>

      <PanelSection scope="gpu" id="usage" label={historyTitle()} class="popup-frame activity-card gpu-usage-card" detail={
        <>
          <strong class="gpu-usage">{formatUsage(selectedLoad())}</strong>
          <Show when={peak() !== undefined}>
            <span class="panel-section-detail">Peak {formatUsage(peak())}</span>
          </Show>
        </>
      }>
        <dl class="gpu-capacity">
          <div>
            <dt>Dedicated Memory</dt>
            <dd>{formatMemory(current()?.dedicatedUsed)} <small>/ {formatMemory(info()?.dedicatedTotal)}</small></dd>
            <i class="gpu-vram-meter" aria-hidden="true" style={{ '--share': `${dedicatedShare()}%` }} />
          </div>
          <div><dt>Shared Memory</dt><dd>{formatMemory(current()?.sharedUsed)}</dd></div>
          <div><dt>Temperature</dt><dd>{temperature()}</dd></div>
        </dl>
        <div class="chart-body">
          <div class="chart-scale" aria-hidden="true"><span>100%</span><span>50%</span><span>0%</span></div>
          <HistoryGraph values={history()} length={historySeconds()} tone="gpu" />
          <Show when={selectedLoad() === undefined}>
            <p class="chart-status" role="status">
              {status() === 'starting' ? 'Waiting for GPU samples…'
                : current() ? 'Engine data unavailable' : 'GPU data unavailable'}
            </p>
          </Show>
        </div>
        <div class="chart-time"><span>{formatWindow(historySeconds())} ago</span><span>Now</span></div>
      </PanelSection>

      <PanelSection scope="gpu" id="engines" label="Engines" class="popup-frame activity-card gpu-engines" detail={
        <span class="panel-section-detail">Last {formatWindow(historySeconds())} · 0–100%</span>
      }>
        <div class="gpu-engine-grid">
          <For each={GPU_ENGINES}>
            {(name, index) => {
              const selected = () => engine() === index();
              const values = createMemo(() => adapterHistory().map(sample => loadOf(sample, index())));
              const load = () => formatUsage(loadOf(current(), index()));
              return (
                <button
                  type="button"
                  class="popup-frame gpu-engine-tile"
                  aria-pressed={selected()}
                  aria-label={`${name} ${load()}`}
                  aria-controls="gpu-usage-body"
                  title={selected() ? 'Show overall GPU history above' : `Show larger ${name} history above`}
                  onClick={() => setEngine(selected() ? undefined : index())}
                >
                  <span>{name.replace('Video ', '')}</span>
                  <strong>{load()}</strong>
                  <HistoryGraph compact values={values()} length={historySeconds()} tone="gpu" />
                </button>
              );
            }}
          </For>
        </div>
      </PanelSection>

      <PanelSection scope="gpu" id="processes" label="Top Processes" class="popup-frame activity-card gpu-processes">
        <Show
          when={processStatus() === 'live' && visibleProcesses().length}
          fallback={<p class="process-status" role="status">
            {processStatus() === 'loading' ? 'Loading processes…'
              : processStatus() === 'live' ? 'No processes are using this GPU'
              : 'GPU process data unavailable'}
          </p>}
        >
          <table class="process-table gpu-process-table">
            <thead>
              <tr><th scope="col">Process</th><th scope="col">PID</th><th scope="col">GPU</th><th scope="col">Dedicated</th></tr>
            </thead>
            <tbody>
              <Index each={visibleProcesses()}>
                {process => (
                  <tr>
                    <td title={process().name}>{process().name}</td>
                    <td>{process().pid}</td>
                    <td>{process().usage}%</td>
                    <td>
                      <span
                        class="process-meter"
                        aria-hidden="true"
                        style={{ '--share': `${100 * process().dedicated / largestProcess()}%` }}
                      />
                      {formatMemory(process().dedicated)}
                    </td>
                  </tr>
                )}
              </Index>
            </tbody>
          </table>
        </Show>
      </PanelSection>
    </main>
  );
}

render(() => <GpuPanel />, document.getElementById('root')!);
