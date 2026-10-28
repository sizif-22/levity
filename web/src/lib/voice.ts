// Dictation with the phone browser's own speech recognition. No model is bundled; the browser vendor's service hears the audio.

interface RecognitionResult { isFinal: boolean; 0: { transcript: string } }
interface RecognitionEvent { resultIndex: number; results: ArrayLike<RecognitionResult> }
export interface Recognizer {
  lang: string; continuous: boolean; interimResults: boolean;
  onresult: ((e: RecognitionEvent) => void) | null; onend: (() => void) | null; onerror: ((e: { error?: string }) => void) | null;
  start(): void; stop(): void;
}
type RecognizerCtor = new () => Recognizer;

export function recognizerCtor(w: unknown = typeof window === 'undefined' ? undefined : window): RecognizerCtor | null {
  const o = w as { SpeechRecognition?: RecognizerCtor; webkitSpeechRecognition?: RecognizerCtor } | undefined;
  return o?.SpeechRecognition ?? o?.webkitSpeechRecognition ?? null;
}

/** Adds dictated text to what is already typed: one space between, nothing doubled, and nothing added for empty speech. */
export function appendDictation(draft: string, spoken: string): string {
  const add = spoken.trim();
  if (!add) return draft;
  if (!draft.trim()) return add;
  return draft.replace(/\s+$/, '') + ' ' + add;
}

/** The final phrases of a recognition event, in order. Interim guesses are ignored so the box never fills with half-words. */
export function finalText(e: RecognitionEvent): string {
  let out = '';
  for (let i = e.resultIndex; i < e.results.length; i++) if (e.results[i].isFinal) out += e.results[i][0].transcript + ' ';
  return out.trim();
}
