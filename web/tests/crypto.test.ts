import { test } from 'node:test';
import assert from 'node:assert/strict';
import { SecureChannel, b64u, importKey, roomIdFromKey } from '../src/lib/crypto.ts';

const raw = crypto.getRandomValues(new Uint8Array(32));

// A "host" that frames messages the way Services/RelayCrypto.cs does (direction h2p).
async function hostFrame(key: CryptoKey, message: unknown, n = 1, t = Date.now(), direction = 'h2p', session = 'hostsession'): Promise<string> {
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const plain = new TextEncoder().encode(JSON.stringify({ s: session, n, t, m: message }));
  const ct = new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-GCM', iv, additionalData: new TextEncoder().encode(direction) }, key, plain));
  const out = new Uint8Array(12 + ct.length);
  out.set(iv); out.set(ct, 12);
  return b64u.enc(out);
}

test('accepts a valid host frame', async () => {
  const key = await importKey(raw);
  const ch = new SecureChannel(key);
  assert.deepEqual(await ch.open(await hostFrame(key, { type: 'status' })), { type: 'status' });
});

test('rejects replayed frames and accepts later ones', async () => {
  const key = await importKey(raw);
  const ch = new SecureChannel(key);
  const f1 = await hostFrame(key, { a: 1 }, 1);
  assert.ok(await ch.open(f1));
  assert.equal(await ch.open(f1), null);
  assert.ok(await ch.open(await hostFrame(key, { a: 2 }, 2)));
});

test('rejects tampered ciphertext', async () => {
  const key = await importKey(raw);
  const ch = new SecureChannel(key);
  const bytes = b64u.dec(await hostFrame(key, { a: 1 }));
  bytes[bytes.length - 1] ^= 1;
  assert.equal(await ch.open(b64u.enc(bytes)), null);
});

test('rejects frames in the wrong direction (reflection)', async () => {
  const key = await importKey(raw);
  const ch = new SecureChannel(key);
  assert.equal(await ch.open(await hostFrame(key, { a: 1 }, 1, Date.now(), 'p2h')), null);
});

test('rejects stale frames', async () => {
  const key = await importKey(raw);
  const ch = new SecureChannel(key);
  assert.equal(await ch.open(await hostFrame(key, { a: 1 }, 1, Date.now() - 10 * 60_000)), null);
});

test('rejects frames from another key', async () => {
  const other = await importKey(crypto.getRandomValues(new Uint8Array(32)));
  const ch = new SecureChannel(await importKey(raw));
  assert.equal(await ch.open(await hostFrame(other, { a: 1 })), null);
});

test('room id is stable, 32 hex chars, and differs per key', async () => {
  const a = await roomIdFromKey(raw);
  assert.match(a, /^[0-9a-f]{32}$/);
  assert.equal(a, await roomIdFromKey(raw));
  assert.notEqual(a, await roomIdFromKey(crypto.getRandomValues(new Uint8Array(32))));
});
