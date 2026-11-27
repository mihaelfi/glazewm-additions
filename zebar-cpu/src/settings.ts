import { createSignal, onMount } from 'solid-js';
import { DEFAULT_CLOCK_FORMAT, validClockFormat } from './clock';
import { requestMetrics } from './metrics-feed';

/**
 * Widget settings. The collector keeps them in `settings.json` in the widget
 * pack folder (see `native/settings.rs`), so every bar and popup shares one copy.
 */
export interface Settings {
  /** How far back graphs, heatmaps and process averages look. */
  historyMinutes: number;
  bar: { storage: boolean; ram: boolean; cpu: boolean; gpu: boolean };
  /** Drive letters to show. `null` shows every drive, including ones added later. */
  drives: string[] | null;
  /** Name of the GPU the bar follows. `null` picks the one with the most video memory. */
  gpuAdapter: string | null;
  clockFormat: string;
  repeatTimerChime: boolean;
}

export type BarItem = keyof Settings['bar'];

export const HISTORY_CHOICES = [1, 5, 10, 15];

export const DEFAULT_SETTINGS: Settings = {
  historyMinutes: 1,
  bar: { storage: true, ram: true, cpu: true, gpu: true },
  drives: null,
  gpuAdapter: null,
  clockFormat: DEFAULT_CLOCK_FORMAT,
  repeatTimerChime: true,
};

interface SettingsSnapshot {
  version: number;
  settings: Settings;
}

/** `60 s`, `5 min`. */
export const formatWindow = (seconds: number) =>
  seconds >= 60 && seconds % 60 === 0 ? `${seconds / 60} min` : `${seconds} s`;

export const showsDrive = (settings: Settings, letter: string) =>
  settings.drives === null || settings.drives.includes(letter);

/** Index of the GPU the bar follows in the collector's adapter list. */
export function gpuIndex(settings: Settings, adapters: { name: string }[]) {
  const index = settings.gpuAdapter === null ? -1 : adapters.findIndex(gpu => gpu.name === settings.gpuAdapter);
  return Math.max(0, index);
}

/**
 * Current settings. Long-lived widgets pass the `settingsVersion` the collector
 * reports with each history poll to `sync`, which refetches when it changes.
 */
export function useSettings() {
  const [settings, setSettings] = createSignal<Settings>(DEFAULT_SETTINGS);
  const [loaded, setLoaded] = createSignal(false);
  let version = 0;
  let fetching = false;

  function apply(snapshot: SettingsSnapshot) {
    version = snapshot.version;
    const format = snapshot.settings.clockFormat ?? '';
    setSettings({ ...DEFAULT_SETTINGS, ...snapshot.settings, clockFormat: validClockFormat(format) ? format : DEFAULT_CLOCK_FORMAT });
  }

  async function refresh() {
    if (fetching) return;
    fetching = true;
    try {
      const snapshot = await requestMetrics<SettingsSnapshot>('/settings');
      apply(snapshot);
      setLoaded(true);
    } catch {
      // Keep the current settings; the next sync tries again.
    } finally {
      fetching = false;
    }
  }

  function sync(collectorVersion: number | undefined) {
    if (collectorVersion !== undefined && collectorVersion !== version) void refresh();
  }

  async function save(next: Settings) {
    const previous = settings();
    setSettings(next);
    try {
      const response = await fetch('http://127.0.0.1:6128/settings/save', {
        method: 'POST',
        body: JSON.stringify(next),
        signal: AbortSignal.timeout(2000),
      });
      if (!response.ok) throw new Error(`Saving settings failed with ${response.status}`);
      const snapshot: SettingsSnapshot = await response.json();
      apply(snapshot);
    } catch (error) {
      setSettings(previous);
      throw error;
    }
  }

  onMount(() => void refresh());

  return { settings, loaded, sync, save, refresh };
}
