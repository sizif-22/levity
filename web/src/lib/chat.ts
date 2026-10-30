/** Chat state driven by the PC's message protocol (same events the desktop sidebar renders). */

export interface ToolCall { key: number; name: string; args: string; running: boolean }

/** A Fast Learner notice: it is running, saving a skill, updating docs, taking a note, or finished. */
export interface Learn {
  id: number;
  phase: 'running' | 'saving' | 'done';
  kind: 'run' | 'skill' | 'docs' | 'notes';
  detail: string;
  /** announce = the agent said so; path = a skills folder was written to. */
  source?: string;
  /** The closing "done" notice. */
  final?: boolean;
}

export interface Msg {
  id: number;
  role: 'user' | 'agent';
  text: string;
  images?: string[];
  thinking: string;
  tools: ToolCall[];
  learn?: Learn[];
  done: boolean;
  stats?: string;
  error?: string;
  source?: string;
  /** The last text segment ended (agent_response DONE); a new segment starts on a new paragraph. */
  segDone?: boolean;
}

export interface ChatState {
  msgs: Msg[];
  nextId: number;
  status: { text: string; state: 'online' | 'busy' | 'error' | 'offline' };
  cwd: string;
}

export const initialChat: ChatState = { msgs: [], nextId: 1, status: { text: 'Connecting…', state: 'offline' }, cwd: '' };

type Json = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any

const isBusy = (text: string, state?: string) => state === 'busy' || /thinking|respond/i.test(text);
export const isGenerating = (s: ChatState) => isBusy(s.status.text, s.status.state);

function openAgent(s: ChatState): [ChatState, Msg] {
  const last = s.msgs[s.msgs.length - 1];
  if (last && last.role === 'agent' && !last.done) return [s, last];
  const msg: Msg = { id: s.nextId, role: 'agent', text: '', thinking: '', tools: [], done: false };
  return [{ ...s, nextId: s.nextId + 1, msgs: [...s.msgs, msg] }, msg];
}

function patchLast(s: ChatState, patch: Partial<Msg>): ChatState {
  const msgs = s.msgs.slice();
  msgs[msgs.length - 1] = { ...msgs[msgs.length - 1], ...patch };
  return { ...s, msgs };
}

/** One-line summary of a tool call's parameters for the activity pill. */
function toolArgs(su: Json): string {
  const p: Json = su.tool_info?.parameters ?? su.args ?? su.parameters ?? {};
  const pick = typeof p === 'string' ? p
    : p.CommandLine ?? p.command ?? p.AbsolutePath ?? p.path ?? p.Query ?? p.query ?? p.Url ?? p.url
      ?? Object.values(p).find((v) => typeof v === 'string') ?? '';
  const text = String(pick).replace(/\s+/g, ' ').trim();
  return text.length > 70 ? text.slice(0, 67) + '…' : text;
}

