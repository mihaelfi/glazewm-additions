import { createMemo, createSignal, For, onCleanup, onMount, Show, type Accessor } from 'solid-js';
import { render } from 'solid-js/web';
import { useCloseActivityPanel } from './activity-panel';
import { useClockTime } from './clock';
import { PanelHeader } from './panel-header';
import {
  clearTimer, elapsedPercent, isOverdue, readTimer, startTimer, timerChanged, timerLabel, TIMER_CHANNEL, type Timer,
} from './timer';
import './widget.css';
import './clock.css';

const locale = Intl.DateTimeFormat().resolvedOptions().locale;
const timeFormat = new Intl.DateTimeFormat(locale, { hour: 'numeric', minute: '2-digit', second: '2-digit' });
const dateFormat = new Intl.DateTimeFormat(locale, { dateStyle: 'full', calendar: 'gregory' });
const monthFormat = new Intl.DateTimeFormat(locale, { month: 'long', year: 'numeric', calendar: 'gregory' });
const weekdayFormat = new Intl.DateTimeFormat(locale, { weekday: 'short' });
const weekLocale = new Intl.Locale(locale) as Intl.Locale & {
  weekInfo?: { firstDay: number };
  getWeekInfo?: () => { firstDay: number };
};
const firstDay = (weekLocale.getWeekInfo?.().firstDay ?? weekLocale.weekInfo?.firstDay ?? 7) % 7;
const weekdays = Array.from({ length: 7 }, (_, day) => weekdayFormat.format(new Date(2024, 0, 7 + firstDay + day)));
const localDay = (date: Date) => new Date(date.getFullYear(), date.getMonth(), date.getDate(), 12).getTime();

function Calendar(props: { now: Accessor<number> }) {
  const today = createMemo(() => localDay(new Date(props.now())));
  const currentMonth = createMemo(() => {
    const date = new Date(today());
    return new Date(date.getFullYear(), date.getMonth(), 1, 12).getTime();
  });
  const [browsedMonth, setBrowsedMonth] = createSignal<number>();
  const month = () => browsedMonth() ?? currentMonth();
  const dates = createMemo(() => {
    const date = new Date(month());
    const offset = (date.getDay() - firstDay + 7) % 7;
    return Array.from({ length: 6 }, (_, week) =>
      Array.from({ length: 7 }, (_, day) => new Date(date.getFullYear(), date.getMonth(), 1 - offset + week * 7 + day, 12)),
    );
  });

  function moveMonth(step: number) {
    const date = new Date(month());
    setBrowsedMonth(new Date(date.getFullYear(), date.getMonth() + step, 1, 12).getTime());
  }

  return (
    <div class="calendar-card">
      <div class="calendar-head">
        <h2 id="calendar-month" aria-live="polite">{monthFormat.format(month())}</h2>
        <div class="calendar-controls">
          <button class="clock-control" aria-disabled={month() === currentMonth()}
            onClick={() => setBrowsedMonth(undefined)}>Today</button>
          <button class="clock-control calendar-arrow" aria-label="Previous month" onClick={() => moveMonth(-1)}>‹</button>
          <button class="clock-control calendar-arrow" aria-label="Next month" onClick={() => moveMonth(1)}>›</button>
        </div>
      </div>
      <table class="calendar-grid" aria-labelledby="calendar-month">
        <thead><tr><For each={weekdays}>{name => <th scope="col">{name}</th>}</For></tr></thead>
        <tbody>
          <For each={dates()}>{week => (
            <tr><For each={week}>{date => (
              <td classList={{ 'other-month': date.getMonth() !== new Date(month()).getMonth() }}>
                <time dateTime={formatCalendarDate(date)} aria-current={date.getTime() === today() ? 'date' : undefined}>
                  {date.getDate()}
                </time>
              </td>
            )}</For></tr>
          )}</For>
        </tbody>
      </table>
    </div>
  );
}

