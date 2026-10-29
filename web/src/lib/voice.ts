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

/** An iPhone or iPad running the installed (home-screen) app. There the microphone prompt for speech recognition never appears and cannot be granted later. */
export function isIosStandalone(nav: { userAgent?: string; platform?: string; maxTouchPoints?: number; standalone?: boolean } = typeof navigator === 'undefined' ? {} : navigator,
  displayStandalone: boolean = typeof window !== 'undefined' && !!window.matchMedia?.('(display-mode: standalone)').matches): boolean {
  const ios = /iPhone|iPad|iPod/.test(nav.userAgent ?? '') || (nav.platform === 'MacIntel' && (nav.maxTouchPoints ?? 0) > 1);
  return ios && (nav.standalone === true || displayStandalone);
}

/** What to tell the person when the browser refuses the microphone. */
export function micDeniedHelp(standalone: boolean, ios: boolean): string {
  if (ios) return 'This phone does not allow the microphone in an installed app. Tap the microphone on your keyboard to dictate instead.';
  if (standalone) return 'Microphone is blocked. Long-press the Levity icon > App info > Permissions > Microphone > Allow, or use the microphone on your keyboard.';
  return 'Microphone is blocked. Tap the lock icon next to the address, allow the microphone, or use the microphone on your keyboard.';
}
