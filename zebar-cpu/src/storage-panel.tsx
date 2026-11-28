import { createEffect, createMemo, For, Show } from 'solid-js';
import { render } from 'solid-js/web';
import { shellSpawn } from 'zebar';
import { useCloseActivityPanel } from './activity-panel';
import { BarIcon } from './bar-icon';
import { useDriveHistoryFeed } from './metrics-feed';
import { PanelHeader } from './panel-header';
import { PanelSection } from './panel-section';
import { formatWindow, showsDrive, useSettings } from './settings';
import { driveLevel, formatDriveRate, formatDriveSize, useDrives, type Drive } from './drives';
import { ThroughputGraph } from './throughput-graph';
import { throughputCeiling } from './throughput';
import './widget.css';
import './activity-popup.css';
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
    <main class="panel popup-frame activity-popup storage-panel" aria-labelledby="storage-title">
      <PanelHeader id="storage-title" title="Storage Activity" onClose={close} icon={<BarIcon name="storage" />} />

      <PanelSection scope="storage" id="capacity" label="Capacity" class="popup-frame activity-card storage-capacity">
        <dl class="storage-overview">
          <div><dt>Free</dt><dd>{drives().length ? formatDriveSize(totals().free) : '—'}</dd></div>
          <div><dt>Total</dt><dd>{drives().length ? formatDriveSize(totals().total) : '—'}</dd></div>
          <div>
            <dt>Fullest</dt>
            <Show when={fullest()} fallback={<dd>—</dd>}>
              {drive => <dd class={driveLevel(drive())}>{`${drive().letter}: ${Math.round(drive().used)}% used`}</dd>}
            </Show>
          </div>
        </dl>
      </PanelSection>

      <PanelSection scope="storage" id="drives" label="Drive Activity" class="popup-frame activity-card activity-scroll-card" detail={
        <span class="panel-section-detail">
          Last {formatWindow(historySeconds())} · <span class="throughput-scale">
            {status() === 'unavailable' ? 'Collector unavailable' : `${formatDriveRate(ceiling())} max`}
          </span>
        </span>
      }>
        <Show when={drives().length} fallback={<p class="process-status" role="status">Loading drives…</p>}>
          <ul class="drive-list" aria-label="Drives" tabIndex={0}>
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
      </PanelSection>
    </main>
  );
}

render(() => <StoragePanel />, document.getElementById('root')!);
