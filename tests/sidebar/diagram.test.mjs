// The architecture diagram view in the desktop sidebar: confirmation before anything is sent, progress, drawing,
// node clicks (numbers, never paths), and what happens when the drawing or the text is hostile.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { open } from './harness.mjs';

const PROJECT = 'C:\\Work\\app';

const SVG = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 300 200">
  <style>.node rect{fill:#dbeafe}</style>
  <g class="node" id="flowchart-n1-0"><rect width="60" height="30"/><text>Entry</text></g>
  <g class="node" id="flowchart-n2-1"><rect width="60" height="30"/><text>Cloud</text></g>
</svg>`;

/** A stand-in for Mermaid: records how it was configured and returns a fixed drawing. */
function fakeMermaid(svg = SVG) {
  const calls = { init: [], render: [] };
  return {
    calls,
    initialize: (cfg) => calls.init.push(JSON.parse(JSON.stringify(cfg))),
    render: async (id, src) => { calls.render.push({ id, src }); return { svg: typeof svg === 'function' ? svg() : svg }; },
  };
}

const diagram = (over = {}) => ({
  type: 'diagram', path: PROJECT, overview: 'A small app that starts and syncs.',
  groups: [{ id: 'core', label: 'Core' }],
  nodes: [{ i: 1, label: 'Entry point', group: 'core', path: 'Program.cs', shape: 'box' }, { i: 2, label: 'Cloud relay', group: null, path: null, shape: 'box' }],
  edges: [{ from: 1, to: 2, label: 'syncs', support: 'inferred', evidence: null }],
  mermaid: 'flowchart TD\n  n1["Entry point"]\n  n2["Cloud relay"]\n  n1 -.->|"syncs"| n2\n',
  generatedUtc: '2026-10-27T12:00:00Z', head: 'abc', stale: false, stats: null, notes: [], tokens: 12345, ...over,
});

const tick = (ms = 30) => new Promise((r) => setTimeout(r, ms));
const visible = (ui, id) => !ui.$(id).hidden;

async function openDiagramFor(ui, path = PROJECT) {
  ui.window.openDiagram(path);
  ui.posted.length = 0;
}

test('opening the view asks the PC for a saved diagram and shows the empty state', async () => {
  const ui = await open({ mermaid: fakeMermaid() });
  ui.window.openDiagram(PROJECT);
  assert.ok(ui.$('diagramSheet').classList.contains('open'));
  assert.deepEqual(ui.sent('diagram_get'), [{ type: 'diagram_get', path: PROJECT }]);
  assert.ok(visible(ui, 'diagEmpty'));
  assert.equal(ui.$('diagSub').textContent, PROJECT);
});

test('the /diagram chip and typing /diagram open the view for the current folder, without messaging the assistant', async () => {
  const ui = await open({ mermaid: fakeMermaid() });
  ui.deliver({ type: 'config', cwd: PROJECT, isPinned: false });

  ui.click(ui.qa('.cmd-chip').find((c) => c.dataset.cmd === '/diagram'));
  assert.ok(ui.$('diagramSheet').classList.contains('open'));
  assert.equal(ui.sent('diagram_get').length, 1);

  ui.$('diagramSheet').classList.remove('open');
  ui.type('/diagram');
  ui.click(ui.$('sendBtn'));
  assert.equal(ui.sent('diagram_get').length, 2);
  assert.equal(ui.sent('send_prompt').length, 0, '/diagram is a view, not a chat message');
  assert.equal(ui.$('promptInput').value, '');
});

test('a project in the list has a diagram button that opens the view for that project', async () => {
  const ui = await open({ mermaid: fakeMermaid() });
  ui.deliver({ type: 'projects', root: 'D:\\', current: PROJECT, projects: [{ name: 'app', path: PROJECT, kind: 'Git', hasReadme: true, hasAgentsDoc: false, hasSkills: false, modified: new Date().toISOString(), pinned: false, uses: 0 }] });
  ui.click(ui.q('.diag-btn'));
  assert.deepEqual(ui.sent('diagram_get'), [{ type: 'diagram_get', path: PROJECT }]);
  assert.ok(ui.$('diagramSheet').classList.contains('open'));
});

test('nothing is generated until the user has seen what will be sent and confirmed', async () => {
  const ui = await open({ mermaid: fakeMermaid() });
  await openDiagramFor(ui);

  ui.click(ui.$('diagMakeBtn'));
  assert.deepEqual(ui.sent('diagram_prepare'), [{ type: 'diagram_prepare', path: PROJECT }]);
  assert.equal(ui.sent('diagram_generate').length, 0, 'asking what would be sent must not send anything');

  ui.deliver({ type: 'diagram_prepared', path: PROJECT, files: 120, excerpts: 24, indexed: 40, redactions: 3, truncated: false, kb: 71 });
  assert.ok(visible(ui, 'diagConfirm'));
  const text = ui.$('diagConfirmText').textContent;
  assert.match(text, /120 project files/);
  assert.match(text, /24 source excerpts/);
  assert.match(text, /71 KB/);
  assert.match(text, /3 secret-looking values will be removed/);
  assert.match(text, /\.env and keys are never read/);
  assert.equal(ui.sent('diagram_generate').length, 0);

  ui.click(ui.$('diagSendBtn'));
  assert.deepEqual(ui.sent('diagram_generate'), [{ type: 'diagram_generate', path: PROJECT }]);
  assert.ok(visible(ui, 'diagProgress'));
});

test('cancelling the confirmation sends nothing and goes back', async () => {
  const ui = await open({ mermaid: fakeMermaid() });
  await openDiagramFor(ui);
  ui.click(ui.$('diagMakeBtn'));
  ui.deliver({ type: 'diagram_prepared', path: PROJECT, files: 5, excerpts: 2, indexed: 2, redactions: 0, truncated: true, kb: 3 });
  assert.match(ui.$('diagConfirmText').textContent, /partial/);
  ui.click(ui.$('diagConfirmCancel'));
  assert.equal(ui.sent('diagram_generate').length, 0);
  assert.ok(visible(ui, 'diagEmpty'));
});

test('progress, Stop, errors and cancellation are shown', async () => {
  const ui = await open({ mermaid: fakeMermaid() });
  await openDiagramFor(ui);

  ui.deliver({ type: 'diagram_state', path: PROJECT, phase: 'generating', message: 'Asking the assistant (40 files listed, 12 excerpts)...' });
  assert.ok(visible(ui, 'diagProgress'));
  assert.equal(ui.$('diagProgressText').textContent, 'Asking the assistant (40 files listed, 12 excerpts)...');
  ui.click(ui.$('diagStopBtn'));
  assert.equal(ui.sent('diagram_cancel').length, 1);

  ui.deliver({ type: 'diagram_state', path: PROJECT, phase: 'cancelled', message: 'Stopped.' });
  assert.ok(visible(ui, 'diagEmpty'));

  ui.deliver({ type: 'diagram_state', path: PROJECT, phase: 'error', message: 'The assistant did not answer within 300 seconds.' });
  assert.ok(visible(ui, 'diagError'));
  assert.match(ui.$('diagErrorText').textContent, /did not answer/);
  ui.click(ui.$('diagRetryBtn'));
  assert.equal(ui.sent('diagram_prepare').length, 1, 'Try again goes through the confirmation again');
});

test('messages about another project are ignored', async () => {
  const ui = await open({ mermaid: fakeMermaid() });
  await openDiagramFor(ui);
  ui.deliver(diagram({ path: 'D:\\Other' }));
  ui.deliver({ type: 'diagram_state', path: 'D:\\Other', phase: 'error', message: 'nope' });
  assert.ok(visible(ui, 'diagEmpty'));
});

test('the same folder written with forward slashes or a different case still matches', async () => {
  const ui = await open({ mermaid: fakeMermaid() });
  ui.window.openDiagram('c:/work/APP/');
  ui.deliver(diagram());
  await tick();
  assert.ok(visible(ui, 'diagView'));
});

test('a diagram is drawn with Mermaid in strict mode with no HTML labels', async () => {
  const mermaid = fakeMermaid();
  const ui = await open({ mermaid });
  await openDiagramFor(ui);
  ui.deliver(diagram());
  await tick();

  assert.ok(visible(ui, 'diagView'));
  assert.equal(mermaid.calls.init.at(-1).securityLevel, 'strict');
  assert.equal(mermaid.calls.init.at(-1).htmlLabels, false);
  assert.equal(mermaid.calls.init.at(-1).flowchart.htmlLabels, false);
  assert.equal(mermaid.calls.render.at(-1).src, diagram().mermaid);
  assert.ok(ui.q('#diagStage svg'));
  assert.equal(ui.$('diagFallback').hidden, true);
});

test('the overview, summary, notes and lists are shown', async () => {
  const ui = await open({ mermaid: fakeMermaid() });
  await openDiagramFor(ui);
  ui.deliver(diagram({ notes: ['node a: path removed.'], edges: [{ from: 1, to: 2, label: 'syncs', support: 'verified', evidence: 'Program.cs' }, { from: 2, to: 1, label: null, support: 'inferred', evidence: null }] }));
  await tick();

  assert.equal(ui.$('diagOverview').textContent, 'A small app that starts and syncs.');
  assert.match(ui.$('diagMeta').textContent, /2 components/);
  assert.match(ui.$('diagMeta').textContent, /1 of 2 relationships verified/);
  assert.match(ui.$('diagMeta').textContent, /12,345 tokens/);
  assert.match(ui.$('diagNotes').textContent, /path removed/);
  assert.deepEqual(ui.qa('#diagNodes li strong').map((e) => e.textContent), ['Entry point', 'Cloud relay']);
  assert.match(ui.qa('#diagEdges li')[0].textContent, /Entry point \u2192 Cloud relay \(syncs\) \u00B7 verified/);
  assert.ok(ui.qa('#diagEdges li')[1].classList.contains('inferred'));
  assert.equal(ui.qa('#diagNodes .diag-open').length, 1, 'only components with a file can be opened');
});

test('clicking a component with a file sends its NUMBER, never a path', async () => {
  const ui = await open({ mermaid: fakeMermaid() });
  await openDiagramFor(ui);
  ui.deliver(diagram());
  await tick();

  const node1 = ui.q('#flowchart-n1-0');
  const node2 = ui.q('#flowchart-n2-1');
  assert.ok(node1.classList.contains('diag-click'));
  assert.ok(!node2.classList.contains('diag-click'), 'a component without a file is not clickable');
  assert.equal(node1.getAttribute('role'), 'button');
  assert.match(ui.q('title', node1).textContent, /Program\.cs/);

  ui.click(node1);
  ui.click(node2);
  assert.deepEqual(ui.sent('diagram_open'), [{ type: 'diagram_open', path: PROJECT, node: 1 }]);
});

test('keyboard Enter and Space open a focused component, other keys do not', async () => {
  const ui = await open({ mermaid: fakeMermaid() });
  await openDiagramFor(ui);
  ui.deliver(diagram());
  await tick();
  const node1 = ui.q('#flowchart-n1-0');
  const key = (k) => node1.dispatchEvent(new ui.window.KeyboardEvent('keydown', { key: k, bubbles: true }));
  key('a'); key('Tab');
  assert.equal(ui.sent('diagram_open').length, 0);
  key('Enter'); key(' ');
  assert.equal(ui.sent('diagram_open').length, 2);
});

test('dragging the picture does not count as a click on a component', async () => {
  const ui = await open({ mermaid: fakeMermaid() });
  await openDiagramFor(ui);
  ui.deliver(diagram());
  await tick();
  const canvas = ui.$('diagCanvas');
  const ptr = (type, x, y) => canvas.dispatchEvent(new ui.window.MouseEvent(type, { bubbles: true, clientX: x, clientY: y, button: 0 }));

  ptr('pointerdown', 10, 10);
  ptr('pointermove', 60, 40);
  ui.click(ui.q('#flowchart-n1-0'));          // the browser sends a click at the end of a drag
  ptr('pointerup', 60, 40);
  assert.equal(ui.sent('diagram_open').length, 0);
  assert.match(ui.$('diagStage').style.transform, /translate\(50px, 30px\)/);

  await tick(10);
  ui.click(ui.q('#flowchart-n1-0'));          // a plain click afterwards works again
  assert.equal(ui.sent('diagram_open').length, 1);
});

test('zoom buttons, the wheel and fit change the view', async () => {
  const ui = await open({ mermaid: fakeMermaid() });
  await openDiagramFor(ui);
  ui.deliver(diagram());
  await tick();
  const scale = () => Number(/scale\(([\d.]+)\)/.exec(ui.$('diagStage').style.transform)[1]);

  const start = scale();
  ui.click(ui.$('diagZoomIn'));
  assert.ok(scale() > start);
  ui.click(ui.$('diagZoomOut'));
  ui.click(ui.$('diagZoomOut'));
  assert.ok(scale() < start);
  ui.$('diagCanvas').dispatchEvent(new ui.window.WheelEvent('wheel', { deltaY: -100, bubbles: true, cancelable: true }));
  const afterWheel = scale();
  ui.click(ui.$('diagFit'));
  assert.notEqual(scale(), afterWheel);
});

test('a stale diagram says so and regenerating goes through the confirmation', async () => {
  const ui = await open({ mermaid: fakeMermaid() });
  await openDiagramFor(ui);
  ui.deliver(diagram({ stale: true }));
  await tick();
  assert.equal(ui.$('diagStale').hidden, false);
  ui.click(ui.$('diagStaleBtn'));
  assert.equal(ui.sent('diagram_prepare').length, 1);
  assert.equal(ui.sent('diagram_generate').length, 0);

  const fresh = await open({ mermaid: fakeMermaid() });
  await openDiagramFor(fresh);
  fresh.deliver(diagram({ stale: false }));
  await tick();
  assert.equal(fresh.$('diagStale').hidden, true);
});

test('Save to docs and Delete each need a second click', async () => {
  const ui = await open({ mermaid: fakeMermaid() });
  await openDiagramFor(ui);
  ui.deliver(diagram());
  await tick();

  ui.click(ui.$('diagSaveBtn'));
  assert.equal(ui.sent('diagram_save_docs').length, 0);
  assert.equal(ui.$('diagSaveBtn').textContent, 'Sure?');
  ui.click(ui.$('diagSaveBtn'));
  assert.deepEqual(ui.sent('diagram_save_docs'), [{ type: 'diagram_save_docs', path: PROJECT }]);
  assert.equal(ui.$('diagSaveBtn').textContent, 'Save to docs/ARCHITECTURE.md');

  ui.click(ui.$('diagDeleteBtn'));
  assert.equal(ui.sent('diagram_delete').length, 0);
  ui.click(ui.$('diagDeleteBtn'));
  assert.equal(ui.sent('diagram_delete').length, 1);

  ui.deliver({ type: 'diagram_none', path: PROJECT });
  assert.ok(visible(ui, 'diagEmpty'));
});

test('Copy Mermaid puts the source on the clipboard', async () => {
  const written = [];
  const ui = await open({ mermaid: fakeMermaid(), clipboard: { writeText: async (t) => written.push(t) } });
  await openDiagramFor(ui);
  ui.deliver(diagram());
  await tick();
  ui.click(ui.$('diagCopyBtn'));
  await tick();
  assert.deepEqual(written, [diagram().mermaid]);
  assert.match(ui.$('toast').textContent, /copied/i);
});

// ---- hostile input ----

const EVIL_SVG = `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 100 100" onload="window.__pwned=1">
  <script>window.__pwned=2</script>
  <style>@import url(https://evil.example/x.css); .a{fill:red}</style>
  <style>.ok{fill:blue}</style>
  <foreignObject><body xmlns="http://www.w3.org/1999/xhtml"><img src=x onerror="window.__pwned=3"></body></foreignObject>
  <a href="javascript:window.__pwned=4"><rect width="5" height="5"/></a>
  <image href="https://evil.example/track.png"/>
  <use xlink:href="https://evil.example/sprite.svg#x"/>
  <g class="node" id="flowchart-n1-0" onclick="window.__pwned=5" style="background:url(javascript:alert(1))"><rect width="9" height="9" onmouseover="window.__pwned=6"/><text xlink:href="javascript:alert(7)">T</text></g>
  <animate attributeName="href" values="javascript:alert(8)"/>
  <set attributeName="onclick" to="alert(9)"/>
</svg>`;

test('a malicious drawing is rebuilt from an allow-list before it reaches the page', async () => {
  const ui = await open({ mermaid: fakeMermaid(EVIL_SVG) });
  await openDiagramFor(ui);
  ui.deliver(diagram());
  await tick();

  const svg = ui.q('#diagStage svg');
  assert.ok(svg, 'the harmless parts still render');
  for (const tag of ['script', 'foreignObject', 'a', 'image', 'use', 'animate', 'set']) assert.equal(svg.querySelector(tag), null, `<${tag}> must be removed`);
  assert.ok(!svg.hasAttribute('onload'));
  const all = Array.from(svg.querySelectorAll('*'));
  assert.ok(all.every((el) => Array.from(el.attributes).every((a) => !a.name.toLowerCase().startsWith('on'))), 'no event-handler attributes');
  assert.ok(all.every((el) => !/javascript:/i.test(el.getAttribute('href') || '') && !/javascript:/i.test(el.getAttribute('xlink:href') || '') && !/javascript:/i.test(el.getAttribute('style') || '')));
  const styles = Array.from(svg.querySelectorAll('style')).map((s) => s.textContent);
  assert.ok(styles.every((s) => !/@import/.test(s)), 'a stylesheet that pulls in remote CSS is dropped');
  assert.ok(styles.some((s) => /\.ok/.test(s)), 'a harmless stylesheet is kept');
  assert.equal(ui.window.__pwned, undefined);
});

test('a drawing that is not exactly one SVG falls back to the lists instead of being inserted', async () => {
  for (const bad of ['<div onclick="window.__pwned=1">not svg</div>', 'just some text', '<svg></svg><svg></svg>', '<svg></svg><script>window.__pwned=1</script>', '']) {
    const ui = await open({ mermaid: fakeMermaid(bad) });
    await openDiagramFor(ui);
    ui.deliver(diagram());
    await tick();
    assert.equal(ui.q('#diagStage svg'), null);
    assert.equal(ui.$('diagFallback').hidden, false);
    assert.equal(ui.$('diagList').open, true, 'the lists open so the information is still reachable');
    assert.equal(ui.window.__pwned, undefined);
  }
});

test('if Mermaid fails to draw, the lists still show everything and say why', async () => {
  const broken = { initialize() {}, render: async () => { throw new Error('Parse error on line 3'); } };
  const ui = await open({ mermaid: broken });
  await openDiagramFor(ui);
  ui.deliver(diagram());
  await tick();
  assert.equal(ui.$('diagFallback').hidden, false);
  assert.match(ui.$('diagFallbackText').textContent, /Parse error on line 3/);
  assert.equal(ui.$('diagNodes').children.length, 2);
});

test('text from the model is only ever shown as text', async () => {
  const hostile = '<img src=x onerror="window.__pwned=1"><b>bold</b>';
  const ui = await open({ mermaid: fakeMermaid() });
  await openDiagramFor(ui);
  ui.deliver(diagram({
    overview: hostile, notes: [hostile],
    nodes: [{ i: 1, label: hostile, group: null, path: hostile, shape: 'box' }, { i: 2, label: 'b', group: null, path: null, shape: 'box' }],
    edges: [{ from: 1, to: 2, label: hostile, support: 'inferred', evidence: null }],
  }));
  await tick();
  ui.deliver({ type: 'diagram_state', path: PROJECT, phase: 'error', message: hostile });
  for (const id of ['diagOverview', 'diagNotes', 'diagNodes', 'diagEdges', 'diagErrorText']) {
    assert.equal(ui.q('img', ui.$(id)), null, `#${id} must not contain an image`);
    assert.equal(ui.q('b', ui.$(id)), null, `#${id} must not contain markup`);
  }
  assert.equal(ui.window.__pwned, undefined);
  assert.match(ui.$('diagErrorText').textContent, /<img src=x/);
});

test('a newer diagram replaces an older one that was still being drawn', async () => {
  let release;
  const gate = new Promise((r) => { release = r; });
  let n = 0;
  const mermaid = {
    initialize() {},
    render: async () => { n += 1; if (n === 1) await gate; return { svg: n === 1 ? SVG.replace('Entry', 'OLD') : SVG.replace('Entry', 'NEW') }; },
  };
  const ui = await open({ mermaid });
  await openDiagramFor(ui);
  ui.deliver(diagram());
  await tick();
  ui.deliver(diagram({ overview: 'second' }));
  await tick();
  release();
  await tick();
  assert.match(ui.$('diagStage').textContent, /NEW/);
  assert.doesNotMatch(ui.$('diagStage').textContent, /OLD/);
  assert.equal(ui.$('diagOverview').textContent, 'second');
});
