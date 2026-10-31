import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it, vi } from 'vitest';

type Handler = (event: unknown) => void;

/** Loads public/sw.js against a fake service-worker scope and returns what the push handler asked the phone to show. */
async function pushWith(data: unknown): Promise<{ title: string; options: Record<string, unknown> }> {
  const handlers: Record<string, Handler> = {};
  const showNotification = vi.fn(async () => undefined);
  const scope = {
    addEventListener: (type: string, fn: Handler) => { handlers[type] = fn; },
    registration: { showNotification },
    clients: { matchAll: async () => [], claim: async () => undefined, openWindow: async () => undefined },
    skipWaiting: async () => undefined,
    location: { origin: 'https://levity.example' },
  };
  const source = readFileSync(resolve(__dirname, '../public/sw.js'), 'utf8');
  new Function('self', 'caches', source)(scope, { open: async () => ({}), keys: async () => [], match: async () => undefined });

  let work: Promise<unknown> = Promise.resolve();
  const event = {
    data: data === undefined ? null : typeof data === 'string' ? { json: () => JSON.parse(data), text: () => data } : { json: () => data, text: () => JSON.stringify(data) },
    waitUntil: (p: Promise<unknown>) => { work = p; },
  };
  handlers.push(event);
  await work;
  const [title, options] = showNotification.mock.calls[0] as unknown as [string, Record<string, unknown>];
  return { title, options };
}

describe('service worker push', () => {
  it('shows the title and body the PC sent, and keeps a notice per project', async () => {
    const { title, options } = await pushWith({ title: 'Levity · PostPilot · done', body: 'Re: add TikTok images\nPlan written.\n44 steps · 11 min 02 s', tag: 'levity-postpilot' });
    expect(title).toBe('Levity · PostPilot · done');
    expect(options.body).toBe('Re: add TikTok images\nPlan written.\n44 steps · 11 min 02 s');
    expect(options.tag).toBe('levity-postpilot');
    expect(options.silent).toBe(false);   // always show something visible: iOS cancels subscriptions that stay silent
  });

  it('an empty ping still shows the generic notice', async () => {
    const { title, options } = await pushWith(undefined);
    expect(title).toBe('Levity has news');
    expect(options.body).toBe('Open Levity to see it.');
    expect(options.tag).toBe('levity-news');
  });

  it('a payload that is not JSON becomes the body', async () => {
    const { title, options } = await pushWith('plain words');
    expect(title).toBe('Levity has news');
    expect(options.body).toBe('plain words');
  });

  it('a missing or odd tag falls back to the shared one', async () => {
    expect((await pushWith({ title: 't', body: 'b' })).options.tag).toBe('levity-news');
    expect((await pushWith({ title: 't', body: 'b', tag: 'Has Spaces!' })).options.tag).toBe('levity-news');
    expect((await pushWith({ title: 't', body: 'b', tag: 'x'.repeat(41) })).options.tag).toBe('levity-news');
  });
});
