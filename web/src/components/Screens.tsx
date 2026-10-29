import { NotifyRow, type PushStateMsg } from './NotifyRow';
import { Suspense, lazy, useEffect, useRef, useState, type ReactNode } from 'react';
import { deviceNameGuess, parsePairingText, requestPairing, type PairingLink } from '../lib/pairing';
// The QR decoder is only needed while pairing, so keep it out of the first load.
const Scanner = lazy(() => import('./Scanner').then((m) => ({ default: m.Scanner })));
import type { DeviceRecord } from '../lib/store';
import { clearPin, hasPin, setPin, verifyPin } from '../lib/pin';
import { canPromptInstall, isIos, isStandalone, onInstallChange, promptInstall } from '../lib/install';

export const Logo = ({ small }: { small?: boolean }) => (
  <div className={small ? 'hero small' : 'hero'}>
    <svg viewBox="0 0 64 64" fill="none">
      <circle className="logo-orb" cx="32" cy="13" r="6" />
      <circle cx="32" cy="38" r="15" strokeWidth="7" />
    </svg>
  </div>
);

export const Center = ({ children }: { children: ReactNode }) => (
  <div className="overlay" style={{ position: 'relative', height: '100%' }}>
    <div className="overlay-card">{children}</div>
  </div>
);

/** Shown until this device is paired: scan the QR from inside the app, or paste the link. */
export function PairScreen({ link: initial, onPaired }: { link: PairingLink | null; onPaired: (d: DeviceRecord) => void }) {
  const [link, setLink] = useState<PairingLink | null>(initial);
  const [scanning, setScanning] = useState(false);
  const [pasting, setPasting] = useState(false);
  const [pasted, setPasted] = useState('');
  const [problem, setProblem] = useState('');
  const [name, setName] = useState(deviceNameGuess());
  const [phase, setPhase] = useState<'idle' | 'waiting' | 'denied' | 'timeout' | 'network'>('idle');
  const [code] = useState(() => String(crypto.getRandomValues(new Uint32Array(1))[0] % 10000).padStart(4, '0'));
  const abort = useRef<AbortController | null>(null);

  useEffect(() => () => abort.current?.abort(), []);

  const accept = (text: string) => {
    const parsed = parsePairingText(text, location.origin);
    if ('error' in parsed) { setProblem(parsed.error); return; }
    setProblem('');
    setPasting(false);
    setLink(parsed.link);
  };

  if (scanning) {
    return (
      <Suspense fallback={<Center><span className="spinner" /></Center>}>
        <Scanner onResult={(text) => { setScanning(false); accept(text); }} onCancel={() => setScanning(false)} />
      </Suspense>
    );
  }

  if (!link) {
    return (
      <Center>
        <Logo small />
        <h3>Pair this phone</h3>
        <p>On your PC open Levity, tap the phone icon, choose <strong>Pair a new phone</strong>, then scan its QR code here.</p>
        {problem && <p style={{ color: 'var(--destructive)' }}>{problem}</p>}
        <button className="btn primary full" onClick={() => { setProblem(''); setScanning(true); }}>Scan QR code</button>
        {pasting ? (
          <div style={{ marginTop: 12 }}>
            <label className="field">
              <input value={pasted} placeholder="Paste the pairing link" spellCheck={false} autoCapitalize="off" autoCorrect="off"
                style={{ fontFamily: 'var(--mono)', fontSize: 16 }} onChange={(e) => setPasted(e.target.value)} />
            </label>
            <button className="btn ghost full" disabled={!pasted.trim()} onClick={() => accept(pasted)}>Use link</button>
          </div>
        ) : (
          <button className="link-btn" onClick={() => setPasting(true)}>Paste a link instead</button>
        )}
        {isIos() && !isStandalone() && (
          <p style={{ marginTop: 18, fontSize: 12 }}>Tip: tap Share, then Add to Home Screen, and open Levity from there before scanning. That way the pairing is saved in the app.</p>
        )}
      </Center>
    );
  }

  const start = async () => {
    abort.current = new AbortController();
    setPhase('waiting');
    const outcome = await requestPairing(link, name.trim() || 'Phone', code, abort.current.signal);
    if (outcome.ok) {
      history.replaceState(null, '', location.pathname); // the one-time key is no longer needed
      onPaired(outcome.device);
    } else {
      setPhase(outcome.reason);
    }
  };

  return (
    <Center>
      <Logo small />
      <h3>Connect to your PC</h3>
      {phase === 'waiting' ? (
        <>
          <p>Approve this phone on your PC. Make sure the code matches:</p>
          <span className="pair-code">{code}</span>
          <p style={{ marginTop: 14 }}><span className="spinner" /> Waiting for approval…</p>
        </>
      ) : (
        <>
          <p>Your PC will ask you to approve this phone. Nothing is shared until you do.</p>
          <label className="field" style={{ textAlign: 'left' }}>
            <span>Name this phone</span>
            <input value={name} maxLength={40} onChange={(e) => setName(e.target.value)} style={{ fontFamily: 'var(--font)', fontSize: 16 }} />
          </label>
          {phase === 'denied' && <p style={{ color: 'var(--destructive)' }}>The request was denied on your PC.</p>}
          {phase === 'timeout' && <p style={{ color: 'var(--destructive)' }}>The pairing QR expired. Create a new one on your PC.</p>}
          {phase === 'network' && <p style={{ color: 'var(--destructive)' }}>Could not reach the relay. Check your connection and that the QR is still valid.</p>}
          <button className="btn primary full" onClick={start}>Request access</button>
          <button className="link-btn" onClick={() => { abort.current?.abort(); setLink(null); setPhase('idle'); }}>Scan a different code</button>
        </>
      )}
    </Center>
  );
}

