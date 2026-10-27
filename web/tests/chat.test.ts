import { test } from 'node:test';
import assert from 'node:assert/strict';
import { applyHostMessage, initialChat } from '../src/lib/chat.ts';

type Json = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any
const ev = (payload: Json) => ({ type: 'agy_event', payload });
const step = (o: Json) => ev({ event: 'step_update', step_update: o });

// Sequence taken from a real agy turn: empty agent_response between tool steps, text only at the end.
function realTurn() {
  const msgs: Json[] = [{ type: 'user_message', text: 'open this project in the IDE', source: 'mobile' }, step({ step_type: 'user_input', state: 'DONE' })];
  for (const [i, tool, args] of [[3, 'read_file', { AbsolutePath: 'D:\Projects\app\README.md' }], [14, 'run_command', { CommandLine: 'Get-Command *antigravity*' }], [18, 'run_command', { CommandLine: '& "antigravity.cmd" "D:\Projects\app"' }]] as const) {
    msgs.push(step({ step_type: 'agent_response', state: 'DONE' }));
    msgs.push(step({ step_type: 'tool', state: 'ACTIVE', tool_name: tool, step_index: i }));
    msgs.push(step({ step_type: 'tool', state: 'DONE', tool_name: tool, step_index: i, tool_info: { name: tool, parameters: args } }));
  }
  msgs.push(step({ step_type: 'agent_response', state: 'DONE', text_delta: 'I have opened the project **grow**.\n' }));
  msgs.push(ev({ event: 'result', result: { status: 'OK', duration_seconds: 32.1, usage: { total_tokens: 7100 } } }));
  return msgs;
}

test('one reply row per turn, no empty rows', () => {
  const s = realTurn().reduce(applyHostMessage, initialChat);
  const agents = s.msgs.filter((m) => m.role === 'agent');
  assert.equal(agents.length, 1);
  assert.equal(s.msgs.length, 2);
});

test('tool steps become one pill each, updated in place', () => {
  const s = realTurn().reduce(applyHostMessage, initialChat);
  const tools = s.msgs[1].tools;
  assert.equal(tools.length, 3);
  assert.deepEqual(tools.map((t) => t.name), ['read_file', 'run_command', 'run_command']);
  assert.ok(tools.every((t) => !t.running));
  assert.match(tools[2].args, /antigravity\.cmd/);
});

test('a tool shows as running until its DONE event', () => {
  const msgs = realTurn();
  const midway = msgs.slice(0, 4).reduce(applyHostMessage, initialChat);
  assert.equal(midway.msgs[1].tools[0].running, true);
  assert.equal(midway.msgs[1].done, false);
});

test('final text and stats land in the same row', () => {
  const s = realTurn().reduce(applyHostMessage, initialChat);
  assert.match(s.msgs[1].text, /opened the project \*\*grow\*\*/);
  assert.equal(s.msgs[1].stats, '32.1s · 7,100 tokens');
  assert.equal(s.msgs[1].done, true);
});

test('separate text segments are split into paragraphs', () => {
  let s = applyHostMessage(initialChat, { type: 'user_message', text: 'x' });
  s = applyHostMessage(s, step({ step_type: 'agent_response', state: 'DONE', text_delta: 'First.' }));
  s = applyHostMessage(s, step({ step_type: 'agent_response', state: 'DONE', text_delta: 'Second.' }));
  assert.equal(s.msgs[1].text, 'First.\n\nSecond.');
});
