import { useEffect, useRef } from 'react';

export interface Job {
  id: number;
  command: string;
  cwd: string;
  state: 'running' | 'done' | 'failed' | 'stopped';
  exitCode: number | null;
  started: string;
  ended: string | null;
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

export function JobsSheet({ jobs, send, onClose }: { jobs: Job[]; send: (m: Record<string, unknown>) => void; onClose: () => void }) {
  const running = jobs.filter((j) => j.state === 'running').length;
  return (
    <section className="sheet open" aria-hidden="false">
      <div className="sheet-head">
        <div>
          <h3>Background jobs</h3>
          <p className="sheet-sub">{jobs.length ? `${running} running · ${jobs.length - running} finished` : 'Nothing running. Start one with /cmd{$bun run dev}.'}</p>
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
