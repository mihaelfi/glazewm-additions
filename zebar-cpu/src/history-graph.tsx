import { createEffect, createSignal, onCleanup, onMount } from 'solid-js';
import { cpuTraceColor, cpuTraceStops } from './cpu-thermal';

const colors = {
  cpu: { grid: '#2a3657', fillTop: '#398dd54d', fillBottom: '#398dd508' },
  ram: { grid: '#28484e', line: '#62d4cc', dot: '#b8fff3', fillTop: '#62d4cc66', fillBottom: '#62d4cc0d' },
  gpu: { grid: '#374d37', line: '#9bd66f', dot: '#d6f7bd', fillTop: '#8fcf6366', fillBottom: '#8fcf630b' },
};

export function useHistoryCanvas(draw: (context: CanvasRenderingContext2D, width: number, height: number) => void) {
  let canvas!: HTMLCanvasElement;
  const [bounds, setBounds] = createSignal({ width: 0, height: 0 });

  onMount(() => {
    const observer = new ResizeObserver(([entry]) => {
      setBounds({ width: entry.contentRect.width, height: entry.contentRect.height });
    });
    observer.observe(canvas);
    onCleanup(() => observer.disconnect());
  });

  createEffect(() => {
    const { width, height } = bounds();
    if (!width || !height) return;

    const scale = window.devicePixelRatio || 1;
    const pixelWidth = Math.round(width * scale);
    const pixelHeight = Math.round(height * scale);
    if (canvas.width !== pixelWidth) canvas.width = pixelWidth;
    if (canvas.height !== pixelHeight) canvas.height = pixelHeight;
    const context = canvas.getContext('2d');
    if (!context) return;
    context.setTransform(scale, 0, 0, scale, 0, 0);
    context.clearRect(0, 0, width, height);
    draw(context, width, height);
  });

  return (element: HTMLCanvasElement) => { canvas = element; };
}

export function HistoryGraph(props: {
  values: (number | undefined)[];
  /** Seconds the x axis covers; values fill it from the right. */
  length: number;
  tone: keyof typeof colors;
  compact?: boolean;
}) {
  const canvas = useHistoryCanvas((context, width, height) => {
    const values = props.values;
    const margin = props.compact ? 2 : 4;
    const bottom = height - margin;
    const span = width - margin * 2;
    const tone = props.tone;
    const palette = colors[tone];
    if (!props.compact) {
      context.strokeStyle = palette.grid;
      context.lineWidth = 1;
      for (const y of [margin, height / 2, bottom]) {
        context.beginPath();
        context.moveTo(margin, y);
        context.lineTo(width - margin, y);
        context.stroke();
      }
    }

    const line = new Path2D();
    const length = Math.max(2, props.length);
    const start = length - values.length;
    let firstX: number | undefined;
    let lastPoint: { x: number; y: number; value: number } | undefined;
    values.forEach((value, index) => {
      if (value === undefined) return;
      const x = margin + span * (start + index) / (length - 1);
      const y = bottom - (bottom - margin) * value / 100;
      if (lastPoint) {
        line.lineTo(x, y);
      } else {
        firstX = x;
        line.moveTo(x, y);
      }
      lastPoint = { x, y, value };
    });
    if (!lastPoint || firstX === undefined) return;

    const area = new Path2D(line);
    area.lineTo(lastPoint.x, bottom);
    area.lineTo(firstX, bottom);
    area.closePath();
    const gradient = context.createLinearGradient(0, margin, 0, bottom);
    gradient.addColorStop(0, palette.fillTop);
    gradient.addColorStop(1, palette.fillBottom);
    context.fillStyle = gradient;
    context.fill(area);

    if (tone === 'cpu') {
      const heat = context.createLinearGradient(0, bottom, 0, margin);
      for (const { position, color } of cpuTraceStops) heat.addColorStop(position, color);
      context.strokeStyle = heat;
    } else {
      context.strokeStyle = colors[tone].line;
    }
    context.lineWidth = props.compact ? 1.5 : 2;
    context.lineJoin = 'round';
    context.stroke(line);
    context.fillStyle = tone === 'cpu' ? cpuTraceColor(lastPoint.value) : colors[tone].dot;
    context.beginPath();
    context.arc(lastPoint.x, lastPoint.y, props.compact ? 1.3 : 3, 0, 2 * Math.PI);
    context.fill();
  });

  return <canvas ref={canvas} class={props.compact ? 'sparkline' : 'usage-graph'} aria-hidden="true" />;
}
