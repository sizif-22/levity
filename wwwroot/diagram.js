// Architecture diagram view (desktop sidebar). The PC does all the work and sends a validated graph plus Mermaid source.
// Here: confirm what will be sent, show progress, draw the diagram (pan and zoom), and send node numbers back when a node is clicked.
// Relies on helpers from app.js: $, postToHost, openSheet, closeSheet, toast, currentCwd.

const diag = { path: '', data: null, scale: 1, x: 0, y: 0, moved: false, mermaidPromise: null, renderId: 0, armed: {} };

const DIAG_SVG_ICON = '<svg viewBox="0 0 24 24"><rect x="3" y="3" width="7" height="6" rx="1"/><rect x="14" y="3" width="7" height="6" rx="1"/><rect x="8.5" y="15" width="7" height="6" rx="1"/><path d="M6.5 9v3h11V9M12 12v3"/></svg>';

function diagSame(a, b) {
  const norm = (p) => String(p).replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase();
  return !!a && !!b && norm(a) === norm(b);
}

function openDiagram(path) {
  path = path || currentCwd;
  if (!path) { toast('Open a project folder first.'); return; }
  diag.path = path;
  diag.data = null;
  $('diagTitle').textContent = 'Architecture diagram';
  $('diagSub').textContent = path;
  diagShow('empty');
  openSheet('diagramSheet');
  postToHost({ type: 'diagram_get', path });
}

function diagShow(panel) {
  for (const name of ['empty', 'confirm', 'progress', 'error', 'view']) $('diag' + name[0].toUpperCase() + name.slice(1)).hidden = name !== panel;
}

// ---- messages from the PC ----

function diagramOnMessage(msg) {
  if (!msg || !diagSame(msg.path, diag.path)) return;
  switch (msg.type) {
    case 'diagram': renderDiagram(msg); break;
    case 'diagram_none': diag.data = null; diagShow('empty'); break;
    case 'diagram_prepared': showDiagramConfirm(msg); break;
    case 'diagram_state': showDiagramState(msg); break;
  }
}

function showDiagramConfirm(m) {
  const lines = [
    `Levity will send this to your assistant (the same service your chats use):`,
    `the list of ${m.files} project files${m.truncated ? ' (the project is larger, so the list is partial)' : ''}, the README, and ${m.excerpts} source excerpts. About ${m.kb} KB in total.`,
    m.redactions > 0 ? `${m.redactions} secret-looking value${m.redactions === 1 ? '' : 's'} will be removed first. Files such as .env and keys are never read.` : `Files such as .env and keys are never read.`,
    `It uses tokens like a chat message does.`,
  ];
  const box = $('diagConfirmText');
  box.textContent = '';
  lines.forEach(t => { const p = document.createElement('p'); p.textContent = t; box.appendChild(p); });
  diagShow('confirm');
}

function showDiagramState(m) {
  if (m.phase === 'error') {
    $('diagErrorText').textContent = m.message || 'The diagram could not be made.';
    diagShow('error');
  } else if (m.phase === 'cancelled') {
    diagShow(diag.data ? 'view' : 'empty');
  } else if (m.phase !== 'done') {
    $('diagProgressText').textContent = m.message || 'Working...';
    diagShow('progress');
  }
}

// ---- drawing ----

const DIAG_WHAT = { verified: 'Backed by a reference in the code', documented: 'Stated in the README', inferred: 'Inferred: not found in the code that was read' };

function renderDiagram(d) {
  diag.data = d;
  diagShow('view');
  $('diagOverview').textContent = d.overview || '';
  $('diagStale').hidden = !d.stale;

  const verified = d.edges.filter(e => e.support === 'verified').length;
  const when = d.generatedUtc ? new Date(d.generatedUtc).toLocaleString() : '';
  $('diagMeta').textContent = `Made ${when} · ${d.nodes.length} components · ${verified} of ${d.edges.length} relationships verified in the code` +
    (d.tokens ? ` · ${Number(d.tokens).toLocaleString()} tokens` : '');
  $('diagNotes').textContent = (d.notes && d.notes.length) ? `Adjusted: ${d.notes.join(' ')}` : '';
  $('diagNotes').hidden = !(d.notes && d.notes.length);

  renderDiagramLists(d);
  drawDiagram(d);
}

