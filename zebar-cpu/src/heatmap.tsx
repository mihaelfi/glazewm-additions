import { createEffect } from 'solid-js';
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

const colors = Array.from({ length: 101 }, (_, load) => {
  const position = load * (stops.length - 1) / 100;
  const index = Math.min(Math.floor(position), stops.length - 2);
  const fraction = position - index;
  return stops[index].map((channel, component) =>
    Math.round(channel + (stops[index + 1][component] - channel) * fraction),
  );
});

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

    // Peak load per cell, so a one-second spike survives merging.
    const peaks = new Int16Array(columns * rows).fill(-1);
    const start = length - samples.length;
    samples.forEach((sample, index) => {
      const column = Math.floor((start + index) * columns / length);
      if (column < 0) return;
      sample.usage.forEach((load, row) => {
        if (row >= rows) return;
        const cell = row * columns + column;
        if (load > peaks[cell]) peaks[cell] = load;
      });
    });

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
