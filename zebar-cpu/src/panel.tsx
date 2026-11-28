import { createMemo, createSignal, For, Index, onCleanup, onMount, Show } from 'solid-js';
import { render } from 'solid-js/web';
import { useCloseActivityPanel } from './activity-panel';
import { BarIcon } from './bar-icon';
import { PanelHeader } from './panel-header';
import { PanelSection } from './panel-section';
import { formatWindow, useSettings } from './settings';
import { CPU_HEATMAP_ROW_HEIGHT, ThermalHeatmap } from './heatmap';
import { HistoryGraph } from './history-graph';
import { requestMetrics, useMetricsFeed } from './metrics-feed';
import './widget.css';
import './activity-popup.css';
import './cpu-panel.css';

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
const usageLabel = (value: number | undefined) => value === undefined ? '—' : `${value}%`;

function Panel() {
  const close = useCloseActivityPanel();
  const { settings } = useSettings();
  const historySeconds = () => settings().historyMinutes * 60;
  const { samples, status } = useMetricsFeed(false, historySeconds);
  const latest = () => samples().at(-1);
  const visibleSamples = createMemo(() => status() === 'live' ? samples() : []);
  const totalHistory = createMemo(() => visibleSamples().map(sample => sample.total));
  const usage = () => latest()?.usage ?? [];
  const heatmapLabels = createMemo(() =>
    Array.from({ length: Math.ceil(usage().length / 8) }, (_, index) => index * 8),
  );
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
    const loads = usage();
    if (status() !== 'live' || !loads.length) return undefined;
    const index = loads.reduce((best, load, index) => load > loads[best] ? index : best, 0);
    return { index, load: loads[index] };
  });

  return (
    <main class="panel popup-frame activity-popup cpu-panel" aria-labelledby="panel-title">
      <PanelHeader id="panel-title" title="CPU Activity" onClose={close} icon={<BarIcon name="cpu" />} />

      <div class="popup-frame activity-card cpu-overview-frame">
        <PanelSection scope="cpu" id="overall" label="Overall CPU" class="cpu-overall" detail={
          <>
            <strong class="cpu-overall-load">{status() === 'live' ? usageLabel(latest()?.total) : '—'}</strong>
            <Show when={busiest()}>
              {cpu => (
                <span class="panel-section-detail">
                  Busiest · {processorLabel(cpu().index)} · {usageLabel(cpu().load)}
                </span>
              )}
            </Show>
          </>
        }>
          <div class="chart-body cpu-overall-plot">
            <div class="chart-scale" aria-hidden="true"><span>100%</span><span>0%</span></div>
            <HistoryGraph values={totalHistory()} length={historySeconds()} tone="cpu" />
            <Show when={status() !== 'live'}>
              <p class="chart-status" role="status">
                {status() === 'starting' ? 'Waiting for CPU samples…' : 'CPU data unavailable'}
              </p>
            </Show>
          </div>
        </PanelSection>
        <PanelSection scope="cpu" id="heatmap" label="Heatmap" class="cpu-heatmap-section" detail={
          <span class="panel-section-detail">
            {usage().length > 0 && `${usage().length} ${usage().length === 1 ? 'row' : 'rows'} · `}
            Last {formatWindow(historySeconds())}
          </span>
        }>
          <Show when={usage().length}>
            <div class="cpu-heatmap-scroll" tabIndex={0} role="region"
              aria-label="Heatmap, one row per logical processor, oldest to newest left to right">
              <div class="cpu-heatmap-axis" aria-hidden="true">
                <For each={heatmapLabels()}>
                  {index => <span style={{ top: `${index * CPU_HEATMAP_ROW_HEIGHT}px` }}>{processorLabel(index)}</span>}
                </For>
              </div>
              <ThermalHeatmap processorCount={usage().length} samples={visibleSamples()} length={historySeconds()} />
            </div>
          </Show>
        </PanelSection>
      </div>

      <PanelSection scope="cpu" id="processors" label="Logical processors" class="popup-frame activity-card cpu-processor-section" detail={
        <>
          <span class="cpu-processor-count">{usage().length || ''}</span>
          <span class="panel-section-detail">Last {formatWindow(historySeconds())} · 0–100%</span>
        </>
      }>
        <Show when={usage().length}>
          <ul class="cpu-processor-grid" classList={{ dense: usage().length > 16 }}
            aria-labelledby="cpu-processors-title" tabIndex={0}>
            <Index each={usage()}>
              {(load, index) => {
                const history = createMemo(() => visibleSamples().map(sample => sample.usage[index]));
                const peak = () => history().reduce<number>((max, value) => Math.max(max, value ?? 0), 0);
                return (
                  <li class="popup-frame cpu-processor-tile" classList={{ busiest: busiest()?.index === index }}
                    title={history().length ? `Peak ${peak()}% in the last ${formatWindow(historySeconds())}` : undefined}>
                    <div class="cpu-processor-label">
                      <span>{processorLabel(index)}</span>
                      <strong>{status() === 'live' ? usageLabel(load()) : '—'}</strong>
                    </div>
                    <HistoryGraph compact values={history()} length={historySeconds()} tone="cpu" />
                  </li>
                );
              }}
            </Index>
          </ul>
        </Show>
      </PanelSection>

      <PanelSection scope="cpu" id="processes" label="Top processes" class="popup-frame activity-card cpu-processes" detail={
        <span class="panel-section-detail">{historyWindow() ? `Last ${formatWindow(historyWindow())}` : ''}</span>
      }>
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
      </PanelSection>
    </main>
  );
}

render(() => <Panel />, document.getElementById('root')!);
