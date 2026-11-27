import { createMemo, createSignal, For, Show } from 'solid-js';
import { CLOCK_FORMATS, formatClock, MAX_CLOCK_FORMAT, validClockFormat } from './clock';

export function ClockSettings(props: {
  format: string;
  onSave: (format: string) => Promise<boolean>;
  repeatTimerChime: boolean;
  onRepeatTimerChimeChange: (repeat: boolean) => void;
}) {
  const [choice, setChoice] = createSignal(CLOCK_FORMATS.find(item => item.format === props.format)?.format ?? 'custom');
  const [draft, setDraft] = createSignal(props.format);
  const format = () => (choice() === 'custom' ? draft() : choice()).trim();
  const valid = createMemo(() => validClockFormat(format()));
  const preview = createMemo(() => valid() ? formatClock(Date.now(), format()) : '—');

  async function select(value: string) {
    setChoice(value);
    if (value === 'custom') {
      setDraft(props.format);
    } else if (!(await props.onSave(value))) {
      setChoice(CLOCK_FORMATS.find(item => item.format === props.format)?.format ?? 'custom');
    }
  }

  return (
    <section class="settings-section" aria-labelledby="clock-setting">
      <div class="clock-setting-heading">
        <h2 id="clock-setting">Clock</h2>
        <div class="clock-help">
          <button class="clock-control clock-help-button" type="button" aria-label="Date and time format syntax"
            aria-describedby="clock-format-help">?</button>
          <div class="clock-help-tip" id="clock-format-help" role="tooltip">
            <strong>Date and time tokens are case-sensitive.</strong>
            <p><code>yyyy</code> year · <code>MM</code> month · <code>dd</code> day</p>
            <p><code>ccc</code> weekday · <code>LLL</code> short month</p>
            <p><code>HH</code> 24-hour · <code>h</code> 12-hour · <code>mm</code> minutes · <code>ss</code> seconds · <code>a</code> AM/PM</p>
            <p>Wrap literal words in single quotes, as in <code>HH:mm 'local'</code>.</p>
          </div>
        </div>
      </div>
      <label class="clock-format-field">Bar date and time format
        <select value={choice()} onChange={event => void select(event.currentTarget.value)}>
          <For each={CLOCK_FORMATS}>{item => <option value={item.format}>{item.label}</option>}</For>
          <option value="custom">Custom</option>
        </select>
      </label>
      <Show when={choice() === 'custom'}>
        <form class="clock-format-custom" onSubmit={event => {
          event.preventDefault();
          if (valid() && format() !== props.format) void props.onSave(format());
        }}>
          <label class="clock-format-field">Custom format
            <input value={draft()} maxlength={MAX_CLOCK_FORMAT} spellcheck={false} aria-invalid={!valid()}
              onInput={event => setDraft(event.currentTarget.value)} />
          </label>
          <button class="clock-control clock-primary" type="submit" disabled={!valid() || format() === props.format}>
            Apply
          </button>
        </form>
        <Show when={!valid()}><p class="clock-error">Enter a format with matching single quotes.</p></Show>
      </Show>
      <div class="clock-preview"><span>Preview</span><output>{preview()}</output></div>
      <label class="check-row">
        <input type="checkbox" checked={props.repeatTimerChime}
          onChange={event => props.onRepeatTimerChimeChange(event.currentTarget.checked)} />
        Repeat timer chime until dismissed
      </label>
    </section>
  );
}
