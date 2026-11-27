import { createEffect, createMemo, For, Show } from 'solid-js';
import { render } from 'solid-js/web';
import { shellSpawn } from 'zebar';
import { useCloseActivityPanel } from './activity-panel';
import { useDriveHistoryFeed } from './metrics-feed';
import { PanelHeader } from './panel-header';
import { formatWindow, showsDrive, useSettings } from './settings';
import { driveLevel, formatDriveRate, formatDriveSize, useDrives, type Drive } from './drives';
import { ThroughputGraph } from './throughput-graph';
import { throughputCeiling } from './throughput';
import './widget.css';
import './storage.css';

// Must match the program allowed for this widget in zpack.json.
const EXPLORER = 'C:\\Windows\\explorer.exe';

function StoragePanel() {
  const close = useCloseActivityPanel();
  const { settings, sync } = useSettings();
  const historySeconds = () => settings().historyMinutes * 60;
  const { samples, status, driveRates, settingsVersion } = useDriveHistoryFeed(historySeconds);
  createEffect(() => sync(settingsVersion()));
  const allDrives = useDrives();
  const drives = createMemo(() => allDrives().filter(drive => showsDrive(settings(), drive.letter)));
  const totals = createMemo(() => drives().reduce(
    (sum, drive) => ({ free: sum.free + drive.free, total: sum.total + drive.total }),
    { free: 0, total: 0 },
  ));
  const fullest = createMemo(() =>
    drives().reduce<Drive | undefined>((worst, drive) => !worst || drive.used > worst.used ? drive : worst, undefined),
  );
  const ceiling = createMemo(() => {
    let peak = 1000;
    const visible = drives();
    for (const sample of samples()) {
      for (const drive of visible) {
        const rate = sample.driveRates?.[drive.letter];
        peak = Math.max(peak, rate?.read ?? 0, rate?.write ?? 0);
      }
    }
    return throughputCeiling(peak);
  });

  function openDrive(drive: Drive) {
    void shellSpawn(EXPLORER, [drive.root])
      .then(close)
      .catch(error => console.error(`Could not open ${drive.root}`, error));
  }

  return (
    <main class="panel storage-panel popup-frame" aria-labelledby="storage-title">
      <PanelHeader id="storage-title" title="Storage" onClose={close} />

      <div class="storage-overview popup-frame">
        <div><strong>{drives().length ? formatDriveSize(totals().free) : '—'}</strong> free</div>
        <div><strong>{drives().length ? formatDriveSize(totals().total) : '—'}</strong> total</div>
        <div>
          <Show when={fullest()} fallback={<strong>—</strong>}>
            {drive => (
              <strong class={driveLevel(drive())}>{`${drive().letter}: ${Math.round(drive().used)}%`}</strong>
            )}
          </Show> used
        </div>
      </div>

      <Show when={drives().length} fallback={<p class="process-status" role="status">Loading drives…</p>}>
        <div class="throughput-legend" aria-label="Shared graph scale">
          <span>Last {formatWindow(historySeconds())}</span>
          <span class="throughput-key drive-read">Read</span>
          <span class="throughput-key drive-write">Write</span>
          <span class="throughput-scale">
            {status() === 'unavailable' ? 'Collector unavailable' : `${formatDriveRate(ceiling())} max`}
          </span>
        </div>
        <ul class="drive-list" aria-label="Drives">
          <For each={drives()}>
            {drive => (
              <li>
                <button
                  class={`drive-row popup-frame ${driveLevel(drive)}`}
                  type="button"
                  title={`Open ${drive.root} in File Explorer`}
                  onClick={() => openDrive(drive)}
                >
                  <span class="drive-tile-header">
                    <b class="drive-letter">{drive.letter}:</b>
                    <strong class="drive-name" title={drive.label}>{drive.label}</strong>
                    <span class="drive-used">{Math.round(drive.used)}% used</span>
                    <span class="drive-free"><strong>{formatDriveSize(drive.free)}</strong> free of {formatDriveSize(drive.total)}</span>
                    <span class="drive-filesystem" title={drive.removable ? `${drive.fileSystem} · removable` : undefined}>{drive.fileSystem}</span>
                  </span>
                  <i class="drive-row-meter" aria-hidden="true" style={{ '--used': `${drive.used}%` }} />
                  <ThroughputGraph
                    class="drive-history-graph" samples={samples()} length={historySeconds()} ceiling={ceiling()}
                    series={[
                      { color: '--drive-read', value: sample => sample.driveRates?.[drive.letter]?.read },
                      { color: '--drive-write', value: sample => sample.driveRates?.[drive.letter]?.write },
                    ]}
                  />
                  <span class="drive-row-activity">
                    <span><span class="drive-read">Read</span> <strong>{formatDriveRate(driveRates()?.[drive.letter]?.read)}</strong></span>
                    <span><span class="drive-write">Write</span> <strong>{formatDriveRate(driveRates()?.[drive.letter]?.write)}</strong></span>
                  </span>
                </button>
              </li>
            )}
          </For>
        </ul>
      </Show>
    </main>
  );
}

render(() => <StoragePanel />, document.getElementById('root')!);
