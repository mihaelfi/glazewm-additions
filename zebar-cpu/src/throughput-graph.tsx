import { useHistoryCanvas } from './history-graph';
export function ThroughputGraph<Sample extends { sequence: number }>(props: {
  class: string;
  samples: readonly Sample[];
  length: number;
  ceiling: number;
  series: readonly { color: string; value: (sample: Sample) => number | null | undefined }[];
}) {
  const canvas = useHistoryCanvas((context, width, height) => {
    const left = 1;
    const right = width - 1;
    const top = 1;
    const bottom = height - 1;
    const grid = new Path2D();
    for (const y of [top, height / 2, bottom]) {
      grid.moveTo(left, y);
      grid.lineTo(right, y);
    }
    for (const portion of [0, 0.25, 0.5, 0.75, 1]) {
      const x = left + (right - left) * portion;
      grid.moveTo(x, top);
      grid.lineTo(x, bottom);
    }
    const style = getComputedStyle(context.canvas);
    context.strokeStyle = style.getPropertyValue('--throughput-grid').trim();
    context.lineWidth = 1;
    context.stroke(grid);

    const latest = props.samples.at(-1)?.sequence;
    if (latest === undefined) return;
    const steps = Math.max(1, props.length - 1);
    context.lineWidth = 1.3;
    context.lineJoin = 'round';
    context.lineCap = 'round';
    for (const series of props.series) {
      const trace = new Path2D();
      let previous: number | undefined;
      for (const sample of props.samples) {
        const age = latest - sample.sequence;
        const rate = series.value(sample);
        if (age > steps || rate == null) {
          previous = undefined;
          continue;
        }
        const x = right - (right - left) * age / steps;
        const y = bottom - (bottom - top) * rate / props.ceiling;
        if (previous === sample.sequence - 1) {
          trace.lineTo(x, y);
        } else {
          trace.moveTo(x, y);
          trace.lineTo(x + 0.01, y);
        }
        previous = sample.sequence;
      }
      context.strokeStyle = style.getPropertyValue(series.color).trim();
      context.stroke(trace);
    }
  });

  return <canvas ref={canvas} class={props.class} aria-hidden="true" />;
}
