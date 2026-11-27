import { createEffect, createSignal, on, onCleanup, onMount, type Accessor } from 'solid-js';
import { currentWidget, shellSpawn } from 'zebar';
import type { Timer } from './timer';

/** Seconds of history used when no setting is available. */
export const DEFAULT_HISTORY_SECONDS = 60;

export interface MemorySample {
  used: number;
  available: number;
  total: number;
}

/** Engine groups, in the order the collector reports `GpuSample.engines`. */
export const GPU_ENGINES = ['3D', 'Copy', 'Video Decode', 'Video Encode', 'Compute'] as const;

export interface GpuAdapter {
  name: string;
  dedicatedTotal: number;
  sharedTotal: number;
}

export interface GpuSample {
  usage: number;
  engines: number[];
  dedicatedUsed: number;
  sharedUsed: number;
  temperature?: number;
}

export interface MetricsSample {
  sequence: number;
  total: number;
  usage: number[];
  memory?: MemorySample;
  /** One entry per adapter in `gpus`; the first is the largest discrete card. */
  gpu?: GpuSample[];
}

export function memoryPercent(memory?: MemorySample): number | undefined {
  return memory?.total ? memory.used / memory.total * 100 : undefined;
}

interface MetricsHistory {
  session: number;
  settingsVersion?: number;
  timer?: Timer | null;
  gpus?: GpuAdapter[];
  samples: MetricsSample[];
}

type FeedStatus = 'starting' | 'live' | 'unavailable';

export async function requestMetrics<T>(path: string): Promise<T> {
  // Zebar's service worker caches cross-origin GET responses.
  const response = await fetch(`http://127.0.0.1:6128${path}`, {
    method: 'POST',
    signal: AbortSignal.timeout(900),
  });
  if (!response.ok) throw new Error(`Metrics collector returned ${response.status}`);
  return response.json();
}

/**
 * Polls the collector once a second and keeps the last `historySeconds` samples.
 * The bar passes `startCollector` so it can start the collector when it isn't running.
 */
export function useMetricsFeed(
  startCollector = false,
  historySeconds: Accessor<number> = () => DEFAULT_HISTORY_SECONDS,
) {
  const [samples, setSamples] = createSignal<MetricsSample[]>([]);
  const [status, setStatus] = createSignal<FeedStatus>('starting');
  const [gpus, setGpus] = createSignal<GpuAdapter[]>([]);
  const [settingsVersion, setSettingsVersion] = createSignal<number>();
  const [activeTimer, setActiveTimer] = createSignal<Timer | null>(null, {
    equals: (previous, next) => previous?.deadlineMs === next?.deadlineMs
      && previous?.alarmAtMs === next?.alarmAtMs && previous?.durationSeconds === next?.durationSeconds,
  });
  let session = 0;
  let lastSequence = 0;
  let lastSampleAt = 0;
  let retryAt = 0;
  let timer: number | undefined;
  let stopped = false;

  async function poll() {
    const startedAt = performance.now();
    try {
      const limit = historySeconds();
      const history = await requestMetrics<MetricsHistory>(
        `/history?session=${session}&after=${lastSequence}&limit=${limit}`,
      );
      if (stopped) return;
      setSettingsVersion(history.settingsVersion);
      setActiveTimer(history.timer ?? null);

      if (history.session !== session) {
        session = history.session;
        setGpus(history.gpus ?? []);
        lastSequence = 0;
        lastSampleAt = 0;
        setSamples(history.samples);
      } else if (history.samples.length) {
        setSamples(previous => [...previous, ...history.samples].slice(-limit));
      }

      const newest = history.samples.at(-1);
      if (newest) {
        lastSequence = newest.sequence;
        lastSampleAt = performance.now();
        setStatus('live');
      } else if (lastSampleAt === 0 || performance.now() - lastSampleAt > 3000) {
        setStatus(lastSampleAt === 0 ? 'starting' : 'unavailable');
      }
    } catch {
      if (stopped) return;
      if (lastSampleAt || startedAt > 4000) setStatus('unavailable');
      if (startCollector && startedAt >= retryAt) {
        retryAt = startedAt + 6000;
        try {
          const packPath = currentWidget().htmlPath.replace(/[\\/]dist[\\/][^\\/]+$/, '');
          const executable = `${packPath}\\zebar-cpu-collector.exe`;
          await shellSpawn(executable, []);
        } catch (error) {
          console.error('Could not start the metrics collector', error);
          setStatus('unavailable');
        }
      }
    } finally {
      if (!stopped) {
        timer = window.setTimeout(poll, Math.max(100, 1000 - (performance.now() - startedAt)));
      }
    }
  }

  // A longer window needs older samples, so fetch the whole window again.
  createEffect(on(historySeconds, (seconds, previous) => {
    if (previous !== undefined && seconds > previous) session = 0;
    else setSamples(current => current.slice(-seconds));
  }, { defer: true }));

  onMount(() => void poll());
  onCleanup(() => {
    stopped = true;
    window.clearTimeout(timer);
  });

  return { samples, status, gpus, settingsVersion, activeTimer };
}
