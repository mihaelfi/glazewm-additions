import { cpuHeatColor } from './cpu-thermal';
import { useHistoryCanvas } from './history-graph';
import type { MetricsSample } from './metrics-feed';

export const CPU_HEATMAP_ROW_HEIGHT = 3;

/** Peak load per cell, preserving spikes when time or CPU rows are grouped. */
function peakCells(samples: MetricsSample[], length: number, columns: number, rows: number, cores: number) {
  const peaks = new Int16Array(columns * rows).fill(-1);
  const start = length - samples.length;
  samples.forEach((sample, index) => {
    const column = Math.floor((start + index) * columns / length);
    if (column < 0) return;
    sample.usage.forEach((load, core) => {
      if (core >= cores) return;
      const row = Math.floor(core * rows / cores);
      const cell = row * columns + column;
      if (load > peaks[cell]) peaks[cell] = load;
    });
  });
  return peaks;
}

/** Keep cells square as processor count and graph dimensions change. */
export function ThermalHeatmap(props: { samples: MetricsSample[]; length: number; processorCount?: number }) {
  const rowCount = () => Math.max(1, props.processorCount ?? 1);
  const canvas = useHistoryCanvas((context, width, height) => {
    const length = Math.max(1, props.length);
    const full = props.processorCount !== undefined;
    const cores = full ? rowCount() : Math.max(1, props.samples.at(-1)?.usage.length ?? 1);
    const rowPitch = full ? CPU_HEATMAP_ROW_HEIGHT : Math.max(3, Math.floor(height / 6));
    const rows = full ? cores : Math.min(cores, Math.max(1, Math.floor(height / rowPitch)));
    const columns = Math.min(length, Math.max(1, Math.floor(width / rowPitch)));
    const columnPitch = full ? width / columns : rowPitch;
    const x = full ? 0 : Math.floor((width - columns * columnPitch) / 2);
    const y = full ? 0 : Math.floor((height - rows * rowPitch) / 2);
    const peaks = peakCells(props.samples, length, columns, rows, cores);
    peaks.forEach((load, cell) => {
      if (load < 0) return;
      context.fillStyle = cpuHeatColor(load);
      const start = Math.floor(x + (cell % columns) * columnPitch);
      const end = Math.floor(x + (cell % columns + 1) * columnPitch);
      context.fillRect(start, y + Math.floor(cell / columns) * rowPitch, end - start - 1, rowPitch - 1);
    });
  });
  return (
    <canvas ref={canvas} class={props.processorCount === undefined ? 'heatmap bar-heatmap' : 'cpu-heatmap'}
      style={props.processorCount === undefined ? undefined : { height: `${rowCount() * CPU_HEATMAP_ROW_HEIGHT}px` }}
      aria-hidden="true" />
  );
}
