import { createMemo, createSignal, Match, onCleanup, Switch } from 'solid-js';
import { createProvider, type NetworkOutput } from 'zebar';
import type { NetworkAdapter, NetworkKind } from './metrics-feed';
import { throughputParts, withUnit } from './throughput';

const units = ['b/s', 'kb/s', 'Mb/s', 'Gb/s', 'Tb/s'];
const networkParts = (bytes: number | null | undefined) => throughputParts(bytes == null ? bytes : bytes * 8, units);
export const formatNetworkRate = (bytes: number | null | undefined) => withUnit(networkParts(bytes));
export const networkKindLabel = (kind: NetworkKind) => kind === 'wifi' ? 'Wi-Fi' : 'Ethernet';

export function useNetworkDetails() {
  // Use Zebar's existing network provider for connection metadata. Throughput
  // comes from the shared collector, including when both views are open.
  const provider = createProvider({ type: 'network' });
  const [details, setDetails] = createSignal(provider.output);
  provider.onOutput(setDetails);
  onCleanup(() => { void provider.stop(); });
  return details;
}

function matchesAdapter(adapter: NetworkAdapter, detail: NetworkOutput['defaultInterface']) {
  if (!detail) return false;
  const guid = (name: string) => name.replace(/[{}]/g, '').toLowerCase();
  return guid(adapter.id) === guid(detail.name)
    || adapter.name === detail.friendlyName
    || adapter.name === detail.name;
}

export function mainNetworkAdapter(adapters: readonly NetworkAdapter[], details: NetworkOutput | null) {
  return adapters.find(adapter => matchesAdapter(adapter, details?.defaultInterface ?? null))
    ?? adapters.find(adapter => adapter.kind === 'ethernet')
    ?? adapters[0];
}

export function adapterSsid(adapter: NetworkAdapter, details: NetworkOutput | null) {
  return adapter.kind === 'wifi' && matchesAdapter(adapter, details?.defaultInterface ?? null)
    ? details?.defaultGateway?.ssid ?? undefined
    : undefined;
}

export function adapterAddresses(adapter: NetworkAdapter, details: NetworkOutput | null) {
  const detail = details?.interfaces.find(candidate => matchesAdapter(adapter, candidate));
  return detail ? [...detail.ipv4Addresses, ...detail.ipv6Addresses] : [];
}

export function NetworkIcon(props: { kind?: NetworkKind }) {
  return (
    <svg class="network-icon" data-kind={props.kind ?? 'disconnected'} viewBox="0 0 24 24"
      fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
      <Switch>
        <Match when={props.kind === 'wifi'}>
          <path d="M3 8.5a14 14 0 0 1 18 0M6.4 12a8.9 8.9 0 0 1 11.2 0m-7.8 3.6a4 4 0 0 1 4.4 0M12 19h.01" />
        </Match>
        <Match when={props.kind === 'ethernet'}>
          <path d="M4 6h16v11h-5v3H9v-3H4zM8 6v4m4-4v4m4-4v4" />
        </Match>
        <Match when={!props.kind}>
          <circle cx="12" cy="12" r="8" /><path d="m6.4 6.4 11.2 11.2" />
        </Match>
      </Switch>
    </svg>
  );
}

export function NetworkBarRate(props: { direction: 'download' | 'upload'; bytes?: number | null }) {
  const parts = createMemo(() => networkParts(props.bytes));
  return (
    <span class="network-bar-rate" aria-hidden="true">
      <b class={`network-${props.direction}`}>{props.direction === 'download' ? '↓' : '↑'}</b>
      <span>{parts().value}</span>
      <span>{parts().unit}</span>
    </span>
  );
}
