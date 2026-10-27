import { beforeEach, describe, expect, it } from 'vitest';
import { clearPin, hasPin, setPin, verifyPin } from '../src/lib/pin';

beforeEach(() => localStorage.clear());

describe('app passcode', () => {
  it('starts with no passcode', () => {
    expect(hasPin()).toBe(false);
  });

  it('accepts the right passcode and rejects the wrong one', async () => {
    await setPin('1234');
    expect(hasPin()).toBe(true);
    expect(await verifyPin('1234')).toBe(true);
    expect(await verifyPin('1235')).toBe(false);
    expect(await verifyPin('')).toBe(false);
  });

  it('never stores the passcode itself', async () => {
    await setPin('987654');
    const stored = JSON.stringify(Object.fromEntries(Object.entries(localStorage)));
    expect(stored).not.toContain('987654');
  });

  it('uses a random salt, so the same passcode stores differently each time', async () => {
    await setPin('1234');
    const first = JSON.stringify(Object.fromEntries(Object.entries(localStorage)));
    localStorage.clear();
    await setPin('1234');
    expect(JSON.stringify(Object.fromEntries(Object.entries(localStorage)))).not.toBe(first);
  });

  it('clearPin removes it', async () => {
    await setPin('1234');
    clearPin();
    expect(hasPin()).toBe(false);
  });
});
