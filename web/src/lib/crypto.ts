/**
 * End-to-end encryption shared with the PC (Services/RelayCrypto.cs).
 * Frame = base64url( iv[12] || AES-256-GCM(ciphertext || tag) ), AAD = direction label.
 * Plaintext = {"s": sessionId, "n": counter, "t": unixMs, "m": message}.
 */

const enc = new TextEncoder();
const dec = new TextDecoder();

export const PHONE_TO_HOST = 'p2h';
export const HOST_TO_PHONE = 'h2p';
const MAX_SKEW_MS = 120_000;

export const b64u = {
  enc(bytes: Uint8Array): string {
    let s = '';
    for (const b of bytes) s += String.fromCharCode(b);
    return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  },
  dec(text: string): Uint8Array {
    const s = text.replace(/-/g, '+').replace(/_/g, '/');
    const bin = atob(s + '='.repeat((4 - (s.length % 4)) % 4));
    return Uint8Array.from(bin, (c) => c.charCodeAt(0));
  },
};

export function randomHex(bytes: number): string {
  return [...crypto.getRandomValues(new Uint8Array(bytes))].map((b) => b.toString(16).padStart(2, '0')).join('');
}

/** The room id is derived from the key, so the relay never learns the key. */
export async function roomIdFromKey(raw: Uint8Array): Promise<string> {
  const prefix = enc.encode('levity-room-v1');
  const input = new Uint8Array(prefix.length + raw.length);
  input.set(prefix);
  input.set(raw, prefix.length);
  const hash = new Uint8Array(await crypto.subtle.digest('SHA-256', input));
  return [...hash].map((b) => b.toString(16).padStart(2, '0')).join('').slice(0, 32);
}

/** Non-extractable: even script running on this page cannot read the key back out. */
export function importKey(raw: Uint8Array): Promise<CryptoKey> {
  return crypto.subtle.importKey('raw', raw as BufferSource, 'AES-GCM', false, ['encrypt', 'decrypt']);
}

export class SecureChannel {
  private readonly session = randomHex(8);
  private counter = 0;
  private readonly lastSeen = new Map<string, number>();

  private readonly key: CryptoKey;

  constructor(key: CryptoKey) {
    this.key = key;
  }

  async seal(message: unknown): Promise<string> {
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const plain = JSON.stringify({ s: this.session, n: ++this.counter, t: Date.now(), m: message });
    const cipher = new Uint8Array(
      await crypto.subtle.encrypt(
        { name: 'AES-GCM', iv, additionalData: enc.encode(PHONE_TO_HOST) },
        this.key,
        enc.encode(plain),
      ),
    );
    const frame = new Uint8Array(12 + cipher.length);
    frame.set(iv);
    frame.set(cipher, 12);
    return b64u.enc(frame);
  }

  /** Returns null for anything that fails authentication, is stale, or replays an earlier frame. */
  async open(frameText: string): Promise<unknown | null> {
    try {
      const frame = b64u.dec(frameText);
      if (frame.length < 28) return null;
      const plainBytes = await crypto.subtle.decrypt(
        { name: 'AES-GCM', iv: frame.slice(0, 12), additionalData: enc.encode(HOST_TO_PHONE) },
        this.key,
        frame.slice(12),
      );
      const { s, n, t, m } = JSON.parse(dec.decode(plainBytes)) as { s: string; n: number; t: number; m: unknown };
      if (typeof s !== 'string' || typeof n !== 'number' || typeof t !== 'number') return null;
      if (Math.abs(Date.now() - t) > MAX_SKEW_MS) return null;
      const last = this.lastSeen.get(s);
      if (last !== undefined && n <= last) return null;
      this.lastSeen.set(s, n);
      return m;
    } catch {
      return null;
    }
  }
}
