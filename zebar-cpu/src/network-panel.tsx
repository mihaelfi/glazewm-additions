import { createEffect, createMemo, For, Show } from 'solid-js';
import { render } from 'solid-js/web';
import { useCloseActivityPanel } from './activity-panel';
import { type NetworkAdapter, useNetworkHistoryFeed } from './metrics-feed';
import { adapterAddresses, adapterSsid, formatNetworkRate, NetworkIcon, networkKindLabel, useNetworkDetails } from './network';
import { PanelHeader } from './panel-header';
import { formatWindow, useSettings } from './settings';
import { throughputCeiling } from './throughput';
import { ThroughputGraph } from './throughput-graph';
import './widget.css';
import './network.css';

function NetworkPanel() {
  const close = useCloseActivityPanel();
  const details = useNetworkDetails();
  const { settings, sync } = useSettings();
  const seconds = () => settings().historyMinutes * 60;
  const { samples, network, status, settingsVersion } = useNetworkHistoryFeed(seconds);
  createEffect(() => sync(settingsVersion()));
  const adapters = createMemo<readonly NetworkAdapter[]>(previous => network()?.adapters ?? previous, [], {
    equals: (left, right) => left.length === right.length && left.every((adapter, index) =>
      adapter.id === right[index].id && adapter.name === right[index].name && adapter.kind === right[index].kind),
  });

  const ceiling = createMemo(() => {
    let peak = 125; // 1 kb/s minimum, in bytes per second.
    const active = adapters();
    for (const sample of samples()) {
      for (const adapter of active) {
        const rates = sample.rates?.[adapter.id];
        peak = Math.max(peak, rates?.download ?? 0, rates?.upload ?? 0);
      }
    }
    return throughputCeiling(peak * 8) / 8;
  });

  return (
    <main class="panel popup-frame network-panel" aria-labelledby="network-title">
      <PanelHeader id="network-title" title="Network" onClose={close} />

      <div class="network-summary popup-frame" aria-label="Total traffic">
        <div><span class="network-download">↓ Total download</span><strong>{formatNetworkRate(network()?.total.download)}</strong></div>
        <div><span class="network-upload">↑ Total upload</span><strong>{formatNetworkRate(network()?.total.upload)}</strong></div>
      </div>

      <Show when={adapters().length} fallback={
        <p class="process-status" role="status">
          {network() ? 'No active Wi-Fi or Ethernet adapters.' : status() === 'starting' ? 'Waiting for network data…' : 'Network data unavailable.'}
        </p>
      }>
        <div class="throughput-legend" aria-label="Shared graph scale">
          <span>Last {formatWindow(seconds())}</span>
          <span class="throughput-key network-download">Download</span>
          <span class="throughput-key network-upload">Upload</span>
          <span class="throughput-scale">
            {network() ? `${formatNetworkRate(ceiling())} max` : 'Network data unavailable'}
          </span>
        </div>
        <ul class="network-adapters" aria-label="Active adapters">
          <For each={adapters()}>
            {adapter => {
              const ssid = () => adapterSsid(adapter, details());
              const addresses = createMemo(() => adapterAddresses(adapter, details()));
              const rates = () => network()?.rates[adapter.id];
              return (
                <li class="network-adapter popup-frame">
                  <header class="network-adapter-header">
                    <NetworkIcon kind={adapter.kind} />
                    <h2 title={adapter.name}>{adapter.name || networkKindLabel(adapter.kind)}</h2>
                  </header>
                  <div class="network-adapter-details">
                    <Show when={ssid()}>{name => <span title={name()}>{name()}</span>}</Show>
                    <Show when={addresses().length} fallback={<span>IP address unavailable</span>}>
                      <span class="network-ip" title={addresses().join('\n')}>{addresses()[0]}</span>
                    </Show>
                  </div>
                  <ThroughputGraph
                    class="network-history-graph" samples={samples()} length={seconds()} ceiling={ceiling()}
                    series={[
                      { color: '--network-download', value: sample => sample.rates?.[adapter.id]?.download },
                      { color: '--network-upload', value: sample => sample.rates?.[adapter.id]?.upload },
                    ]}
                  />
                  <div class="network-adapter-rates">
                    <span><span class="network-download">↓</span> <strong>{formatNetworkRate(rates()?.download)}</strong></span>
                    <span><span class="network-upload">↑</span> <strong>{formatNetworkRate(rates()?.upload)}</strong></span>
                  </div>
                </li>
              );
            }}
          </For>
        </ul>
      </Show>
    </main>
  );
}

render(() => <NetworkPanel />, document.getElementById('root')!);
