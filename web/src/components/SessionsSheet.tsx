import { useState } from 'react';
import { ago } from './ProjectsSheet';

export interface SessionInfo {
  id: string;
  title: string;
  cwd: string;
  updated: string;
  messages: number;
}

export interface SessionsMsg {
  current: string;
  sessions: SessionInfo[];
}

export function SessionsSheet({ data, send, onClose }: { data: SessionsMsg | null; send: (m: Record<string, unknown>) => void; onClose: () => void }) {
  const [armed, setArmed] = useState<string | null>(null); // delete needs a second tap
  const items = data?.sessions ?? [];
  const open = (id: string) => { send({ type: 'session_open', id }); onClose(); };

  return (
    <section className="sheet open" aria-hidden="false">
      <div className="sheet-head">
        <div>
          <h3>Chat history</h3>
          <p className="sheet-sub">{items.length ? `${items.length} saved on your PC` : 'No saved chats yet. Chats are saved automatically.'}</p>
        </div>
        <button className="icon-btn" onClick={onClose} title="Close">
          <svg viewBox="0 0 24 24"><line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" /></svg>
        </button>
      </div>
      <div className="sheet-body">
        <ul className="sess-list">
          {items.map((s) => {
            const current = s.id === data?.current;
            const folder = s.cwd.split(/[\\/]/).filter(Boolean).pop() || s.cwd;
            return (
              <li key={s.id} className={`sess ${current ? 'current' : ''}`}>
                <div className="sess-main" onClick={() => !current && open(s.id)}>
                  <div className="sess-title">{s.title || '(untitled)'}</div>
                  <div className="sess-meta">{folder} · {s.messages} message{s.messages === 1 ? '' : 's'} · {ago(s.updated)}</div>
                </div>
                <button className="btn ghost" disabled={current} onClick={() => open(s.id)}>{current ? 'Open now' : 'Open'}</button>
                <button className={`btn ghost ${armed === s.id ? 'danger' : ''}`}
                  onClick={() => {
                    if (armed === s.id) { send({ type: 'session_delete', id: s.id }); setArmed(null); }
                    else { setArmed(s.id); window.setTimeout(() => setArmed((a) => (a === s.id ? null : a)), 3000); }
                  }}>{armed === s.id ? 'Sure?' : 'Delete'}</button>
              </li>
            );
          })}
        </ul>
      </div>
    </section>
  );
}
