import { createMemo, createSignal, For } from 'solid-js';
import { createProvider, type Disk } from 'zebar';

// Free space changes slowly, so drives refresh once a minute rather than every second.
const REFRESH_MS = 60_000;
const WARN_USED = 85;
const FULL_USED = 95;
const gibibyte = 1024 ** 3;
const tebibyte = 1024 ** 4;

/** Binary units with GB/TB labels, the way Explorer shows drive sizes. */
export function formatDriveSize(bytes: number) {
  if (bytes >= tebibyte) return `${(bytes / tebibyte).toFixed(1)} TB`;
  if (bytes >= 10 * gibibyte) return `${Math.round(bytes / gibibyte)} GB`;
  return `${(bytes / gibibyte).toFixed(1)} GB`;
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

function toDrive(disk: Disk): Drive | null {
  const total = disk.totalSpace.bytes;
  if (!total) return null;
  const free = disk.availableSpace.bytes;
  const letter = disk.mountPoint.replace(/[:\\/]+$/, '');
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

/** One small vertical bar per drive, filled to its used space, with the letter underneath. */
export function DriveBars(props: { drives: Drive[] }) {
  return (
    <span class="drive-bars" aria-hidden="true">
      <For each={props.drives}>
        {drive => (
          <span class={`drive-bar ${driveLevel(drive)}`}>
            <i style={{ '--used': `${drive.used}%` }} />
            <small>{drive.letter}</small>
          </span>
        )}
      </For>
    </span>
  );
}
