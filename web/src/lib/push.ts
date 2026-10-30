// Web Push on the phone: subscribe with the PC's key, hand the address to the PC, never to anything else.
// The push itself carries no text; the service worker shows a generic notice and the app then loads the details over the encrypted link.

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
    default: return 'You get a short "Levity has news" notice when an answer or a job finishes and the app is not open. It never contains your text.';
  }
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
