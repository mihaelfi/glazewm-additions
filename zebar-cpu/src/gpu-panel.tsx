import { createEffect, createMemo, createSignal, For, Index, onCleanup, onMount, Show } from 'solid-js';
import { render } from 'solid-js/web';
import { useCloseActivityPanel } from './activity-panel';
import { PanelHeader } from './panel-header';
import { formatWindow, gpuIndex, useSettings } from './settings';
import { HistoryGraph } from './history-graph';
import { GPU_ENGINES, requestMetrics, useMetricsFeed } from './metrics-feed';
import './widget.css';

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

/** Selected engine: -1 is overall load, otherwise an index into GPU_ENGINES. */
const engineLabel = (engine: number) => engine < 0 ? 'Overall' : GPU_ENGINES[engine];
/** Short names that fit the six-column engine grid. */
const shortEngineLabel = (engine: number) => engineLabel(engine).replace('Video ', '');

function GpuPanel() {
  const close = useCloseActivityPanel();
  const { settings, loaded } = useSettings();
  const historySeconds = () => settings().historyMinutes * 60;
  const { samples, status, gpus } = useMetricsFeed(false, historySeconds);
  const [adapter, setAdapter] = createSignal(0);
  // Start on the GPU the bar follows, until the user picks a tab.
  let picked = false;
  createEffect(() => {
    if (!picked && loaded() && gpus().length) setAdapter(gpuIndex(settings(), gpus()));
  });
  const [engine, setEngine] = createSignal(-1);
  const [processes, setProcesses] = createSignal<GpuProcess[]>([]);
  const [processStatus, setProcessStatus] = createSignal<'loading' | 'live' | 'unavailable'>('loading');

  const info = () => gpus()[adapter()];
  const current = () => status() === 'live' ? samples().at(-1)?.gpu?.[adapter()] : undefined;
  const loadOf = (sample: { usage: number; engines: number[] } | undefined, index: number) =>
    sample === undefined ? undefined : index < 0 ? sample.usage : sample.engines[index];
  const history = createMemo(() =>
    status() === 'live' ? samples().map(sample => loadOf(sample.gpu?.[adapter()], engine())) : [],
  );
  const peak = createMemo(() => Math.max(0, ...history().map(value => value ?? 0)));
  const selectedLoad = () => loadOf(current(), engine());
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
    <main class="panel gpu-panel" aria-labelledby="gpu-title">
      <PanelHeader id="gpu-title" title="GPU activity" onClose={close} />

      <Show when={gpus().length > 1} fallback={<p class="adapter-name">{info()?.name ?? ''}</p>}>
        <div class="adapter-tabs" role="tablist" aria-label="Graphics adapter">
          <For each={gpus()}>
            {(gpu, index) => (
              <button
                role="tab"
                aria-selected={adapter() === index()}
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

      <div class="overview gpu-overview">
        <div><span>Load</span><strong>{current() ? `${current()!.usage}%` : '—'}</strong></div>
        <div>
          <span>Dedicated memory</span>
          <strong>{formatMemory(current()?.dedicatedUsed)} <small>/ {formatMemory(info()?.dedicatedTotal)}</small></strong>
          <i class="memory-meter" aria-hidden="true" style={{ '--share': `${dedicatedShare()}%` }} />
        </div>
        <div><span>Shared memory</span><strong>{formatMemory(current()?.sharedUsed)}</strong></div>
        <div>
          <span>Temperature</span>
          <strong>{current()?.temperature === undefined ? '—' : `${Math.round(current()!.temperature!)}°C`}</strong>
        </div>
      </div>

      <section class="chart" aria-label="Selected engine history">
        <div class="chart-header">
          <h2>{engineLabel(engine())}</h2>
          <div>
            <strong class="gpu-usage">{selectedLoad() === undefined ? '—' : `${selectedLoad()}%`}</strong>
            <span>{current() ? `Peak ${peak()}%` : ''}</span>
          </div>
        </div>
        <div class="chart-body">
          <div class="chart-scale" aria-hidden="true"><span>100%</span><span>50%</span><span>0%</span></div>
          <HistoryGraph values={history()} length={historySeconds()} tone="gpu" />
          <Show when={!current()}>
            <p class="chart-status" role="status">
              {status() === 'starting' ? 'Waiting for GPU samples…' : 'GPU data unavailable'}
            </p>
          </Show>
        </div>
        <div class="chart-time"><span>{formatWindow(historySeconds())} ago</span><span>Now</span></div>
      </section>

      <section class="processors gpu-engines" aria-labelledby="engine-title">
        <h2 id="engine-title">Engines</h2>
        <div class="processor-grid">
          <Index each={[-1, ...GPU_ENGINES.keys()]}>
            {index => (
              <button
                class="processor"
                aria-pressed={engine() === index()}
                title={engineLabel(index())}
                onClick={() => setEngine(index())}
              >
                <span>{shortEngineLabel(index())}</span>
                <strong>{loadOf(current(), index()) === undefined ? '—' : `${loadOf(current(), index())}%`}</strong>
                <i aria-hidden="true" style={{ '--load': `${loadOf(current(), index()) ?? 0}%` }} />
              </button>
            )}
          </Index>
        </div>
      </section>

      <section class="memory-processes" aria-labelledby="gpu-process-title">
        <h2 id="gpu-process-title">Top processes</h2>
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
      </section>
    </main>
  );
}

render(() => <GpuPanel />, document.getElementById('root')!);
