import { DateTime } from 'luxon';
import { createSignal, onCleanup } from 'solid-js';
import { createProvider } from 'zebar';

export const DEFAULT_CLOCK_FORMAT = 'yyyy-MM-dd HH:mm';
export const MAX_CLOCK_FORMAT = 80;
export const CLOCK_FORMATS = [
  { label: 'Year-month-day · 24-hour', format: DEFAULT_CLOCK_FORMAT },
  { label: 'Weekday and month · 24-hour', format: 'ccc d LLL · HH:mm' },
  { label: 'Day/month/year · 24-hour', format: 'dd/MM/yyyy HH:mm' },
  { label: 'Time only · 24-hour', format: 'HH:mm' },
  { label: 'Time only · 12-hour', format: 'h:mm a' },
];

export function validClockFormat(format: string) {
  const trimmed = format.trim();
  return trimmed.length > 0 && trimmed.length <= MAX_CLOCK_FORMAT
    && !/\p{Cc}/u.test(trimmed) && (trimmed.match(/'/g)?.length ?? 0) % 2 === 0;
}

export const formatClock = (now: number, format: string) => DateTime.fromMillis(now).toFormat(format);

/** Use the existing Zebar date provider as the display clock, even while metrics are offline. */
export function useClockTime() {
  const provider = createProvider({ type: 'date', refreshInterval: 1000, formatting: '' });
  const [now, setNow] = createSignal(provider.output?.now ?? Date.now());
  provider.onOutput(output => setNow(output.now));
  onCleanup(() => void provider.stop());
  return now;
}
