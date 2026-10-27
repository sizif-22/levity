// Shared by the sidebar UI tests: loads the real wwwroot page in jsdom behind a fake WebView2 bridge.
// (UI tests for the desktop sidebar live next to this file.) The real page and scripts are loaded in jsdom with a fake
// WebView2 bridge, then driven with the same messages the PC sends and the same clicks a user makes.
import { JSDOM, VirtualConsole } from 'jsdom';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const page = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'wwwroot', 'index.html');

/** Loads the sidebar like WebView2 would. `posted` collects what the UI sends to the PC. */
export async function open(options = {}) {
  const posted = [];
  const handlers = [];
  const errors = [];
  const virtualConsole = new VirtualConsole();
  virtualConsole.on('jsdomError', (e) => errors.push(e.stack || String(e)));

  const dom = await JSDOM.fromFile(page, {
    runScripts: 'dangerously',
    resources: 'usable',
    pretendToBeVisual: true,
    virtualConsole,
    beforeParse(window) {
      window.chrome = {
        webview: {
          postMessage: (m) => posted.push(m),
          addEventListener: (type, fn) => { if (type === 'message') handlers.push(fn); },
        },
      };
      window.HTMLElement.prototype.scrollTo = function () {};
      window.HTMLElement.prototype.scrollIntoView = function () {};
      window.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };
      if (options.mermaid) window.mermaid = options.mermaid;
      if (options.clipboard) Object.defineProperty(window.navigator, 'clipboard', { value: options.clipboard, configurable: true });
      window.matchMedia ??= () => ({ matches: false, addEventListener() {}, removeEventListener() {} });
    },
  });
  await new Promise((resolve) => dom.window.addEventListener('load', resolve));

  const { window } = dom;
  const { document } = window;
  const api = {
    window,
    document,
    posted,
    errors,
    $: (id) => document.getElementById(id),
    q: (sel, root = document) => root.querySelector(sel),
    qa: (sel, root = document) => Array.from(root.querySelectorAll(sel)),
    deliver(msg) { handlers.forEach((h) => h({ data: msg })); },
    click(el) { el.dispatchEvent(new window.MouseEvent('click', { bubbles: true })); },
    type(text) {
      const box = document.getElementById('promptInput');
      box.value = text;
      box.dispatchEvent(new window.Event('input', { bubbles: true }));
    },
    // Round-trip through JSON so objects built inside jsdom compare equal to plain objects.
    sent(type) { return JSON.parse(JSON.stringify(posted.filter((m) => m.type === type))); },
  };
  posted.length = 0; // drop the startup "ready" message
  return api;
}

export const reply = (text) => ({ type: 'agy_event', payload: { event: 'step_update', step_update: { step_type: 'agent_response', state: 'ACTIVE', text_delta: text } } });
export const result = (extra = {}) => ({ type: 'agy_event', payload: { event: 'result', result: { status: 'SUCCESS', duration_seconds: 1.5, usage: { total_tokens: 1234 }, ...extra } } });

