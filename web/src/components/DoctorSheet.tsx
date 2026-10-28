import { useEffect } from 'react';

export interface DoctorFixMsg { index: number; kind: 'tool' | 'prompt' | 'settings'; label: string; changes: boolean }
export interface DoctorFindingMsg { id: string; severity: 'critical' | 'warning' | 'info' | 'ok'; title: string; detail: string; fixes: DoctorFixMsg[] }
export interface DoctorMsg { type: 'doctor'; findings: DoctorFindingMsg[]; skipped: string[]; time: string }
export interface DoctorResultMsg { type: 'doctor_result'; id: string; index: number; ok: boolean; text: string }

type Send = (m: Record<string, unknown>) => void;

const LABEL: Record<DoctorFindingMsg['severity'], string> = { critical: 'Fix now', warning: 'Worth fixing', info: 'For your information', ok: 'All good' };

/** PC Doctor on the phone: scan this PC (no tokens), then fix. Anything that changes Windows is done from the PC, not from here. */
export function DoctorSheet({ data, result, send, onClose, onScan }: { data: DoctorMsg | null; result: DoctorResultMsg | null; send: Send; onClose: () => void; onScan: () => void }) {
  useEffect(() => { send({ type: 'doctor_scan' }); }, [send]);

  const press = (f: DoctorFindingMsg, fx: DoctorFixMsg) => {
    if (fx.changes) return;   // changes are PC-only
    send({ type: 'doctor_fix', id: f.id, index: fx.index });
  };

  return (
    <section className="sheet open" aria-hidden="false">
      <div className="sheet-head">
        <div><h3>PC Doctor</h3><p className="sheet-sub">Checks this PC in seconds, with no assistant and no tokens</p></div>
        <button className="icon-btn" onClick={onClose} title="Close">
          <svg viewBox="0 0 24 24"><line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" /></svg>
        </button>
      </div>
      <div className="sheet-body win-body">
        <div className="diag-actions"><button className="btn primary" onClick={() => { onScan(); send({ type: 'doctor_scan' }); }}>{data ? 'Scan again' : 'Scanning...'}</button></div>
        {!data && <p className="win-muted">Looking at disk space, memory, restarts, start-up, the network, the event log and PATH...</p>}
        {data?.findings.map((f) => (
          <div className={`win-card doctor-${f.severity}`} key={f.id}>
            <span className="win-muted">{LABEL[f.severity]}</span>
            <strong>{f.title}</strong>
            <p className="win-muted">{f.detail}</p>
            {f.fixes.length > 0 && (
              <div className="diag-actions">
                {f.fixes.map((fx) => fx.changes
                  ? <span className="win-muted" key={fx.index}>{fx.label}: do this from the PC</span>
                  : <button className="btn ghost" key={fx.index} onClick={() => press(f, fx)}>{fx.label}</button>)}
              </div>
            )}
            {result && result.id === f.id && <pre className="win-muted" style={{ whiteSpace: 'pre-wrap', maxHeight: 220, overflow: 'auto' }}>{result.text}</pre>}
          </div>
        ))}
        {!!data?.skipped.length && <p className="win-muted">Could not check: {data.skipped.join(', ')}.</p>}
      </div>
    </section>
  );
}
