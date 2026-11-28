import { createSignal, For, onMount, Show } from 'solid-js';
import { render } from 'solid-js/web';
import { useCloseActivityPanel } from './activity-panel';
import { ClockSettings } from './clock-settings';
import { formatDriveSize, useDrives } from './drives';
import { requestMetrics, type GpuAdapter } from './metrics-feed';
import { type BarItem, driveActivityMode, type DriveActivityMode, HISTORY_CHOICES, MAX_BAR_HEIGHT, MIN_BAR_HEIGHT, type Settings, useSettings } from './settings';
import './widget.css';
import './clock.css';

const barItems: { key: BarItem; label: string }[] = [
  { key: 'storage', label: 'Storage' },
  { key: 'ram', label: 'RAM' },
  { key: 'cpu', label: 'CPU' },
  { key: 'gpu', label: 'GPU' },
  { key: 'network', label: 'Network' },
];

function SettingsPanel() {
  const close = useCloseActivityPanel();
  const { settings, loaded, save } = useSettings();
  const drives = useDrives();
  const [gpus, setGpus] = createSignal<GpuAdapter[]>([]);
  const [error, setError] = createSignal('');
  const [saving, setSaving] = createSignal(false);

  onMount(() => {
    requestMetrics<{ gpus?: GpuAdapter[] }>('/history?limit=0')
      .then(history => setGpus(history.gpus ?? []))
      .catch(() => setGpus([]));
  });

  async function update(change: Partial<Settings>) {
    if (saving()) return false;
    setSaving(true);
    setError('');
    try {
      await save({ ...settings(), ...change });
      return true;
    } catch {
      setError("Couldn't save your changes. Try again.");
      return false;
    } finally {
      setSaving(false);
    }
  }

  const barCount = () => barItems.filter(item => settings().bar[item.key]).length;
  const driveShown = (letter: string) => settings().drives?.includes(letter) ?? true;
  const shownDriveCount = () => drives().filter(drive => driveShown(drive.letter)).length;

  function toggleDrive(letter: string, shown: boolean) {
    const letters = drives().map(drive => drive.letter).filter(other => other === letter ? shown : driveShown(other));
    // Every drive selected means "all", so drives added later appear too.
    update({ drives: letters.length === drives().length ? null : letters });
  }

  function changeDriveActivity(letter: string, mode: DriveActivityMode) {
    const driveActivity = { ...settings().driveActivity };
    if (mode === 'none') delete driveActivity[letter];
    else driveActivity[letter] = mode;
    void update({ driveActivity });
  }

  return (
    <main class="panel settings-panel" aria-labelledby="settings-title">
      <header class="panel-header">
        <h1 id="settings-title">Settings</h1>
        <button class="close-panel" aria-label="Close settings" title="Close (Esc)" onClick={close}>×</button>
      </header>

      <Show when={loaded()} fallback={<p class="process-status" role="status">Loading settings…</p>}>
        <Show when={error()}><p class="settings-note clock-error" role="alert">{error()}</p></Show>
        <fieldset class="settings-fields" disabled={saving()} aria-label="Widget settings">
        <ClockSettings format={settings().clockFormat} onSave={clockFormat => update({ clockFormat })}
          repeatTimerChime={settings().repeatTimerChime} onRepeatTimerChimeChange={repeatTimerChime => void update({ repeatTimerChime })} />
        <section class="settings-section" aria-labelledby="history-setting">
          <h2 id="history-setting">History</h2>
          <p>How far back graphs, heatmaps and process averages look.</p>
          <div class="segmented" role="radiogroup" aria-labelledby="history-setting">
            <For each={HISTORY_CHOICES}>
              {minutes => (
                <button
                  role="radio"
                  aria-checked={settings().historyMinutes === minutes}
                  onClick={() => update({ historyMinutes: minutes })}
                >
                  {minutes} min
                </button>
              )}
            </For>
          </div>
        </section>

        <section class="settings-section" aria-labelledby="bar-height-setting">
          <h2 id="bar-height-setting">Bar height</h2>
          <label class="settings-bar-height">
            <select aria-label="Bar height" value={settings().barHeight}
              onChange={event => update({ barHeight: Number(event.currentTarget.value) })}>
              <For each={Array.from({ length: MAX_BAR_HEIGHT - MIN_BAR_HEIGHT + 1 }, (_, index) => index + MIN_BAR_HEIGHT)}>
                {height => <option value={height}>{height} px</option>}
              </For>
            </select>
          </label>
          <p>GlazeWM’s top gap controls the space above tiled windows.</p>
        </section>
        <section class="settings-section" aria-labelledby="bar-setting">
          <h2 id="bar-setting">Show on the bar</h2>
          <div class="check-grid">
            <For each={barItems}>
              {item => {
                const checked = () => settings().bar[item.key];
                // The bar items open the popups, so one has to stay.
                const locked = () => checked() && barCount() === 1;
                return (
                  <label class="check-row" title={locked() ? 'Keep at least one item on the bar' : undefined}>
                    <input
                      type="checkbox"
                      checked={checked()}
                      disabled={locked()}
                      onChange={event => update({ bar: { ...settings().bar, [item.key]: event.currentTarget.checked } })}
                    />
                    {item.label}
                  </label>
                );
              }}
            </For>
          </div>
        </section>

        <section class="settings-section" aria-labelledby="drives-setting">
          <h2 id="drives-setting">Drives</h2>
          <p>Choose which drives to show and how each displays activity on the bar. The popup shows speeds for all selected drives.</p>
          <Show when={drives().length} fallback={<p>Looking for drives…</p>}>
            <div class="check-list">
              <For each={drives()}>
                {drive => {
                  const locked = () => driveShown(drive.letter) && shownDriveCount() === 1;
                  return (
                    <div class="settings-drive">
                      <label class="check-row" title={locked() ? 'Keep at least one drive' : undefined}>
                        <input
                          type="checkbox"
                          checked={driveShown(drive.letter)}
                          disabled={locked()}
                          onChange={event => toggleDrive(drive.letter, event.currentTarget.checked)}
                        />
                        <b>{drive.letter}:</b>
                        <span class="settings-drive-name" title={drive.label}>{drive.label}</span>
                        <small>{formatDriveSize(drive.total)}</small>
                      </label>
                      <select
                        aria-label={`${drive.letter}: activity on the bar`}
                        value={driveActivityMode(settings(), drive.letter)}
                        disabled={!driveShown(drive.letter)}
                        onChange={event => changeDriveActivity(drive.letter, event.currentTarget.value as DriveActivityMode)}
                      >
                        <option value="none">None</option>
                        <option value="marks">Activity marks</option>
                        <option value="rates">Live rates</option>
                      </select>
                    </div>
                  );
                }}
              </For>
            </div>
          </Show>
        </section>

        <section class="settings-section" aria-labelledby="gpu-setting">
          <h2 id="gpu-setting">GPU on the bar</h2>
          <Show
            when={gpus().length > 1}
            fallback={<p>{gpus().length ? `Only one GPU found: ${gpus()[0].name}.` : 'No GPU data available.'}</p>}
          >
            <div class="check-list" role="radiogroup" aria-labelledby="gpu-setting">
              <label class="check-row">
                <input
                  type="radio"
                  name="gpu"
                  checked={settings().gpuAdapter === null}
                  onChange={() => update({ gpuAdapter: null })}
                />
                Automatic <small>most video memory ({gpus()[0].name})</small>
              </label>
              <For each={gpus()}>
                {gpu => (
                  <label class="check-row">
                    <input
                      type="radio"
                      name="gpu"
                      checked={settings().gpuAdapter === gpu.name}
                      onChange={() => update({ gpuAdapter: gpu.name })}
                    />
                    {gpu.name}
                  </label>
                )}
              </For>
            </div>
          </Show>
        </section>

        </fieldset>
      </Show>
    </main>
  );
}

render(() => <SettingsPanel />, document.getElementById('root')!);
