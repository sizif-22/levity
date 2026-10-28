import { useEffect } from 'react';

export interface ToolsState { type: 'tools_state'; enabled: boolean; running: boolean; count: number; canElevate: boolean; elevateNote: string | null }
export interface JournalEntryMsg { id: string; time: string; tool: string; summary: string; reverted: boolean; canUndo: boolean }

type Send = (m: Record<string, unknown>) => void;

/** /windows on the phone: see whether the assistant has Windows tools, what this PC is, and what was changed, with Revert. The switch itself is on the PC. */
export function WindowsSheet({ state, journal, machine, send, onClose }: { state: ToolsState | null; journal: JournalEntryMsg[]; machine: string | null; send: Send; onClose: () => void }) {
  useEffect(() => {
    send({ type: 'tools_get' });
    send({ type: 'journal_list' });
    send({ type: 'machine_get' });
  }, [send]);

  return (
    <section className="sheet open" aria-hidden="false">
      <div className="sheet-head">
        <div><h3>Windows tools</h3><p className="sheet-sub">Let the assistant look at and change Windows, safely</p></div>
        <button className="icon-btn" onClick={onClose} title="Close">
          <svg viewBox="0 0 24 24"><line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" /></svg>
        </button>
      </div>
      <div className="sheet-body win-body">
        <div className="win-card">
          <strong>Windows tools for the assistant</strong>
          <p className="win-muted">
            {!state ? 'Checking...' : state.enabled
              ? (state.running ? `On: ${state.count} tools are available to the assistant.` : 'On, but the tools are not running right now.')
              : 'Off. Turn it on from Levity on your PC.'}
          </p>
          <p className="win-muted">Every change is recorded below first, so it can be undone. Changes that need administrator rights only happen after you approve a Windows prompt on the PC.</p>
        </div>

        <div className="win-card">
          <strong>This PC</strong>
          <p className="win-muted">{machine ?? 'Not read yet.'}</p>
          <div className="diag-actions"><button className="btn ghost" onClick={() => send({ type: 'machine_get', refresh: true })}>Refresh</button></div>
        </div>

        <div>
          <strong>Windows changes</strong>
          {journal.length === 0 && <p className="win-muted">Nothing has been changed yet.</p>}
          <ul className="action-list">
            {journal.map((e) => (
              <li key={e.id} className={`action-row ${e.reverted ? 'reverted' : ''}`}>
                <div className="action-text"><strong>{e.summary}</strong><code>{new Date(e.time).toLocaleString()}{e.reverted ? ' · reverted' : ''}</code></div>
                {!e.reverted && e.canUndo && <button className="btn ghost action-go" onClick={() => send({ type: 'journal_revert', id: e.id })}>Revert</button>}
              </li>
            ))}
          </ul>
        </div>
      </div>
    </section>
  );
}
