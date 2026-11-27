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

export type DriveRates = Partial<Record<string, { read: number | null; write: number | null }>>;

export interface DriveHistorySample {
  sequence: number;
  driveRates: DriveRates;
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

interface MetricsHistory<Sample> {
  session: number;
  settingsVersion?: number;
  timer?: Timer | null;
  gpus?: GpuAdapter[];
  driveRates?: DriveRates;
  samples: Sample[];
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
  return useHistoryFeed<MetricsSample>(historySeconds, startCollector);
}

export function useDriveHistoryFeed(historySeconds: Accessor<number>) {
  return useHistoryFeed<DriveHistorySample>(historySeconds, false, '&view=drives');
}

function useHistoryFeed<Sample extends { sequence: number }>(
  historySeconds: Accessor<number>,
  startCollector: boolean,
  query = '',
) {
  const [samples, setSamples] = createSignal<Sample[]>([]);
  const [status, setStatus] = createSignal<FeedStatus>('starting');
  const [gpus, setGpus] = createSignal<GpuAdapter[]>([]);
  const [driveRates, setDriveRates] = createSignal<DriveRates>();
  const [settingsVersion, setSettingsVersion] = createSignal<number>();
  const [activeTimer, setActiveTimer] = createSignal<Timer | null>(null, {
    equals: (previous, next) => previous?.deadlineMs === next?.deadlineMs
      && previous?.alarmAtMs === next?.alarmAtMs && previous?.durationSeconds === next?.durationSeconds,
  });
  let session = 0;
  let cachedLimit = 0;
  let lastSequence = 0;
  let lastSampleAt = 0;
  let retryAt = 0;
  let timer: number | undefined;
  let stopped = false;

  async function poll() {
    const startedAt = performance.now();
    try {
      const limit = historySeconds();
      const requestSession = limit > cachedLimit ? 0 : session;
      const history = await requestMetrics<MetricsHistory<Sample>>(
        `/history?session=${requestSession}&after=${lastSequence}&limit=${limit}${query}`,
      );
      if (stopped) return;
      setSettingsVersion(history.settingsVersion);
      setActiveTimer(history.timer ?? null);

      cachedLimit = Math.min(limit, historySeconds());
      if (requestSession === 0 || history.session !== session) {
        session = history.session;
        setGpus(history.gpus ?? []);
        lastSequence = 0;
        lastSampleAt = 0;
        setSamples(history.samples.slice(-cachedLimit));
      } else if (history.samples.length) {
        setSamples(previous => [...previous, ...history.samples].slice(-cachedLimit));
      }

      const newest = history.samples.at(-1);
      if (newest) {
        lastSequence = newest.sequence;
        lastSampleAt = performance.now();
        setDriveRates(history.driveRates);
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

  createEffect(on(historySeconds, seconds => {
    cachedLimit = Math.min(cachedLimit, seconds);
    setSamples(current => current.slice(-seconds));
  }, { defer: true }));

  onMount(() => void poll());
  onCleanup(() => {
    stopped = true;
    window.clearTimeout(timer);
  });

  return {
    samples,
    status,
    gpus,
    settingsVersion,
    activeTimer,
    driveRates: () => status() === 'live' ? driveRates() : undefined,
  };
}
