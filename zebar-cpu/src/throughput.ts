export function throughputParts(value: number | null | undefined, units: readonly string[]) {
  if (value == null) return { value: '—', unit: '' };
  let rate = value;
  let unit = 0;
  while (rate >= 999.5 && unit < units.length - 1) {
    rate /= 1000;
    unit++;
  }
  return {
    value: rate >= 999.5 ? '999+' : String(Number(rate.toPrecision(3))),
    unit: units[unit],
  };
}

export function withUnit(parts: { value: string; unit: string }) {
  return parts.unit ? `${parts.value} ${parts.unit}` : parts.value;
}

/** Round up to a readable shared chart scale. */
export function throughputCeiling(peak: number) {
  const magnitude = 10 ** Math.floor(Math.log10(peak));
  const step = [1, 2, 5, 10].find(value => peak <= value * magnitude) ?? 10;
  return step * magnitude;
}
