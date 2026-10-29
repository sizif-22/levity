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
import { DiagramSheet } from './components/DiagramSheet';
import { GitSheet, RunSheet } from './components/ActionSheets';
import { WindowsSheet, type JournalEntryMsg, type ToolsState } from './components/WindowsSheet';
import { SpeedSheet, type SpeedMsg } from './components/SpeedSheet';
import { recognizerCtor, appendDictation, finalText, isIosStandalone, micDeniedHelp, type Recognizer } from './lib/voice';
import { DoctorSheet, type DoctorMsg, type DoctorResultMsg } from './components/DoctorSheet';
import { CheckpointBar, type CheckpointMsg } from './components/CheckpointBar';
import type { ActionsMsg, GitStateMsg } from './lib/actions';
import { onboardingChips } from './lib/onboarding';
import { sameFolder, type DiagramMsg, type DiagramPrepared, type DiagramState } from './lib/diagram';
import { ProjectsSheet, syncCounts, type ProjectsMsg, type SyncState } from './components/ProjectsSheet';
import { ConfirmDialog, Logo, LockScreen, PairScreen, SettingsSheet } from './components/Screens';

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
  const [sheet, setSheet] = useState<'none' | 'projects' | 'settings' | 'jobs' | 'history' | 'diagram' | 'run' | 'git' | 'windows' | 'speed' | 'doctor'>('none');
  const [speed, setSpeed] = useState<SpeedMsg | null>(null);
  const [listening, setListening] = useState(false);
  const recog = useRef<Recognizer | null>(null);
  const iosInstalled = isIosStandalone();
  const canDictate = recognizerCtor() !== null && !iosInstalled;   // the keyboard's own microphone works there instead
  const [doctor, setDoctor] = useState<DoctorMsg | null>(null);
  const [doctorResult, setDoctorResult] = useState<DoctorResultMsg | null>(null);
  const [checkpoint, setCheckpoint] = useState<CheckpointMsg | null>(null);
  const [toolsState, setToolsState] = useState<ToolsState | null>(null);
  const [journal, setJournal] = useState<JournalEntryMsg[]>([]);
  const [machine, setMachine] = useState<string | null>(null);
  const [actions, setActions] = useState<ActionsMsg | null>(null);
  const [mode, setMode] = useState<'auto' | 'plan'>('auto');   // Plan: the assistant only plans and proposes
  const [effort, setEffort] = useState<'low' | 'medium' | 'high'>('low');   // Fast (low) or Deep (medium/high), set on the PC
  const [gitState, setGitState] = useState<GitStateMsg | null>(null);
  const [diagPath, setDiagPath] = useState('');
  const diagPathRef = useRef('');
  const [diagram, setDiagram] = useState<DiagramMsg | null>(null);
  const [diagPrepared, setDiagPrepared] = useState<DiagramPrepared | null>(null);
  const [diagState, setDiagState] = useState<DiagramState | null>(null);
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

  const chips = onboardingChips(actions, projects?.projects.length ?? 0);
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
        if (type === 'config' && msg.mode !== undefined) setMode(msg.mode === 'plan' ? 'plan' : 'auto');
        if (type === 'config' && msg.effort !== undefined) setEffort(msg.effort === 'high' || msg.effort === 'medium' ? msg.effort : 'low');
        if (type === 'sessions') { setSessions(msg as unknown as SessionsMsg); return; }
        if (type === 'diagram' || type === 'diagram_none' || type === 'diagram_prepared' || type === 'diagram_state') {
          if (!sameFolder(String(msg.path ?? ''), diagPathRef.current)) return;   // about another project
          if (type === 'diagram') { setDiagram(msg as unknown as DiagramMsg); setDiagState(null); }
          else if (type === 'diagram_none') { setDiagram(null); setDiagState(null); }
          else if (type === 'diagram_prepared') { setDiagPrepared(msg as unknown as DiagramPrepared); setDiagState(null); }
          else setDiagState(msg as unknown as DiagramState);
          return;
        }
        if (type === 'fast_learner') { toast(String(msg.text ?? '')); dispatch(msg); return; }
        if (type === 'queue') { setQueue((msg.items as { id: number; text: string }[]) ?? []); return; }
        if (type === 'jobs') { setJobs((msg.jobs as Job[]) ?? []); return; }
        if (type === 'checkpoint') { setCheckpoint(msg as unknown as CheckpointMsg); return; }
        if (type === 'speed') { setSpeed(msg as unknown as SpeedMsg); return; }
        if (type === 'doctor') { setDoctor(msg as unknown as DoctorMsg); setDoctorResult(null); return; }
        if (type === 'doctor_result') { setDoctorResult(msg as unknown as DoctorResultMsg); return; }
        if (type === 'tools_state') { setToolsState(msg as unknown as ToolsState); return; }
        if (type === 'journal') { setJournal((msg.entries as JournalEntryMsg[]) ?? []); return; }
        if (type === 'machine') { setMachine(String(msg.summary ?? '')); return; }
        if (type === 'actions') { setActions(msg as unknown as ActionsMsg); return; }
        if (type === 'git_state') { setGitState(msg as unknown as GitStateMsg); return; }
        if (type === 'jobs_open') { setSheet('jobs'); return; }   // a command is waiting for an answer
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

  const openDiagram = useCallback((path: string) => {
    if (!path) return;
    diagPathRef.current = path;
    setDiagPath(path);
    setDiagram(null);
    setDiagPrepared(null);
    setDiagState(null);
    setSheet('diagram');
    send({ type: 'diagram_get', path });
  }, [send]);

  const toggleDictation = () => {
    if (recog.current) { recog.current.stop(); return; }
    const Ctor = recognizerCtor();
    if (!Ctor) return;
    const r = new Ctor();
    r.lang = navigator.language || 'en-US';
    r.continuous = true;
    r.interimResults = true;
    r.onresult = (e) => { const t = finalText(e); if (t) setDraft((d) => appendDictation(d, t)); };
    r.onerror = (e) => { if (e.error === 'not-allowed' || e.error === 'service-not-allowed') toast(micDeniedHelp(window.matchMedia?.('(display-mode: standalone)').matches ?? false, iosInstalled)); };
    r.onend = () => { recog.current = null; setListening(false); };
    recog.current = r;
    setListening(true);
    try { r.start(); } catch { recog.current = null; setListening(false); }
  };

  const submit = () => {
    const text = draft.trim();
    if (!text || !online) return; // while busy the PC queues it
    if (text.toLowerCase() === '/diagram') { setDraft(''); openDiagram(chat.cwd); return; } // a view, not a chat message
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
              {chips.reviewAll && (
                <button className="chip chip-primary" onClick={openProjects}>
                  <span className="chip-ico"><svg viewBox="0 0 24 24"><path d="M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9z" /></svg></span>
                  <span><strong>Review all my projects</strong><small>Refresh docs and skills across your drive</small></span>
                </button>
              )}
              {chips.newProject && (
                <button className="chip" onClick={() => send({ type: 'send_prompt', text: 'This folder is empty. Ask me what I want to build here, then set up a sensible starting project.' })}>
                  <span className="chip-ico"><svg viewBox="0 0 24 24"><line x1="12" y1="5" x2="12" y2="19" /><line x1="5" y1="12" x2="19" y2="12" /></svg></span>
                  <span><strong>Start a new project</strong><small>This folder is empty</small></span>
                </button>
              )}
              {chips.inspect && <button className="chip" onClick={() => send({ type: 'send_prompt', text: 'What files and structure are in the current working directory?' })}><span><strong>Inspect workspace</strong></span></button>}
              {chips.git && <button className="chip" onClick={() => send({ type: 'send_prompt', text: 'Check the git status and recent commits of this repository.' })}><span><strong>Git status</strong></span></button>}
              {chips.tests && <button className="chip" onClick={() => send({ type: 'send_prompt', text: `Run the project's "${chips.testScript}" script and summarize any failures.` })}><span><strong>Run the tests</strong></span></button>}
              {chips.overview && <button className="chip" onClick={() => send({ type: 'send_prompt', text: 'Explain what this project does and recommend next steps.' })}><span><strong>Project overview</strong></span></button>}
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
        <CheckpointBar data={checkpoint} send={send} onDismiss={() => setCheckpoint(null)} />
        <div className="quick-commands">
          {!!actions?.git.repo && <span className="cmd-chip" title="Draw this project's architecture" onClick={() => openDiagram(chat.cwd)}>/diagram</span>}
          <button className={`cmd-chip effort-chip ${mode === 'plan' ? 'deep' : ''}`} aria-pressed={mode === 'plan'}
            title="Plan: the assistant only plans and proposes, it changes nothing. Work: it does the task." onClick={() => send({ type: 'set_mode', value: mode === 'plan' ? 'auto' : 'plan' })}>
            {mode === 'plan' ? 'Plan' : 'Work'}
          </button>
          <button className={`cmd-chip effort-chip ${effort !== 'low' ? 'deep' : ''}`} aria-pressed={effort !== 'low'}
            title="Fast: quick answers. Deep: thinks harder, slower." onClick={() => send({ type: 'set_effort', value: effort === 'low' ? 'high' : 'low' })}>
            {effort === 'low' ? 'Fast' : 'Deep'}
          </button>
          <span className="cmd-chip" title="Scan this PC for problems: disk, memory, restarts, network, event log" onClick={() => { setDoctor(null); setDoctorResult(null); setSheet('doctor'); }}>/doctor</span>
          <span className="cmd-chip" title="Where the time and tokens of this conversation went" onClick={() => { setSpeed(null); setSheet('speed'); }}>/speed</span>
          {!!chat.cwd && <span className="cmd-chip" title="Open this folder in the Antigravity IDE on your PC" onClick={() => send({ type: 'open_ide', path: chat.cwd })}>/open-ide</span>}
          {!!actions?.scripts.length && <span className="cmd-chip" title="Run a script from this folder's package.json" onClick={() => setSheet('run')}>/run</span>}
          {!!actions?.git.repo && <span className="cmd-chip" title="Common git steps: add, commit, push..." onClick={() => { setGitState(null); setSheet('git'); }}>/git</span>}
          <span className="cmd-chip" title="Run a CLI command directly (no AI), e.g. /cmd$git status. A slow command moves to the Jobs panel by itself." onClick={() => { setDraft('/cmd$'); setTimeout(() => { composer.current?.focus(); composer.current?.setSelectionRange(5, 5); }, 0); }}>/cmd</span>
        </div>
        <div className="composer">
          <div className="input-stack">
          <div className="composer-hl" ref={hl} aria-hidden="true">{CMD_RE.test(draft) && <><UserText text={draft} />{'\u200b'}</>}</div>
          <textarea
            className={CMD_RE.test(draft) ? 'hl-on' : ''} spellCheck={!CMD_RE.test(draft)} autoCorrect="off" autoCapitalize="off"
            onScroll={(e) => { if (hl.current) hl.current.scrollTop = e.currentTarget.scrollTop; }}
            ref={composer} rows={1} value={draft} placeholder={online ? 'Ask Levity anything… or /cmd$git status' : 'Waiting for your PC…'}
            onChange={(e) => { setDraft(e.target.value); e.target.style.height = 'auto'; e.target.style.height = Math.min(e.target.scrollHeight, 180) + 'px'; }}
          />
          </div>
          {canDictate && !generating && (
            <button className={`send-btn mic-btn ${listening ? 'listening' : ''}`} aria-pressed={listening} disabled={!online}
              title={listening ? 'Stop dictating' : 'Dictate (your phone browser turns speech into text; you check it before sending)'} onClick={toggleDictation}>
              <svg viewBox="0 0 24 24"><rect x="9" y="3" width="6" height="11" rx="3" /><path d="M5 11a7 7 0 0 0 14 0" /><line x1="12" y1="18" x2="12" y2="22" /></svg>
            </button>
          )}
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
          onDiagram={(path) => openDiagram(path)}
          startSync={(paths, mode) => {
            setSync({ running: true, mode, optimistic: true, items: paths.map((p) => ({ name: projects?.projects.find((x) => x.path === p)?.name ?? p, path: p, status: 'pending', summary: '', seconds: 0 })) });
            send({ type: 'sync_start', paths, mode });
          }}
        />
      )}
      {sheet === 'diagram' && (
        <DiagramSheet path={diagPath} data={diagram} prepared={diagPrepared} state={diagState} send={send}
          onClearPrepared={() => setDiagPrepared(null)} onClose={() => { setSheet('none'); setDiagPrepared(null); }} />
      )}
      {sheet === 'doctor' && <DoctorSheet data={doctor} result={doctorResult} send={send} onClose={() => setSheet('none')} onScan={() => { setDoctor(null); setDoctorResult(null); }} />}
      {sheet === 'speed' && <SpeedSheet data={speed} send={send} onClose={() => setSheet('none')} />}
      {sheet === 'windows' && <WindowsSheet state={toolsState} journal={journal} machine={machine} send={send} onClose={() => setSheet('none')} />}
      {sheet === 'run' && <RunSheet actions={actions} send={send} onClose={() => setSheet('none')} />}
      {sheet === 'git' && <GitSheet state={gitState} send={send} onClose={() => setSheet('none')} />}
      {sheet === 'history' && <SessionsSheet data={sessions} send={send} onClose={() => setSheet('none')} />}
      {sheet === 'jobs' && <JobsSheet jobs={jobs} send={send} onClose={() => setSheet('none')} />}
      {sheet === 'settings' && (
        <SettingsSheet device={device} onClose={() => setSheet('none')} onUnpair={onUnpair} confirm={confirm} toast={toast} onOpenWindows={() => setSheet('windows')} />
      )}

      {dialog && <ConfirmDialog {...dialog} onResult={(v) => { dialog.resolve(v); setDialog(null); }} />}
      <div className={`toast ${toastText ? 'show' : ''}`} role="status">{toastText}</div>
      {!online && linkState === 'offline' && <div className="reconnect-spin" role="status" aria-label="Reconnecting"><span className="spinner big" /></div>}
    </div>
  );
}
