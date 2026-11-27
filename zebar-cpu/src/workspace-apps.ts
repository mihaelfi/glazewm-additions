import { ContainerType, WmClient, WmEventType, type SplitContainer, type Window, type Workspace } from 'glazewm';

export interface WorkspaceApp {
  key: string;
  label: string;
  glyph?: string;
  initials?: string;
}

const knownApps: [label: string, glyph: string, processes: string[]][] = [
  ['ChatGPT', 'nf-cod-openai', ['chatgpt']],
  ['Claude', 'nf-cod-claude', ['claude']],
  ['Discord', 'nf-fa-discord', ['discord', 'discordcanary', 'discordptb']],
  ['Docker Desktop', 'nf-dev-docker', ['docker desktop']],
  ['File Explorer', 'nf-md-folder_outline', ['explorer']],
  ['Firefox', 'nf-fa-firefox', ['firefox']],
  ['FxSound', 'nf-md-equalizer', ['fxsound']],
  ['Google Chrome', 'nf-fa-chrome', ['chrome']],
  ['Jellyfin', 'nf-md-play_circle_outline', ['jellyfinmediaplayer', 'jellyfin desktop']],
  ['Microsoft Edge', 'nf-fa-edge', ['msedge']],
  ['Microsoft Excel', 'nf-md-microsoft_excel', ['excel']],
  ['Microsoft Outlook', 'nf-md-microsoft_outlook', ['outlook', 'olk']],
  ['Microsoft PowerPoint', 'nf-md-microsoft_powerpoint', ['powerpnt']],
  ['Microsoft Word', 'nf-md-microsoft_word', ['winword']],
  ['Notepad', 'nf-md-note_text_outline', ['notepad']],
  ['PyCharm', 'nf-dev-pycharm', ['pycharm', 'pycharm64']],
  ['ShareX', 'nf-md-share_variant', ['sharex']],
  ['Signal', 'nf-md-message_outline', ['signal']],
  ['Spotify', 'nf-fa-spotify', ['spotify']],
  ['Steam', 'nf-fa-steam', ['steam', 'steamwebhelper']],
  ['Visual Studio Code', 'nf-dev-vscode', ['code', 'code - insiders']],
  ['Windows Terminal', 'nf-md-console', ['windowsterminal', 'wt']],
];

const appByProcess = new Map(knownApps.flatMap(([label, glyph, processes]) => {
  const app = { key: processes[0], label, glyph };
  return processes.map(process => [process, app]);
}));

function appForProcess(processName: string): WorkspaceApp | null {
  const label = processName?.trim().replace(/\.exe$/i, '');
  if (!label) return null;

  const key = label.toLowerCase();
  const knownApp = appByProcess.get(key);
  if (knownApp) return knownApp;

  const [first = '', second] = label.match(/[\p{L}\p{N}]+/gu) ?? [];
  const letters = Array.from(first);
  const initials = second ? letters[0] + Array.from(second)[0] : letters.slice(0, 2).join('');
  return { key, label, initials: initials.toUpperCase() || '?' };
}

export function appsInWorkspace(workspace: Workspace): WorkspaceApp[] {
  const apps = new Map<string, WorkspaceApp>();

  function visit(container: Workspace | SplitContainer | Window) {
    if (container.type === ContainerType.WINDOW) {
      const app = appForProcess(container.processName);
      if (app && !apps.has(app.key)) apps.set(app.key, app);
      return;
    }
    for (const child of container.children) visit(child);
  }

  visit(workspace);
  return [...apps.values()].sort((left, right) => left.label.localeCompare(right.label));
}

export function watchWorkspaceApps(onApps: (apps: Map<string, WorkspaceApp[]> | null) => void) {
  const client = new WmClient();
  const events = [
    WmEventType.WINDOW_MANAGED,
    WmEventType.WINDOW_UNMANAGED,
    WmEventType.FOCUSED_CONTAINER_MOVED,
    WmEventType.WORKSPACE_ACTIVATED,
    WmEventType.WORKSPACE_DEACTIVATED,
    WmEventType.WORKSPACE_UPDATED,
    WmEventType.MONITOR_ADDED,
    WmEventType.MONITOR_REMOVED,
    WmEventType.PAUSE_CHANGED,
  ];
  let revision = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let subscription: Promise<unknown> | undefined;

  // Batch event bursts. Zebar's built-in provider still handles the workspace controls.
  function refresh() {
    clearTimeout(timer);
    const request = ++revision;
    timer = setTimeout(async () => {
      try {
        const { workspaces } = await client.queryWorkspaces();
        if (request === revision) {
          onApps(new Map(workspaces.map(workspace => [workspace.id, appsInWorkspace(workspace)])));
        }
      } catch {
        if (request === revision) onApps(null);
      }
    }, 50);
  }

  const stopConnect = client.onConnect(() => {
    subscription ??= client.subscribeMany(events, refresh).catch(() => {
      subscription = undefined;
    });
    refresh();
  });
  const stopDisconnect = client.onDisconnect(() => {
    clearTimeout(timer);
    revision++;
    onApps(null);
  });

  return () => {
    clearTimeout(timer);
    revision++;
    stopConnect();
    stopDisconnect();
    void client.closeConnection().catch(() => {});
  };
}
