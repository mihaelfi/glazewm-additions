const paths = {
  storage: 'M3 6c0-1.7 4-3 9-3s9 1.3 9 3-4 3-9 3-9-1.3-9-3Zm0 0v12c0 1.7 4 3 9 3s9-1.3 9-3V6M3 12c0 1.7 4 3 9 3s9-1.3 9-3',
  cpu: 'M6 6h12v12H6zM9 9h6v6H9zM9 3v3m3-3v3m3-3v3m-6 12v3m3-3v3m3-3v3M3 9h3m-3 3h3m-3 3h3m12-6h3m-3 3h3m-3 3h3',
  ram: 'M3 7h18v10H3zM1 8v8m4 1v3m3-3v3m4-3v3m4-3v3m3-3v3M6 10h3v4H6zm5 0h3v4h-3zm5 0h3v4h-3z',
  gpu: 'M3 5v14m0-12h18v10h-3m-15 0h12v2h3v-2M11 9a3 3 0 1 1 0 6 3 3 0 0 1 0-6Zm5 3h3',
  clock: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18Zm0-14v5l3 2',
  timer: 'M12 21a7 7 0 1 0 0-14 7 7 0 0 0 0 14Zm0-14V3m-2 0h4m4 5 2-2m-8 5v4',
  calendar: 'M4 6h16v14H4zM4 10h16M8 4v4m8-4v4m-9 7h2m2 0h2m2 0h2',
  battery: 'M2 7h18v10H2zm18 3h2v4h-2M5 10h10v4H5z',
  network: 'M7 3v17m-4-4 4 4 4-4M17 21V4m-4 4 4-4 4 4',
  copy: 'M8 8h12v12H8zM16 8V4H4v12h4',
  check: 'm4 12 5 5L20 6',
  external: 'M14 4h6v6m0-6-9 9m9 1v6H4V4h6',
  thermometer: 'M9 14.5V5a3 3 0 1 1 6 0v9.5a5 5 0 1 1-6 0ZM12 8v9m0 2v.01',
};

export function BarIcon(props: { name: keyof typeof paths }) {
  return (
    <svg class={`bar-icon bar-icon-${props.name}`} viewBox="0 0 24 24" fill="none" stroke="currentColor"
      stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
      <path d={paths[props.name]} />
    </svg>
  );
}
