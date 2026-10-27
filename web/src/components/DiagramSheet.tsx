import { useCallback, useEffect, useRef, useState } from 'react';
import { SUPPORT_TEXT, toneClass, type DiagramMsg, type DiagramPrepared, type DiagramState } from '../lib/diagram';
import { sanitizeSvg, svgSize } from '../lib/svg';
import { diagramMermaidConfig } from '../lib/diagramTheme';

/** The part of Mermaid this view uses. Injected in tests; loaded on demand in the app so it never slows down startup. */
export interface MermaidApi {
  initialize(config: Record<string, unknown>): void;
  render(id: string, source: string): Promise<{ svg: string }>;
}
const loadMermaidDefault = async (): Promise<MermaidApi> => (await import('mermaid')).default as unknown as MermaidApi;

interface Props {
  path: string;
  data: DiagramMsg | null;
  prepared: DiagramPrepared | null;
  state: DiagramState | null;
  send: (m: Record<string, unknown>) => void;
  onClearPrepared: () => void;
  onClose: () => void;
  loadMermaid?: () => Promise<MermaidApi>;
}

/** A button that must be pressed twice (for actions that change files or data). */
function TwoStep({ label, onConfirm, id }: { label: string; onConfirm: () => void; id: string }) {
  const [armed, setArmed] = useState(false);
  useEffect(() => {
    if (!armed) return;
    const t = window.setTimeout(() => setArmed(false), 3000);
    return () => window.clearTimeout(t);
  }, [armed]);
  return (
    <button id={id} className={`btn ghost ${armed ? 'danger' : ''}`} onClick={() => { if (armed) { setArmed(false); onConfirm(); } else setArmed(true); }}>
      {armed ? 'Sure?' : label}
    </button>
  );
}

