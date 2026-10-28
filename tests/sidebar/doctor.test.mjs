// The PC Doctor sheet in the desktop sidebar.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { open } from './harness.mjs';

const report = (over = {}) => ({
  type: 'doctor', time: '2026-10-28T10:00:00Z', skipped: [],
  findings: [
    { id: 'disk-C:', severity: 'critical', title: 'Drive C: is almost full', detail: '4 GB free of 500 GB (1%).', fixes: [{ index: 0, kind: 'tool', label: 'Find what is using it', changes: false }, { index: 1, kind: 'prompt', label: 'Help me free space', changes: false }] },
    { id: 'path', severity: 'info', title: 'PATH has problems', detail: '2 entries point to folders that do not exist.', fixes: [{ index: 0, kind: 'tool', label: 'Clean the user PATH', changes: true }] },
  ],
  ...over,
});

test('the /doctor chip opens the sheet and starts a scan, and does not type into the chat box', async () => {
  const ui = await open();
  ui.click(ui.$('doctorChip'));
  assert.ok(ui.$('doctorSheet').classList.contains('open'));
  assert.equal(ui.sent('doctor_scan').length, 1);
  assert.equal(ui.$('doctorScan').disabled, true);
  assert.equal(ui.$('promptInput').value, '');
});

test('findings show with their severity, text and fix buttons, and the scan button comes back', async () => {
  const ui = await open();
  ui.click(ui.$('doctorChip'));
  ui.deliver(report());
  const cards = ui.qa('#doctorList .win-card');
  assert.equal(cards.length, 2);
  assert.match(cards[0].textContent, /Fix now/);
  assert.match(cards[0].textContent, /Drive C: is almost full/);
  assert.deepEqual(Array.from(cards[0].querySelectorAll('button')).map((b) => b.textContent), ['Find what is using it', 'Help me free space']);
  assert.equal(ui.$('doctorScan').disabled, false);
  assert.equal(ui.$('doctorScan').textContent, 'Scan again');
});

test('a fix asks the PC for exactly that fix, by finding and index', async () => {
  const ui = await open();
  ui.deliver(report());
  ui.click(ui.qa('#doctorList .win-card')[0].querySelectorAll('button')[1]);
  assert.deepEqual(ui.sent('doctor_fix'), [{ type: 'doctor_fix', id: 'disk-C:', index: 1 }]);
});

test('a fix that changes Windows needs a second press', async () => {
  const ui = await open();
  ui.deliver(report());
  const btn = ui.qa('#doctorList .win-card')[1].querySelector('button');
  ui.click(btn);
  assert.equal(btn.textContent, 'Sure?');
  assert.equal(ui.sent('doctor_fix').length, 0);
  ui.click(btn);
  assert.deepEqual(ui.sent('doctor_fix'), [{ type: 'doctor_fix', id: 'path', index: 0 }]);
  assert.equal(btn.textContent, 'Clean the user PATH');
});

test('a tool answer appears under its finding as text, never as markup', async () => {
  const ui = await open();
  ui.deliver(report());
  ui.deliver({ type: 'doctor_result', id: 'disk-C:', index: 0, ok: true, text: '<img src=x onerror=alert(1)> Users 120 GB' });
  const pre = ui.q('#doctorList [data-id="disk-C:"] pre');
  assert.match(pre.textContent, /<img src=x/);
  assert.equal(ui.qa('#doctorList img').length, 0);
});

test('titles and details from the scan are shown as text, and skipped checks are named', async () => {
  const ui = await open();
  ui.deliver(report({ skipped: ['event log'], findings: [{ id: 'x', severity: 'warning', title: '<b>bold</b>', detail: '<img src=x>', fixes: [] }] }));
  assert.equal(ui.qa('#doctorList b, #doctorList img').length, 0);
  assert.match(ui.$('doctorSkipped').textContent, /event log/);
  assert.ok(!ui.$('doctorSkipped').hidden);
});
