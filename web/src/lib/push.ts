// Web Push on the phone: subscribe with the PC's key, hand the address to the PC, never to anything else.
// The PC encrypts the notice text to this phone's keys (p256dh, auth), so the push services only relay bytes. With no keys the PC can only send an empty ping and the service worker shows a generic notice.

export type PushSupport = 'ok' | 'unsupported' | 'needs-install' | 'denied';

export function urlBase64ToUint8Array(s: string): Uint8Array {
  const b64 = (s + '='.repeat((4 - (s.length % 4)) % 4)).replace(/-/g, '+').replace(/_/g, '/');
  const raw = atob(b64);
  const out = new Uint8Array(raw.length);
  for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
  return out;
}

interface Env {
  hasServiceWorker: boolean; hasPushManager: boolean; hasNotification: boolean;
  permission: string; ios: boolean; standalone: boolean;
}

export function readEnv(): Env {
  const w = typeof window === 'undefined' ? undefined : window;
  const nav = typeof navigator === 'undefined' ? undefined : navigator;
  const ua = nav?.userAgent ?? '';
  const ios = /iPhone|iPad|iPod/.test(ua) || (nav?.platform === 'MacIntel' && (nav?.maxTouchPoints ?? 0) > 1);
  return {
    hasServiceWorker: !!nav && 'serviceWorker' in nav,
    hasPushManager: !!w && 'PushManager' in w,
    hasNotification: !!w && 'Notification' in w,
    permission: w && 'Notification' in w ? Notification.permission : 'default',
    ios,
    standalone: (nav as { standalone?: boolean } | undefined)?.standalone === true || !!w?.matchMedia?.('(display-mode: standalone)').matches,
  };
}

/** Whether notifications can work here, and if not, why. On an iPhone they only work in the installed app. */
export function pushSupport(e: Env = readEnv()): PushSupport {
  if (e.ios && !e.standalone) return 'needs-install';
  if (!e.hasServiceWorker || !e.hasPushManager || !e.hasNotification) return 'unsupported';
  if (e.permission === 'denied') return 'denied';
  return 'ok';
}

export function pushHelp(s: PushSupport, ios: boolean): string {
  switch (s) {
    case 'needs-install': return 'On an iPhone, notifications only work in the installed app. Tap Share, then Add to Home Screen, and open Levity from there.';
    case 'unsupported': return 'This browser cannot show notifications from a web app.';
    case 'denied': return ios
      ? 'Notifications are blocked. Open the iPhone Settings app, find Levity, and allow notifications.'
      : 'Notifications are blocked. Long-press the Levity icon, then App info > Notifications > Allow.';
    default: return 'When an answer or a job finishes and the app is not open, you get a notice with the project, what you asked, how the answer starts, the steps and time it took, and how many files changed. The text is encrypted to this phone. To keep notices plain ("Levity has news"), set LEVITY_PUSH_DETAILS=0 on the PC.';
  }
}

/**
 * An old subscription (made before notices carried text) has no keys, so the PC can only send an empty ping. When notifications are already
 * allowed on this phone it can be renewed without a prompt: the PC says `detailed: false` for it.
 */
export function needsKeyUpgrade(state: { subscribed: boolean; detailed?: boolean } | null, support: PushSupport, permission: string): boolean {
  return !!state && state.subscribed && state.detailed === false && support === 'ok' && permission === 'granted';
}

/** Asks permission (must be called from a tap), subscribes with the PC's key and returns the address to give to the PC. */
export async function subscribeForPush(publicKey: string): Promise<{ endpoint: string; p256dh?: string; auth?: string } | { error: string }> {
  try {
    if (Notification.permission !== 'granted') {
      const p = await Notification.requestPermission();
      if (p !== 'granted') return { error: 'Notifications were not allowed.' };
    }
    const reg = await navigator.serviceWorker.ready;
    const existing = await reg.pushManager.getSubscription();
    // A subscription made with another key (a reinstalled PC app) must be replaced.
    if (existing) await existing.unsubscribe().catch(() => undefined);
    const sub = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: urlBase64ToUint8Array(publicKey) as BufferSource });
    const json = typeof sub.toJSON === 'function' ? sub.toJSON() : null;
    return {
      endpoint: sub.endpoint,
      p256dh: json?.keys?.p256dh,
      auth: json?.keys?.auth,
    };
  } catch (e) {
    return { error: 'Could not turn notifications on: ' + (e instanceof Error ? e.message : String(e)) };
  }
}

export async function unsubscribeFromPush(): Promise<void> {
  try {
    const reg = await navigator.serviceWorker.ready;
    await (await reg.pushManager.getSubscription())?.unsubscribe();
  } catch { /* nothing to undo */ }
}