function renderDiagramLists(d) {
  const label = (i) => (d.nodes.find(n => n.i === i) || {}).label || '?';
  const nodes = $('diagNodes');
  nodes.textContent = '';
  d.nodes.forEach(n => {
    const li = document.createElement('li');
    const name = document.createElement('strong');
    name.textContent = n.label;
    li.appendChild(name);
    if (n.path) {
      const code = document.createElement('code');
      code.textContent = ' ' + n.path;
      li.appendChild(code);
      const open = document.createElement('button');
      open.className = 'btn ghost diag-open';
      open.textContent = 'Open';
      open.addEventListener('click', () => postToHost({ type: 'diagram_open', path: diag.path, node: n.i }));
      li.appendChild(open);
    }
    nodes.appendChild(li);
  });

  const edges = $('diagEdges');
  edges.textContent = '';
  d.edges.forEach(e => {
    const li = document.createElement('li');
    li.className = 'diag-edge ' + e.support;
    li.title = DIAG_WHAT[e.support] || '';
    li.textContent = `${label(e.from)} → ${label(e.to)}${e.label ? ' (' + e.label + ')' : ''} · ${e.support}`;
    edges.appendChild(li);
  });
}

function loadMermaid() {
  if (window.mermaid) return Promise.resolve(window.mermaid);
  if (!diag.mermaidPromise) {
    diag.mermaidPromise = new Promise((resolve, reject) => {
      const s = document.createElement('script');
      s.src = 'vendor/mermaid.min.js';
      s.onload = () => (window.mermaid ? resolve(window.mermaid) : reject(new Error('Mermaid did not load')));
      s.onerror = () => { diag.mermaidPromise = null; reject(new Error('Mermaid could not be loaded')); };
      document.head.appendChild(s);
    });
  }
  return diag.mermaidPromise;
}

// Mermaid's layout engine can fail on diagrams with many relationships crossing between the same group boxes.
// The flat version (groups shown by colour) always lays out, so it is tried when the boxed one cannot be drawn.
async function drawOne(mermaid, id, source) {
  const out = await mermaid.render(id, source);
  const svg = sanitizeSvg(out.svg);
  if (!svg) throw new Error('The drawing was rejected as unsafe');
  return svg;
}

function showGroupLegend(d, flat) {
  const list = $('diagGroups');
  list.textContent = '';
  (d.groups || []).forEach(g => {
    const li = document.createElement('li');
    const sw = document.createElement('i');
    sw.className = 'diag-sw diag-sw' + (Number(g.tone) || 0);
    li.appendChild(sw);
    li.appendChild(document.createTextNode(' ' + g.label));
    list.appendChild(li);
  });
  $('diagFlatNote').hidden = !flat;
  list.hidden = !flat || !(d.groups && d.groups.length);
}

async function drawDiagram(d) {
  const stage = $('diagStage');
  const myRender = ++diag.renderId;
  stage.textContent = '';
  $('diagFallback').hidden = true;
  $('diagList').open = false;
  showGroupLegend(d, false);
  try {
    const mermaid = await loadMermaid();
    const dark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
    mermaid.initialize({
      startOnLoad: false,
      securityLevel: 'strict',
      // A directive hidden in a label (%%{init: ...}%%) must not be able to change any of these.
      secure: ['secure', 'securityLevel', 'startOnLoad', 'maxTextSize', 'htmlLabels', 'flowchart'],
      theme: 'base',
      htmlLabels: false,
      flowchart: { htmlLabels: false, curve: 'basis', useMaxWidth: false },
      themeVariables: { lineColor: dark ? '#cbd5e1' : '#334155', textColor: dark ? '#e2e8f0' : '#0f172a', background: 'transparent', fontFamily: 'inherit' },
    });

    let svg = null, flat = false, firstError = null;
    try { svg = await drawOne(mermaid, 'diagramSvg' + myRender, d.mermaid); }
    catch (e) {
      firstError = e;
      if (!d.mermaidFlat || d.mermaidFlat === d.mermaid) throw e;
    }
    if (myRender !== diag.renderId) return;             // a newer diagram replaced this one while it was drawing
    if (!svg) {
      flat = true;
      svg = await drawOne(mermaid, 'diagramSvgFlat' + myRender, d.mermaidFlat);
      if (myRender !== diag.renderId) return;
    }
    stage.appendChild(svg);
    wireDiagramNodes(svg, d);
    showGroupLegend(d, flat);
    fitDiagram();
  } catch (e) {
    if (myRender !== diag.renderId) return;
    $('diagFallback').hidden = false;
    $('diagFallbackText').textContent = `The picture could not be drawn here (${e && e.message ? e.message : 'unknown error'}). The lists below show the same information.`;
    $('diagList').open = true;
  }
}

