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
