// Integration tests for the relay: boots the real Worker + Durable Object locally with `wrangler dev`
// and talks to it over WebSockets, exactly as the PC and the phones do. Needs Node 22+ (global WebSocket).
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const PORT = Number(process.env.RELAY_TEST_PORT ?? 8799);
const HTTP = `http://127.0.0.1:${PORT}`;
const WS = `ws://127.0.0.1:${PORT}`;
const state = mkdtempSync(join(tmpdir(), 'levity-relay-'));
let wrangler;

const hex32 = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, '0')).join('');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

before(async () => {
  wrangler = spawn(process.execPath, [join(root, 'node_modules', 'wrangler', 'bin', 'wrangler.js'), 'dev',
    '--port', String(PORT), '--ip', '127.0.0.1', '--persist-to', state, '--log-level', 'warn'], {
    cwd: root,
    env: { ...process.env, CI: '1', WRANGLER_SEND_METRICS: 'false', NO_COLOR: '1' },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  let log = '';
  wrangler.stdout.on('data', (d) => (log += d));
  wrangler.stderr.on('data', (d) => (log += d));

  const deadline = Date.now() + 90_000;
  for (;;) {
    try {
      const r = await fetch(HTTP);
      if (r.ok) return;
    } catch { /* not up yet */ }
    if (wrangler.exitCode !== null) throw new Error(`wrangler exited early:\n${log}`);
    if (Date.now() > deadline) throw new Error(`wrangler did not start in 90 s:\n${log}`);
    await sleep(500);
  }
});

after(async () => {
  if (wrangler && wrangler.exitCode === null) {
    wrangler.kill();
    await Promise.race([new Promise((r) => wrangler.once('exit', r)), sleep(5000)]);
  }
  try { rmSync(state, { recursive: true, force: true }); } catch { /* best effort */ }
});

/** Opens a socket and records every JSON frame it receives. */
function connect(room, role) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`${WS}/ws?room=${room}&role=${role}`);
    const frames = [];
    const waiters = [];
    ws.onmessage = (e) => {
      let m;
      try { m = JSON.parse(e.data); } catch { return; }
      frames.push(m);
      for (const w of [...waiters]) if (w.pred(m)) { waiters.splice(waiters.indexOf(w), 1); w.resolve(m); }
    };
    const client = {
      ws,
      frames,
      closed: new Promise((r) => { ws.onclose = (e) => r(e); }),
      send: (obj) => ws.send(typeof obj === 'string' ? obj : JSON.stringify(obj)),
      next: (pred, ms = 5000) => {
        const seen = frames.find(pred);
        if (seen) return Promise.resolve(seen);
        return new Promise((res, rej) => {
          const t = setTimeout(() => rej(new Error(`timed out waiting for a frame; got ${JSON.stringify(frames)}`)), ms);
          waiters.push({ pred, resolve: (m) => { clearTimeout(t); res(m); } });
        });
      },
      close: () => ws.close(),
    };
    ws.onopen = () => resolve(client);
    ws.onerror = () => reject(new Error('websocket error'));
  });
}

test('plain HTTP answers and bad requests are rejected', async () => {
  assert.equal(await (await fetch(HTTP)).text(), 'levity relay');
  assert.equal((await fetch(`${HTTP}/ws?room=nothex&role=host`)).status, 400);
  assert.equal((await fetch(`${HTTP}/ws?room=${hex32()}&role=admin`)).status, 400);
  assert.equal((await fetch(`${HTTP}/ws?room=${hex32()}&role=host`)).status, 426); // no websocket upgrade
});

test('host and phone exchange opaque frames, with presence events', async () => {
  const room = hex32();
  const host = await connect(room, 'host');
  const phone = await connect(room, 'phone');

  const join = await host.next((m) => m.ev === 'join');
  assert.match(join.c, /^[0-9a-f-]{8}$/);
  assert.equal((await phone.next((m) => m.ev === 'host')).online, true);

  phone.send({ d: 'ciphertext-from-phone' });
  const toHost = await host.next((m) => m.d === 'ciphertext-from-phone');
  assert.equal(toHost.c, join.c); // the host learns which phone sent it

  host.send({ d: 'ciphertext-from-host' }); // no "to": every phone
  assert.deepEqual(await phone.next((m) => m.d === 'ciphertext-from-host'), { d: 'ciphertext-from-host' });

  phone.close();
  assert.equal((await host.next((m) => m.ev === 'leave')).c, join.c);
  host.close();
});

