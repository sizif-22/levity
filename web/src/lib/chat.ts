/** Chat state driven by the PC's message protocol (same events the desktop sidebar renders). */

export interface ToolCall { name: string; args: string }

export interface Msg {
  id: number;
  role: 'user' | 'agent';
  text: string;
  thinking: string;
  tools: ToolCall[];
  done: boolean;
  stats?: string;
  error?: string;
  source?: string;
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

function agyEvent(s: ChatState, ev: Json): ChatState {
  if (ev.event === 'init') {
    return { ...s, cwd: ev.init?.cwd ?? s.cwd, status: { text: 'Ready', state: 'online' } };
  }

  if (ev.event === 'step_update') {
    const su: Json = ev.step_update ?? {};
    const [s1, cur] = openAgent(s);

    if (su.step_type === 'agent_response') {
      const next = patchLast(s1, { text: cur.text + (su.text_delta ?? ''), done: su.state === 'DONE' ? true : cur.done });
      return next;
    }
    if (su.step_type === 'thinking' || su.thought || su.thinking_delta) {
      return patchLast(s1, { thinking: cur.thinking + (su.thought || su.thinking_delta || su.text_delta || '') });
    }
    if (su.step_type === 'tool_call' || su.step_type === 'tool_use') {
      let args = '';
      try {
        const a = su.args ?? su.parameters ?? '';
        args = typeof a === 'string' ? a : JSON.stringify(a);
        if (args.length > 60) args = args.slice(0, 57) + '…';
      } catch { /* ignore */ }
      return patchLast(s1, { tools: [...cur.tools, { name: su.tool_name || su.name || 'tool', args }] });
    }
    return s;
  }

  if (ev.event === 'result') {
    const res: Json = ev.result ?? {};
    let [s1] = openAgent(s);
    const last = s1.msgs[s1.msgs.length - 1];
    const parts: string[] = [];
    if (res.duration_seconds) parts.push(`${Number(res.duration_seconds).toFixed(1)}s`);
    if (res.usage?.total_tokens) parts.push(`${Number(res.usage.total_tokens).toLocaleString()} tokens`);
    s1 = patchLast(s1, {
      done: true,
      stats: parts.length ? parts.join(' · ') : last.stats,
      error: res.status === 'ERROR' ? String(res.error || 'This turn ended with an error.') : last.error,
    });
    return { ...s1, status: { text: 'Ready', state: 'online' } };
  }

  return s;
}

export function applyHostMessage(s: ChatState, msg: Json): ChatState {
  switch (msg.type) {
    case 'batch':
      return (msg.items as Json[]).reduce<ChatState>((acc, item) => applyHostMessage(acc, item), s);

    case 'user_message': {
      const user: Msg = { id: s.nextId, role: 'user', text: String(msg.text ?? ''), thinking: '', tools: [], done: true, source: msg.source };
      const agent: Msg = { id: s.nextId + 1, role: 'agent', text: '', thinking: '', tools: [], done: false };
      return { ...s, nextId: s.nextId + 2, msgs: [...s.msgs, user, agent] };
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
