import { createEffect, createMemo, createSignal, For, onCleanup, Show } from 'solid-js';
import { render } from 'solid-js/web';
import { shellSpawn } from 'zebar';
import { useCloseActivityPanel } from './activity-panel';
import { BarIcon } from './bar-icon';
import { type NetworkAdapter, useNetworkHistoryFeed } from './metrics-feed';
import { adapterAddresses, adapterSsid, formatNetworkRate, NetworkIcon, networkKindLabel, useNetworkDetails } from './network';
import { PanelHeader } from './panel-header';
import { PanelSection } from './panel-section';
import { formatWindow, useSettings } from './settings';
import { throughputCeiling } from './throughput';
import { ThroughputGraph } from './throughput-graph';
import './widget.css';
import './activity-popup.css';
import './network.css';

const EXPLORER = 'C:\\Windows\\explorer.exe';
const adapterSettings = {
  wifi: { page: 'Wi-Fi Settings', uri: 'ms-settings:network-wifi' },
  ethernet: { page: 'Ethernet Settings', uri: 'ms-settings:network-ethernet' },
};
const advancedSettings = { page: 'Advanced Network Settings', uri: 'ms-settings:network-advancedsettings' };

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
    <main class="panel popup-frame activity-popup network-panel" aria-labelledby="network-title">
      <PanelHeader id="network-title" title="Network Activity" onClose={close} icon={<BarIcon name="network" />} />

      <PanelSection scope="network" id="traffic" label="Total Traffic" class="popup-frame activity-card">
        <dl class="network-summary">
          <div><dt class="network-download">↓ Download</dt><dd>{formatNetworkRate(network()?.total.download)}</dd></div>
          <div><dt class="network-upload">↑ Upload</dt><dd>{formatNetworkRate(network()?.total.upload)}</dd></div>
        </dl>
      </PanelSection>

      <PanelSection scope="network" id="adapters" label="Adapters" class="popup-frame activity-card activity-scroll-card" detail={
        <span class="panel-section-detail">
          Last {formatWindow(seconds())} · <span class="throughput-scale">
            {network() ? `${formatNetworkRate(ceiling())} max` : 'Network data unavailable'}
          </span>
        </span>
      }>
        <Show when={adapters().length} fallback={
          <p class="process-status" role="status">
            {network() ? 'No active Wi-Fi or Ethernet adapters.' : status() === 'starting' ? 'Waiting for network data…' : 'Network data unavailable.'}
          </p>
        }>
          <ul class="network-adapters" aria-label="Active adapters" tabIndex={0}>
            <For each={adapters()}>
              {adapter => {
                const ssid = () => adapterSsid(adapter, details());
                const addresses = createMemo(() => adapterAddresses(adapter, details()));
                const rates = () => network()?.rates[adapter.id];
                const name = adapter.name || networkKindLabel(adapter.kind);
                const target = () => adapters().filter(other => other.kind === adapter.kind).length > 1
                  ? advancedSettings : adapterSettings[adapter.kind];
                const [feedback, setFeedback] = createSignal('');
                let feedbackTimer: number | undefined;
                onCleanup(() => window.clearTimeout(feedbackTimer));

                function report(message: string) {
                  window.clearTimeout(feedbackTimer);
                  setFeedback(message);
                  feedbackTimer = window.setTimeout(() => setFeedback(''), 2400);
                }

                async function copyAddress() {
                  const address = addresses()[0];
                  if (!address) return;
                  try {
                    await navigator.clipboard.writeText(address);
                    report('Copied');
                  } catch {
                    report('Couldn’t copy IP');
                  }
                }

                async function openAdapterSettings() {
                  try {
                    await shellSpawn(EXPLORER, [target().uri]);
                  } catch {
                    report('Couldn’t open Settings');
                  }
                }

                return (
                  <li class="network-adapter popup-frame">
                    <header class="network-adapter-header">
                      <NetworkIcon kind={adapter.kind} />
                      <h3 title={name}>{name}</h3>
                      <div class="network-adapter-actions">
                        <span class="network-action-status" role="status">{feedback()}</span>
                        <button type="button" class="network-adapter-action network-copy-ip"
                          title={addresses().length ? 'Copy IP address' : 'IP address unavailable'}
                          aria-label={`Copy IP address for ${name}`}
                          disabled={!addresses().length}
                          onClick={() => void copyAddress()}>
                          <BarIcon name={feedback() === 'Copied' ? 'check' : 'copy'} />
                        </button>
                        <button type="button" class="network-adapter-action network-open-settings"
                          title={`Open Windows ${target().page}`}
                          aria-label={`Open Windows ${target().page} for ${name}`}
                          onClick={() => void openAdapterSettings()}>
                          <BarIcon name="external" />
                        </button>
                      </div>
                    </header>
                    <div class="network-adapter-details">
                      <Show when={ssid()}>{name => <span title={name()}>{name()}</span>}</Show>
                      <Show when={addresses().length} fallback={<span>IP address unavailable</span>}>
                        <span class="network-ip" title={addresses().join('\n')}>{addresses()[0]}</span>
                      </Show>
                    </div>
                    <div class="network-adapter-telemetry">
                      <ThroughputGraph
                        class="network-history-graph" samples={samples()} length={seconds()} ceiling={ceiling()}
                        series={[
                          { color: '--network-download', value: sample => sample.rates?.[adapter.id]?.download },
                          { color: '--network-upload', value: sample => sample.rates?.[adapter.id]?.upload },
                        ]}
                      />
                      <dl class="network-adapter-rates">
                        <div>
                          <dt class="network-download" title="Download">
                            <span aria-hidden="true">↓</span><span class="network-rate-label">Download</span>
                          </dt>
                          <dd>{formatNetworkRate(rates()?.download)}</dd>
                        </div>
                        <div>
                          <dt class="network-upload" title="Upload">
                            <span aria-hidden="true">↑</span><span class="network-rate-label">Upload</span>
                          </dt>
                          <dd>{formatNetworkRate(rates()?.upload)}</dd>
                        </div>
                      </dl>
                    </div>
                  </li>
                );
              }}
            </For>
          </ul>
        </Show>
      </PanelSection>
    </main>
  );
}

render(() => <NetworkPanel />, document.getElementById('root')!);
