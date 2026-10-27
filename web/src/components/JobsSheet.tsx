import { useEffect, useRef, useState } from 'react';
import { JOB_KEYS } from '../lib/actions';

export interface Job {
  id: number;
  command: string;
  cwd: string;
  state: 'running' | 'done' | 'failed' | 'stopped';
  exitCode: number | null;
  started: string;
  ended: string | null;
  /** Runs in a terminal of its own, so it can ask questions that are answered here. */
  interactive?: boolean;
  tail: string;
}

const elapsed = (j: Job) => {
  const s = Math.max(0, Math.round(((j.ended ? new Date(j.ended).getTime() : Date.now()) - new Date(j.started).getTime()) / 1000));
  return s < 60 ? `${s}s` : s < 3600 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m`;
};

function Output({ text }: { text: string }) {
  const ref = useRef<HTMLPreElement>(null);
  const stick = useRef(true);
  useEffect(() => {
    const el = ref.current;
    if (el && stick.current) el.scrollTop = el.scrollHeight;
  }, [text]);
  return (
    <pre ref={ref} className="job-out"
      onScroll={(e) => { const el = e.currentTarget; stick.current = el.scrollHeight - el.scrollTop - el.clientHeight < 24; }}>
      {text || '(no output yet)'}
    </pre>
  );
}

/** Under a running interactive command: a box for typed answers and the keys menus need. */
function Ask({ job, send, autoFocus }: { job: Job; send: (m: Record<string, unknown>) => void; autoFocus: boolean }) {
  const [text, setText] = useState('');
  const box = useRef<HTMLInputElement>(null);
  useEffect(() => { if (autoFocus) box.current?.focus(); }, [autoFocus]);
  const reply = () => { send({ type: 'job_input', id: job.id, text, enter: true }); setText(''); };
  return (
    <div className="job-ask">
      <div className="job-keys">
        {JOB_KEYS.map((k) => <button key={k.key} className="key-btn" title={k.title} onClick={() => send({ type: 'job_key', id: job.id, key: k.key })}>{k.label}</button>)}
      </div>
      <div className="job-reply">
        <input ref={box} className="job-input" type="text" value={text} placeholder="Type an answer, then Enter" autoComplete="off" spellCheck={false}
          onChange={(e) => setText(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); reply(); } }} />
        <button className="btn primary job-send" onClick={reply}>Send</button>
      </div>
    </div>
  );
}

export function JobsSheet({ jobs, send, onClose }: { jobs: Job[]; send: (m: Record<string, unknown>) => void; onClose: () => void }) {
  const running = jobs.filter((j) => j.state === 'running').length;
  return (
    <section className="sheet open" aria-hidden="false">
      <div className="sheet-head">
        <div>
          <h3>Background jobs</h3>
          <p className="sheet-sub">{jobs.length ? `${running} running · ${jobs.length - running} finished` : 'Nothing running. Start one with /cmd$bun run dev'}</p>
        </div>
        <button className="icon-btn" onClick={onClose} title="Close">
          <svg viewBox="0 0 24 24"><line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" /></svg>
        </button>
      </div>
      <div className="sheet-body">
        <ul className="job-list">
          {[...jobs].reverse().map((j) => (
            <li key={j.id} className="job" data-state={j.state}>
              <div className="job-head">
                <i className="job-dot" />
                <code className="job-cmd">#{j.id} {j.command}</code>
                <span className="job-meta">
                  {j.state === 'running' ? `running ${elapsed(j)}` : `${j.state}${j.exitCode != null ? ` (exit ${j.exitCode})` : ''} · ${elapsed(j)}`}
                </span>
                {j.state === 'running' && <button className="btn danger job-stop" onClick={() => send({ type: 'job_stop', id: j.id })}>Stop</button>}
              </div>
              <Output text={j.tail} />
              {j.interactive && j.state === 'running' && <Ask job={j} send={send} autoFocus={false} />}
            </li>
          ))}
        </ul>
      </div>
      <div className="sheet-foot">
        <button className="btn ghost" onClick={() => send({ type: 'job_clear' })}>Clear finished</button>
      </div>
    </section>
  );
}
