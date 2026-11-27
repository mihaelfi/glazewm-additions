import { createMemo, createSignal, Index, onCleanup, onMount, Show } from 'solid-js';
import { render } from 'solid-js/web';
import { useCloseActivityPanel } from './activity-panel';
import { PanelHeader } from './panel-header';
import { formatWindow, useSettings } from './settings';
import { HistoryGraph } from './history-graph';
import { memoryPercent, requestMetrics, useMetricsFeed } from './metrics-feed';
import './widget.css';

interface ProcessMemory {
  name: string;
  pid: number;
  bytes: number;
}

const mebibyte = 1024 ** 2;
const gibibyte = 1024 ** 3;
const formatCapacity = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 });

function formatMemory(bytes: number | undefined) {
  if (bytes === undefined) return '—';
  if (bytes >= gibibyte) return `${formatCapacity.format(bytes / gibibyte)} GiB`;
  return `${formatCapacity.format(bytes / mebibyte)} MiB`;
}

function RamPanel() {
  const close = useCloseActivityPanel();
  const { settings } = useSettings();
  const historySeconds = () => settings().historyMinutes * 60;
  const { samples, status } = useMetricsFeed(false, historySeconds);
  const [processes, setProcesses] = createSignal<ProcessMemory[]>([]);
  const [processStatus, setProcessStatus] = createSignal<'loading' | 'live' | 'unavailable'>('loading');

  const memory = () => status() === 'live' ? samples().at(-1)?.memory : undefined;
  const usage = () => memoryPercent(memory());
  const usageLabel = () => {
    const value = usage();
    return value === undefined ? '—' : `${Math.round(value)}%`;
  };
  const history = createMemo(() =>
    status() === 'live' ? samples().map(sample => memoryPercent(sample.memory)) : [],
  );

  onMount(() => {
    let timer: number | undefined;
    let stopped = false;

    async function refreshProcesses() {
      try {
        const latest = await requestMetrics<ProcessMemory[]>('/memory/processes');
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
    <main class="panel ram-panel" aria-labelledby="ram-title">
      <PanelHeader id="ram-title" title="RAM activity" onClose={close} />

      <div class="overview ram-overview">
        <div><span>Used</span><strong>{formatMemory(memory()?.used)}</strong></div>
        <div><span>Available</span><strong>{formatMemory(memory()?.available)}</strong></div>
        <div><span>Total RAM</span><strong>{formatMemory(memory()?.total)}</strong></div>
      </div>

      <section class="chart" aria-labelledby="ram-history-title">
        <div class="chart-header">
          <h2 id="ram-history-title">Memory usage</h2>
          <strong class="ram-usage">{usageLabel()}</strong>
        </div>
        <div class="chart-body">
          <div class="chart-scale" aria-hidden="true"><span>100%</span><span>50%</span><span>0%</span></div>
          <HistoryGraph values={history()} length={historySeconds()} tone="ram" />
          <Show when={usage() === undefined}>
            <p class="chart-status" role="status">
              {status() === 'starting' ? 'Waiting for RAM samples…' : 'RAM data unavailable'}
            </p>
          </Show>
        </div>
        <div class="chart-time"><span>{formatWindow(historySeconds())} ago</span><span>Now</span></div>
      </section>

      <section class="memory-processes" aria-labelledby="process-title">
        <h2 id="process-title">Top 10 processes</h2>
        <Show
          when={processStatus() === 'live' && processes().length}
          fallback={<p class="process-status" role="status">
            {processStatus() === 'loading' ? 'Loading processes…' : 'Process memory data unavailable'}
          </p>}
        >
          <table class="process-table">
            <thead><tr><th scope="col">Process</th><th scope="col">PID</th><th scope="col">Working set</th></tr></thead>
            <tbody>
              <Index each={processes()}>
                {process => (
                  <tr>
                    <td title={process().name}>{process().name}</td>
                    <td>{process().pid}</td>
                    <td>
                      <span
                        class="process-meter"
                        aria-hidden="true"
                        style={{ '--share': `${100 * process().bytes / processes()[0].bytes}%` }}
                      />
                      {formatMemory(process().bytes)}
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

render(() => <RamPanel />, document.getElementById('root')!);
