import { useEffect, useMemo, useState } from 'react';

export interface Project {
  name: string; path: string; kind: string;
  hasReadme: boolean; hasAgentsDoc: boolean; hasSkills: boolean;
  modified: string; pinned: boolean; uses: number; lastUsed: string | null;
}
export interface ProjectsMsg { root: string; current: string; projects: Project[] }
export interface SyncItem { name: string; path: string; status: 'pending' | 'running' | 'done' | 'error' | 'cancelled'; summary: string; seconds: number }
export interface SyncState { running: boolean; mode: 'update' | 'report'; items: SyncItem[]; optimistic?: boolean }

const DAY = 86_400_000;

export function ago(iso: string | null | undefined): string {
  if (!iso) return '';
  const d = (Date.now() - new Date(iso).getTime()) / DAY;
  if (d < 1) return 'today';
  if (d < 2) return 'yesterday';
  if (d < 30) return `${Math.floor(d)}d ago`;
  if (d < 365) return `${Math.floor(d / 30)}mo ago`;
  return `${Math.floor(d / 365)}y ago`;
}

export function syncCounts(items: SyncItem[]) {
  return {
    finished: items.filter((i) => ['done', 'error', 'cancelled'].includes(i.status)).length,
    ok: items.filter((i) => i.status === 'done').length,
    failed: items.filter((i) => i.status === 'error').length,
    cancelled: items.filter((i) => i.status === 'cancelled').length,
  };
}

function group(list: Project[]): [string, Project[]][] {
  const used = new Set<string>();
  const take = (arr: Project[]) => arr.filter((p) => !used.has(p.path) && used.add(p.path));
  const pinned = take(list.filter((p) => p.pinned).sort((a, b) => a.name.localeCompare(b.name)));
  const frequent = take(list.filter((p) => p.uses >= 2)
    .sort((a, b) => b.uses - a.uses || +new Date(b.lastUsed ?? 0) - +new Date(a.lastUsed ?? 0)).slice(0, 5));
  const recent = take(list.filter((p) => Date.now() - +new Date(p.modified) < 30 * DAY)
    .sort((a, b) => +new Date(b.modified) - +new Date(a.modified)).slice(0, 6));
  const rest = list.filter((p) => !used.has(p.path));
  return ([['Pinned', pinned], ['Frequently used', frequent], ['Recently active', recent], ['All projects', rest]] as [string, Project[]][])
    .filter((g) => g[1].length);
}

const PIN = (
  <svg viewBox="0 0 24 24"><line x1="12" y1="17" x2="12" y2="22" /><path d="M5 17h14v-2l-2-2V5a1 1 0 0 0-1-1H8a1 1 0 0 0-1 1v8l-2 2v2z" /></svg>
);
const IDE = (
  <svg viewBox="0 0 24 24"><polyline points="16 18 22 12 16 6"/><polyline points="8 6 2 12 8 18"/></svg>
);
const SYNC_ICON = { pending: '○', running: '◌', done: '✓', error: '✕', cancelled: '–' } as const;

interface Props {
  data: ProjectsMsg | null;
  scanning: boolean;
  sync: SyncState | null;
  onClose: () => void;
  send: (m: Record<string, unknown>) => void;
  confirm: (title: string, text: string, ok: string) => Promise<boolean>;
  startSync: (paths: string[], mode: 'update' | 'report') => void;
}