export function LockScreen({ onUnlock }: { onUnlock: () => void }) {
  const [pin, setValue] = useState('');
  const [bad, setBad] = useState(false);
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    setBusy(true);
    const ok = await verifyPin(pin);
    setBusy(false);
    if (ok) onUnlock(); else { setBad(true); setValue(''); }
  };

  return (
    <Center>
      <Logo small />
      <h3>Levity is locked</h3>
      <label className="field">
        <input
          type="password" inputMode="numeric" autoFocus value={pin} placeholder="Passcode"
          style={{ fontFamily: 'var(--font)', fontSize: 16, textAlign: 'center' }}
          onChange={(e) => { setValue(e.target.value); setBad(false); }}
          onKeyDown={(e) => e.key === 'Enter' && pin && submit()}
        />
      </label>
      {bad && <p style={{ color: 'var(--destructive)' }}>Wrong passcode.</p>}
      <button className="btn primary full" disabled={!pin || busy} onClick={submit}>Unlock</button>
    </Center>
  );
}

export function SettingsSheet({ device, onClose, onUnpair, confirm, toast, onOpenWindows, push, send }: {
  push?: PushStateMsg | null;
  send?: (m: Record<string, unknown>) => void;
  device: DeviceRecord;
  onClose: () => void;
  onOpenWindows?: () => void;
  onUnpair: () => void;
  confirm: (title: string, text: string, ok: string) => Promise<boolean>;
  toast: (t: string) => void;
}) {
  const [pinSet, setPinSet] = useState(hasPin());
  const [pin, setValue] = useState('');
  const [, bump] = useState(0);
  useEffect(() => onInstallChange(() => bump((n) => n + 1)), []);
  const installed = isStandalone();

  return (
    <section className="sheet open" aria-hidden="false">
      <div className="sheet-head">
        <div><h3>This phone</h3><p className="sheet-sub">{device.name}</p></div>
        <button className="icon-btn" onClick={onClose} title="Close">
          <svg viewBox="0 0 24 24"><line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" /></svg>
        </button>
      </div>
      <div className="sheet-body">
        {onOpenWindows && (
          <>
            <div className="grp-title" style={{ marginTop: 0 }}>This PC</div>
            <button className="settings-row" onClick={onOpenWindows}>
              <span className="settings-ico"><svg viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="13" rx="2" /><line x1="8" y1="21" x2="16" y2="21" /><line x1="12" y1="17" x2="12" y2="21" /></svg></span>
              <span className="settings-text"><strong>Windows tools</strong><small>What the assistant may do on your PC, and the changes it made (with Revert)</small></span>
              <svg className="settings-chev" viewBox="0 0 24 24"><polyline points="9 6 15 12 9 18" /></svg>
            </button>
          </>
        )}
        {send && <NotifyRow state={push ?? null} send={send} toast={toast} />}
        <div className="grp-title" style={{ marginTop: onOpenWindows || send ? 24 : 0 }}>Install</div>
        {installed ? (
          <p className="empty" style={{ paddingTop: 0 }}>Running as an installed app.</p>
        ) : canPromptInstall() ? (
          <button className="btn primary full" onClick={async () => { if (await promptInstall()) toast('Installed'); }}>Install Levity</button>
        ) : (
          <p className="empty" style={{ paddingTop: 0 }}>
            {isIos()
              ? 'Tap the Share button in Safari, then "Add to Home Screen" to use Levity like an app.'
              : 'Open the browser menu and choose "Install app" or "Add to Home screen".'}
          </p>
        )}

        <div className="grp-title">App passcode</div>
        <p className="empty" style={{ paddingTop: 0 }}>Locks Levity when you leave the app for a minute. Your phone's own lock screen is still the main protection.</p>
        {pinSet ? (
          <button className="btn ghost full" onClick={() => { clearPin(); setPinSet(false); toast('Passcode removed'); }}>Remove passcode</button>
        ) : (
          <div className="root-row">
            <input type="password" inputMode="numeric" placeholder="4+ digits" value={pin}
              style={{ fontFamily: 'var(--font)', fontSize: 16 }} onChange={(e) => setValue(e.target.value)} />
            <button className="btn ghost" disabled={pin.length < 4} onClick={async () => { await setPin(pin); setPinSet(true); setValue(''); toast('Passcode set'); }}>Set</button>
          </div>
        )}

        <div className="grp-title" style={{ marginTop: 24 }}>Connection</div>
        <p className="empty" style={{ paddingTop: 0 }}>Relay: <code>{device.relay}</code><br />Device id: <code>{device.deviceId}</code></p>
        <button
          className="btn danger-ghost"
          onClick={async () => {
            if (await confirm('Unpair this phone?', 'The key is deleted from this phone. You will need to scan a new QR code to reconnect.', 'Unpair')) onUnpair();
          }}
        >
          Unpair this phone
        </button>
      </div>
    </section>
  );
}

export function ConfirmDialog({ title, text, ok, onResult }: { title: string; text: string; ok: string; onResult: (v: boolean) => void }) {
  return (
    <div className="overlay">
      <div className="dialog" role="alertdialog" aria-modal="true">
        <h3>{title}</h3>
        <p>{text}</p>
        <div className="dialog-foot">
          <button className="btn ghost" onClick={() => onResult(false)}>Cancel</button>
          <button className="btn primary" autoFocus onClick={() => onResult(true)}>{ok}</button>
        </div>
      </div>
    </div>
  );
}
