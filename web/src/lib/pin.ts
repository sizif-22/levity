/**
 * Optional app passcode. It only guards this app on an unlocked phone (the key itself is already
 * non-extractable); the phone's own lock screen remains the real protection.
 */
import { b64u } from './crypto';

const KEY = 'levity.pin';
const ITERATIONS = 210_000;
const enc = new TextEncoder();

async function derive(pin: string, salt: Uint8Array): Promise<string> {
  const base = await crypto.subtle.importKey('raw', enc.encode(pin), 'PBKDF2', false, ['deriveBits']);
  const bits = await crypto.subtle.deriveBits(
    { name: 'PBKDF2', hash: 'SHA-256', salt: salt as BufferSource, iterations: ITERATIONS },
    base,
    256,
  );
  return b64u.enc(new Uint8Array(bits));
}

export const hasPin = (): boolean => !!localStorage.getItem(KEY);

export async function setPin(pin: string): Promise<void> {
  const salt = crypto.getRandomValues(new Uint8Array(16));
  localStorage.setItem(KEY, JSON.stringify({ salt: b64u.enc(salt), hash: await derive(pin, salt) }));
}

export async function verifyPin(pin: string): Promise<boolean> {
  const raw = localStorage.getItem(KEY);
  if (!raw) return true;
  try {
    const { salt, hash } = JSON.parse(raw) as { salt: string; hash: string };
    return (await derive(pin, b64u.dec(salt))) === hash;
  } catch {
    return false;
  }
}

export const clearPin = (): void => localStorage.removeItem(KEY);
