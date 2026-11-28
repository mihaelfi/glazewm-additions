const flags = import.meta.glob<string>('../node_modules/flag-icons/flags/4x3/??.svg', {
  eager: true,
  import: 'default',
  query: '?url&no-inline',
});

export function keyboardFlag(localeTag: string) {
  try {
    const locale = new Intl.Locale(localeTag);
    const region = (locale.region ?? locale.maximize().region)?.toLowerCase();
    return region ? flags[`../node_modules/flag-icons/flags/4x3/${region}.svg`] : undefined;
  } catch {
    return undefined;
  }
}
