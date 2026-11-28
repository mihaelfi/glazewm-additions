import { createMemo, createSignal, Index, onCleanup, onMount, Show } from 'solid-js';
import { render } from 'solid-js/web';
import { useCloseActivityPanel } from './activity-panel';
import { BarIcon } from './bar-icon';
import { PanelHeader } from './panel-header';
import { PanelSection } from './panel-section';
import { formatWindow, useSettings } from './settings';
import { HistoryGraph } from './history-graph';
import { memoryPercent, requestMetrics, useMetricsFeed } from './metrics-feed';
import './widget.css';
import './activity-popup.css';
import './ram-panel.css';

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

const formatUsage = (value: number | undefined) => value === undefined ? '—' : `${Math.round(value)}%`;

function RamPanel() {
  const close = useCloseActivityPanel();
  const { settings } = useSettings();
  const historySeconds = () => settings().historyMinutes * 60;
  const { samples, status } = useMetricsFeed(false, historySeconds);
  const [processes, setProcesses] = createSignal<ProcessMemory[]>([]);
  const [processStatus, setProcessStatus] = createSignal<'loading' | 'live' | 'unavailable'>('loading');

  const memory = () => status() === 'live' ? samples().at(-1)?.memory : undefined;
  const usage = () => memoryPercent(memory());
  const history = createMemo(() =>
    status() === 'live' ? samples().map(sample => memoryPercent(sample.memory)) : [],
  );
  const peak = createMemo(() => history().reduce<number | undefined>(
    (highest, value) => value === undefined ? highest : Math.max(highest ?? 0, value),
    undefined,
  ));
  const largestWorkingSet = () => Math.max(1, processes()[0]?.bytes ?? 0);

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
    <main class="panel popup-frame activity-popup ram-panel" aria-labelledby="ram-title">
      <PanelHeader id="ram-title" title="RAM Activity" onClose={close} icon={<BarIcon name="ram" />} />

      <PanelSection scope="ram" id="memory" label="Memory Usage" class="popup-frame activity-card ram-memory" detail={
        <>
          <strong class="ram-usage">{formatUsage(usage())}</strong>
          <Show when={peak() !== undefined}>
            <span class="panel-section-detail">Peak {formatUsage(peak())}</span>
          </Show>
        </>
      }>
        <dl class="ram-capacity">
          <div><dt>Used</dt><dd>{formatMemory(memory()?.used)}</dd></div>
          <div><dt>Available</dt><dd>{formatMemory(memory()?.available)}</dd></div>
          <div><dt>Total</dt><dd>{formatMemory(memory()?.total)}</dd></div>
        </dl>
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
      </PanelSection>

      <PanelSection scope="ram" id="processes" label="Top 10 Processes" class="popup-frame activity-card ram-processes">
        <Show
          when={processStatus() === 'live' && processes().length}
          fallback={<p class="process-status" role="status">
            {processStatus() === 'loading' ? 'Loading processes…'
              : processStatus() === 'live' ? 'Waiting for process samples…'
              : 'Process memory data unavailable'}
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
                        style={{ '--share': `${100 * process().bytes / largestWorkingSet()}%` }}
                      />
                      {formatMemory(process().bytes)}
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

render(() => <RamPanel />, document.getElementById('root')!);
