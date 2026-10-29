import { describe, expect, it } from 'vitest';
import { appendDictation, finalText, recognizerCtor } from '../src/lib/voice';

describe('voice helpers', () => {
  it('appends with one space and ignores empty speech', () => {
    expect(appendDictation('', ' open the logs ')).toBe('open the logs');
    expect(appendDictation('check disk  ', 'then restart')).toBe('check disk then restart');
    expect(appendDictation('keep this', '   ')).toBe('keep this');
  });

  it('takes only final phrases from the new results', () => {
    const r = (t: string, f: boolean) => ({ isFinal: f, 0: { transcript: t } });
    expect(finalText({ resultIndex: 1, results: [r('old', true), r('one', true), r('half wo', false), r('two', true)] })).toBe('one two');
    expect(finalText({ resultIndex: 0, results: [r('maybe', false)] })).toBe('');
  });

  it('finds the standard or the webkit recognizer, or none', () => {
    class A {}
    expect(recognizerCtor({ SpeechRecognition: A })).toBe(A);
    expect(recognizerCtor({ webkitSpeechRecognition: A })).toBe(A);
    expect(recognizerCtor({})).toBeNull();
  });
});

import { isIosStandalone, micDeniedHelp } from '../src/lib/voice';
describe('installed-app microphone', () => {
  it('spots an iPhone home-screen app, but not Safari in a tab or Android', () => {
    const iphone = 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)';
    expect(isIosStandalone({ userAgent: iphone, standalone: true }, false)).toBe(true);
    expect(isIosStandalone({ userAgent: iphone }, true)).toBe(true);
    expect(isIosStandalone({ userAgent: iphone, standalone: false }, false)).toBe(false);
    expect(isIosStandalone({ userAgent: 'Mozilla/5.0 (Linux; Android 14)' }, true)).toBe(false);
    expect(isIosStandalone({ userAgent: 'Macintosh', platform: 'MacIntel', maxTouchPoints: 5, standalone: true }, false)).toBe(true);
  });
  it('tells the person what to do, per situation', () => {
    expect(micDeniedHelp(true, true)).toMatch(/keyboard/);
    expect(micDeniedHelp(true, false)).toMatch(/App info/);
    expect(micDeniedHelp(false, false)).toMatch(/lock icon/);
  });
});