function agyEvent(s: ChatState, ev: Json): ChatState {
  if (ev.event === 'init') {
    return { ...s, cwd: ev.init?.cwd ?? s.cwd, status: { text: 'Ready', state: 'online' } };
  }

  if (ev.event === 'step_update') {
    const su: Json = ev.step_update ?? {};
    const type = su.step_type;

    // One reply row per turn: text, thinking and tool activity all land in it.
    if (type === 'agent_response') {
      const delta: string = su.text_delta ?? '';
      if (!delta) {
        // Steps report an empty agent_response between tools: remember the segment ended, never create a row.
        const last = s.msgs[s.msgs.length - 1];
        return su.state === 'DONE' && last && last.role === 'agent' && !last.done ? patchLast(s, { segDone: true }) : s;
      }
      const [s1, cur] = openAgent(s);
      const sep = cur.segDone && cur.text ? '\n\n' : '';
      return patchLast(s1, { text: cur.text + sep + delta, segDone: su.state === 'DONE' });
    }

    if (type === 'thinking' || su.thought || su.thinking_delta) {
      const chunk = su.thought || su.thinking_delta || su.text_delta || '';
      if (!chunk) return s;
      const [s1, cur] = openAgent(s);
      return patchLast(s1, { thinking: cur.thinking + chunk });
    }

    if (type === 'tool' || type === 'tool_call' || type === 'tool_use') {
      const [s1, cur] = openAgent(s);
      const key = Number(su.step_index ?? cur.tools.length);
      const running = su.state === 'ACTIVE' || su.state === 'RUNNING';
      const call: ToolCall = { key, name: su.tool_name || su.name || su.tool_info?.name || 'tool', args: toolArgs(su), running };
      const i = cur.tools.findIndex((t) => t.key === key);
      const tools = i >= 0
        ? cur.tools.map((t, j) => (j === i ? { ...t, running, args: call.args || t.args } : t))
        : [...cur.tools, call];
      return patchLast(s1, { tools });
    }

    return s;
  }

  if (ev.event === 'result') {
    const res: Json = ev.result ?? {};
    const [s1, cur] = openAgent(s);
    const parts: string[] = [];
    if (res.duration_seconds) parts.push(`${Number(res.duration_seconds).toFixed(1)}s`);
    if (res.usage?.total_tokens) parts.push(`${Number(res.usage.total_tokens).toLocaleString()} tokens`);
    const next = patchLast(s1, {
      done: true,
      tools: cur.tools.map((t) => ({ ...t, running: false })),
      learn: cur.learn?.map((l) => (l.phase === 'running' ? { ...l, phase: 'done' as const } : l)),
      stats: parts.length ? parts.join(' · ') : cur.stats,
      error: res.status === 'ERROR' ? String(res.error || 'This turn ended with an error.') : cur.error,
    });
    return { ...next, status: { text: 'Ready', state: 'online' } };
  }

  return s;
}

/** Only the latest rows stay in the phone's memory; the PC keeps the whole conversation. */
export const MAX_ROWS = 300;

export function applyHostMessage(s: ChatState, msg: Json): ChatState {
  const next = applyHost(s, msg);
  return next.msgs.length > MAX_ROWS ? { ...next, msgs: next.msgs.slice(next.msgs.length - MAX_ROWS) } : next;
}

function applyHost(s: ChatState, msg: Json): ChatState {
  switch (msg.type) {
    case 'batch':
      return (msg.items as Json[]).reduce<ChatState>((acc, item) => applyHostMessage(acc, item), s);

    case 'user_message': {
      const user: Msg = { id: s.nextId, role: 'user', text: String(msg.text ?? ''), images: Array.isArray(msg.images) ? (msg.images as string[]) : undefined, thinking: '', tools: [], done: true, source: msg.source };
      const agent: Msg = { id: s.nextId + 1, role: 'agent', text: '', thinking: '', tools: [], done: false };
      return { ...s, nextId: s.nextId + 2, msgs: [...s.msgs, user, agent] };
    }

    case 'fast_learner': {
      const [s1, cur] = openAgent(s);
      const before = (cur.learn ?? []).map((l) => (msg.phase === 'done' && l.phase === 'running' ? { ...l, phase: 'done' as const } : l));
      const entry: Learn = { id: before.length + 1, phase: msg.phase, kind: msg.kind, detail: String(msg.detail ?? ''), source: msg.source, final: msg.phase === 'done' };
      return patchLast(s1, { learn: [...before, entry] });
    }

    case 'agy_event':
      return agyEvent(s, msg.payload ?? {});

    case 'clear_chat':
      return { ...s, msgs: [], status: { text: 'Ready', state: 'online' } };

    case 'config':
      return msg.cwd ? { ...s, cwd: String(msg.cwd) } : s;

    case 'status': {
      let next: ChatState = { ...s, status: { text: String(msg.text ?? ''), state: msg.state ?? 'online' } };
      // Engine went idle with an empty reply row: drop it so the typing dots do not hang around.
      const last = next.msgs[next.msgs.length - 1];
      if (!isBusy(next.status.text, next.status.state) && last && last.role === 'agent' && !last.done && !last.text && !last.thinking && !last.tools.length) {
        next = { ...next, msgs: next.msgs.slice(0, -1) };
      }
      return next;
    }

    default:
      return s;
  }
}
