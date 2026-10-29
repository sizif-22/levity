import { useEffect, useState } from 'react';
import { pushHelp, pushSupport, readEnv, subscribeForPush, unsubscribeFromPush } from '../lib/push';

export interface PushStateMsg { type: 'push_state'; publicKey: string; subscribed: boolean }
type Send = (m: Record<string, unknown>) => void;

/** The "Notify me" switch in Settings. The permission prompt only appears from a tap, as phones require. */
export function NotifyRow({ state, send, toast }: { state: PushStateMsg | null; send: Send; toast: (t: string) => void }) {
  const [busy, setBusy] = useState(false);
  useEffect(() => { send({ type: 'push_get' }); }, [send]);
  const env = readEnv();
  const support = pushSupport(env);
  const on = !!state?.subscribed && support === 'ok';

  const toggle = async () => {
    if (!state || busy) return;
    setBusy(true);
    try {
      if (on) {
        await unsubscribeFromPush();
        send({ type: 'push_unsubscribe' });
      } else {
        const r = await subscribeForPush(state.publicKey);
        if ('error' in r) toast(r.error);
        else send({ type: 'push_subscribe', endpoint: r.endpoint });
      }
    } finally { setBusy(false); }
  };

  return (
    <>
      <div className="grp-title">Notifications</div>
      <div className="win-card">
        <strong>Notify me when something finishes</strong>
        <p className="win-muted">{pushHelp(support, env.ios)}</p>
        <div className="diag-actions">
          <button className="btn primary" disabled={support !== 'ok' || !state || busy} onClick={toggle}>
            {!state ? 'Checking...' : on ? 'Turn off' : 'Turn on'}
          </button>
        </div>
      </div>
    </>
  );
}
