import { test } from 'node:test';
import assert from 'node:assert/strict';
import { applyHostMessage, initialChat, type ChatState } from '../src/lib/chat.ts';

type Json = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any
const apply = (s: ChatState, ...msgs: Json[]) => msgs.reduce(applyHostMessage, s);
const user = { type: 'user_message', text: 'change my wallpaper' };
const fl = (o: Json = {}) => ({ type: 'fast_learner', phase: 'running', kind: 'run', detail: 'learning', source: 'announce', ...o });
const text = (t: string) => ({ type: 'agy_event', payload: { event: 'step_update', step_update: { step_type: 'agent_response', state: 'DONE', text_delta: t } } });
const result = { type: 'agy_event', payload: { event: 'result', result: { status: 'SUCCESS' } } };

test('a Fast Learner notice lands in the current reply as a card', () => {
  const s = apply(initialChat, user, fl());
  const learn = s.msgs[1].learn!;
  assert.equal(learn.length, 1);
  assert.deepEqual([learn[0].phase, learn[0].kind, learn[0].detail], ['running', 'run', 'learning']);
});

test('skill, docs and note notices are kept in order', () => {
  const s = apply(initialChat, user, fl(), fl({ phase: 'saving', kind: 'skill', detail: 'a-skill' }), fl({ phase: 'saving', kind: 'docs', detail: 'AGENTS.md' }), fl({ phase: 'saving', kind: 'notes', detail: 'n' }));
  assert.deepEqual(s.msgs[1].learn!.map((l) => l.kind), ['run', 'skill', 'docs', 'notes']);
});

test('"done" settles earlier running notices and adds a final one', () => {
  const s = apply(initialChat, user, fl(), fl({ phase: 'done', detail: 'Saved 1 skill' }));
  assert.deepEqual(s.msgs[1].learn!.map((l) => [l.phase, !!l.final]), [['done', false], ['done', true]]);
});

test('a turn that ends settles notices that never said done', () => {
  const s = apply(initialChat, user, fl(), text('ok'), result);
  assert.equal(s.msgs[1].learn![0].phase, 'done');
});

test('a notice with no reply yet opens a reply row for it', () => {
  const s = apply(initialChat, fl());
  assert.equal(s.msgs.length, 1);
  assert.equal(s.msgs[0].role, 'agent');
  assert.equal(s.msgs[0].learn!.length, 1);
});

test('notices replay from a saved chat in the same order', () => {
  const s = apply(initialChat, { type: 'batch', items: [user, fl({ phase: 'saving', kind: 'notes', detail: 'n' }), result] });
  assert.equal(s.msgs.length, 2);
  assert.equal(s.msgs[1].learn!.length, 1);
});

test('a missing detail becomes an empty string, not "undefined"', () => {
  const s = apply(initialChat, user, { type: 'fast_learner', phase: 'running', kind: 'run' });
  assert.equal(s.msgs[1].learn![0].detail, '');
});
