// What the PC offers as one-tap buttons for the current folder ("actions"), and the repository state for the git dialog ("git_state").

export interface ProjectScript { name: string; command: string }

export interface ActionsMsg {
  type: 'actions';
  path: string;
  packageManager: string | null;
  scripts: ProjectScript[];
  git: { repo: boolean; branch: string | null };
}

export interface GitStateMsg {
  type: 'git_state';
  path?: string;
  ok: boolean;
  branch?: string | null;
  upstream?: string | null;
  ahead?: number;
  behind?: number;
  changes?: number;
}

/** "feature/login · 4 changes · 2 to push": one line that says where the repository stands. */
export function gitSummary(g: GitStateMsg | null): string {
  if (!g) return 'Checking...';
  if (!g.ok) return 'Could not read the repository status.';
  const parts = [g.branch || 'detached HEAD'];
  const changes = g.changes ?? 0;
  parts.push(changes === 0 ? 'clean' : `${changes} change${changes === 1 ? '' : 's'}`);
  if (g.ahead) parts.push(`${g.ahead} to push`);
  if (g.behind) parts.push(`${g.behind} to pull`);
  if (g.upstream == null) parts.push('no upstream');
  return parts.join(' · ');
}

/** The keys offered under a running command that asks questions. */
export const JOB_KEYS: { key: string; label: string; title?: string }[] = [
  { key: 'up', label: '↑', title: 'Up' },
  { key: 'down', label: '↓', title: 'Down' },
  { key: 'left', label: '←', title: 'Left' },
  { key: 'right', label: '→', title: 'Right' },
  { key: 'space', label: 'Space' },
  { key: 'tab', label: 'Tab' },
  { key: 'esc', label: 'Esc' },
  { key: 'enter', label: 'Enter' },
  { key: 'ctrl-c', label: 'Ctrl+C', title: 'Interrupt' },
];
