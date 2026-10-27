import type { Learn } from '../lib/chat';

const LABEL: Record<Learn['kind'], string> = {
  run: 'Learning',
  skill: 'Saving a skill',
  docs: 'Updating docs',
  notes: 'Taking a note for later',
};

/** Wording for a notice: still running, settled once the work is over, or the closing "done". */
export function learnLabel(l: Learn): string {
  if (l.final) return 'Finished';
  if (l.kind === 'run' && l.phase === 'done') return 'Learned';
  return LABEL[l.kind];
}

/** A notice that Fast Learner (or a skill write) is happening, shown inline in the reply that triggered it. */
export function FastLearnerCard({ l }: { l: Learn }) {
  return (
    <div className={`fl-card fl-${l.kind} ${l.phase === 'running' ? 'running' : 'done'}`} role="status">
      <span className="fl-ico">
        <svg viewBox="0 0 24 24"><path d="M13 2 3 14h9l-1 8 10-12h-9l1-8z" /></svg>
      </span>
      <div className="fl-body">
        <span className="fl-title">{l.source === 'path' ? 'Skills' : 'Fast Learner'} · {learnLabel(l)}</span>
        {l.detail && <span className="fl-detail"> {l.detail}</span>}
      </div>
    </div>
  );
}