// ---- keeping model-derived drawings safe ----
// The compiler already turns every label into plain text and Mermaid runs in strict mode. This is a second wall:
// the drawing is rebuilt from an allow-list before it ever touches the page.

const SVG_DROP = new Set(['script', 'foreignobject', 'iframe', 'object', 'embed', 'link', 'meta', 'base', 'form', 'image', 'use', 'a', 'audio', 'video', 'canvas', 'animate', 'set', 'animatetransform']);

function sanitizeSvg(svgText) {
  // Parsed like a browser would parse it (tolerant of small slips in Mermaid's output). Nothing is ever serialised and parsed again:
  // the elements below are walked, filtered and inserted as they are, so there is no second parse to exploit.
  let doc;
  try { doc = new DOMParser().parseFromString('<!doctype html><body>' + String(svgText), 'text/html'); } catch { return null; }
  const root = doc.body && doc.body.firstElementChild;
  if (!root || doc.body.children.length !== 1 || root.localName.toLowerCase() !== 'svg' || root.namespaceURI !== 'http://www.w3.org/2000/svg') return null;

  const walk = (el) => {
    for (const child of Array.from(el.children)) {
      const tag = child.localName.toLowerCase();
      if (SVG_DROP.has(tag)) { child.remove(); continue; }
      if (tag === 'style' && /@import|expression\s*\(|javascript:|url\(\s*['"]?\s*(?!#)/i.test(child.textContent)) { child.remove(); continue; }
      for (const attr of Array.from(child.attributes)) {
        const name = attr.name.toLowerCase();
        const val = attr.value.trim().toLowerCase();
        if (name.startsWith('on')) child.removeAttribute(attr.name);
        else if ((name === 'href' || name === 'xlink:href' || name === 'src') && !val.startsWith('#')) child.removeAttribute(attr.name);
        else if (name === 'style' && /javascript:|expression\s*\(|url\(\s*['"]?\s*(?!#)/i.test(val)) child.removeAttribute(attr.name);
      }
      walk(child);
    }
  };
  for (const attr of Array.from(root.attributes)) if (attr.name.toLowerCase().startsWith('on')) root.removeAttribute(attr.name);
  walk(root);
  return document.importNode(root, true);
}

function wireDiagramNodes(svg, d) {
  const byIndex = new Map(d.nodes.map(n => [n.i, n]));
  svg.querySelectorAll('g.node').forEach(g => {
    const m = /(?:^|-)n(\d+)(?:-\d+)?$/.exec(g.id || '');
    const node = m && byIndex.get(Number(m[1]));
    if (!node) return;
    const title = document.createElementNS('http://www.w3.org/2000/svg', 'title');
    title.textContent = node.path ? `${node.label}\n${node.path}\nClick to open` : `${node.label} (no file)`;
    g.insertBefore(title, g.firstChild);
    if (!node.path) return;
    g.classList.add('diag-click');
    g.setAttribute('tabindex', '0');
    g.setAttribute('role', 'button');
    g.setAttribute('aria-label', `Open ${node.label}`);
    const open = () => { if (!diag.moved) postToHost({ type: 'diagram_open', path: diag.path, node: node.i }); };
    g.addEventListener('click', open);
    g.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); } });
  });
}

// ---- pan and zoom ----

function diagApply() {
  $('diagStage').style.transform = `translate(${diag.x}px, ${diag.y}px) scale(${diag.scale})`;
}

function diagNaturalSize() {
  const svg = $('diagStage').querySelector('svg');
  if (!svg) return null;
  const vb = (svg.getAttribute('viewBox') || '').trim().split(/[\s,]+/).map(Number);
  const hasBox = vb.length === 4 && vb.every(Number.isFinite);
  const w = (hasBox && vb[2]) || parseFloat(svg.getAttribute('width')) || 0;
  const h = (hasBox && vb[3]) || parseFloat(svg.getAttribute('height')) || 0;
  return w > 0 && h > 0 ? { w, h } : null;
}

function fitDiagram() {
  const svg = $('diagStage').querySelector('svg');
  const size = diagNaturalSize();
  const canvas = $('diagCanvas');
  if (!svg || !size) return;
  svg.setAttribute('width', size.w);
  svg.setAttribute('height', size.h);
  svg.style.maxWidth = 'none';
  const cw = canvas.clientWidth, ch = canvas.clientHeight;
  if (cw > 0 && ch > 0) {
    diag.scale = Math.max(0.05, Math.min(cw / size.w, ch / size.h, 1.5));
    diag.x = (cw - size.w * diag.scale) / 2;
    diag.y = (ch - size.h * diag.scale) / 2;
  } else { diag.scale = 1; diag.x = 0; diag.y = 0; }
  diagApply();
}

function zoomDiagram(factor, cx, cy) {
  const canvas = $('diagCanvas');
  if (cx === undefined) { cx = canvas.clientWidth / 2; cy = canvas.clientHeight / 2; }
  const next = Math.max(0.05, Math.min(6, diag.scale * factor));
  const k = next / diag.scale;
  diag.x = cx - (cx - diag.x) * k;
  diag.y = cy - (cy - diag.y) * k;
  diag.scale = next;
  diagApply();
}

(function wireDiagramControls() {
  const canvas = $('diagCanvas');
  canvas.addEventListener('wheel', e => {
    e.preventDefault();
    const r = canvas.getBoundingClientRect();
    zoomDiagram(e.deltaY < 0 ? 1.12 : 1 / 1.12, e.clientX - r.left, e.clientY - r.top);
  }, { passive: false });

  let drag = null;
  canvas.addEventListener('pointerdown', e => {
    if (e.button !== 0) return;
    drag = { x: e.clientX, y: e.clientY, ox: diag.x, oy: diag.y };
    diag.moved = false;
    try { canvas.setPointerCapture(e.pointerId); } catch { /* not every environment supports capture */ }
  });
  canvas.addEventListener('pointermove', e => {
    if (!drag) return;
    const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
    if (Math.abs(dx) + Math.abs(dy) > 4) diag.moved = true;
    diag.x = drag.ox + dx;
    diag.y = drag.oy + dy;
    diagApply();
  });
  const end = () => { drag = null; setTimeout(() => { diag.moved = false; }, 0); };
  canvas.addEventListener('pointerup', end);
  canvas.addEventListener('pointercancel', end);
  canvas.addEventListener('dblclick', fitDiagram);

  $('diagZoomIn').addEventListener('click', () => zoomDiagram(1.25));
  $('diagZoomOut').addEventListener('click', () => zoomDiagram(1 / 1.25));
  $('diagFit').addEventListener('click', fitDiagram);

  // Starting a run always passes through a confirmation that shows what will be sent.
  const ask = () => postToHost({ type: 'diagram_prepare', path: diag.path });
  $('diagMakeBtn').addEventListener('click', ask);
  $('diagRegenBtn').addEventListener('click', ask);
  $('diagRetryBtn').addEventListener('click', ask);
  $('diagStaleBtn').addEventListener('click', ask);
  $('diagSendBtn').addEventListener('click', () => { diagShow('progress'); $('diagProgressText').textContent = 'Starting...'; postToHost({ type: 'diagram_generate', path: diag.path }); });
  $('diagConfirmCancel').addEventListener('click', () => diagShow(diag.data ? 'view' : 'empty'));
  $('diagStopBtn').addEventListener('click', () => postToHost({ type: 'diagram_cancel' }));
  $('diagErrorBack').addEventListener('click', () => diagShow(diag.data ? 'view' : 'empty'));

  $('diagCopyBtn').addEventListener('click', async () => {
    if (!diag.data) return;
    try { await navigator.clipboard.writeText(diag.data.mermaid); toast('Mermaid source copied'); }
    catch { toast('Could not copy. Select the text in the lists instead.'); }
  });

  // Actions that change files or data need a second click.
  const twoStep = (btn, original, action) => {
    btn.addEventListener('click', () => {
      if (diag.armed[btn.id]) { clearTimeout(diag.armed[btn.id]); diag.armed[btn.id] = 0; btn.textContent = original; btn.classList.remove('danger'); action(); return; }
      btn.textContent = 'Sure?';
      btn.classList.add('danger');
      diag.armed[btn.id] = setTimeout(() => { diag.armed[btn.id] = 0; btn.textContent = original; btn.classList.remove('danger'); }, 3000);
    });
  };
  twoStep($('diagSaveBtn'), 'Save to docs/ARCHITECTURE.md', () => postToHost({ type: 'diagram_save_docs', path: diag.path }));
  twoStep($('diagDeleteBtn'), 'Delete', () => postToHost({ type: 'diagram_delete', path: diag.path }));

  // /diagram chip
  const chip = Array.from(document.querySelectorAll('.cmd-chip')).find(c => c.dataset.cmd === '/diagram');
  if (chip) chip.addEventListener('click', () => openDiagram(currentCwd));
})();
