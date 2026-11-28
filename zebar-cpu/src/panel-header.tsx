import type { JSX } from 'solid-js';
import { openActivityPanel } from './activity-panel';

/** Popup title with a settings button and a close button. */
export function PanelHeader(props: { id: string; title: string; onClose: () => void; icon?: JSX.Element }) {
  function openSettings() {
    // This popup closes on blur when Settings takes focus.
    void openActivityPanel('settings').catch(error => console.error('Could not open settings', error));
  }

  return (
    <header class="panel-header">
      <h1 id={props.id}>{props.icon}{props.title}</h1>
      <div class="panel-actions">
        <button class="close-panel settings-button" aria-label="Open settings" title="Settings" onClick={openSettings}>
          <svg class="gear-icon" viewBox="0 0 20 20" aria-hidden="true">
            {/* Dashes on the outer ring form the teeth. */}
            <circle cx="10" cy="10" r="7" fill="none" stroke="currentColor" stroke-width="3" stroke-dasharray="2.75 2.75" />
            <circle cx="10" cy="10" r="4.6" fill="none" stroke="currentColor" stroke-width="2" />
          </svg>
        </button>
        <button class="close-panel" aria-label={`Close ${props.title}`} title="Close (Esc)" onClick={() => props.onClose()}>×</button>
      </div>
    </header>
  );
}
