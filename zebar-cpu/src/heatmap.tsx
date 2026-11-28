import { createEffect } from 'solid-js';
import { useHistoryCanvas } from './history-graph';
import type { MetricsSample } from './metrics-feed';

/** Longer windows are merged into this many columns, keeping each column's peak. */
const MAX_COLUMNS = 120;

const stops = [
  [37, 55, 104], // Idle blue
  [82, 66, 157],
  [152, 74, 177],
  [218, 78, 130],
  [255, 81, 93], // Full-load red
];

function palette(values: number[][]) {
  return Array.from({ length: 101 }, (_, load) => {
    const position = load * (values.length - 1) / 100;
    const index = Math.min(Math.floor(position), values.length - 2);
    const fraction = position - index;
    return values[index].map((channel, component) =>
      Math.round(channel + (values[index + 1][component] - channel) * fraction),
    );
  });
}

const colors = palette(stops);
const thermalColors = palette([
  [13, 43, 82],
  [29, 98, 192],
  [57, 168, 213],
  [244, 189, 61],
  [249, 119, 39],
  [246, 66, 57],
]).map(channels => `rgb(${channels.join(' ')})`);

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

/** Keep square cells readable on a small bar, regardless of logical processor count. */
export function BarHeatmap(props: { samples: MetricsSample[]; length: number }) {
  const canvas = useHistoryCanvas((context, width, height) => {
    const length = Math.max(1, props.length);
    const cores = Math.max(1, props.samples.at(-1)?.usage.length ?? 1);
    const pitch = Math.max(3, Math.floor(height / 6));
    const rows = Math.min(cores, Math.max(1, Math.floor(height / pitch)));
    const columns = Math.min(length, Math.max(1, Math.floor(width / pitch)));
    const x = Math.floor((width - columns * pitch) / 2);
    const y = Math.floor((height - rows * pitch) / 2);
    const peaks = peakCells(props.samples, length, columns, rows, cores);
    peaks.forEach((load, cell) => {
      if (load < 0) return;
      context.fillStyle = thermalColors[load] ?? thermalColors[0];
      context.fillRect(x + (cell % columns) * pitch, y + Math.floor(cell / columns) * pitch, pitch - 1, pitch - 1);
    });
  });
  return <canvas ref={canvas} class="heatmap bar-heatmap" aria-hidden="true" />;
}

/** One row per logical processor, newest on the right, covering `length` seconds. */
export function Heatmap(props: { samples: MetricsSample[]; length: number }) {
  let canvas!: HTMLCanvasElement;

  createEffect(() => {
    const samples = props.samples;
    const length = Math.max(1, props.length);
    const columns = Math.min(length, MAX_COLUMNS);
    const rows = samples.at(-1)?.usage.length ?? 1;
    if (canvas.width !== columns) canvas.width = columns;
    if (canvas.height !== rows) canvas.height = rows;
    const context = canvas.getContext('2d');
    if (!context) return;

    const peaks = peakCells(samples, length, columns, rows, rows);
    const pixels = context.createImageData(columns, rows);
    peaks.forEach((load, cell) => {
      if (load < 0) return;
      pixels.data.set(colors[load] ?? colors[0], cell * 4);
      pixels.data[cell * 4 + 3] = 255;
    });
    context.putImageData(pixels, 0, 0);
  });

  return <canvas ref={canvas} class="heatmap" width={MAX_COLUMNS} height={1} aria-hidden="true" />;
}
