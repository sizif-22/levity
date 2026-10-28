import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';

// The App is driven exactly as the real one is: through the relay connection and the stored pairing. Both are replaced with fakes.
const live = vi.hoisted(() => ({ conn: null as null | { handlers: { onMessage: (m: Record<string, unknown>) => void; onState: (s: string) => void; onOpen?: () => void }; sent: Record<string, unknown>[] } }));

vi.mock('../src/lib/store', () => ({
  loadDevice: async () => ({ deviceId: 'd1', name: 'test phone', relay: 'wss://relay.example', roomId: 'room', key: {} as CryptoKey }),
  clearDevice: async () => {},
  saveDevice: async () => {},
}));

vi.mock('../src/lib/crypto', async (orig) => ({ ...(await orig<typeof import('../src/lib/crypto')>()), SecureChannel: class { } }));

vi.mock('../src/lib/relay', () => ({
  RelayConnection: class {
    handlers: { onMessage: (m: Record<string, unknown>) => void; onState: (s: string) => void; onOpen?: () => void };
    sent: Record<string, unknown>[] = [];
    constructor(_url: string, _room: string, _channel: unknown, handlers: typeof live.conn extends null ? never : NonNullable<typeof live.conn>['handlers']) {
      this.handlers = handlers;
      live.conn = this;
    }
    start() { queueMicrotask(() => { this.handlers.onState('online'); this.handlers.onOpen?.(); }); }
    close() {}
    async send(m: Record<string, unknown>) { this.sent.push(m); return true; }
  },
}));

const render_ = vi.fn(async (_id: string, _src: string) => ({
  svg: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 50"><g class="node" id="flowchart-n1-0"><rect width="10" height="10"/><text>E</text></g></svg>',
}));
vi.mock('mermaid', () => ({ default: { initialize: vi.fn(), render: (id: string, src: string) => render_(id, src) } }));

import App from '../src/App';

const PROJECT = 'C:\\Work\\app';

const diagramMsg = (over: Record<string, unknown> = {}) => ({
  type: 'diagram', path: PROJECT, overview: 'A small app.', groups: [], stale: false, notes: [], tokens: 100,
  nodes: [{ i: 1, label: 'Entry point', group: null, path: 'Program.cs', shape: 'box' }],
  edges: [],
  mermaid: 'flowchart TD\n  n1["Entry point"]\n', mermaidFlat: 'flowchart TD\n  n1["Entry point"]\n',
  generatedUtc: '2026-10-27T12:00:00Z', ...over,
});

async function openApp() {
  render(<App />);
  await waitFor(() => expect(live.conn).not.toBeNull());
  await waitFor(() => expect(screen.getByPlaceholderText(/Ask Levity anything/)).toBeTruthy());
  const conn = live.conn!;
  await act(async () => { conn.handlers.onMessage({ type: 'config', cwd: PROJECT, isPinned: false }); });
  await act(async () => { conn.handlers.onMessage({ type: 'actions', path: PROJECT, packageManager: null, scripts: [], git: { repo: true, branch: 'main' } }); });   // the diagram chip is for git projects
  conn.sent.length = 0;
  const deliver = (m: Record<string, unknown>) => act(async () => { conn.handlers.onMessage(m); });
  return { conn, deliver };
}

beforeEach(() => { live.conn = null; render_.mockClear(); localStorage.clear(); });
afterEach(cleanup);

