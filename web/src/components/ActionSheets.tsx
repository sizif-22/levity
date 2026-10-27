import { useEffect, useState } from 'react';
import { gitSummary, type ActionsMsg, type GitStateMsg } from '../lib/actions';

type Send = (m: Record<string, unknown>) => void;

const Close = ({ onClose }: { onClose: () => void }) => (
  <button className="icon-btn" onClick={onClose} title="Close">
    <svg viewBox="0 0 24 24"><line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" /></svg>
  </button>
);

/** /run: the scripts in the folder's package.json. Only the script's name is sent back; the PC builds and checks the command. */
export function RunSheet({ actions, send, onClose }: { actions: ActionsMsg | null; send: Send; onClose: () => void }) {
  const [interactive, setInteractive] = useState(false);
  const scripts = actions?.scripts ?? [];
  return (
    <section className="sheet open" aria-hidden="false">
      <div className="sheet-head">
        <div>
          <h3>Run a script</h3>
          <p className="sheet-sub">{scripts.length ? `${actions?.packageManager ?? 'npm'} · ${scripts.length} script${scripts.length === 1 ? '' : 's'} in package.json` : 'No scripts in this folder.'}</p>
        </div>
        <Close onClose={onClose} />
      </div>
      <div className="sheet-body">
        <ul className="action-list">
          {scripts.map((s) => (
            <li key={s.name} className="action-row">
              <div className="action-text"><strong>{s.name}</strong><code>{s.command}</code></div>
              <button className="btn primary action-go" onClick={() => { send({ type: 'run_script', name: s.name, interactive }); onClose(); }}>Run</button>
            </li>
          ))}
        </ul>
      </div>
      <div className="sheet-foot">
        <label className="check"><input type="checkbox" checked={interactive} onChange={(e) => setInteractive(e.target.checked)} /> It may ask me questions</label>
      </div>
    </section>
  );
}

const GIT_BUTTONS: { action: string; title: string; cmd: string }[] = [
  { action: 'status', title: 'Status', cmd: 'git status -sb' },
  { action: 'add', title: 'Stage all', cmd: 'git add .' },
  { action: 'commit', title: 'Commit…', cmd: 'git commit -m "…"' },
  { action: 'push', title: 'Push', cmd: 'git push' },
  { action: 'pull', title: 'Pull', cmd: 'git pull' },
  { action: 'fetch', title: 'Fetch', cmd: 'git fetch --all --prune' },
  { action: 'log', title: 'Log', cmd: 'git log --oneline -n 15' },
  { action: 'diff', title: 'Diff', cmd: 'git diff --stat' },
  { action: 'branches', title: 'Branches', cmd: 'git branch -vv' },
  { action: 'switch-new', title: 'New branch…', cmd: 'git switch -c …' },
  { action: 'stash', title: 'Stash', cmd: 'git stash' },
  { action: 'stash-pop', title: 'Stash pop', cmd: 'git stash pop' },
];

/** /git: the common steps. A commit asks for its message here first. */
export function GitSheet({ state, send, onClose }: { state: GitStateMsg | null; send: Send; onClose: () => void }) {
  const [form, setForm] = useState<null | 'commit' | 'branch'>(null);
  const [message, setMessage] = useState('');
  const [name, setName] = useState('');
  const [stage, setStage] = useState(true);

  useEffect(() => { send({ type: 'git_state' }); }, [send]);
  useEffect(() => { if (state?.ok) setStage((state.changes ?? 0) > 0); }, [state]);

  const run = (action: string, extra: Record<string, unknown> = {}) => { send({ type: 'git_run', action, ...extra }); onClose(); };
  const click = (action: string) => (action === 'commit' ? setForm('commit') : action === 'switch-new' ? setForm('branch') : run(action));

  return (
    <section className="sheet open" aria-hidden="false">
      <div className="sheet-head">
        <div><h3>Git</h3><p className="sheet-sub">{gitSummary(state)}</p></div>
        <Close onClose={onClose} />
      </div>
      <div className="sheet-body">
        {form === null && (
          <div className="git-grid">
            {GIT_BUTTONS.map((b) => (
              <button key={b.action} className="git-btn" onClick={() => click(b.action)}><strong>{b.title}</strong><code>{b.cmd}</code></button>
            ))}
          </div>
        )}

        {form === 'commit' && (
          <div className="git-form">
            <label htmlFor="gitMessage"><strong>Commit message</strong></label>
            <textarea id="gitMessage" rows={4} value={message} maxLength={2000} placeholder="What changed and why?" autoFocus onChange={(e) => setMessage(e.target.value)} />
            <label className="check"><input type="checkbox" checked={stage} onChange={(e) => setStage(e.target.checked)} /> Stage all changes first (<code>git add .</code>)</label>
            <div className="diag-actions">
              <button className="btn primary" disabled={!message.trim()} onClick={() => run('commit', { message: message.trim(), stage })}>Commit</button>
              <button className="btn ghost" onClick={() => setForm(null)}>Back</button>
            </div>
          </div>
        )}

        {form === 'branch' && (
          <div className="git-form">
            <label htmlFor="gitBranchName"><strong>New branch name</strong></label>
            <input id="gitBranchName" type="text" value={name} maxLength={100} placeholder="feature/login" autoFocus autoComplete="off" spellCheck={false}
              onChange={(e) => setName(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter' && name.trim()) run('switch-new', { name: name.trim() }); }} />
            <div className="diag-actions">
              <button className="btn primary" disabled={!name.trim()} onClick={() => run('switch-new', { name: name.trim() })}>Create and switch</button>
              <button className="btn ghost" onClick={() => setForm(null)}>Back</button>
            </div>
          </div>
        )}
      </div>
    </section>
  );
}