function formatCalendarDate(date: Date) {
  const pad = (value: number) => String(value).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

function TimerPanel(props: { now: Accessor<number> }) {
  const [timer, setTimer] = createSignal<Timer | null>();
  const [error, setError] = createSignal('');
  const [busy, setBusy] = createSignal(false);
  const [hours, setHours] = createSignal('');
  const [minutes, setMinutes] = createSignal('');
  let readRevision = 0;

  async function refresh() {
    const revision = ++readRevision;
    try {
      const next = await readTimer();
      if (revision === readRevision) setTimer(next);
    } catch {
      if (revision === readRevision) setError('Could not load the timer.');
    }
  }

  onMount(() => {
    const channel = new BroadcastChannel(TIMER_CHANNEL);
    channel.onmessage = () => void refresh();
    void refresh();
    onCleanup(() => channel.close());
  });

  async function update(action: () => Promise<Timer | null>) {
    if (busy()) return;
    setError('');
    setBusy(true);
    ++readRevision;
    try {
      setTimer(await action());
      timerChanged();
    } catch {
      await refresh();
      setError('Could not update the timer. Try again.');
    } finally {
      setBusy(false);
    }
  }

  const customSeconds = () => (Number(hours()) * 60 + Number(minutes())) * 60;

  return (
    <div class="timer-card">
      <Show when={timer() !== undefined} fallback={
        <Show when={!error()} fallback={<button class="clock-control" onClick={() => { setError(''); void refresh(); }}>Retry</button>}>
          <p class="clock-muted" role="status">Loading timer…</p>
        </Show>
      }>
        <Show when={timer()} fallback={
          <>
            <h2>Quick timers</h2>
            <div class="timer-presets">
              <For each={[5, 10, 15]}>{minutes => (
                <button class="clock-control" disabled={busy()} onClick={() => void update(() => startTimer(minutes * 60))}>
                  {minutes} min
                </button>
              )}</For>
            </div>
            <form class="timer-form" onSubmit={event => {
              event.preventDefault();
              if (customSeconds() > 0) void update(() => startTimer(customSeconds()));
            }}>
              <h2>Custom duration</h2>
              <div class="timer-fields">
                <label>Hours<input type="number" min="0" max="999" placeholder="0" value={hours()}
                  onInput={event => setHours(event.currentTarget.value)} /></label>
                <label>Minutes<input type="number" min="0" max="59" placeholder="0" value={minutes()}
                  onInput={event => setMinutes(event.currentTarget.value)} /></label>
                <button class="clock-control clock-primary" type="submit" disabled={busy() || customSeconds() < 60}>Start timer</button>
              </div>
            </form>
          </>
        }>{active => {
          const overdue = createMemo(() => isOverdue(active(), props.now()));
          return (
            <div class="timer-running" classList={{ 'timer-finished': overdue() }}>
              <h2 role="status">{overdue() ? 'Timer finished' : 'Time remaining'}</h2>
              <strong>{timerLabel(active(), props.now())}</strong>
              <progress value={elapsedPercent(active(), props.now())} max="100" aria-label="Timer progress" />
              <p class="clock-muted">{overdue() ? 'Ended at' : 'Ends at'} {timeFormat.format(active().deadlineMs)} · {dateFormat.format(active().deadlineMs)}</p>
              <button class="clock-control" disabled={busy()} onClick={() => void update(() => clearTimer(active()))}>
                {overdue() ? 'Dismiss timer' : 'Cancel timer'}
              </button>
            </div>
          );
        }}</Show>
      </Show>
      <Show when={error()}><p class="clock-error" role="alert">{error()}</p></Show>
    </div>
  );
}

const tabs = ['Calendar', 'Timer'] as const;

function ClockPanel() {
  const close = useCloseActivityPanel();
  const now = useClockTime();
  const [tab, setTab] = createSignal<(typeof tabs)[number]>('Calendar');

  return (
    <main class="panel clock-panel" aria-labelledby="clock-title">
      <PanelHeader id="clock-title" title="Clock" onClose={close} />
      <div class="clock-hero">
        <time dateTime={new Date(now()).toISOString()}>{timeFormat.format(now())}</time>
        <p>{dateFormat.format(now())}</p>
      </div>
      <div class="clock-tabs" role="tablist" aria-label="Clock views" onKeyDown={event => {
        if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
        event.preventDefault();
        const next = event.key === 'Home' || (event.key !== 'End' && tab() === 'Timer') ? 'Calendar' : 'Timer';
        setTab(next);
        document.getElementById(`clock-tab-${next}`)?.focus();
      }}>
        <For each={tabs}>{name => (
          <button class="clock-control" role="tab" id={`clock-tab-${name}`} aria-controls={`clock-view-${name}`}
            aria-selected={tab() === name} tabIndex={tab() === name ? 0 : -1} onClick={() => setTab(name)}>{name}</button>
        )}</For>
      </div>
      <section id="clock-view-Calendar" role="tabpanel" aria-labelledby="clock-tab-Calendar" hidden={tab() !== 'Calendar'}>
        <Calendar now={now} />
      </section>
      <section id="clock-view-Timer" role="tabpanel" aria-labelledby="clock-tab-Timer" hidden={tab() !== 'Timer'}>
        <TimerPanel now={now} />
      </section>
    </main>
  );
}

render(() => <ClockPanel />, document.getElementById('root')!);
