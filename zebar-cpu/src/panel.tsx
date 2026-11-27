import { createMemo, createSignal, For, Index, onCleanup, onMount, Show } from 'solid-js';
import { render } from 'solid-js/web';
import { useCloseActivityPanel } from './activity-panel';
import { PanelHeader } from './panel-header';
import { formatWindow, useSettings } from './settings';
import { Heatmap } from './heatmap';
import { HistoryGraph } from './history-graph';
import { requestMetrics, useMetricsFeed } from './metrics-feed';
import './widget.css';

const processorLabel = (index: number) => `CPU ${String(index).padStart(2, '0')}`;

interface CpuProcess {
  name: string;
  pid: number;
  /** Shares of total CPU, in percent. */
  current: number;
  average: number;
  peak: number;
}

type SortKey = 'current' | 'average' | 'peak';
const sortColumns: { key: SortKey; label: string; title: string }[] = [
  { key: 'current', label: 'Now', title: 'CPU in the last second' },
  { key: 'average', label: 'Avg', title: 'Average CPU over the history window' },
  { key: 'peak', label: 'Peak', title: 'Highest one-second CPU in the history window' },
];
const PROCESS_ROWS = 8;
const formatShare = (value: number) => value >= 10 ? `${Math.round(value)}%` : `${value.toFixed(1)}%`;


function Panel() {
  const close = useCloseActivityPanel();
  const { settings } = useSettings();
  const historySeconds = () => settings().historyMinutes * 60;
  const { samples, status } = useMetricsFeed(false, historySeconds);
  const [selected, setSelected] = createSignal(0);
  const latest = () => samples().at(-1);
  const visibleSamples = () => status() === 'live' ? samples() : [];
  const history = createMemo(() => visibleSamples().map(sample => sample.usage[selected()]));
  const peak = createMemo(() => Math.max(0, ...history().map(value => value ?? 0)));
  const [processes, setProcesses] = createSignal<CpuProcess[]>([]);
  const [historyWindow, setHistoryWindow] = createSignal(0);
  const [processStatus, setProcessStatus] = createSignal<'loading' | 'live' | 'unavailable'>('loading');
  const [sortKey, setSortKey] = createSignal<SortKey>('average');
  const sortedProcesses = createMemo(() => {
    const key = sortKey();
    return [...processes()].sort((a, b) => b[key] - a[key] || b.average - a.average).slice(0, PROCESS_ROWS);
  });
  const largestShare = () => Math.max(0.1, ...sortedProcesses().map(process => process[sortKey()]));

  onMount(() => {
    let timer: number | undefined;
    let stopped = false;

    async function refreshProcesses() {
      try {
        const latest = await requestMetrics<{ window: number; processes: CpuProcess[] }>(
          `/cpu/processes?window=${historySeconds()}`,
        );
        if (stopped) return;
        setProcesses(latest.processes);
        setHistoryWindow(latest.window);
        setProcessStatus('live');
      } catch {
        if (!stopped) setProcessStatus('unavailable');
      } finally {
        if (!stopped) timer = window.setTimeout(refreshProcesses, 2000);
      }
    }

    void refreshProcesses();
    onCleanup(() => {
      stopped = true;
      window.clearTimeout(timer);
    });
  });

  const busiest = createMemo(() => {
    const usage = latest()?.usage ?? [];
    return usage.reduce((best, load, index) => load > usage[best] ? index : best, 0);
  });

  return (
    <main class="panel" aria-labelledby="panel-title">
      <PanelHeader id="panel-title" title="CPU activity" onClose={close} />

      <div class="overview">
        <div><span>Total</span><strong>{status() === 'live' ? `${latest()?.total}%` : '—'}</strong></div>
        <div><span>Busiest</span><strong>{status() === 'live' ? `${processorLabel(busiest())} · ${latest()?.usage[busiest()]}%` : '—'}</strong></div>
        <div class="overview-heatmap" title={`All logical processors, last ${formatWindow(historySeconds())}`}>
          <Heatmap samples={visibleSamples()} length={historySeconds()} />
          <div class="heatmap-key"><span>0%</span><i /><span>100%</span></div>
        </div>
      </div>

      <section class="chart" aria-label="Selected processor history">
        <div class="chart-header">
          <h2>{processorLabel(selected())}</h2>
          <div>
            <strong>{status() === 'live' ? `${latest()?.usage[selected()]}%` : '—'}</strong>
            <span>{status() === 'live' ? `Peak ${peak()}%` : ''}</span>
          </div>
        </div>
        <div class="chart-body">
          <div class="chart-scale" aria-hidden="true"><span>100%</span><span>50%</span><span>0%</span></div>
          <HistoryGraph values={history()} length={historySeconds()} tone="cpu" />
          <Show when={status() !== 'live'}>
            <p class="chart-status" role="status">
              {status() === 'starting' ? 'Waiting for CPU samples…' : 'CPU data unavailable'}
            </p>
          </Show>
        </div>
        <div class="chart-time"><span>{formatWindow(historySeconds())} ago</span><span>Now</span></div>
      </section>

      <section class="processors" aria-labelledby="processor-title">
        <h2 id="processor-title">Logical processors <span>{latest()?.usage.length ?? ''}</span></h2>
        <div class="processor-grid">
          <Index each={latest()?.usage}>
            {(load, index) => (
              <button
                class="processor"
                aria-pressed={selected() === index}
                onClick={() => setSelected(index)}
              >
                <span>{processorLabel(index)}</span>
                <strong>{status() === 'live' ? `${load()}%` : '—'}</strong>
                <i aria-hidden="true" style={{ '--load': status() === 'live' ? `${load()}%` : '0%' }} />
              </button>
            )}
          </Index>
        </div>
      </section>

      <section class="memory-processes cpu-processes" aria-labelledby="cpu-process-title">
        <h2 id="cpu-process-title">
          Top processes <span>{historyWindow() ? `· last ${formatWindow(historyWindow())}` : ''}</span>
        </h2>
        <Show
          when={processStatus() === 'live' && sortedProcesses().length}
          fallback={<p class="process-status" role="status">
            {processStatus() === 'loading' ? 'Loading processes…'
              : processStatus() === 'live' ? 'Collecting process samples…'
              : 'Process CPU data unavailable'}
          </p>}
        >
          <table class="process-table cpu-process-table">
            <thead>
              <tr>
                <th scope="col">Process</th>
                <th scope="col">PID</th>
                <For each={sortColumns}>
                  {column => (
                    <th scope="col" aria-sort={sortKey() === column.key ? 'descending' : 'none'}>
                      <button
                        class="sort-button"
                        classList={{ active: sortKey() === column.key }}
                        title={`${column.title}. Click to sort.`}
                        onClick={() => setSortKey(column.key)}
                      >
                        {column.label}
                      </button>
                    </th>
                  )}
                </For>
              </tr>
            </thead>
            <tbody>
              <Index each={sortedProcesses()}>
                {process => (
                  <tr>
                    <td title={process().name}>{process().name}</td>
                    <td>{process().pid}</td>
                    <For each={sortColumns}>
                      {column => (
                        <td classList={{ sorted: sortKey() === column.key }}>
                          <Show when={sortKey() === column.key}>
                            <span
                              class="process-meter"
                              aria-hidden="true"
                              style={{ '--share': `${100 * process()[column.key] / largestShare()}%` }}
                            />
                          </Show>
                          {formatShare(process()[column.key])}
                        </td>
                      )}
                    </For>
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

render(() => <Panel />, document.getElementById('root')!);