test('host frames with "to" reach only that phone', async () => {
  const room = hex32();
  const host = await connect(room, 'host');
  const a = await connect(room, 'phone');
  const b = await connect(room, 'phone');
  const joins = [];
  await host.next((m) => (m.ev === 'join' ? joins.push(m.c) >= 2 : false));
  assert.equal(joins.length, 2);

  // Ask each phone for its id by having it speak first.
  a.send({ d: 'i-am-a' });
  const idA = (await host.next((m) => m.d === 'i-am-a')).c;

  host.send({ d: 'only-for-a', to: idA });
  host.send({ d: 'for-everyone' });
  await a.next((m) => m.d === 'only-for-a');
  await b.next((m) => m.d === 'for-everyone');
  assert.ok(!b.frames.some((m) => m.d === 'only-for-a'), 'phone B must not see a frame addressed to phone A');
  [host, a, b].forEach((c) => c.close());
});

test('rooms are isolated from each other', async () => {
  const host1 = await connect(hex32(), 'host');
  const room2 = hex32();
  const host2 = await connect(room2, 'host');
  const phone2 = await connect(room2, 'phone');
  phone2.send({ d: 'room-two-secret' });
  await host2.next((m) => m.d === 'room-two-secret');
  await sleep(200);
  assert.ok(!host1.frames.some((m) => m.d === 'room-two-secret'));
  [host1, host2, phone2].forEach((c) => c.close());
});

test('a phone joining first sees the host offline, then online when it arrives', async () => {
  const room = hex32();
  const phone = await connect(room, 'phone');
  assert.equal((await phone.next((m) => m.ev === 'host')).online, false);
  const host = await connect(room, 'host');
  await phone.next((m) => m.ev === 'host' && m.online === true);
  await host.next((m) => m.ev === 'join'); // the host is told about phones already waiting
  host.close();
  await phone.next((m) => m.ev === 'host' && m.online === false);
  phone.close();
});

test('a newer host replaces the old one without reporting the phone as offline', async () => {
  const room = hex32();
  const phone = await connect(room, 'phone');
  const first = await connect(room, 'host');
  const second = await connect(room, 'host');
  const closed = await first.closed;
  assert.equal(closed.code, 1000);
  await sleep(300);
  const presence = phone.frames.filter((m) => m.ev === 'host').map((m) => m.online);
  // offline (phone arrived first), then online for each host; never offline again while a host remains
  assert.deepEqual(presence, [false, true, true], 'replacement must not look like the host going offline');
  phone.send({ d: 'after-replace' });
  await second.next((m) => m.d === 'after-replace');
  [phone, second].forEach((c) => c.close());
});

test('malformed or non-string payloads are ignored and the socket stays usable', async () => {
  const room = hex32();
  const host = await connect(room, 'host');
  const phone = await connect(room, 'phone');
  await host.next((m) => m.ev === 'join');
  phone.send('this is not json');
  phone.send({ d: 42 });
  phone.send({ nothing: true });
  phone.send({ d: 'still-works' });
  assert.ok(await host.next((m) => m.d === 'still-works'));
  assert.ok(!host.frames.some((m) => m.d === 42));
  [host, phone].forEach((c) => c.close());
});

test('an oversized frame closes the connection', async () => {
  const room = hex32();
  const phone = await connect(room, 'phone');
  phone.send({ d: 'x'.repeat(1_000_001) });
  const closed = await Promise.race([phone.closed, sleep(5000).then(() => null)]);
  assert.ok(closed, 'socket should have been closed');
  assert.equal(closed.code, 1009);
});

test('a room holds at most 8 phones', async () => {
  const room = hex32();
  const phones = [];
  for (let i = 0; i < 8; i++) phones.push(await connect(room, 'phone'));
  await assert.rejects(connect(room, 'phone'), 'the ninth phone must be refused (HTTP 429 on the upgrade)');
  phones.forEach((p) => p.close());
});
