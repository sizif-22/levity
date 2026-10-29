// A short two-note chime made with the Web Audio API (no audio file to ship). Browsers only allow sound after a tap,
// so the first touch anywhere "unlocks" it; until then it stays silent rather than failing.

let ctx: AudioContext | null = null;
let installed = false;

function context(): AudioContext | null {
  if (typeof window === 'undefined') return null;
  if (ctx) return ctx;
  const Ctor = window.AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
  if (!Ctor) return null;
  ctx = new Ctor();
  return ctx;
}

export function installChimeUnlock(): void {
  if (typeof window === 'undefined' || installed) return;
  installed = true;
  const unlock = () => {
    const c = context();
    if (c && c.state === 'suspended') void c.resume();
    window.removeEventListener('pointerdown', unlock);
    window.removeEventListener('keydown', unlock);
  };
  window.addEventListener('pointerdown', unlock);
  window.addEventListener('keydown', unlock);
}

export async function playChime(): Promise<void> {
  const c = context();
  if (!c) return;
  try { if (c.state === 'suspended') await c.resume(); } catch { return; }
  if (c.state !== 'running') return;
  const start = c.currentTime;
  for (const { f, at } of [{ f: 880, at: 0 }, { f: 1318.5, at: 0.12 }]) {
    const osc = c.createOscillator();
    const gain = c.createGain();
    osc.type = 'sine';
    osc.frequency.value = f;
    gain.gain.setValueAtTime(0.0001, start + at);
    gain.gain.exponentialRampToValueAtTime(0.18, start + at + 0.02);
    gain.gain.exponentialRampToValueAtTime(0.0001, start + at + 0.35);
    osc.connect(gain);
    gain.connect(c.destination);
    osc.start(start + at);
    osc.stop(start + at + 0.4);
  }
}

/** Plays the chime when the service worker says a push arrived and the app is on screen. Returns the cleanup. */
export function listenForPushChime(): () => void {
  if (typeof navigator === 'undefined' || !('serviceWorker' in navigator)) return () => {};
  installChimeUnlock();
  const onMessage = (e: MessageEvent) => {
    if ((e.data as { type?: string } | null)?.type === 'push-received' && !document.hidden) void playChime();
  };
  navigator.serviceWorker.addEventListener('message', onMessage);
  return () => navigator.serviceWorker.removeEventListener('message', onMessage);
}
