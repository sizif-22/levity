// The undo bar after an answer that changed files in a git folder.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { open } from './harness.mjs';

const ckpt = (over = {}) => ({
  type: 'checkpoint', none: false, id: '20261028-153012-ab12cd', root: 'C:\\Work\\app', added: 1, modified: 2, deleted: 1, insertions: 12, deletions: 4,
  changes: [{ status: 'M', path: 'src\\a.ts' }, { status: 'M', path: 'src\\b.ts' }, { status: 'A', path: 'new.txt' }, { status: 'D', path: 'old.txt' }], more: 0, ...over,
});

test('no bar until an answer has changed something', async () => {
  const ui = await open();
  assert.ok(ui.$('checkpointBar').hidden);
});

test('after an answer it says what changed and counts files and lines', async () => {
  const ui = await open();
  ui.deliver(ckpt());
  assert.ok(!ui.$('checkpointBar').hidden);
  assert.equal(ui.$('checkpointText').textContent, 'That answer changed 4 files (2 changed, 1 new, 1 deleted; +12 −4)');
});

test('one file reads as singular', async () => {
  const ui = await open();
  ui.deliver(ckpt({ added: 0, modified: 1, deleted: 0, insertions: 1, deletions: 0, changes: [{ status: 'M', path: 'a.ts' }] }));
  assert.match(ui.$('checkpointText').textContent, /^That answer changed 1 file \(1 changed; \+1 −0\)$/);
});

test('View shows the files and hides them again', async () => {
  const ui = await open();
  ui.deliver(ckpt());
  assert.ok(ui.$('checkpointFiles').hidden);
  ui.click(ui.$('checkpointView'));
  assert.ok(!ui.$('checkpointFiles').hidden);
  assert.deepEqual(ui.qa('#checkpointFiles li').map((e) => e.textContent), ['changed  src\\a.ts', 'changed  src\\b.ts', 'new  new.txt', 'deleted  old.txt']);
  ui.click(ui.$('checkpointView'));
  assert.ok(ui.$('checkpointFiles').hidden);
});

test('a long list says how many more there are', async () => {
  const ui = await open();
  ui.deliver(ckpt({ more: 37 }));
  ui.click(ui.$('checkpointView'));
  assert.equal(ui.qa('#checkpointFiles li').at(-1).textContent, '... and 37 more');
});

test('Undo needs a second press, then asks the PC to undo that checkpoint', async () => {
  const ui = await open();
  ui.deliver(ckpt());
  ui.click(ui.$('checkpointUndo'));
  assert.equal(ui.$('checkpointUndo').textContent, 'Sure?');
  assert.equal(ui.sent('checkpoint_undo').length, 0, 'one press must not undo anything');
  ui.click(ui.$('checkpointUndo'));
  assert.deepEqual(ui.sent('checkpoint_undo'), [{ type: 'checkpoint_undo', id: '20261028-153012-ab12cd' }]);
  assert.equal(ui.$('checkpointUndo').textContent, 'Undo');
});

test('a new answer or an empty summary removes the bar, and Dismiss hides it', async () => {
  const ui = await open();
  ui.deliver(ckpt());
  ui.deliver({ type: 'checkpoint', none: true });
  assert.ok(ui.$('checkpointBar').hidden);
  ui.deliver(ckpt());
  ui.click(ui.$('checkpointDismiss'));
  assert.ok(ui.$('checkpointBar').hidden);
});

test('file names are shown as text, never as markup', async () => {
  const ui = await open();
  ui.deliver(ckpt({ changes: [{ status: 'A', path: '<img src=x onerror=alert(1)>.txt' }] }));
  ui.click(ui.$('checkpointView'));
  assert.equal(ui.qa('#checkpointFiles img').length, 0);
  assert.match(ui.q('#checkpointFiles li').textContent, /<img src=x onerror=alert\(1\)>\.txt/);
});
