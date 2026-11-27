import { availableMonitors, currentMonitor } from '@tauri-apps/api/window';
import { onCleanup, onMount } from 'solid-js';
import { currentWidget, startWidget } from 'zebar';

export const panelHeights = {
  'cpu-activity': 780,
  'ram-activity': 620,
  'gpu-activity': 700,
  'storage-activity': 470,
  clock: 492,
  settings: 660,
};
export type PanelName = keyof typeof panelHeights;

// Widgets share an origin, so the bar and its panels can talk over a channel.
type PanelMessage = { type: 'closed' | 'close'; name: string };
const CHANNEL = 'cpu-activity-panels';
/** A panel that closed this recently was closed by the click that is now landing on the bar. */
const CLOSED_BY_CLICK_MS = 600;

/** Opens a popup at the top right of the monitor this widget is on. */
export async function openActivityPanel(name: PanelName) {
  const [monitor, monitors] = await Promise.all([currentMonitor(), availableMonitors()]);
  monitors.sort((a, b) => a.position.x - b.position.x || a.position.y - b.position.y);
  const index = monitors.findIndex(candidate =>
    candidate.position.x === monitor?.position.x && candidate.position.y === monitor.position.y,
  );
  const preferredHeight = panelHeights[name];
  const preferredWidth = name === 'clock' ? 384 : 576;
  const width = monitor ? Math.min(preferredWidth, Math.floor(monitor.size.width / monitor.scaleFactor) - 24) : preferredWidth;
  const height = monitor
    ? Math.min(preferredHeight, Math.floor(monitor.size.height / monitor.scaleFactor) - 68)
    : preferredHeight;

  return startWidget(name, {
    anchor: 'top_right',
    offsetX: '-12px',
    offsetY: '50px',
    width: `${width}px`,
    height: `${height}px`,
    monitorSelection: index >= 0 ? { type: 'index', match: index } : { type: 'primary' },
    dockToEdge: { enabled: false, edge: null, windowMargin: '0px' },
  });
}

/**
 * Bar side: clicking a metric opens its panel, and clicking it again closes it.
 *
 * Pressing the mouse on the bar takes focus from an open panel, which closes it
 * on blur before the click arrives. So the state is read on pointerdown, and a
 * panel that closed in the last moment counts as open.
 */
export function usePanelToggle() {
  const channel = new BroadcastChannel(CHANNEL);
  const open = new Set<PanelName>();
  const closedAt = new Map<PanelName, number>();
  let pressed: { name: PanelName; wasOpen: boolean } | undefined;
  let opening = false;

  channel.onmessage = ({ data }: MessageEvent<PanelMessage>) => {
    const name = data.name as PanelName;
    if (data.type === 'closed' && open.delete(name)) closedAt.set(name, performance.now());
  };
  onCleanup(() => channel.close());

  const isOpen = (name: PanelName) =>
    open.has(name) || performance.now() - (closedAt.get(name) ?? -Infinity) < CLOSED_BY_CLICK_MS;

  function press(name: PanelName) {
    pressed = { name, wasOpen: isOpen(name) };
  }

  async function toggle(name: PanelName) {
    // Keyboard activation has no pointerdown, so fall back to the current state.
    const wasOpen = pressed?.name === name ? pressed.wasOpen : open.has(name);
    pressed = undefined;
    if (wasOpen) {
      closedAt.delete(name);
      if (open.has(name)) channel.postMessage({ type: 'close', name } satisfies PanelMessage);
      return;
    }
    if (opening) return;
    opening = true;
    try {
      await openActivityPanel(name);
      open.add(name);
    } finally {
      opening = false;
    }
  }

  return { press, toggle };
}

/** Panel side: close on Esc, on blur, or when the bar asks, and tell the bar. */
export function useCloseActivityPanel() {
  const widget = currentWidget();
  const channel = new BroadcastChannel(CHANNEL);
  let closing = false;

  function close() {
    if (closing) return;
    closing = true;
    channel.postMessage({ type: 'closed', name: widget.name } satisfies PanelMessage);
    void widget.close().catch(error => {
      closing = false;
      console.error('Could not close activity panel', error);
    });
  }

  channel.onmessage = ({ data }: MessageEvent<PanelMessage>) => {
    if (data.type === 'close' && data.name === widget.name) close();
  };

  onMount(() => {
    const handleKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') close();
    };
    window.addEventListener('keydown', handleKey);
    onCleanup(() => {
      window.removeEventListener('keydown', handleKey);
      channel.close();
    });
    void widget.tauriWindow.listen('tauri://blur', close).catch(console.error);
  });

  return close;
}
