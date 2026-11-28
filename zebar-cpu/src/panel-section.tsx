import { createSignal, Show, type JSX, type ParentProps } from 'solid-js';

export function PanelSection(props: ParentProps<{
  scope: string;
  id: string;
  label: string;
  class?: string;
  detail?: JSX.Element;
}>) {
  const key = `${props.scope}-popup:section:${props.id}`;
  const heading = `${props.scope}-${props.id}-title`;
  const body = `${props.scope}-${props.id}-body`;
  const [expanded, setExpanded] = createSignal(true);
  try {
    setExpanded(localStorage.getItem(key) !== 'closed');
  } catch { /* Keep the default if WebView storage is unavailable. */ }

  function toggle() {
    const next = !expanded();
    setExpanded(next);
    try {
      localStorage.setItem(key, next ? 'open' : 'closed');
    } catch { /* The popup still responds within this session. */ }
  }

  return (
    <section class={props.class} classList={{ 'panel-section': true, collapsed: !expanded() }} aria-labelledby={heading}>
      <h2 class="panel-section-heading" id={heading}>
        <button type="button" aria-expanded={expanded()} aria-controls={body} onClick={toggle}>
          <span class="panel-section-name">{props.label}</span>
          {props.detail}
          <i class="panel-section-arrow" aria-hidden="true" />
        </button>
      </h2>
      <div class="panel-section-body" id={body} hidden={!expanded()}>
        <Show when={expanded()}>{props.children}</Show>
      </div>
    </section>
  );
}
