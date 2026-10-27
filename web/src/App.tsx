import { useCallback, useEffect, useReducer, useRef, useState } from 'react';
import { SecureChannel } from './lib/crypto';
import { applyHostMessage, initialChat, isGenerating, type Msg } from './lib/chat';
import { RelayConnection, type LinkState } from './lib/relay';
import { readPairingLink } from './lib/pairing';
import { clearDevice, loadDevice, type DeviceRecord } from './lib/store';
import { hasPin } from './lib/pin';
import { Markdown } from './components/Markdown';
import { JobsSheet, type Job } from './components/JobsSheet';
import { SessionsSheet, type SessionsMsg } from './components/SessionsSheet';
import { CMD_RE, UserText } from './components/UserText';
import { FastLearnerCard } from './components/FastLearnerCard';
import { ProjectsSheet, syncCounts, type ProjectsMsg, type SyncState } from './components/ProjectsSheet';
import { Center, ConfirmDialog, Logo, LockScreen, PairScreen, SettingsSheet } from './components/Screens';

type Phase = 'loading' | 'pair' | 'locked' | 'main';
type Json = Record<string, unknown>;

const LOCK_AFTER_MS = 60_000;

/** A finished reply with nothing in it (agy emits empty steps) is not worth a row. */
const visible = (m: Msg) => m.role === 'user' || !m.done || !!(m.text || m.thinking || m.tools.length || m.learn?.length || m.error);

export default function App() {
  const [phase, setPhase] = useState<Phase>('loading');
  const [device, setDevice] = useState<DeviceRecord | null>(null);
  const [link] = useState(() => readPairingLink(location.hash));
  const hiddenAt = useRef<number | null>(null);

  useEffect(() => {
    void loadDevice().then((d) => {
      setDevice(d ?? null);
      // A fresh pairing QR always wins over stored credentials.
      setPhase(link ? 'pair' : d ? (hasPin() ? 'locked' : 'main') : 'pair');
    });
  }, [link]);

  // Scanning a pairing QR while the app is already open only changes the URL fragment: reload to pick it up.
  useEffect(() => {
    const onHash = () => { if (readPairingLink(location.hash)) location.reload(); };
    window.addEventListener('hashchange', onHash);
    return () => window.removeEventListener('hashchange', onHash);
  }, []);

  // iOS does not resize the layout when the keyboard opens: follow the visible viewport so the composer stays in view.
  useEffect(() => {
    const vv = window.visualViewport;
    if (!vv) return;
    const root = document.documentElement;
    const update = () => {
      const keyboardOpen = window.innerHeight - vv.height > 120;
      if (keyboardOpen) root.style.setProperty('--app-h', `${vv.height}px`); else root.style.removeProperty('--app-h');
      if (keyboardOpen) window.scrollTo(0, 0);
    };
    vv.addEventListener('resize', update);
    vv.addEventListener('scroll', update);
    return () => { vv.removeEventListener('resize', update); vv.removeEventListener('scroll', update); root.style.removeProperty('--app-h'); };
  }, []);

  // Re-lock after the app has been in the background for a minute.
  useEffect(() => {
    const onVisibility = () => {
      if (document.hidden) { hiddenAt.current = Date.now(); return; }
      if (hiddenAt.current && Date.now() - hiddenAt.current > LOCK_AFTER_MS && hasPin()) setPhase((p) => (p === 'main' ? 'locked' : p));
      hiddenAt.current = null;
    };
    document.addEventListener('visibilitychange', onVisibility);
    return () => document.removeEventListener('visibilitychange', onVisibility);
  }, []);

  if (phase === 'loading') return <div className="app" />;
  if (phase === 'pair') return <div className="app"><PairScreen link={link} onPaired={(d) => { setDevice(d); setPhase('main'); }} /></div>;
  if (phase === 'locked') return <div className="app"><LockScreen onUnlock={() => setPhase('main')} /></div>;
  return (
    <Session
      device={device!}
      onUnpair={async () => { await clearDevice(); setDevice(null); setPhase('pair'); }}
    />
  );
}

