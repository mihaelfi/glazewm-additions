import { PhysicalSize } from '@tauri-apps/api/dpi';
import { createEffect, onCleanup, type Accessor } from 'solid-js';
import { currentWidget } from 'zebar';

/** Resize the actual WebView window, preserving the native width of this monitor's bar. */
export function useBarWindowHeight(height: Accessor<number>) {
  let stopped = false;
  let applying = false;
  let wanted = height();

  async function resize() {
    if (applying || stopped) return;
    applying = true;
    let target = wanted;
    try {
      const bar = currentWidget().tauriWindow;
      const [size, scale] = await Promise.all([bar.innerSize(), bar.scaleFactor()]);
      target = wanted;
      const pixels = Math.round(target * scale);
      if (!stopped && pixels > 0 && size.width > 0 && size.height !== pixels) {
        await bar.setSize(new PhysicalSize(size.width, pixels));
      }
    } catch (error) {
      if (!stopped) console.error('Could not resize the Zebar bar', error);
    } finally {
      applying = false;
      if (target !== wanted) void resize();
    }
  }

  createEffect(() => {
    wanted = height();
    void resize();
  });
  onCleanup(() => { stopped = true; });
}
