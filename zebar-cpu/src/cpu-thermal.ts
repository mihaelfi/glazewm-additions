const stops = [
  [13, 43, 82],
  [29, 98, 192],
  [57, 168, 213],
  [244, 189, 61],
  [249, 119, 39],
  [246, 66, 57],
];

const rgb = (channels: number[]) => `rgb(${channels.join(' ')})`;

const colors = Array.from({ length: 101 }, (_, load) => {
  const position = load * (stops.length - 1) / 100;
  const index = Math.min(Math.floor(position), stops.length - 2);
  const fraction = position - index;
  return rgb(stops[index].map((channel, component) =>
    Math.round(channel + (stops[index + 1][component] - channel) * fraction),
  ));
});

export const cpuHeatColor = (load: number) => colors[Math.round(load)] ?? colors[0];

// Floor the trace at the second blue stop for contrast against the plot surface.
export const cpuTraceColor = (load: number) => cpuHeatColor(Math.max(20, load));
export const cpuTraceStops = stops.map((_, index) => {
  const position = index / (stops.length - 1);
  return { position, color: cpuTraceColor(position * 100) };
});