export function ProjectsSheet({ data, scanning, sync, onClose, send, confirm, startSync }: Props) {
  const projects = data?.projects ?? [];
  const [unchecked, setUnchecked] = useState<Set<string>>(new Set());
  const [mode, setMode] = useState<'update' | 'report'>('update');
  const [root, setRoot] = useState('');
  const [showSync, setShowSync] = useState(!!sync?.running);

  useEffect(() => { if (data) setRoot(data.root); }, [data?.root]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (sync?.running) setShowSync(true); }, [sync?.running]);

  const groups = useMemo(() => group(projects), [projects]);
  const selected = projects.filter((p) => !unchecked.has(p.path)).map((p) => p.path);
  const allSelected = selected.length === projects.length && projects.length > 0;
  const toggle = (path: string) => setUnchecked((u) => { const n = new Set(u); n.has(path) ? n.delete(path) : n.add(path); return n; });
  const missing = projects.filter((p) => !p.hasReadme || !p.hasAgentsDoc || !p.hasSkills).length;

  const start = async () => {
    const n = selected.length;
    const plural = n === 1 ? '' : 's';
    const ok = await confirm(
      mode === 'update' ? `Update docs in ${n} project${plural}?` : `Review ${n} project${plural}?`,
      mode === 'update'
        ? `Levity will create or edit README, AGENTS.md and skill files in ${n} project${plural}, one at a time. Source code is not touched and nothing is committed.`
        : `Levity will read ${n} project${plural} one at a time and report suggested changes. No files are written.`,
      'Start',
    );
    if (!ok) return;
    setShowSync(true);
    startSync(selected, mode);
  };

  const items = sync?.items ?? [];
  const c = syncCounts(items);
  const viewSync = showSync && items.length > 0;
  const subtitle = viewSync
    ? sync!.running
      ? `${sync!.optimistic ? 'Starting' : sync!.mode === 'report' ? 'Reading' : 'Updating'} ${Math.min(c.finished + 1, items.length)} of ${items.length}…`
      : `Finished: ${c.ok} done${c.failed ? `, ${c.failed} failed` : ''}${c.cancelled ? `, ${c.cancelled} stopped` : ''}`
    : scanning ? `Scanning ${root || 'projects'}…` : `${projects.length} projects found · ${missing} missing docs or skills`;

  return (
    <section className="sheet open" aria-hidden="false">
      <div className="sheet-head">
        <div><h3>Projects</h3><p className="sheet-sub">{subtitle}</p></div>
        <button className="icon-btn" onClick={onClose} title="Close">
          <svg viewBox="0 0 24 24"><line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" /></svg>
        </button>
      </div>

      {!viewSync ? (
        <>
          <div className="sheet-body">
            <div className="folder-row">
              <button className="btn ghost" onClick={() => { send({ type: 'no_project' }); onClose(); }}>No project (general chat)</button>
            </div>
            <div className="root-row">
              <input value={root} spellCheck={false} onChange={(e) => setRoot(e.target.value)}
                onKeyDown={(e) => e.key === 'Enter' && root.trim() && send({ type: 'set_root', path: root.trim() })} />
              <button className={`btn ghost ${scanning ? 'busy' : ''}`} onClick={() => root.trim() && send({ type: 'set_root', path: root.trim() })}>
                {scanning && <span className="spinner" />}{scanning ? 'Scanning…' : 'Scan'}
              </button>
            </div>
            <div className="list-tools">
              <label className="check">
                <input type="checkbox" checked={allSelected} onChange={(e) => setUnchecked(e.target.checked ? new Set() : new Set(projects.map((p) => p.path)))} />
                <span>Select all</span>
              </label>
              <span className="legend"><i className="d ok" />has<i className="d miss" />missing &nbsp;README · AGENTS · Skills</span>
            </div>

            <div className={scanning ? 'loading' : ''}>
              {groups.map(([title, items]) => (
                <div key={title}>
                  <div className="grp-title">{title}</div>
                  <ul className="proj-list">
                    {items.map((p) => {
                      const current = !!data && p.path.toLowerCase() === data.current.toLowerCase();
                      const meta = [p.uses ? `${p.uses} prompt${p.uses === 1 ? '' : 's'}` : '', ago(p.modified)].filter(Boolean).join(' · ');
                      return (
                        <li key={p.path} className={`proj ${current ? 'current' : ''}`}>
                          <input type="checkbox" className="cb" checked={!unchecked.has(p.path)} onChange={() => toggle(p.path)} aria-label={`Select ${p.name}`} />
                          <div className="proj-main" onClick={() => toggle(p.path)}>
                            <div className="proj-name">{p.name}</div>
                            <div className="proj-meta">
                              <span className="kind">{p.kind}</span>
                              {[p.hasReadme, p.hasAgentsDoc, p.hasSkills].map((has, i) => <i key={i} className={`d ${has ? 'ok' : 'miss'}`} />)}
                              <span>{meta}</span>
                            </div>
                          </div>
                          <button className="pin-btn" title="Open in Antigravity IDE" onClick={() => send({ type: 'open_ide', path: p.path })}>{IDE}</button>
                          <button className={`pin-btn ${p.pinned ? 'on' : ''}`} title={p.pinned ? 'Unpin' : 'Pin to top'}
                            onClick={() => send({ type: 'pin_project', path: p.path, pinned: !p.pinned })}>{PIN}</button>
                          <button className="open-btn" disabled={current}
                            onClick={() => { send({ type: 'set_cwd', path: p.path }); onClose(); }}>{current ? 'Active' : 'Open'}</button>
                        </li>
                      );
                    })}
                  </ul>
                </div>
              ))}
            </div>
          </div>

          <div className="sheet-foot">
            <div className="seg">
              {(['update', 'report'] as const).map((m) => (
                <button key={m} className={`seg-btn ${mode === m ? 'active' : ''}`} onClick={() => setMode(m)}>
                  {m === 'update' ? 'Update docs' : 'Report only'}
                </button>
              ))}
            </div>
            <button className="btn primary" disabled={!selected.length} onClick={start}>
              {!selected.length ? 'Select projects' : allSelected ? `Review all ${selected.length} projects` : `Review ${selected.length} project${selected.length === 1 ? '' : 's'}`}
            </button>
          </div>
        </>
      ) : (
        <>
          <div className="sheet-body">
            {!sync!.running && (
              <div className={`sync-result ${c.failed || c.cancelled ? 'failed' : 'done'}`}>
                <span className="big">{c.failed || c.cancelled ? '!' : '✓'}</span>
                <span>
                  <strong>{c.failed ? 'Review finished with errors' : c.cancelled ? 'Review stopped' : 'Review complete'}</strong>
                  <small>{c.ok} of {items.length} projects {sync!.mode === 'report' ? 'reported on' : 'updated'}{c.failed ? ` · ${c.failed} failed` : ''}. Tap a project for details.</small>
                </span>
              </div>
            )}
            <div className="progress"><div className="progress-bar" style={{ width: `${Math.round((c.finished / items.length) * 100)}%` }} /></div>
            <p className="sheet-sub">{sync!.mode === 'report' ? 'Report only — no files were written.' : 'Docs and skills only — source code is never modified.'}</p>
            <ul className="sync-list" style={{ marginTop: 10 }}>
              {items.map((i) => (
                <li key={i.path}>
                  <details className="sync-item">
                    <summary className="sync-row">
                      <span className={`sync-ico ${i.status}`}>{SYNC_ICON[i.status]}</span>
                      <span className="sync-name">{i.name}</span>
                      <span className="sync-time">{i.seconds ? `${i.seconds}s` : i.status === 'running' ? 'running' : ''}</span>
                    </summary>
                    {i.summary && <div className="sync-detail">{i.summary}</div>}
                  </details>
                </li>
              ))}
            </ul>
          </div>
          <div className="sheet-foot">
            {sync!.running
              ? <button className="btn danger" onClick={() => send({ type: 'sync_cancel' })}>Stop</button>
              : <button className="btn ghost" onClick={() => { setShowSync(false); send({ type: 'list_projects' }); }}>Back to projects</button>}
          </div>
        </>
      )}
    </section>
  );
}