function Session({ device, onUnpair }: { device: DeviceRecord; onUnpair: () => void }) {
  const [chat, dispatch] = useReducer(applyHostMessage, initialChat);
  const [linkState, setLinkState] = useState<LinkState>('connecting');
  const [projects, setProjects] = useState<ProjectsMsg | null>(null);
  const [scanning, setScanning] = useState(false);
  const [sync, setSync] = useState<SyncState | null>(null);
  const [sheet, setSheet] = useState<'none' | 'projects' | 'settings' | 'jobs' | 'history'>('none');
  const [sessions, setSessions] = useState<SessionsMsg | null>(null);
  const [queue, setQueue] = useState<{ id: number; text: string }[]>([]);
  const [jobs, setJobs] = useState<Job[]>([]);
  const [syncSeen, setSyncSeen] = useState(true);
  const [toastText, setToastText] = useState('');
  const [dialog, setDialog] = useState<{ title: string; text: string; ok: string; resolve: (v: boolean) => void } | null>(null);
  const [draft, setDraft] = useState('');

  const conn = useRef<RelayConnection | null>(null);
  const viewport = useRef<HTMLElement | null>(null);
  const stick = useRef(true);
  const toastTimer = useRef<number>();
  const syncRef = useRef<SyncState | null>(null);
  const composer = useRef<HTMLTextAreaElement | null>(null);
  const hl = useRef<HTMLDivElement | null>(null);
  syncRef.current = sync;

  const toast = useCallback((t: string) => {
    setToastText(t);
    window.clearTimeout(toastTimer.current);
    toastTimer.current = window.setTimeout(() => setToastText(''), 3200);
  }, []);

  const confirm = useCallback((title: string, text: string, ok: string) =>
    new Promise<boolean>((resolve) => setDialog({ title, text, ok, resolve })), []);

  const send = useCallback((m: Json) => { void conn.current?.send(m); }, []);

  useEffect(() => {
    const channel = new SecureChannel(device.key);
    const c = new RelayConnection(device.relay, device.roomId, channel, {
      onState: setLinkState,
      onOpen: () => {
        dispatch({ type: 'clear_chat' });        // the PC replays the whole conversation after "ready"
        void c.send({ type: 'ready' });
        void c.send({ type: 'list_projects' });
      },
      onMessage: (msg) => {
        const type = msg.type as string;
        if (type === 'sessions') { setSessions(msg as unknown as SessionsMsg); return; }
        if (type === 'fast_learner') { toast(String(msg.text ?? '')); dispatch(msg); return; }
        if (type === 'queue') { setQueue((msg.items as { id: number; text: string }[]) ?? []); return; }
        if (type === 'jobs') { setJobs((msg.jobs as Job[]) ?? []); return; }
        if (type === 'projects') { setProjects(msg as unknown as ProjectsMsg); setScanning(false); return; }
        if (type === 'sync_state') {
          const next = msg as unknown as SyncState;
          if (!next.running && next.items.length === 0) { setSync(null); return; } // the PC has no review: clear a stale one
          const was = syncRef.current;
          if (was?.running && !next.running) {
            const k = syncCounts(next.items);
            toast(`Review finished: ${k.ok} done${k.failed ? `, ${k.failed} failed` : ''}`);
            setSyncSeen(false);
          }
          setSync(next);
          return;
        }
        if (type === 'toast') {
          toast(String(msg.text ?? ''));
          if (syncRef.current?.optimistic && syncRef.current.running) setSync(null); // PC refused the review start
          return;
        }
        dispatch(msg);
      },
    });
    conn.current = c;
    c.start();
    return () => c.close();
  }, [device, toast]);

  // Keep the newest message in view unless the user scrolled up.
  useEffect(() => {
    const el = viewport.current;
    if (el && stick.current) el.scrollTo({ top: el.scrollHeight });
  }, [chat.msgs]);

  const generating = isGenerating(chat);
  const online = linkState === 'online';
  const statusText = linkState === 'online' ? chat.status.text
    : linkState === 'host-offline' ? 'PC offline' : linkState === 'connecting' ? 'Connecting…' : 'Reconnecting…';
  const statusState = online ? chat.status.state : 'offline';

  // The box grows with long text by setting an inline height; shrink it back once the draft is cleared (sent or emptied).
  useEffect(() => {
    if (!draft && composer.current) composer.current.style.height = 'auto';
  }, [draft]);

  const submit = () => {
    const text = draft.trim();
    if (!text || !online) return; // while busy the PC queues it
    send({ type: 'send_prompt', text });
    setDraft('');
  };

  const openProjects = () => {
    setSheet('projects');
    setSyncSeen(true);
    setScanning(true);
    send({ type: 'list_projects' });
  };

  const k = sync ? syncCounts(sync.items) : null;
  const showBanner = sheet !== 'projects' && sync && sync.items.length > 0 && (sync.running || !syncSeen);
  const cwdName = chat.cwd.split(/[\\/]/).filter(Boolean).pop() || (chat.cwd || 'Loading…');

  return (
    <div className="app">
      <header className="app-header">
        <div className="brand">
          <div className="logo-wrap">
            <svg className="logo" viewBox="0 0 64 64" fill="none">
              <circle className="logo-orb" cx="32" cy="13" r="6" />
              <circle cx="32" cy="38" r="15" strokeWidth="7" />
            </svg>
            <span className={`status-dot ${statusState}`} />
          </div>
          <div className="brand-text">
            <span className="brand-title">Levity</span>
            <span className="status-label">{sync?.running && k ? `Reviewing ${Math.min(k.finished + 1, sync.items.length)}/${sync.items.length}` : statusText}</span>
          </div>
        </div>
        <div className="header-actions">
          <button className="icon-btn" onClick={openProjects} title="Projects & review">
            <svg viewBox="0 0 24 24"><rect x="3" y="3" width="7" height="7" rx="1.5" /><rect x="14" y="3" width="7" height="7" rx="1.5" /><rect x="3" y="14" width="7" height="7" rx="1.5" /><rect x="14" y="14" width="7" height="7" rx="1.5" /></svg>
          </button>
          <button className="icon-btn" onClick={() => { setSheet('history'); send({ type: 'list_sessions' }); }} title="Chat history">
            <svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9" /><polyline points="12 7 12 12 15 14" /></svg>
          </button>
          <button className="icon-btn" onClick={() => setSheet('jobs')} title="Background jobs">
            <svg viewBox="0 0 24 24"><polyline points="4 17 10 11 4 5" /><line x1="12" y1="19" x2="20" y2="19" /></svg>
            {jobs.some((j) => j.state === 'running') && <span className="badge">{jobs.filter((j) => j.state === 'running').length}</span>}
          </button>
          <button className="icon-btn" onClick={() => send({ type: 'new_chat' })} title="New conversation">
            <svg viewBox="0 0 24 24"><line x1="12" y1="5" x2="12" y2="19" /><line x1="5" y1="12" x2="19" y2="12" /></svg>
          </button>
          <button className="icon-btn" onClick={() => setSheet('settings')} title="This phone">
            <svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="3" /><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z" /></svg>
          </button>
        </div>
      </header>

      <button className="cwd-pill" onClick={openProjects}>
        <svg viewBox="0 0 24 24"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z" /></svg>
        <span title={chat.cwd}><bdi>{chat.cwd || cwdName}</bdi></span>
        <svg className="chev" viewBox="0 0 24 24"><polyline points="6 9 12 15 18 9" /></svg>
      </button>

      {linkState === 'host-offline' && (
        <div className="sync-banner failed"><span className="sync-banner-text">Your PC is offline. Start Levity on it and this reconnects automatically.</span></div>
      )}

      {showBanner && sync && k && (
        <button className={`sync-banner ${sync.running ? '' : k.failed ? 'failed' : 'done'}`} onClick={openProjects}>
          {sync.running && <span className="spinner" />}
          <span className="sync-banner-text">
            {sync.running
              ? `Reviewing ${Math.min(k.finished + 1, sync.items.length)} of ${sync.items.length}`
              : `Review finished · ${k.ok} done${k.failed ? `, ${k.failed} failed` : ''}`}
          </span>
          <span className="sync-banner-link">View</span>
        </button>
      )}

      <main
        className="chat-viewport"
        ref={viewport}
        onScroll={(e) => {
          const el = e.currentTarget;
          stick.current = el.scrollHeight - el.scrollTop <= el.clientHeight + 60;
        }}
      >
        {chat.msgs.length === 0 ? (
          <div className="welcome">
            <Logo />
            <h2>What are we building?</h2>
            <p className="welcome-sub">You are controlling Levity on your PC.</p>
            <div className="chips">
              <button className="chip chip-primary" onClick={openProjects}>
                <span className="chip-ico"><svg viewBox="0 0 24 24"><path d="M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9z" /></svg></span>
                <span><strong>Review all my projects</strong><small>Refresh docs and skills across your drive</small></span>
              </button>
              <button className="chip" onClick={() => send({ type: 'open_ide', path: chat.cwd })}>
                <span className="chip-ico"><svg viewBox="0 0 24 24"><polyline points="16 18 22 12 16 6"/><polyline points="8 6 2 12 8 18"/></svg></span>
                <span><strong>Open in Antigravity IDE</strong><small>Opens the current project on your PC</small></span>
              </button>
              {['What files and structure are in the current working directory?', 'Check the git status and recent commits of this repository.', 'Explain what this project does and recommend next steps.'].map((t, i) => (
                <button key={t} className="chip" onClick={() => send({ type: 'send_prompt', text: t })}>
                  <span><strong>{['Inspect workspace', 'Git status', 'Project overview'][i]}</strong></span>
                </button>
              ))}
            </div>
          </div>
        ) : (
          <div className="messages-list">
            {chat.msgs.filter(visible).map((m) => m.role === 'user' ? (
              <div key={m.id} className="message-row user">
                <div className={`user-bubble${CMD_RE.test(m.text) ? ' cmd-bubble' : ''}`}><UserText text={m.text} /></div>
                {m.source === 'desktop' && <span className="via">from your PC</span>}
              </div>
            ) : (
              <div key={m.id} className="message-row agent">
                <div className="agent-header">
                  <div className="agent-avatar"><svg viewBox="0 0 24 24"><circle cx="12" cy="5" r="2" /><circle cx="12" cy="15" r="6" /></svg></div>
                  <span className="agent-name">Levity</span>
                </div>
                <div className="agent-bubble">
                  <div className="agent-content">
                    {m.thinking.trim() && (
                      <details className="thinking-box" open>
                        <summary className="thinking-summary">Reasoning</summary>
                        <div className="thinking-content">{m.thinking}</div>
                      </details>
                    )}
                    {m.text && <Markdown text={m.text} />}
                    {m.tools.map((t) => (
                      <div key={t.key} className={`tool-pill ${t.running ? 'running' : ''}`}>
                        {t.running && <span className="spinner mini" />}<strong>{t.name}</strong> {t.args}
                      </div>
                    ))}
                    {(m.learn ?? []).map((l) => <FastLearnerCard key={l.id} l={l} />)}
                    {!m.text && !m.done && <div className="typing" aria-label="Levity is typing"><i /><i /><i /></div>}
                    {m.error && <div className="error-banner">{m.error}</div>}
                  </div>
                  {m.stats && <div className="turn-stats">{m.stats}</div>}
                </div>
              </div>
            ))}
          </div>
        )}
      </main>

      <footer className="composer-wrap">
        {queue.length > 0 && (
          <div className="queue">
            {queue.map((q) => (
              <div key={q.id} className="queue-item">
                <span className="queue-tag">Queued</span><span className="queue-text">{q.text}</span>
                <button className="icon-btn" title="Remove from queue" onClick={() => send({ type: 'queue_remove', id: q.id })}>
                  <svg viewBox="0 0 24 24"><line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" /></svg>
                </button>
              </div>
            ))}
          </div>
        )}
        <div className="quick-commands">
          {['/plan', '/goal', '/boost', '/compact', '/help'].map((c) => (
            <span key={c} className="cmd-chip" onClick={() => setDraft(c + ' ')}>{c}</span>
          ))}
          <span className="cmd-chip" title="Run a CLI command directly (no AI), e.g. /cmd{$git status}. Add -b to run it in the background." onClick={() => { setDraft('/cmd{$}'); setTimeout(() => { composer.current?.focus(); composer.current?.setSelectionRange(6, 6); }, 0); }}>/cmd</span>
        </div>
        <div className="composer">
          <div className="input-stack">
          <div className="composer-hl" ref={hl} aria-hidden="true">{CMD_RE.test(draft) && <><UserText text={draft} />{'\u200b'}</>}</div>
          <textarea
            className={CMD_RE.test(draft) ? 'hl-on' : ''} spellCheck={!CMD_RE.test(draft)} autoCorrect="off" autoCapitalize="off"
            onScroll={(e) => { if (hl.current) hl.current.scrollTop = e.currentTarget.scrollTop; }}
            ref={composer} rows={1} value={draft} placeholder={online ? 'Ask Levity anything… or /cmd{$git status}' : 'Waiting for your PC…'}
            onChange={(e) => { setDraft(e.target.value); e.target.style.height = 'auto'; e.target.style.height = Math.min(e.target.scrollHeight, 180) + 'px'; }}
          />
          </div>
          {generating && (
            <button className="send-btn stop-btn" onClick={() => send({ type: 'stop_response' })} title="Stop the response">
              <svg viewBox="0 0 24 24"><rect x="6" y="6" width="12" height="12" rx="2" fill="currentColor" /></svg>
            </button>
          )}
          <button className="send-btn" disabled={!draft.trim() || !online} onClick={submit} title="Send (queued while Levity is busy)">
            <svg viewBox="0 0 24 24"><line x1="12" y1="19" x2="12" y2="5" /><polyline points="5 12 12 5 19 12" /></svg>
          </button>
        </div>
      </footer>

      {sheet === 'projects' && (
        <ProjectsSheet
          data={projects} scanning={scanning} sync={sync} confirm={confirm} send={(m) => { if (m.type === 'set_root') setScanning(true); send(m); }}
          onClose={() => setSheet('none')}
          startSync={(paths, mode) => {
            setSync({ running: true, mode, optimistic: true, items: paths.map((p) => ({ name: projects?.projects.find((x) => x.path === p)?.name ?? p, path: p, status: 'pending', summary: '', seconds: 0 })) });
            send({ type: 'sync_start', paths, mode });
          }}
        />
      )}
      {sheet === 'history' && <SessionsSheet data={sessions} send={send} onClose={() => setSheet('none')} />}
      {sheet === 'jobs' && <JobsSheet jobs={jobs} send={send} onClose={() => setSheet('none')} />}
      {sheet === 'settings' && (
        <SettingsSheet device={device} onClose={() => setSheet('none')} onUnpair={onUnpair} confirm={confirm} toast={toast} />
      )}

      {dialog && <ConfirmDialog {...dialog} onResult={(v) => { dialog.resolve(v); setDialog(null); }} />}
      <div className={`toast ${toastText ? 'show' : ''}`} role="status">{toastText}</div>
      {!online && linkState === 'offline' && <Center><span className="spinner" /></Center>}
    </div>
  );
}
