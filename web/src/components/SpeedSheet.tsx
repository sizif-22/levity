import { useEffect } from 'react';

export interface SpeedMsg {
  type: 'speed';
  current: {
    turns: number; seconds: number; inputTokens: number; outputTokens: number; thinkingTokens: number;
    tools: { name: string; count: number; seconds: number; max: number }[];
    slow: { tool: string; seconds: number; what: string }[];
    findings: string[];
  };
  recent: { title: string; turns: number; seconds: number; inputTokens: number }[];
  footprint: { totalMb: number; files: number; truncated: boolean; folders: { name: string; mb: number; files: number }[]; notes: string[] };
}

export const fmtSecs = (s: number) => (s >= 90 ? `${Math.floor(s / 60)} min ${Math.round(s % 60)} s` : `${Math.round(s)} s`);
export const fmtTokens = (n: number) => (n >= 1000 ? `${(n / 1000).toFixed(n >= 100000 ? 0 : 1)}k` : String(n));

type Send = (m: Record<string, unknown>) => void;

/** /speed on the phone: where the time and tokens of this conversation went, and what would make it faster. */
export function SpeedSheet({ data, send, onClose }: { data: SpeedMsg | null; send: Send; onClose: () => void }) {
  useEffect(() => { send({ type: 'speed_get' }); }, [send]);
  const c = data?.current;
  const f = data?.footprint;
  return (
    <section className="sheet open" aria-hidden="false">
      <div className="sheet-head">
        <div><h3>Speed and cost</h3><p className="sheet-sub">Where the time and tokens went</p></div>
        <button className="icon-btn" onClick={onClose} title="Close">
          <svg viewBox="0 0 24 24"><line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" /></svg>
        </button>
      </div>
      <div className="sheet-body win-body">
        <div className="win-card">
          <strong>This conversation</strong>
          <p className="win-muted">
            {!c ? 'Working it out...' : c.turns
              ? `${c.turns} answer${c.turns === 1 ? '' : 's'} · ${fmtSecs(c.seconds)} · ${fmtTokens(c.inputTokens)} tokens in, ${fmtTokens(c.outputTokens)} out (${fmtTokens(c.thinkingTokens)} thinking)`
              : 'Nothing to measure yet in this conversation.'}
          </p>
        </div>
        {!!c?.findings.length && <div><strong>What would make it faster</strong><ul className="speed-list">{c.findings.map((t, i) => <li key={i}>{t}</li>)}</ul></div>}
        {!!c?.slow.length && <div><strong>Slowest steps</strong><ul className="speed-list">{c.slow.map((s, i) => <li key={i}>{`${Math.round(s.seconds)} s · ${s.tool}${s.what ? ' · ' + s.what : ''}`}</li>)}</ul></div>}
        {!!c?.tools.length && <div><strong>Time by tool</strong><ul className="speed-list">{c.tools.map((t) => <li key={t.name}>{`${t.name}: ${t.count} call${t.count === 1 ? '' : 's'}, ${fmtSecs(t.seconds)} in total (slowest ${fmtSecs(t.max)})`}</li>)}</ul></div>}
        {!!data?.recent.filter((r) => r.turns > 0).length && <div><strong>Recent conversations</strong><ul className="speed-list">{data!.recent.filter((r) => r.turns > 0).map((r, i) => <li key={i}>{`${r.title || 'Untitled'}: ${r.turns} answer${r.turns === 1 ? '' : 's'}, ${fmtSecs(r.seconds)}, ${fmtTokens(r.inputTokens)} tokens in`}</li>)}</ul></div>}
        <div className="win-card">
          <strong>The assistant's folder (~/.gemini)</strong>
          <p className="win-muted">{!f ? 'Measuring...' : f.files ? `${Math.round(f.totalMb)} MB in ${f.files.toLocaleString()} files${f.truncated ? ' (stopped early: very large)' : ''}. Only what the assistant loads costs tokens; this is a read-only look.` : 'No assistant folder found.'}</p>
          <ul className="speed-list">{(f?.folders ?? []).map((x) => <li key={x.name}>{`${x.name}: ${Math.round(x.mb)} MB, ${x.files.toLocaleString()} files`}</li>)}</ul>
          <ul className="speed-list">{(f?.notes ?? []).map((t, i) => <li key={i}>{t}</li>)}</ul>
        </div>
      </div>
    </section>
  );
}
