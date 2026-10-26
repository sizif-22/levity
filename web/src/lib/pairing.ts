import { SecureChannel, b64u, importKey, roomIdFromKey } from './crypto';
import { saveDevice, type DeviceRecord } from './store';

export interface PairingLink {
  /** One-time pairing key from the QR code (base64url). */
  p: string;
  /** Relay address, e.g. wss://levity-relay.example.workers.dev */
  r: string;
}

/** Reads #p=…&r=… from the QR link. Returns null when the page was opened normally. */
export function readPairingLink(hash: string): PairingLink | null {
  const params = new URLSearchParams(hash.replace(/^#/, ''));
  const p = params.get('p');
  const r = params.get('r');
  if (!p || !r || !/^[A-Za-z0-9_-]{43}$/.test(p)) return null;
  try {
    const url = new URL(r);
    const local = ['localhost', '127.0.0.1'].includes(url.hostname);
    if (url.protocol !== 'wss:' && !(url.protocol === 'ws:' && local)) return null;
  } catch {
    return null;
  }
  return { p, r };
}

export function deviceNameGuess(): string {
  const ua = navigator.userAgent;
  const os = /iPhone/.test(ua) ? 'iPhone' : /iPad/.test(ua) ? 'iPad' : /Android/.test(ua) ? 'Android phone' : 'Phone';
  const browser = /EdgA|EdgiOS/.test(ua) ? 'Edge' : /CriOS|Chrome/.test(ua) ? 'Chrome' : /FxiOS|Firefox/.test(ua) ? 'Firefox' : /Safari/.test(ua) ? 'Safari' : 'browser';
  return `${os} (${browser})`;
}

export type PairOutcome = { ok: true; device: DeviceRecord } | { ok: false; reason: 'denied' | 'timeout' | 'network' };

/**
 * Asks the PC for access over the one-time pairing room. The PC shows `code` next to an approval prompt;
 * on approval it replies with a fresh per-device key, which is stored non-extractable.
 */
export async function requestPairing(link: PairingLink, name: string, code: string, signal: AbortSignal): Promise<PairOutcome> {
  const pairingKeyRaw = b64u.dec(link.p);
  const channel = new SecureChannel(await importKey(pairingKeyRaw));
  const room = await roomIdFromKey(pairingKeyRaw);

  return new Promise<PairOutcome>((resolve) => {
    const socket = new WebSocket(`${link.r.replace(/\/$/, '')}/ws?room=${room}&role=phone`);
    let settled = false;
    const finish = (outcome: PairOutcome) => {
      if (settled) return;
      settled = true;
      window.clearTimeout(timeout);
      socket.close();
      resolve(outcome);
    };
    const timeout = window.setTimeout(() => finish({ ok: false, reason: 'timeout' }), 5 * 60_000);
    signal.addEventListener('abort', () => finish({ ok: false, reason: 'timeout' }));

    socket.onopen = async () => {
      socket.send(JSON.stringify({ d: await channel.seal({ type: 'pair_request', name, code }) }));
    };
    socket.onerror = () => finish({ ok: false, reason: 'network' });
    socket.onclose = () => finish({ ok: false, reason: 'network' });

    socket.onmessage = async (e) => {
      let env: { d?: string };
      try { env = JSON.parse(e.data as string); } catch { return; }
      if (typeof env.d !== 'string') return;

      const msg = (await channel.open(env.d)) as { type?: string; deviceId?: string; key?: string } | null;
      if (!msg) return;
      if (msg.type === 'pair_denied') return finish({ ok: false, reason: 'denied' });
      if (msg.type !== 'pair_approved' || !msg.deviceId || !msg.key) return;

      const raw = b64u.dec(msg.key);
      if (raw.length !== 32) return;
      const device: DeviceRecord = {
        deviceId: msg.deviceId,
        name,
        relay: link.r,
        roomId: await roomIdFromKey(raw),
        key: await importKey(raw),
      };
      await saveDevice(device);
      finish({ ok: true, device });
    };
  });
}
