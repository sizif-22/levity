import { useEffect, useState } from 'react';

export interface CheckpointMsg {
  type: 'checkpoint';
  none?: boolean;
  id?: string;
  added?: number;
  modified?: number;
  deleted?: number;
  insertions?: number;
  deletions?: number;
  changes?: { status: string; path: string }[];
  more?: number;
}

const LABEL: Record<string, string> = { A: 'new', M: 'changed', D: 'deleted', T: 'changed' };

export function checkpointSummary(m: CheckpointMsg): string {
  const added = m.added ?? 0, modified = m.modified ?? 0, deleted = m.deleted ?? 0;
  const n = added + modified + deleted;
  const parts = [modified ? `${modified} changed` : '', added ? `${added} new` : '', deleted ? `${deleted} deleted` : ''].filter(Boolean);
  return `That answer changed ${n} file${n === 1 ? '' : 's'} (${parts.join(', ')}; +${m.insertions ?? 0} −${m.deletions ?? 0})`;
}

/** After an answer in a git folder: what changed, a list of files, and Undo (two presses, because it puts files back on the PC). */
export function CheckpointBar({ data, send, onDismiss }: { data: CheckpointMsg | null; send: (m: Record<string, unknown>) => void; onDismiss: () => void }) {
  const [showFiles, setShowFiles] = useState(false);
  const [armed, setArmed] = useState(false);
  useEffect(() => { setShowFiles(false); setArmed(false); }, [data?.id]);
  useEffect(() => {
    if (!armed) return;
    const t = window.setTimeout(() => setArmed(false), 3000);
    return () => window.clearTimeout(t);
  }, [armed]);
  if (!data || data.none || !data.id) return null;
  return (
    <div className="checkpoint-bar" id="checkpointBar">
      <div className="checkpoint-row">
        <span>{checkpointSummary(data)}</span>
        <span className="checkpoint-actions">
          <button className="btn ghost" onClick={() => setShowFiles((v) => !v)}>View</button>
          <button className={`btn ghost ${armed ? 'danger' : ''}`} onClick={() => { if (armed) { setArmed(false); send({ type: 'checkpoint_undo', id: data.id }); } else setArmed(true); }}>{armed ? 'Sure?' : 'Undo'}</button>
          <button className="icon-btn" title="Dismiss" onClick={onDismiss}><svg viewBox="0 0 24 24"><line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" /></svg></button>
        </span>
      </div>
      {showFiles && (
        <ul className="checkpoint-files">
          {(data.changes ?? []).map((c, i) => <li key={i}>{`${LABEL[c.status] ?? c.status}  ${c.path}`}</li>)}
          {!!data.more && <li>{`... and ${data.more} more`}</li>}
        </ul>
      )}
    </div>
  );
}
