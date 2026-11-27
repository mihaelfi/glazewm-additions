import { requestMetrics } from './metrics-feed';

export interface Timer {
  deadlineMs: number;
  durationSeconds: number;
  alarmAtMs: number | null;
}

export const TIMER_CHANNEL = 'cpu-activity-timer';

export const readTimer = () => requestMetrics<Timer | null>('/timer');
export const startTimer = (seconds: number) => requestMetrics<Timer | null>(`/timer/start?seconds=${seconds}`);
export const clearTimer = (timer: Timer) => requestMetrics<Timer | null>(`/timer/clear?deadline=${timer.deadlineMs}`);

export function timerChanged() {
  const channel = new BroadcastChannel(TIMER_CHANNEL);
  channel.postMessage('changed');
  channel.close();
}

export const isOverdue = (timer: Timer, now: number) => timer.alarmAtMs !== null || now >= timer.deadlineMs;

export const elapsedPercent = (timer: Timer, now: number) =>
  isOverdue(timer, now) ? 100 : Math.max(0, 100 - (timer.deadlineMs - now) / (timer.durationSeconds * 10));

export function timerLabel(timer: Timer, now: number) {
  const overdue = isOverdue(timer, now);
  const delta = timer.deadlineMs - now;
  const seconds = Math.max(0, overdue ? Math.floor(-delta / 1000) : Math.ceil(delta / 1000));
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor(seconds / 60) % 60;
  const pad = (value: number) => String(value).padStart(2, '0');
  const duration = hours ? `${hours}:${pad(minutes)}:${pad(seconds % 60)}` : `${pad(minutes)}:${pad(seconds % 60)}`;
  return `${overdue ? '−' : ''}${duration}`;
}
