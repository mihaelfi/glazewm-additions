import { createSignal, onMount } from 'solid-js';
import { DEFAULT_CLOCK_FORMAT, validClockFormat } from './clock';
import { requestMetrics } from './metrics-feed';

export type DriveActivityMode = 'none' | 'marks' | 'rates';

export const DEFAULT_BAR_ORDER = ['storage', 'ram', 'cpu', 'gpu', 'tilingDirection', 'weather', 'language', 'network'] as const;
export type BarLayoutItem = typeof DEFAULT_BAR_ORDER[number];
export const PANEL_BAR_ITEMS = ['storage', 'ram', 'cpu', 'gpu', 'network'] as const;
export type BarItem = typeof PANEL_BAR_ITEMS[number] | 'weather' | 'language';
export const LANGUAGE_DISPLAY_MODES = ['code', 'codeAndFlag', 'flag'] as const;
export type LanguageDisplay = typeof LANGUAGE_DISPLAY_MODES[number];

/**
 * Widget settings. The collector keeps them in `settings.json` in the widget
 * pack folder (see `native/settings.rs`), so every bar and popup shares one copy.
 */
export interface Settings {
  /** How far back graphs, heatmaps and process averages look. */
  historyMinutes: number;
  barHeight: number;
  bar: Record<BarItem, boolean>;
  barOrder: readonly BarLayoutItem[];
  languageDisplay: LanguageDisplay;
  /** Drive letters to show. `null` shows every drive, including ones added later. */
  drives: string[] | null;
  /** Activity shown on the bar. Missing letters show capacity only. */
  driveActivity: Partial<Record<string, DriveActivityMode>>;
  /** Name of the GPU the bar follows. `null` picks the one with the most video memory. */
  gpuAdapter: string | null;
  clockFormat: string;
  repeatTimerChime: boolean;
}

export const HISTORY_CHOICES = [1, 5, 10, 15];
export const MIN_BAR_HEIGHT = 34;
export const MAX_BAR_HEIGHT = 52;
export const DEFAULT_BAR_HEIGHT = 38;

export const DEFAULT_SETTINGS: Settings = {
  historyMinutes: 1,
  barHeight: DEFAULT_BAR_HEIGHT,
  bar: { storage: true, ram: true, cpu: true, gpu: true, network: true, weather: true, language: true },
  barOrder: DEFAULT_BAR_ORDER,
  languageDisplay: 'codeAndFlag',
  drives: null,
  driveActivity: {},
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

export const driveActivityMode = (settings: Settings, letter: string): DriveActivityMode =>
  settings.driveActivity[letter] ?? 'none';

function normalizeBarOrder(order: readonly BarLayoutItem[] | undefined) {
  const known = Array.isArray(order)
    ? order.filter(item => DEFAULT_BAR_ORDER.includes(item))
    : [];
  return [...new Set([...known, ...DEFAULT_BAR_ORDER])];
}

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
    setSettings({
      ...DEFAULT_SETTINGS,
      ...snapshot.settings,
      barHeight: Math.max(MIN_BAR_HEIGHT, Math.min(MAX_BAR_HEIGHT, snapshot.settings.barHeight ?? DEFAULT_BAR_HEIGHT)),
      bar: { ...DEFAULT_SETTINGS.bar, ...snapshot.settings.bar },
      barOrder: normalizeBarOrder(snapshot.settings.barOrder),
      languageDisplay: LANGUAGE_DISPLAY_MODES.includes(snapshot.settings.languageDisplay)
        ? snapshot.settings.languageDisplay : DEFAULT_SETTINGS.languageDisplay,
      clockFormat: validClockFormat(format) ? format : DEFAULT_CLOCK_FORMAT,
    });
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
