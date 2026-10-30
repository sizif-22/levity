import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { pushHelp, pushSupport, urlBase64ToUint8Array } from '../src/lib/push';
import { NotifyRow } from '../src/components/NotifyRow';

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

const env = (o: Record<string, unknown> = {}) => ({ hasServiceWorker: true, hasPushManager: true, hasNotification: true, permission: 'default', ios: false, standalone: true, ...o }) as Parameters<typeof pushSupport>[0];

describe('push support', () => {
  it('an iPhone outside the installed app needs installing first', () => {
    expect(pushSupport(env({ ios: true, standalone: false }))).toBe('needs-install');
    expect(pushSupport(env({ ios: true, standalone: true }))).toBe('ok');
  });
  it('missing browser features and a denied permission are told apart', () => {
    expect(pushSupport(env({ hasPushManager: false }))).toBe('unsupported');
    expect(pushSupport(env({ permission: 'denied' }))).toBe('denied');
    expect(pushSupport(env({ permission: 'granted' }))).toBe('ok');
  });
  it('each problem says what to do', () => {
    expect(pushHelp('needs-install', true)).toMatch(/Add to Home Screen/);
    expect(pushHelp('denied', true)).toMatch(/iPhone Settings/);
    expect(pushHelp('denied', false)).toMatch(/App info/);
    expect(pushHelp('ok', false)).toMatch(/never contains your text/);
  });
  it('decodes the PC key from base64url', () => {
    expect(Array.from(urlBase64ToUint8Array('AQID'))).toEqual([1, 2, 3]);
    expect(Array.from(urlBase64ToUint8Array('-_8'))).toEqual([251, 255]);
  });
});

describe('NotifyRow', () => {
  const installFakes = (permission: 'granted' | 'denied') => {
    const sub = {
      endpoint: 'https://fcm.googleapis.com/fcm/send/abc',
      toJSON: () => ({ keys: { p256dh: 'test-p256dh', auth: 'test-auth' } }),
      unsubscribe: vi.fn(async () => true),
    };
    const reg = { pushManager: { getSubscription: vi.fn(async () => null), subscribe: vi.fn(async () => sub) } };
    Object.defineProperty(navigator, 'serviceWorker', { value: { ready: Promise.resolve(reg) }, configurable: true });
    (window as unknown as Record<string, unknown>).PushManager = class {};
    (window as unknown as Record<string, unknown>).Notification = Object.assign(class {}, { permission: 'default', requestPermission: vi.fn(async () => permission) });
    return reg;
  };

  it('asks the PC for its state when it opens and waits for the answer', () => {
    installFakes('granted');
    const send = vi.fn();
    render(<NotifyRow state={null} send={send} toast={() => {}} />);
    expect(send).toHaveBeenCalledWith({ type: 'push_get' });
    expect(screen.getByRole('button', { name: 'Checking...' })).toHaveProperty('disabled', true);
  });

  it('turning on subscribes with the PC key and gives the address only to the PC', async () => {
    const reg = installFakes('granted');
    const send = vi.fn();
    render(<NotifyRow state={{ type: 'push_state', publicKey: 'AQID', subscribed: false }} send={send} toast={() => {}} />);
    fireEvent.click(screen.getByRole('button', { name: 'Turn on' }));
    await waitFor(() => expect(send).toHaveBeenCalledWith({
      type: 'push_subscribe',
      endpoint: 'https://fcm.googleapis.com/fcm/send/abc',
      p256dh: 'test-p256dh',
      auth: 'test-auth',
    }));
    const opts = reg.pushManager.subscribe.mock.calls[0][0] as { userVisibleOnly: boolean };
    expect(opts.userVisibleOnly).toBe(true);
  });

  it('a refused permission shows a message and tells the PC nothing', async () => {
    installFakes('denied');
    const send = vi.fn();
    const toast = vi.fn();
    render(<NotifyRow state={{ type: 'push_state', publicKey: 'AQID', subscribed: false }} send={send} toast={toast} />);
    fireEvent.click(screen.getByRole('button', { name: 'Turn on' }));
    await waitFor(() => expect(toast).toHaveBeenCalledWith('Notifications were not allowed.'));
    expect(send).not.toHaveBeenCalledWith(expect.objectContaining({ type: 'push_subscribe' }));
  });

  it('turning off removes the phone subscription and tells the PC', async () => {
    installFakes('granted');
    const send = vi.fn();
    render(<NotifyRow state={{ type: 'push_state', publicKey: 'AQID', subscribed: true }} send={send} toast={() => {}} />);
    fireEvent.click(screen.getByRole('button', { name: 'Turn off' }));
    await waitFor(() => expect(send).toHaveBeenCalledWith({ type: 'push_unsubscribe' }));
  });
});