export function DiagramSheet({ path, data, prepared, state, send, onClearPrepared, onClose, loadMermaid = loadMermaidDefault }: Props) {
  const [starting, setStarting] = useState(false);
  const [flat, setFlat] = useState(false);
  const [drawError, setDrawError] = useState<string | null>(null);
  const [asking, setAsking] = useState(false);     // waiting for the PC to read the project before the confirmation appears
  const [full, setFull] = useState(false);         // the diagram fills the whole screen
  const touched = useRef(false);                   // the user has moved or zoomed, so stop re-fitting on resize
  const stage = useRef<HTMLDivElement>(null);
  const canvas = useRef<HTMLDivElement>(null);
  const view = useRef({ x: 0, y: 0, s: 1 });
  const moved = useRef(false);
  const draw = useRef(0);

  const running = !!state && (state.phase === 'ingest' || state.phase === 'generating' || state.phase === 'validating');
  // A fresh confirmation always wins over an old error; running always wins over everything.
  const panel = running || starting ? 'progress' : prepared ? 'confirm' : state?.phase === 'error' ? 'error' : data ? 'view' : 'empty';

  useEffect(() => { setStarting(false); }, [state, data]);
  useEffect(() => { setAsking(false); }, [state, data, prepared]);
  useEffect(() => {
    if (!asking) return;
    const t = window.setTimeout(() => setAsking(false), 45000);   // never leave a button stuck if the PC does not answer
    return () => window.clearTimeout(t);
  }, [asking]);
  useEffect(() => {
    if (!full) return;
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') setFull(false); };
    document.addEventListener('keydown', esc);
    return () => document.removeEventListener('keydown', esc);
  }, [full]);

  const apply = useCallback(() => {
    if (stage.current) stage.current.style.transform = `translate(${view.current.x}px, ${view.current.y}px) scale(${view.current.s})`;
  }, []);

  const fit = useCallback(() => {
    const svg = stage.current?.querySelector('svg');
    const box = canvas.current;
    const size = svg ? svgSize(svg) : null;
    if (!svg || !box || !size) return;
    svg.setAttribute('width', String(size.w));
    svg.setAttribute('height', String(size.h));
    (svg as unknown as HTMLElement).style.maxWidth = 'none';
    const cw = box.clientWidth, ch = box.clientHeight;
    if (cw > 0 && ch > 0) {
      const s = Math.max(0.05, Math.min(cw / size.w, ch / size.h, 1.5));
      view.current = { s, x: (cw - size.w * s) / 2, y: (ch - size.h * s) / 2 };
    } else view.current = { x: 0, y: 0, s: 1 };
    apply();
  }, [apply]);

  const zoom = useCallback((factor: number, cx?: number, cy?: number) => {
    const box = canvas.current;
    const x = cx ?? (box ? box.clientWidth / 2 : 0), y = cy ?? (box ? box.clientHeight / 2 : 0);
    touched.current = true;
    const next = Math.max(0.05, Math.min(6, view.current.s * factor));
    const k = next / view.current.s;
    view.current = { x: x - (x - view.current.x) * k, y: y - (y - view.current.y) * k, s: next };
    apply();
  }, [apply]);

  // Draw (or redraw) whenever a new diagram arrives. A newer diagram cancels an older one that is still being drawn.
  useEffect(() => {
    if (!data || panel !== 'view' || !stage.current) return;
    const mine = ++draw.current;
    const host = stage.current;
    host.textContent = '';
    setFlat(false);
    setDrawError(null);

    const drawOne = async (mermaid: MermaidApi, id: string, source: string) => {
      const out = await mermaid.render(id, source);
      const svg = sanitizeSvg(out.svg);
      if (!svg) throw new Error('The drawing was rejected as unsafe');
      return svg;
    };

    (async () => {
      try {
        const mermaid = await loadMermaid();
        const dark = window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false;
        mermaid.initialize(diagramMermaidConfig(dark));

        // Mermaid's layout can fail on boxed groups with many crossing relationships; the flat version (groups by colour) always lays out.
        let svg: SVGSVGElement | null = null;
        let usedFlat = false;
        try { svg = await drawOne(mermaid, `diagramSvg${mine}`, data.mermaid); }
        catch (e) { if (!data.mermaidFlat || data.mermaidFlat === data.mermaid) throw e; }
        if (mine !== draw.current) return;
        if (!svg) {
          usedFlat = true;
          svg = await drawOne(mermaid, `diagramSvgFlat${mine}`, data.mermaidFlat!);
          if (mine !== draw.current) return;
        }
        host.appendChild(svg);
        setFlat(usedFlat);

        const byIndex = new Map(data.nodes.map((n) => [n.i, n]));
        svg.querySelectorAll('g.node').forEach((g) => {
          const m = /(?:^|-)n(\d+)(?:-\d+)?$/.exec(g.id || '');
          const node = m ? byIndex.get(Number(m[1])) : undefined;
          if (!node) return;
          const title = document.createElementNS('http://www.w3.org/2000/svg', 'title');
          title.textContent = node.path ? `${node.label}\n${node.path}\nTap to open` : `${node.label} (no file)`;
          g.insertBefore(title, g.firstChild);
          if (!node.path) return;
          g.classList.add('diag-click');
          g.setAttribute('tabindex', '0');
          g.setAttribute('role', 'button');
          g.setAttribute('aria-label', `Open ${node.label}`);
          const open = () => { if (!moved.current) send({ type: 'diagram_open', path: data.path, node: node.i }); };
          g.addEventListener('click', open);
          g.addEventListener('keydown', (e) => { const k = (e as KeyboardEvent).key; if (k === 'Enter' || k === ' ') { e.preventDefault(); open(); } });
        });
        touched.current = false;
        fit();
        window.requestAnimationFrame(() => { if (!touched.current) fit(); });   // the sheet may not have had its size yet when the drawing finished
      } catch (e) {
        if (mine !== draw.current) return;
        setDrawError(e instanceof Error ? e.message : 'unknown error');
      }
    })();
  }, [data, panel, loadMermaid, send, fit]);

  // pan (one finger or the mouse), pinch (two fingers) and wheel zoom
  useEffect(() => {
    const box = canvas.current;
    if (!box) return;
    const pointers = new Map<number, { x: number; y: number }>();
    let start = { x: 0, y: 0, ox: 0, oy: 0 };
    let pinch = 0;

    const down = (e: PointerEvent) => {
      if (e.pointerType === 'mouse' && e.button !== 0) return;
      pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
      touched.current = true;
      moved.current = false;
      start = { x: e.clientX, y: e.clientY, ox: view.current.x, oy: view.current.y };
      if (pointers.size === 2) { const [a, b] = [...pointers.values()]; pinch = Math.hypot(a.x - b.x, a.y - b.y); }
      try { box.setPointerCapture(e.pointerId); } catch { /* not every environment supports capture */ }
    };
    const move = (e: PointerEvent) => {
      if (!pointers.has(e.pointerId)) return;
      pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
      if (pointers.size === 2) {
        const [a, b] = [...pointers.values()];
        const d = Math.hypot(a.x - b.x, a.y - b.y);
        if (pinch > 0 && d > 0) {
          const r = box.getBoundingClientRect();
          zoom(d / pinch, (a.x + b.x) / 2 - r.left, (a.y + b.y) / 2 - r.top);
        }
        pinch = d;
        moved.current = true;
        return;
      }
      const dx = e.clientX - start.x, dy = e.clientY - start.y;
      if (Math.abs(dx) + Math.abs(dy) > 4) moved.current = true;
      view.current = { ...view.current, x: start.ox + dx, y: start.oy + dy };
      apply();
    };
    const up = (e: PointerEvent) => {
      pointers.delete(e.pointerId);
      pinch = 0;
      if (pointers.size === 1) { const [p] = [...pointers.values()]; start = { x: p.x, y: p.y, ox: view.current.x, oy: view.current.y }; }
      window.setTimeout(() => { if (pointers.size === 0) moved.current = false; }, 0);
    };
    const wheel = (e: WheelEvent) => {
      e.preventDefault();
      const r = box.getBoundingClientRect();
      zoom(e.deltaY < 0 ? 1.12 : 1 / 1.12, e.clientX - r.left, e.clientY - r.top);
    };

    box.addEventListener('pointerdown', down);
    box.addEventListener('pointermove', move);
    box.addEventListener('pointerup', up);
    box.addEventListener('pointercancel', up);
    box.addEventListener('wheel', wheel, { passive: false });
    const refit = () => { touched.current = false; fit(); };
    box.addEventListener('dblclick', refit);
    // Until the user moves or zooms, keep the whole diagram in view whenever the canvas changes size (sheet opening, full screen).
    const watcher = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(() => { if (!touched.current) fit(); }) : null;
    watcher?.observe(box);
    return () => {
      watcher?.disconnect();
      box.removeEventListener('pointerdown', down);
      box.removeEventListener('pointermove', move);
      box.removeEventListener('pointerup', up);
      box.removeEventListener('pointercancel', up);
      box.removeEventListener('wheel', wheel);
      box.removeEventListener('dblclick', refit);
    };
  }, [panel, apply, fit, zoom]);

  const ask = () => { if (asking) return; setAsking(true); send({ type: 'diagram_prepare', path }); };
  const askLabel = (text: string) => (asking ? <><span className="spinner" />Reading the project...</> : text);
  const toggleFull = () => { touched.current = false; setFull((v) => !v); window.requestAnimationFrame(fit); };
  const nodeLabel = (i: number) => data?.nodes.find((n) => n.i === i)?.label ?? '?';
  const verified = data ? data.edges.filter((e) => e.support === 'verified').length : 0;

  return (
    <section className="sheet open" aria-hidden="false">
      <div className="sheet-head">
        <div><h3>Architecture diagram</h3><p className="sheet-sub">{path}</p></div>
        <button className="icon-btn" onClick={onClose} title="Close">
          <svg viewBox="0 0 24 24"><line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" /></svg>
        </button>
      </div>

      <div className="sheet-body diag-body">
        {panel === 'empty' && (
          <div className="diag-panel" id="diagEmpty">
            <p><strong>No diagram for this project yet.</strong></p>
            <p>Levity reads the project's files on your PC, asks your assistant to draw its architecture, then checks every relationship against the code. Components link to their files.</p>
            <button className={`btn primary ${asking ? 'busy' : ''}`} disabled={asking} onClick={ask}>{askLabel('Make a diagram')}</button>
          </div>
        )}

        {panel === 'confirm' && prepared && (
          <div className="diag-panel" id="diagConfirm">
            <p>Levity will send this to your assistant (the same service your chats use):</p>
            <p>
              the list of {prepared.files} project files{prepared.truncated ? ' (the project is larger, so the list is partial)' : ''}, the README, and {prepared.excerpts} source excerpts. About {prepared.kb} KB in total.
            </p>
            <p>
              {prepared.redactions > 0
                ? `${prepared.redactions} secret-looking value${prepared.redactions === 1 ? '' : 's'} will be removed first. Files such as .env and keys are never read.`
                : 'Files such as .env and keys are never read.'}
            </p>
            <p>It uses tokens like a chat message does.</p>
            <div className="diag-actions">
              <button className="btn primary" onClick={() => { onClearPrepared(); setStarting(true); send({ type: 'diagram_generate', path }); }}>Generate</button>
              <button className="btn ghost" onClick={onClearPrepared}>Cancel</button>
            </div>
          </div>
        )}

        {panel === 'progress' && (
          <div className="diag-panel" id="diagProgress">
            <p><span className="spinner" /> {running ? state!.message : 'Starting...'}</p>
            <button className="btn danger" onClick={() => send({ type: 'diagram_cancel' })}>Stop</button>
          </div>
        )}

        {panel === 'error' && (
          <div className="diag-panel" id="diagError">
            <p><strong>The diagram could not be made.</strong></p>
            <p>{state!.message}</p>
            <div className="diag-actions"><button className={`btn primary ${asking ? 'busy' : ''}`} disabled={asking} onClick={ask}>{askLabel('Try again')}</button></div>
          </div>
        )}

        {panel === 'view' && data && (
          <div id="diagView" className={full ? 'expanded' : ''}>
            {data.stale && (
              <div className="diag-stale">The project has changed since this diagram was made. <button className={`btn ghost ${asking ? 'busy' : ''}`} disabled={asking} onClick={ask}>{askLabel('Regenerate')}</button></div>
            )}
            {data.overview && <p className="diag-overview">{data.overview}</p>}
            <div className="diag-toolbar">
              <button className="icon-btn" title="Zoom out" onClick={() => zoom(1 / 1.25)}><svg viewBox="0 0 24 24"><line x1="5" y1="12" x2="19" y2="12" /></svg></button>
              <button className="icon-btn" title="Fit to view" onClick={() => { touched.current = false; fit(); }}><svg viewBox="0 0 24 24"><rect x="7" y="7" width="10" height="10" rx="1" /><path d="M3 8V4h4M21 8V4h-4M3 16v4h4M21 16v4h-4" /></svg></button>
              <button className="icon-btn" title="Zoom in" onClick={() => zoom(1.25)}><svg viewBox="0 0 24 24"><line x1="12" y1="5" x2="12" y2="19" /><line x1="5" y1="12" x2="19" y2="12" /></svg></button>
              <button className="icon-btn diag-expand" title={full ? 'Back to the page' : 'Open full screen'} aria-pressed={full} onClick={toggleFull}><svg viewBox="0 0 24 24"><path d="M14 4h6v6M10 20H4v-6M20 4l-7 7M4 20l7-7" /></svg></button>
            </div>
            <div className="diag-canvas" ref={canvas} tabIndex={0} aria-label="Architecture diagram. Drag to move, pinch or scroll to zoom, double-tap to fit.">
              <div className="diag-stage" ref={stage} />
            </div>
            {flat && (
              <>
                <p className="diag-meta">The group boxes could not be drawn for this diagram, so groups are shown by colour instead.</p>
                {data.groups.length > 0 && (
                  <ul className="diag-groups">
                    {data.groups.map((g) => <li key={g.id}><i className={toneClass(g.tone)} /> {g.label}</li>)}
                  </ul>
                )}
              </>
            )}
            <p className="diag-legend"><span className="diag-solid" /> backed by the code <span className="diag-dashed" /> inferred (not found in the code that was read)</p>
            {drawError && (
              <div className="diag-fallback">The picture could not be drawn here ({drawError}). The lists below show the same information.</div>
            )}
            <details open={!!drawError}>
              <summary>Components and relationships</summary>
              <ul className="diag-nodes">
                {data.nodes.map((n) => (
                  <li key={n.i}>
                    <strong>{n.label}</strong>
                    {n.path && <code> {n.path}</code>}
                    {n.path && <button className="btn ghost diag-open" onClick={() => send({ type: 'diagram_open', path: data.path, node: n.i })}>Open</button>}
                  </li>
                ))}
              </ul>
              <ul className="diag-edges">
                {data.edges.map((e, k) => (
                  <li key={k} className={`diag-edge ${e.support}`} title={SUPPORT_TEXT[e.support]}>
                    {`${nodeLabel(e.from)} → ${nodeLabel(e.to)}${e.label ? ` (${e.label})` : ''} · ${e.support}`}
                  </li>
                ))}
              </ul>
            </details>
            <p className="diag-meta">
              {`Made ${data.generatedUtc ? new Date(data.generatedUtc).toLocaleString() : ''} · ${data.nodes.length} components · ${verified} of ${data.edges.length} relationships verified in the code`}
              {data.tokens ? ` · ${Number(data.tokens).toLocaleString()} tokens` : ''}
            </p>
            {data.notes && data.notes.length > 0 && <p className="diag-meta">{`Adjusted: ${data.notes.join(' ')}`}</p>}
            <div className="diag-actions">
              <button className={`btn ghost ${asking ? 'busy' : ''}`} disabled={asking} onClick={ask}>{askLabel('Regenerate')}</button>
              <button className="btn ghost" onClick={async () => { try { await navigator.clipboard.writeText(data.mermaid); } catch { /* clipboard may be blocked */ } }}>Copy Mermaid</button>
              <TwoStep id="diagSaveBtn" label="Save to docs/ARCHITECTURE.md" onConfirm={() => send({ type: 'diagram_save_docs', path })} />
              <TwoStep id="diagDeleteBtn" label="Delete" onConfirm={() => send({ type: 'diagram_delete', path })} />
            </div>
          </div>
        )}
      </div>
    </section>
  );
}
