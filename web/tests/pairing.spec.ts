import { describe, expect, it } from 'vitest';
import { parsePairingText, readPairingLink } from '../src/lib/pairing';

const KEY = 'A'.repeat(43); // base64url of 32 bytes is 43 chars
const RELAY = 'wss://levity-relay.example.workers.dev';
const hash = (p: string, r: string) => `#p=${p}&r=${encodeURIComponent(r)}`;

describe('readPairingLink', () => {
  it('accepts a valid link', () => {
    expect(readPairingLink(hash(KEY, RELAY))).toEqual({ p: KEY, r: RELAY });
  });

  it('accepts a local ws:// relay for development only', () => {
    expect(readPairingLink(hash(KEY, 'ws://localhost:8787'))).not.toBeNull();
    expect(readPairingLink(hash(KEY, 'ws://127.0.0.1:8787'))).not.toBeNull();
  });

  it.each([
    ['plain ws:// to a real host', hash(KEY, 'ws://levity-relay.example.com')],
    ['https:// instead of wss://', hash(KEY, 'https://levity-relay.example.com')],
    ['a key that is too short', hash('abc', RELAY)],
    ['a key with illegal characters', hash('!'.repeat(43), RELAY)],
    ['a missing key', `#r=${encodeURIComponent(RELAY)}`],
    ['a missing relay', `#p=${KEY}`],
    ['a relay that is not a URL', hash(KEY, 'not a url')],
    ['an empty hash', ''],
  ])('rejects %s', (_name, h) => {
    expect(readPairingLink(h)).toBeNull();
  });

  it('works with or without the leading #', () => {
    expect(readPairingLink(`p=${KEY}&r=${encodeURIComponent(RELAY)}`)).not.toBeNull();
  });
});

describe('parsePairingText', () => {
  const origin = 'https://levity.example.com';

  it('accepts a link from this very site', () => {
    const out = parsePairingText(`${origin}/${hash(KEY, RELAY)}`, origin);
    expect('link' in out && out.link).toEqual({ p: KEY, r: RELAY });
  });

  it('tolerates whitespace around a pasted link', () => {
    expect('link' in parsePairingText(`  ${origin}/${hash(KEY, RELAY)}\n`, origin)).toBe(true);
  });

  it('refuses a QR code that points at another site, so nobody can steer the app to their relay', () => {
    const out = parsePairingText(`https://evil.example.net/${hash(KEY, 'wss://evil.example.net')}`, origin);
    expect(out).toEqual({ error: 'That QR code is not from Levity on this site.' });
  });

  it('refuses text that is not a URL', () => {
    expect(parsePairingText('hello', origin)).toEqual({ error: 'That is not a Levity link.' });
  });

  it('explains an incomplete or expired link', () => {
    const out = parsePairingText(`${origin}/#p=short`, origin);
    expect('error' in out && out.error).toMatch(/incomplete or has expired/);
  });
});
