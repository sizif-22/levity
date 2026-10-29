import { afterEach, describe, expect, it, vi } from 'vitest';
import { listenForPushChime } from '../src/lib/chime';

afterEach(() => { vi.restoreAllMocks(); });

describe('push chime', () => {
  it('listens for the service worker message and stops listening when asked', () => {
    const add = vi.fn(); const remove = vi.fn();
    Object.defineProperty(navigator, 'serviceWorker', { value: { addEventListener: add, removeEventListener: remove }, configurable: true });
    const stop = listenForPushChime();
    expect(add).toHaveBeenCalledWith('message', expect.any(Function));
    stop();
    expect(remove).toHaveBeenCalledWith('message', add.mock.calls[0][1]);
  });

  it('does nothing where there is no service worker', () => {
    Object.defineProperty(navigator, 'serviceWorker', { value: undefined, configurable: true });
    delete (navigator as unknown as Record<string, unknown>).serviceWorker;
    expect(() => listenForPushChime()()).not.toThrow();
  });
});
