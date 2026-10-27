import { test } from 'node:test';
import assert from 'node:assert/strict';
import { applyHostMessage, initialChat, isGenerating, type ChatState } from '../src/lib/chat.ts';

type Json = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any
const apply = (s: ChatState, ...msgs: Json[]) => msgs.reduce(applyHostMessage, s);
const text = (t: string) => ({ type: 'agy_event', payload: { event: 'step_update', step_update: { step_type: 'agent_response', state: 'DONE', text_delta: t } } });
const result = (r: Json = {}) => ({ type: 'agy_event', payload: { event: 'result', result: { status: 'SUCCESS', ...r } } });

test('a user message adds the user row and an empty reply row', () => {
  const s = apply(initialChat, { type: 'user_message', text: 'hi', source: 'mobile' });
  assert.deepEqual(s.msgs.map((m) => [m.role, m.text, m.done]), [['user', 'hi', true], ['agent', '', false]]);
  assert.equal(s.msgs[0].source, 'mobile');
});

test('clear_chat empties the conversation and goes back to Ready', () => {
  const s = apply(initialChat, { type: 'user_message', text: 'hi' }, { type: 'clear_chat' });
  assert.equal(s.msgs.length, 0);
  assert.equal(s.status.text, 'Ready');
});

test('config sets the working folder; a config without one keeps the old folder', () => {
  const s = apply(initialChat, { type: 'config', cwd: 'D:\\Work\\app' });
  assert.equal(s.cwd, 'D:\\Work\\app');
  assert.equal(apply(s, { type: 'config', isPinned: true }).cwd, 'D:\\Work\\app');
});

test('a batch applies its items in order (the replay a phone gets when it connects or opens a saved chat)', () => {
  const s = apply(initialChat, { type: 'batch', items: [{ type: 'user_message', text: 'one' }, text('reply one'), result()] });
  assert.deepEqual(s.msgs.map((m) => m.text), ['one', 'reply one']);
  assert.equal(s.msgs[1].done, true);
});

test('a failed turn keeps its error message', () => {
  const s = apply(initialChat, { type: 'user_message', text: 'x' }, text('partial'), result({ status: 'ERROR', error: 'quota exceeded' }));
  assert.equal(s.msgs[1].error, 'quota exceeded');
  assert.equal(s.msgs[1].done, true);
});

test('a failed turn without a message still says it ended with an error', () => {
  const s = apply(initialChat, { type: 'user_message', text: 'x' }, text('partial'), result({ status: 'ERROR' }));
  assert.match(String(s.msgs[1].error), /ended with an error/);
});

test('a stopped turn (status CANCELLED) is finished and not shown as an error', () => {
  const s = apply(initialChat, { type: 'user_message', text: 'x' }, text('_Stopped._'), result({ status: 'CANCELLED' }));
  assert.equal(s.msgs[1].done, true);
  assert.equal(s.msgs[1].error, undefined);
});

test('when the engine goes idle, an empty unfinished reply row is dropped', () => {
  const busy = apply(initialChat, { type: 'user_message', text: 'x' }, { type: 'status', text: 'Thinking...', state: 'busy' });
  assert.equal(busy.msgs.length, 2);
  const idle = apply(busy, { type: 'status', text: 'Ready', state: 'online' });
  assert.deepEqual(idle.msgs.map((m) => m.role), ['user']);
});

test('isGenerating follows the status text and state', () => {
  const at = (text: string, state: string) => apply(initialChat, { type: 'status', text, state });
  assert.equal(isGenerating(at('Thinking...', 'online')), true);
  assert.equal(isGenerating(at('Responding...', 'online')), true);
  assert.equal(isGenerating(at('Running command...', 'busy')), true);
  assert.equal(isGenerating(at('Ready', 'online')), false);
});

test('unknown host messages are ignored', () => {
  assert.equal(apply(initialChat, { type: 'something_new' }), initialChat);
});
