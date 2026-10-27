import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { DiagramSheet, type MermaidApi } from '../src/components/DiagramSheet';
import type { DiagramMsg, DiagramPrepared, DiagramState } from '../src/lib/diagram';

afterEach(cleanup);

const PROJECT = 'C:\\Work\\app';

const SVG = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 300 200">
  <style>.node rect{fill:#dbeafe}</style>
  <g class="node" id="flowchart-n1-0"><rect width="60" height="30"/><text>Entry</text></g>
  <g class="node" id="flowchart-n2-1"><rect width="60" height="30"/><text>Cloud</text></g>
</svg>`;

interface Calls { init: Record<string, unknown>[]; render: { id: string; src: string }[] }

function fakeMermaid(svg: string | (() => string) = SVG): { api: MermaidApi; calls: Calls } {
  const calls: Calls = { init: [], render: [] };
  return {
    calls,
    api: {
      initialize: (c) => { calls.init.push(c); },
      render: async (id, src) => { calls.render.push({ id, src }); return { svg: typeof svg === 'function' ? svg() : svg }; },
    },
  };
}

const diagram = (over: Partial<DiagramMsg> = {}): DiagramMsg => ({
  type: 'diagram', path: PROJECT, overview: 'A small app that starts and syncs.',
  groups: [{ id: 'core', label: 'Core', tone: 0 }, { id: 'web', label: 'Web', tone: 1 }],
  nodes: [{ i: 1, label: 'Entry point', group: 'core', path: 'Program.cs', shape: 'box' }, { i: 2, label: 'Cloud relay', group: null, path: null, shape: 'box' }],
  edges: [{ from: 1, to: 2, label: 'syncs', support: 'inferred', evidence: null }],
  mermaid: 'flowchart TD\n  n1["Entry point"]\n  n2["Cloud relay"]\n  n1 -.->|"syncs"| n2\n',
  mermaidFlat: 'flowchart TD\n  n1["Entry point"]\n  n2["Cloud relay"]\n  n1 -.->|"syncs"| n2\n',
  generatedUtc: '2026-10-27T12:00:00Z', stale: false, notes: [], tokens: 12345, ...over,
});

const prepared = (over: Partial<DiagramPrepared> = {}): DiagramPrepared => ({ path: PROJECT, files: 120, excerpts: 24, indexed: 40, redactions: 3, truncated: false, kb: 71, ...over });

interface Setup {
  data?: DiagramMsg | null;
  prepared?: DiagramPrepared | null;
  state?: DiagramState | null;
  mermaid?: MermaidApi;
}

function setup(o: Setup = {}) {
  const send = vi.fn();
  const onClearPrepared = vi.fn();
  const onClose = vi.fn();
  const loadMermaid = () => Promise.resolve(o.mermaid ?? fakeMermaid().api);
  const props = { path: PROJECT, data: o.data ?? null, prepared: o.prepared ?? null, state: o.state ?? null, send, onClearPrepared, onClose, loadMermaid };
  const utils = render(<DiagramSheet {...props} />);
  const again = (next: Partial<typeof props>) => utils.rerender(<DiagramSheet {...props} {...next} />);
  return { ...utils, send, onClearPrepared, onClose, again };
}

const drawn = async (container: HTMLElement) => { await waitFor(() => expect(container.querySelector('#flowchart-n1-0')).not.toBeNull()); };

describe('DiagramSheet: before a diagram exists', () => {
  it('offers to make one, and asking only requests a preview of what would be sent', () => {
    const { send } = setup();
    expect(screen.getByText('No diagram for this project yet.')).toBeTruthy();
    fireEvent.click(screen.getByText('Make a diagram'));
    expect(send).toHaveBeenCalledWith({ type: 'diagram_prepare', path: PROJECT });
    expect(send).not.toHaveBeenCalledWith(expect.objectContaining({ type: 'diagram_generate' }));
  });

  it('shows what will be sent and only generates after Generate is pressed', () => {
    const { send, onClearPrepared } = setup({ prepared: prepared() });
    const text = document.body.textContent!;
    expect(text).toMatch(/120 project files/);
    expect(text).toMatch(/24 source excerpts/);
    expect(text).toMatch(/71 KB/);
    expect(text).toMatch(/3 secret-looking values will be removed/);
    expect(text).toMatch(/\.env and keys are never read/);
    expect(send).not.toHaveBeenCalled();

    fireEvent.click(screen.getByText('Generate'));
    expect(send).toHaveBeenCalledWith({ type: 'diagram_generate', path: PROJECT });
    expect(onClearPrepared).toHaveBeenCalled();
    expect(screen.getByText(/Starting/)).toBeTruthy();
  });

  it('says when the project list is partial and uses the singular for one secret', () => {
    setup({ prepared: prepared({ truncated: true, redactions: 1 }) });
    expect(document.body.textContent).toMatch(/partial/);
    expect(document.body.textContent).toMatch(/1 secret-looking value will be removed/);
  });

  it('Cancel clears the preview and sends nothing', () => {
    const { send, onClearPrepared } = setup({ prepared: prepared() });
    fireEvent.click(screen.getByText('Cancel'));
    expect(onClearPrepared).toHaveBeenCalled();
    expect(send).not.toHaveBeenCalled();
  });

  it('shows progress with a Stop button, and an error with Try again that goes through the preview again', () => {
    const { send, again } = setup({ state: { path: PROJECT, phase: 'generating', message: 'Asking the assistant (40 files listed, 12 excerpts)...' } });
    expect(screen.getByText(/Asking the assistant \(40 files/)).toBeTruthy();
    fireEvent.click(screen.getByText('Stop'));
    expect(send).toHaveBeenCalledWith({ type: 'diagram_cancel' });

    again({ state: { path: PROJECT, phase: 'error', message: 'The assistant did not answer within 300 seconds.' } });
    expect(screen.getByText(/did not answer/)).toBeTruthy();
    fireEvent.click(screen.getByText('Try again'));
    expect(send).toHaveBeenCalledWith({ type: 'diagram_prepare', path: PROJECT });
  });

  it('a fresh preview wins over an old error', () => {
    setup({ prepared: prepared(), state: { path: PROJECT, phase: 'error', message: 'old problem' } });
    expect(screen.getByText('Generate')).toBeTruthy();
    expect(screen.queryByText('old problem')).toBeNull();
  });

  it('a cancelled run goes back to what was there', () => {
    setup({ state: { path: PROJECT, phase: 'cancelled', message: 'Stopped.' } });
    expect(screen.getByText('Make a diagram')).toBeTruthy();
  });

  it('shows error text as text, never as markup', () => {
    setup({ state: { path: PROJECT, phase: 'error', message: '<img src=x onerror="window.__e=1"><b>x</b>' } });
    expect(document.querySelector('#diagError img')).toBeNull();
    expect(document.querySelector('#diagError b')).toBeNull();
    expect((window as unknown as { __e?: number }).__e).toBeUndefined();
  });

  it('Close calls onClose', () => {
    const { onClose } = setup();
    fireEvent.click(screen.getByTitle('Close'));
    expect(onClose).toHaveBeenCalled();
  });
});

describe('DiagramSheet: a diagram', () => {
  it('is drawn with Mermaid in strict mode, with no HTML labels and protected settings', async () => {
    const m = fakeMermaid();
    const { container } = setup({ data: diagram(), mermaid: m.api });
    await drawn(container);
    const cfg = m.calls.init.at(-1)!;
    expect(cfg.securityLevel).toBe('strict');
    expect(cfg.htmlLabels).toBe(false);
    expect((cfg.flowchart as { htmlLabels: boolean }).htmlLabels).toBe(false);
    for (const key of ['secure', 'securityLevel', 'startOnLoad', 'maxTextSize', 'htmlLabels', 'flowchart']) expect(cfg.secure as string[]).toContain(key);
    expect(m.calls.render.at(-1)!.src).toBe(diagram().mermaid);
  });

  it('shows the overview, the summary, notes and the lists', async () => {
    const { container } = setup({
      data: diagram({ notes: ['node a: path removed.'], edges: [{ from: 1, to: 2, label: 'syncs', support: 'verified', evidence: 'Program.cs' }, { from: 2, to: 1, label: null, support: 'inferred', evidence: null }] }),
    });
    await drawn(container);
    expect(screen.getByText('A small app that starts and syncs.')).toBeTruthy();
    expect(document.body.textContent).toMatch(/2 components/);
    expect(document.body.textContent).toMatch(/1 of 2 relationships verified/);
    expect(document.body.textContent).toMatch(/12,345 tokens/);
    expect(document.body.textContent).toMatch(/path removed/);
    expect(container.querySelectorAll('.diag-nodes li strong').length).toBe(2);
    expect(container.querySelectorAll('.diag-edges li')[1].className).toContain('inferred');
    expect(container.querySelectorAll('.diag-open').length).toBe(1);
  });

  it('sends a component NUMBER, never a path, when a node is tapped', async () => {
    const { container, send } = setup({ data: diagram() });
    await drawn(container);
    const n1 = container.querySelector('#flowchart-n1-0')!;
    const n2 = container.querySelector('#flowchart-n2-1')!;
    expect(n1.classList.contains('diag-click')).toBe(true);
    expect(n2.classList.contains('diag-click')).toBe(false);
    expect(n1.getAttribute('role')).toBe('button');
    expect(n1.querySelector('title')!.textContent).toContain('Program.cs');

    fireEvent.click(n1);
    fireEvent.click(n2);
    expect(send).toHaveBeenCalledTimes(1);
    expect(send).toHaveBeenCalledWith({ type: 'diagram_open', path: PROJECT, node: 1 });
  });

  it('opens with Enter or Space but not other keys', async () => {
    const { container, send } = setup({ data: diagram() });
    await drawn(container);
    const n1 = container.querySelector('#flowchart-n1-0')!;
    fireEvent.keyDown(n1, { key: 'a' });
    fireEvent.keyDown(n1, { key: 'Tab' });
    expect(send).not.toHaveBeenCalled();
    fireEvent.keyDown(n1, { key: 'Enter' });
    fireEvent.keyDown(n1, { key: ' ' });
    expect(send).toHaveBeenCalledTimes(2);
  });

  it('the Open buttons in the list send the component number too', async () => {
    const { container, send } = setup({ data: diagram() });
    await drawn(container);
    fireEvent.click(container.querySelector('.diag-open')!);
    expect(send).toHaveBeenCalledWith({ type: 'diagram_open', path: PROJECT, node: 1 });
  });

  it('a drag does not count as a tap on a component', async () => {
    const { container, send } = setup({ data: diagram() });
    await drawn(container);
    const canvas = container.querySelector('.diag-canvas')!;
    // jsdom has no PointerEvent, so the events are built from MouseEvent to keep their coordinates.
    const ptr = (type: string, x: number, y: number) => canvas.dispatchEvent(new MouseEvent(type, { bubbles: true, clientX: x, clientY: y, button: 0 }));

    ptr('pointerdown', 10, 10);
    ptr('pointermove', 70, 50);
    fireEvent.click(container.querySelector('#flowchart-n1-0')!);   // the browser sends a click at the end of a drag
    ptr('pointerup', 70, 50);
    expect(send).not.toHaveBeenCalled();
    expect((container.querySelector('.diag-stage') as HTMLElement).style.transform).toMatch(/translate\(60px, 40px\)/);

    await new Promise((r) => setTimeout(r, 10));
    fireEvent.click(container.querySelector('#flowchart-n1-0')!);   // a plain tap afterwards works again
    expect(send).toHaveBeenCalledTimes(1);
  });

  it('zoom buttons, wheel and fit change the view', async () => {
    const { container } = setup({ data: diagram() });
    await drawn(container);
    const stage = container.querySelector('.diag-stage') as HTMLElement;
    const scale = () => Number(/scale\(([\d.]+)\)/.exec(stage.style.transform)![1]);
    const start = scale();
    fireEvent.click(screen.getByTitle('Zoom in'));
    expect(scale()).toBeGreaterThan(start);
    fireEvent.click(screen.getByTitle('Zoom out'));
    fireEvent.click(screen.getByTitle('Zoom out'));
    expect(scale()).toBeLessThan(start);
    fireEvent.wheel(container.querySelector('.diag-canvas')!, { deltaY: -100 });
    const afterWheel = scale();
    fireEvent.click(screen.getByTitle('Fit to view'));
    expect(scale()).not.toBe(afterWheel);
  });

  it('a stale diagram says so and regenerating goes through the preview', async () => {
    const { container, send } = setup({ data: diagram({ stale: true }) });
    await drawn(container);
    expect(screen.getByText(/changed since this diagram was made/)).toBeTruthy();
    fireEvent.click(container.querySelector('.diag-stale button')!);
    expect(send).toHaveBeenCalledWith({ type: 'diagram_prepare', path: PROJECT });
    expect(send).not.toHaveBeenCalledWith(expect.objectContaining({ type: 'diagram_generate' }));
  });

  it('Save to docs and Delete each need a second tap', async () => {
    const { container, send } = setup({ data: diagram() });
    await drawn(container);

    fireEvent.click(screen.getByText('Save to docs/ARCHITECTURE.md'));
    expect(send).not.toHaveBeenCalled();
    fireEvent.click(screen.getByText('Sure?'));
    expect(send).toHaveBeenCalledWith({ type: 'diagram_save_docs', path: PROJECT });

    fireEvent.click(screen.getByText('Delete'));
    expect(send).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByText('Sure?'));
    expect(send).toHaveBeenCalledWith({ type: 'diagram_delete', path: PROJECT });
  });

  it('Copy Mermaid puts the source on the clipboard', async () => {
    const written: string[] = [];
    Object.defineProperty(navigator, 'clipboard', { value: { writeText: async (t: string) => { written.push(t); } }, configurable: true });
    const { container } = setup({ data: diagram() });
    await drawn(container);
    fireEvent.click(screen.getByText('Copy Mermaid'));
    await waitFor(() => expect(written).toEqual([diagram().mermaid]));
  });
});

describe('DiagramSheet: when drawing is hard or the drawing is hostile', () => {
  it('falls back to the flat colour-coded version, with a legend and a note, and components stay tappable', async () => {
    const sources: string[] = [];
    const api: MermaidApi = {
      initialize: () => {},
      render: async (_id, src) => {
        sources.push(src);
        if (src.includes('SUBGRAPHS')) throw new Error("Cannot set properties of undefined (setting 'order')");
        return { svg: SVG };
      },
    };
    const { container, send } = setup({ data: diagram({ mermaid: 'flowchart TD\n  subgraph g1["x"] SUBGRAPHS\n  end\n', mermaidFlat: 'flowchart TD\n  n1["flat"]\n' }), mermaid: api });
    await drawn(container);
    expect(sources.length).toBe(2);
    expect(sources[1]).toContain('flat');
    expect(screen.getByText(/groups are shown by colour instead/)).toBeTruthy();
    expect(Array.from(container.querySelectorAll('.diag-groups li')).map((li) => li.textContent!.trim())).toEqual(['Core', 'Web']);
    expect(container.querySelector('.diag-sw0')).not.toBeNull();
    expect(container.querySelector('.diag-sw1')).not.toBeNull();
    expect(container.querySelector('.diag-fallback')).toBeNull();
    fireEvent.click(container.querySelector('#flowchart-n1-0')!);
    expect(send).toHaveBeenCalledWith({ type: 'diagram_open', path: PROJECT, node: 1 });
  });

  it('shows no legend when the boxed version drew fine', async () => {
    const { container } = setup({ data: diagram() });
    await drawn(container);
    expect(container.querySelector('.diag-groups')).toBeNull();
    expect(screen.queryByText(/groups are shown by colour/)).toBeNull();
  });

  it('if both versions fail, the lists open and say why', async () => {
    const api: MermaidApi = { initialize: () => {}, render: async () => { throw new Error('layout failed'); } };
    const { container } = setup({ data: diagram(), mermaid: api });
    await waitFor(() => expect(container.querySelector('.diag-fallback')).not.toBeNull());
    expect(container.querySelector('.diag-fallback')!.textContent).toContain('layout failed');
    expect((container.querySelector('details') as HTMLDetailsElement).open).toBe(true);
    expect(container.querySelectorAll('.diag-nodes li').length).toBe(2);
  });

  it('a diagram from an older PC with no flat version still shows the lists when drawing fails', async () => {
    const api: MermaidApi = { initialize: () => {}, render: async () => { throw new Error('boom'); } };
    const d = diagram();
    delete d.mermaidFlat;
    const { container } = setup({ data: d, mermaid: api });
    await waitFor(() => expect(container.querySelector('.diag-fallback')).not.toBeNull());
  });

  it('falls back to the lists when Mermaid cannot even be loaded', async () => {
    const send = vi.fn();
    const { container } = render(<DiagramSheet path={PROJECT} data={diagram()} prepared={null} state={null} send={send} onClearPrepared={() => {}} onClose={() => {}} loadMermaid={() => Promise.reject(new Error('Mermaid could not be loaded'))} />);
    await waitFor(() => expect(container.querySelector('.diag-fallback')).not.toBeNull());
    expect(container.querySelector('.diag-fallback')!.textContent).toContain('could not be loaded');
  });

  const EVIL = `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 100 100" onload="window.__pwned=1">
    <script>window.__pwned=2</script>
    <style>@import url(https://evil.example/x.css); .a{fill:red}</style><style>.ok{fill:blue}</style>
    <a href="javascript:window.__pwned=4"><rect width="5" height="5"/></a>
    <image href="https://evil.example/track.png"/><use xlink:href="https://evil.example/s.svg#x"/>
    <g class="node" id="flowchart-n1-0" onclick="window.__pwned=5" style="background:url(javascript:alert(1))"><rect width="9" height="9" onmouseover="window.__pwned=6"/><text xlink:href="javascript:alert(7)">T</text></g>
    <animate attributeName="href" values="javascript:alert(8)"/><set attributeName="onclick" to="alert(9)"/>
  </svg>`;

  it('a malicious drawing is rebuilt from an allow-list before it reaches the page', async () => {
    const { container } = setup({ data: diagram(), mermaid: fakeMermaid(EVIL).api });
    await drawn(container);
    const svg = container.querySelector('.diag-stage svg')!;
    expect(svg).not.toBeNull();
    for (const tag of ['script', 'a', 'image', 'use', 'animate', 'set']) expect(svg.querySelector(tag)).toBeNull();
    const all = [svg, ...Array.from(svg.querySelectorAll('*'))];
    expect(all.every((el) => Array.from(el.attributes).every((a) => !a.name.toLowerCase().startsWith('on')))).toBe(true);
    expect(all.every((el) => !/javascript:/i.test((el.getAttribute('href') ?? '') + (el.getAttribute('xlink:href') ?? '') + (el.getAttribute('style') ?? '')))).toBe(true);
    const styles = Array.from(svg.querySelectorAll('style')).map((s) => s.textContent ?? '');
    expect(styles.some((s) => /@import/.test(s))).toBe(false);
    expect(styles.some((s) => /\.ok/.test(s))).toBe(true);
    expect((window as unknown as { __pwned?: number }).__pwned).toBeUndefined();
  });

  it.each([
    ['not svg', '<div onclick="window.__pwned=1">x</div>'],
    ['plain text', 'just some text'],
    ['two svgs', '<svg></svg><svg></svg>'],
    ['empty', ''],
  ])('a drawing that is %s is refused and the lists are shown instead', async (_n, bad) => {
    const { container } = setup({ data: diagram(), mermaid: fakeMermaid(bad).api });
    await waitFor(() => expect(container.querySelector('.diag-fallback')).not.toBeNull());
    expect(container.querySelector('.diag-stage svg')).toBeNull();
    expect((window as unknown as { __pwned?: number }).__pwned).toBeUndefined();
  });

  it('text from the model is only ever shown as text', async () => {
    const hostile = '<img src=x onerror="window.__pwned=1"><b>bold</b>';
    const d = diagram({
      overview: hostile, notes: [hostile],
      nodes: [{ i: 1, label: hostile, group: null, path: hostile, shape: 'box' }, { i: 2, label: 'b', group: null, path: null, shape: 'box' }],
      edges: [{ from: 1, to: 2, label: hostile, support: 'inferred', evidence: null }],
      groups: [{ id: 'g', label: hostile, tone: 0 }],
    });
    const { container } = setup({ data: d });
    await drawn(container);
    expect(container.querySelector('#diagView img')).toBeNull();
    expect(container.querySelector('#diagView b')).toBeNull();
    expect(container.querySelector('.diag-overview')!.textContent).toBe(hostile);
    expect((window as unknown as { __pwned?: number }).__pwned).toBeUndefined();
  });

  it('a newer diagram replaces an older one that was still being drawn', async () => {
    let release: () => void = () => {};
    const gate = new Promise<void>((r) => { release = r; });
    let n = 0;
    const api: MermaidApi = {
      initialize: () => {},
      render: async () => { n += 1; if (n === 1) await gate; return { svg: SVG.replace('Entry', n === 1 ? 'OLD' : 'NEW') }; },
    };
    const { container, again } = setup({ data: diagram(), mermaid: api });
    await new Promise((r) => setTimeout(r, 20));
    again({ data: diagram({ overview: 'second' }) });
    await waitFor(() => expect(container.textContent).toContain('NEW'));
    release();
    await new Promise((r) => setTimeout(r, 30));
    expect(container.querySelector('.diag-stage')!.textContent).toContain('NEW');
    expect(container.querySelector('.diag-stage')!.textContent).not.toContain('OLD');
  });
});

describe('DiagramSheet: waiting spinner and full screen', () => {
  it('Make a diagram shows a spinner, ignores a second tap, and recovers when the PC answers', () => {
    const { send, again, container } = setup();
    const btn = screen.getByRole('button', { name: 'Make a diagram' });
    fireEvent.click(btn);
    const busy = screen.getByRole('button', { name: /Reading the project/ }) as HTMLButtonElement;
    expect(busy.disabled).toBe(true);
    expect(busy.querySelector('.spinner')).not.toBeNull();
    fireEvent.click(busy);
    expect(send.mock.calls.filter(([m]) => (m as { type: string }).type === 'diagram_prepare')).toHaveLength(1);

    again({ prepared: prepared() });
    expect(container.querySelector('#diagConfirm')).not.toBeNull();
    expect(screen.queryByText(/Reading the project/)).toBeNull();
  });

  it('the spinner clears when the PC answers with an error', () => {
    const { again } = setup();
    fireEvent.click(screen.getByRole('button', { name: 'Make a diagram' }));
    again({ state: { type: 'diagram_state', path: PROJECT, phase: 'error', message: 'That folder is not available.' } as DiagramState });
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Try again' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('the expand button fills the screen and Escape or a second tap brings it back', async () => {
    const { container } = setup({ data: diagram() });
    await drawn(container);
    const view = container.querySelector('#diagView')!;
    expect(view.classList.contains('expanded')).toBe(false);
    fireEvent.click(screen.getByTitle('Open full screen'));
    expect(view.classList.contains('expanded')).toBe(true);
    expect(screen.getByTitle('Back to the page').getAttribute('aria-pressed')).toBe('true');
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(view.classList.contains('expanded')).toBe(false);
    fireEvent.click(screen.getByTitle('Open full screen'));
    fireEvent.click(screen.getByTitle('Back to the page'));
    expect(view.classList.contains('expanded')).toBe(false);
  });

  it('draws with the shared theme (readable text and group colours), not inherited fonts', async () => {
    const m = fakeMermaid();
    const { container } = setup({ data: diagram(), mermaid: m.api });
    await drawn(container);
    const cfg = m.calls.init.at(-1)! as { fontFamily: string; themeVariables: Record<string, string> };
    expect(cfg.fontFamily).not.toBe('inherit');
    for (const key of ['clusterBkg', 'clusterBorder', 'titleColor', 'edgeLabelBackground']) expect(cfg.themeVariables[key]).toBeTruthy();
  });
});
