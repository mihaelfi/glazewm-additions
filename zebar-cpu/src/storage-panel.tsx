import { createMemo, For, Show } from 'solid-js';
import { render } from 'solid-js/web';
import { shellSpawn } from 'zebar';
import { useCloseActivityPanel } from './activity-panel';
import { useMetricsFeed } from './metrics-feed';
import { PanelHeader } from './panel-header';
import { showsDrive, useSettings } from './settings';
import { driveLevel, formatDriveRate, formatDriveSize, useDrives, type Drive } from './drives';
import './widget.css';

// Must match the program allowed for this widget in zpack.json.
const EXPLORER = 'C:\\Windows\\explorer.exe';

function StoragePanel() {
  const close = useCloseActivityPanel();
  const { settings } = useSettings();
  const { driveRates } = useMetricsFeed(false, () => 1);
  const allDrives = useDrives();
  const drives = createMemo(() => allDrives().filter(drive => showsDrive(settings(), drive.letter)));
  const totals = createMemo(() => drives().reduce(
    (sum, drive) => ({ free: sum.free + drive.free, total: sum.total + drive.total }),
    { free: 0, total: 0 },
  ));
  const fullest = createMemo(() =>
    drives().reduce<Drive | undefined>((worst, drive) => !worst || drive.used > worst.used ? drive : worst, undefined),
  );

  function openDrive(drive: Drive) {
    void shellSpawn(EXPLORER, [drive.root])
      .then(close)
      .catch(error => console.error(`Could not open ${drive.root}`, error));
  }

  return (
    <main class="panel storage-panel" aria-labelledby="storage-title">
      <PanelHeader id="storage-title" title="Storage" onClose={close} />

      <div class="overview storage-overview">
        <div><span>Free</span><strong>{drives().length ? formatDriveSize(totals().free) : '—'}</strong></div>
        <div><span>Capacity</span><strong>{drives().length ? formatDriveSize(totals().total) : '—'}</strong></div>
        <div>
          <span>Fullest</span>
          <Show when={fullest()} fallback={<strong>—</strong>}>
            {drive => (
              <strong class={driveLevel(drive())}>{`${drive().letter}: · ${Math.round(drive().used)}%`}</strong>
            )}
          </Show>
        </div>
      </div>

      <Show when={drives().length} fallback={<p class="process-status" role="status">Loading drives…</p>}>
        <ul class="drive-list" aria-label="Drives">
          <For each={drives()}>
            {drive => (
              <li>
                <button
                  class={`drive-row ${driveLevel(drive)}`}
                  title={`Open ${drive.root} in File Explorer`}
                  onClick={() => openDrive(drive)}
                >
                  <b class="drive-letter">{drive.letter}</b>
                  <span class="drive-name">
                    {drive.label}
                    <small>{drive.fileSystem}{drive.removable ? ' · removable' : ''}</small>
                  </span>
                  <span class="drive-free"><strong>{formatDriveSize(drive.free)}</strong> free of {formatDriveSize(drive.total)}</span>
                  <i class="drive-row-meter" aria-hidden="true" style={{ '--used': `${drive.used}%` }} />
                  <span class="drive-row-activity">
                    <span><span class="drive-read">Read</span> <strong>{formatDriveRate(driveRates()?.[drive.letter]?.read)}</strong></span>
                    <span><span class="drive-write">Write</span> <strong>{formatDriveRate(driveRates()?.[drive.letter]?.write)}</strong></span>
                  </span>
                  <span class="drive-used">{Math.round(drive.used)}% used</span>
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
