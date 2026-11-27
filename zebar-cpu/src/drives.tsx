import { createMemo, createSignal, For, Match, Switch } from 'solid-js';
import { createProvider, type Disk } from 'zebar';
import type { DriveRates } from './metrics-feed';
import type { DriveActivityMode } from './settings';

// Free space changes slowly, so drives refresh once a minute rather than every second.
const REFRESH_MS = 60_000;
const WARN_USED = 85;
const FULL_USED = 95;
const gibibyte = 1024 ** 3;
const tebibyte = 1024 ** 4;
const rateUnits = ['B/s', 'KB/s', 'MB/s', 'GB/s', 'TB/s'];

/** Binary units with GB/TB labels, the way Explorer shows drive sizes. */
export function formatDriveSize(bytes: number) {
  if (bytes >= tebibyte) return `${(bytes / tebibyte).toFixed(1)} TB`;
  if (bytes >= 10 * gibibyte) return `${Math.round(bytes / gibibyte)} GB`;
  return `${(bytes / gibibyte).toFixed(1)} GB`;
}

function driveRateParts(bytes: number | null | undefined) {
  if (bytes == null) return { value: '—', unit: '' };
  let rate = bytes;
  let unit = 0;
  while (rate >= 999.5 && unit < rateUnits.length - 1) {
    rate /= 1000;
    unit++;
  }
  const value = rate >= 999.5 ? '999+' : String(Number(rate.toPrecision(3)));
  return { value, unit: rateUnits[unit] };
}

export function formatDriveRate(bytes: number | null | undefined) {
  const { value, unit } = driveRateParts(bytes);
  return unit ? `${value} ${unit}` : value;
}

export interface Drive {
  /** `C` */
  letter: string;
  /** `C:\` */
  root: string;
  label: string;
  fileSystem: string;
  removable: boolean;
  free: number;
  total: number;
  used: number;
}

export type DriveLevel = 'ok' | 'warn' | 'full';

export const driveLevel = (drive: Drive): DriveLevel =>
  drive.used >= FULL_USED ? 'full' : drive.used >= WARN_USED ? 'warn' : 'ok';

export const describeDrive = (drive: Drive) =>
  `${drive.letter}: ${formatDriveSize(drive.free)} free of ${formatDriveSize(drive.total)} (${Math.round(drive.used)}% used)`;

export function describeDriveActivity(rates: DriveRates[string]) {
  return `Read ${formatDriveRate(rates?.read)} · Write ${formatDriveRate(rates?.write)}`;
}

function toDrive(disk: Disk): Drive | null {
  const total = disk.totalSpace.bytes;
  if (!total) return null;
  const free = disk.availableSpace.bytes;
  const letter = disk.mountPoint.replace(/[:\\/]+$/, '').toUpperCase();
  return {
    letter,
    root: `${letter}:\\`,
    label: disk.name?.trim() || 'Local Disk',
    fileSystem: disk.fileSystem,
    removable: disk.isRemovable,
    free,
    total,
    used: (1 - free / total) * 100,
  };
}

/** Drives from Zebar's disk provider, sorted by letter. */
export function useDrives() {
  const disk = createProvider({ type: 'disk', refreshInterval: REFRESH_MS });
  const [output, setOutput] = createSignal(disk.output);
  disk.onOutput(setOutput);

  return createMemo(() =>
    (output()?.disks ?? [])
      .map(toDrive)
      .filter((drive): drive is Drive => drive !== null)
      .sort((left, right) => left.letter.localeCompare(right.letter)),
  );
}

function DriveRate(props: { direction: 'R' | 'W'; bytes: number | null | undefined }) {
  const parts = createMemo(() => driveRateParts(props.bytes));
  return (
    <span class="drive-rate" classList={{ idle: !props.bytes }}>
      <b class={props.direction === 'R' ? 'drive-read' : 'drive-write'}>{props.direction}</b>
      <span class="drive-rate-value">{parts().value}</span>
      <span class="drive-rate-unit">{parts().unit}</span>
    </span>
  );
}

/** Capacity and the selected activity display for each drive. */
export function DriveBars(props: {
  drives: Drive[];
  rates?: DriveRates;
  mode: (letter: string) => DriveActivityMode;
}) {
  return (
    <span class="drive-bars" aria-hidden="true">
      <For each={props.drives}>
        {drive => {
          const mode = () => props.mode(drive.letter);
          const read = createMemo(() => props.rates?.[drive.letter]?.read);
          const write = createMemo(() => props.rates?.[drive.letter]?.write);
          return (
            <span class={`drive-bar ${driveLevel(drive)}`} classList={{ 'with-activity': mode() !== 'none' }}>
              <span class="drive-bar-capacity">
                <i class="drive-bar-meter" style={{ '--used': `${drive.used}%` }} />
                <small>{drive.letter}</small>
              </span>
              <Switch>
                <Match when={mode() === 'marks'}>
                  <span class="drive-marks">
                    <span class="drive-read" classList={{ active: !!read(), unavailable: read() == null }} />
                    <span class="drive-write" classList={{ active: !!write(), unavailable: write() == null }} />
                  </span>
                </Match>
                <Match when={mode() === 'rates'}>
                  <span class="drive-bar-rates">
                    <DriveRate direction="R" bytes={read()} />
                    <DriveRate direction="W" bytes={write()} />
                  </span>
                </Match>
              </Switch>
            </span>
          );
        }}
      </For>
    </span>
  );
}