describe('phone App: architecture diagram', () => {
  it('the /diagram chip opens the diagram view for the current folder and asks the PC for a saved diagram', async () => {
    const { conn } = await openApp();
    fireEvent.click(screen.getByText('/diagram'));
    expect(screen.getByText('Architecture diagram')).toBeTruthy();
    expect(conn.sent).toEqual([{ type: 'diagram_get', path: PROJECT }]);
  });

  it('typing /diagram opens the view and is not sent to the assistant as a chat message', async () => {
    const { conn } = await openApp();
    const box = screen.getByPlaceholderText(/Ask Levity anything/);
    fireEvent.change(box, { target: { value: '/diagram' } });
    fireEvent.click(screen.getByTitle(/^Send/));
    expect(conn.sent).toEqual([{ type: 'diagram_get', path: PROJECT }]);
    expect(conn.sent.some((m) => m.type === 'send_prompt')).toBe(false);
    expect((box as HTMLTextAreaElement).value).toBe('');
  });

  it('a project in the Projects list has a diagram button for that project', async () => {
    const { conn, deliver } = await openApp();
    await deliver({ type: 'projects', root: 'D:\\', current: PROJECT, projects: [{ name: 'other', path: 'D:\\Work\\other', kind: 'Git', hasReadme: true, hasAgentsDoc: false, hasSkills: false, modified: new Date().toISOString(), pinned: false, uses: 0 }] });
    fireEvent.click(screen.getByTitle('Projects & review'));
    await deliver({ type: 'projects', root: 'D:\\', current: PROJECT, projects: [{ name: 'other', path: 'D:\\Work\\other', kind: 'Git', hasReadme: true, hasAgentsDoc: false, hasSkills: false, modified: new Date().toISOString(), pinned: false, uses: 0 }] });
    conn.sent.length = 0;
    fireEvent.click(await screen.findByTitle('Architecture diagram'));
    expect(conn.sent).toEqual([{ type: 'diagram_get', path: 'D:\\Work\\other' }]);
  });

  it('shows the preview, and only a press of Generate starts the run', async () => {
    const { conn, deliver } = await openApp();
    fireEvent.click(screen.getByText('/diagram'));
    conn.sent.length = 0;

    await deliver({ type: 'diagram_none', path: PROJECT });
    fireEvent.click(screen.getByText('Make a diagram'));
    expect(conn.sent).toEqual([{ type: 'diagram_prepare', path: PROJECT }]);

    await deliver({ type: 'diagram_prepared', path: PROJECT, files: 50, excerpts: 12, indexed: 20, redactions: 2, truncated: false, kb: 30 });
    expect(document.body.textContent).toMatch(/50 project files/);
    expect(conn.sent.some((m) => m.type === 'diagram_generate')).toBe(false);

    fireEvent.click(screen.getByText('Generate'));
    expect(conn.sent.at(-1)).toEqual({ type: 'diagram_generate', path: PROJECT });
  });

  it('shows progress and errors that arrive for this project', async () => {
    const { deliver } = await openApp();
    fireEvent.click(screen.getByText('/diagram'));
    await deliver({ type: 'diagram_state', path: PROJECT, phase: 'generating', message: 'Asking the assistant (50 files listed, 12 excerpts)...' });
    expect(screen.getByText(/Asking the assistant \(50 files/)).toBeTruthy();
    await deliver({ type: 'diagram_state', path: PROJECT, phase: 'error', message: 'The assistant did not answer within 300 seconds.' });
    expect(screen.getByText(/did not answer/)).toBeTruthy();
  });

  it('draws a diagram that arrives, and a tap on a component sends its number', async () => {
    const { conn, deliver } = await openApp();
    fireEvent.click(screen.getByText('/diagram'));
    conn.sent.length = 0;
    await deliver(diagramMsg());
    await waitFor(() => expect(document.querySelector('#flowchart-n1-0')).not.toBeNull());
    expect(screen.getByText('A small app.')).toBeTruthy();
    fireEvent.click(document.querySelector('#flowchart-n1-0')!);
    expect(conn.sent).toEqual([{ type: 'diagram_open', path: PROJECT, node: 1 }]);
  });

  it('ignores diagram messages about another project, whatever the slashes or case', async () => {
    const { deliver } = await openApp();
    fireEvent.click(screen.getByText('/diagram'));
    await deliver(diagramMsg({ path: 'D:\\Somewhere\\else' }));
    await deliver({ type: 'diagram_state', path: 'D:\\Somewhere\\else', phase: 'error', message: 'not mine' });
    expect(screen.getByText('No diagram for this project yet.')).toBeTruthy();
    expect(screen.queryByText('not mine')).toBeNull();

    await deliver(diagramMsg({ path: 'c:/work/APP/' }));   // the same folder spelled differently
    await waitFor(() => expect(screen.getByText('A small app.')).toBeTruthy());
  });

  it('a diagram for a different project than the one now open does not replace it', async () => {
    const { deliver } = await openApp();
    fireEvent.click(screen.getByText('/diagram'));
    await deliver(diagramMsg());
    await waitFor(() => expect(screen.getByText('A small app.')).toBeTruthy());
    await deliver(diagramMsg({ path: 'D:\\Other', overview: 'someone else' }));
    expect(screen.queryByText('someone else')).toBeNull();
  });

  it('closing the view returns to the chat and leaves a pending preview behind it cleared', async () => {
    const { deliver } = await openApp();
    fireEvent.click(screen.getByText('/diagram'));
    await deliver({ type: 'diagram_prepared', path: PROJECT, files: 5, excerpts: 2, indexed: 2, redactions: 0, truncated: false, kb: 3 });
    expect(screen.getByText('Generate')).toBeTruthy();
    fireEvent.click(screen.getAllByTitle('Close')[0]);
    expect(screen.queryByText('Architecture diagram')).toBeNull();

    fireEvent.click(screen.getByText('/diagram'));
    expect(screen.queryByText('Generate')).toBeNull();      // the old preview did not come back
    expect(screen.getByText('Make a diagram')).toBeTruthy();
  });
});

describe('phone App: the /diagram chip', () => {
  it('is not offered for a folder that is not a git repository, and comes back when it is one', async () => {
    const { deliver } = await openApp();
    expect(screen.queryByText('/diagram')).not.toBeNull();
    await deliver({ type: 'actions', path: 'D:\Plain', packageManager: null, scripts: [], git: { repo: false, branch: null } });
    expect(screen.queryByText('/diagram')).toBeNull();
    await deliver({ type: 'actions', path: PROJECT, packageManager: null, scripts: [], git: { repo: true, branch: 'main' } });
    expect(screen.queryByText('/diagram')).not.toBeNull();
  });
});

describe('phone App: Fast / Deep', () => {
  it('shows the level the PC reports and asks the PC to switch when tapped', async () => {
    const { conn, deliver } = await openApp();
    const chip = () => screen.getByTitle(/Fast: quick answers/);
    expect(chip().textContent).toBe('Fast');

    await deliver({ type: 'config', effort: 'high' });
    expect(chip().textContent).toBe('Deep');
    expect(chip().getAttribute('aria-pressed')).toBe('true');

    fireEvent.click(chip());
    expect(conn.sent.at(-1)).toEqual({ type: 'set_effort', value: 'low' });

    await deliver({ type: 'config', effort: 'low' });
    expect(chip().textContent).toBe('Fast');
    fireEvent.click(chip());
    expect(conn.sent.at(-1)).toEqual({ type: 'set_effort', value: 'high' });
  });

  it('keeps the old level when a config message does not mention it (a folder change)', async () => {
    const { deliver } = await openApp();
    await deliver({ type: 'config', effort: 'high' });
    await deliver({ type: 'config', cwd: 'D:\\Other', isPinned: false });
    expect(screen.getByTitle(/Fast: quick answers/).textContent).toBe('Deep');
  });
});

describe('phone App: Plan / Work', () => {
  it('shows the mode the PC reports, asks to switch, and keeps it across a folder change', async () => {
    const { conn, deliver } = await openApp();
    const chip = () => screen.getByTitle(/Plan: the assistant only plans/);
    expect(chip().textContent).toBe('Work');
    await deliver({ type: 'config', mode: 'plan' });
    expect(chip().textContent).toBe('Plan');
    fireEvent.click(chip());
    expect(conn.sent.at(-1)).toEqual({ type: 'set_mode', value: 'auto' });
    await deliver({ type: 'config', cwd: 'D:\\Other', isPinned: false });
    expect(chip().textContent).toBe('Plan');
  });
});
