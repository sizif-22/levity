// The /windows sheet: the assistant's Windows tools switch, this PC's summary and the list of changes with Revert.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { open } from './harness.mjs';

const state = (over = {}) => ({ type: 'tools_state', enabled: false, running: false, count: 34, canElevate: false, elevateNote: 'Administrator changes are only offered when Levity is installed in Program Files.', ...over });
const entry = (over = {}) => ({ id: 'abc123', time: '2026-10-28T12:00:00Z', tool: 'settings_set', summary: 'Set the wallpaper to C:\\Pics\\a.jpg', reverted: false, canUndo: true, ...over });

test('the /windows chip opens the sheet and asks the PC for the state, the changes and the machine summary', async () => {
  const ui = await open();
  ui.click(ui.$('windowsChip'));
  assert.ok(ui.$('windowsSheet').classList.contains('open'));
  assert.equal(ui.sent('tools_get').length, 1);
  assert.equal(ui.sent('journal_list').length, 1);
  assert.equal(ui.sent('machine_get').length, 1);
});

test('off: the switch says Turn on, and sends the request without enabling anything by itself', async () => {
  const ui = await open();
  ui.click(ui.$('windowsChip'));
  ui.deliver(state());
  assert.equal(ui.$('winToggle').textContent, 'Turn on');
  assert.match(ui.$('winStateText').textContent, /^Off\./);
  assert.ok(!ui.$('winToggle').disabled);

  ui.click(ui.$('winToggle'));
  assert.deepEqual(ui.sent('tools_set'), [{ type: 'tools_set', on: true }]);
  assert.ok(ui.$('winToggle').disabled, 'disabled until the PC answers');
  ui.deliver(state({ enabled: true, running: true }));
  assert.ok(!ui.$('winToggle').disabled);
});

test('on: it says how many tools, offers Turn off, and shows the administrator note', async () => {
  const ui = await open();
  ui.click(ui.$('windowsChip'));
  ui.deliver(state({ enabled: true, running: true }));
  assert.equal(ui.$('winToggle').textContent, 'Turn off');
  assert.match(ui.$('winStateText').textContent, /34 tools/);
  assert.ok(!ui.$('winElevateNote').hidden);
  assert.match(ui.$('winElevateNote').textContent, /Program Files/);

  ui.click(ui.$('winToggle'));
  assert.deepEqual(ui.sent('tools_set').at(-1), { type: 'tools_set', on: false });
});

test('on but not running is described honestly', async () => {
  const ui = await open();
  ui.deliver(state({ enabled: true, running: false }));
  assert.match(ui.$('winStateText').textContent, /not running/);
});

test('the machine summary is shown, refreshing asks again, and it is shown as text', async () => {
  const ui = await open();
  ui.click(ui.$('windowsChip'));
  ui.deliver({ type: 'machine', summary: 'This PC: Windows 11 Pro <img src=x onerror=alert(1)>', built: '2026-10-28T10:00:00Z' });
  assert.equal(ui.$('winMachine').textContent, 'This PC: Windows 11 Pro <img src=x onerror=alert(1)>');
  assert.equal(ui.qa('#winMachine img').length, 0);
  ui.click(ui.$('winMachineRefresh'));
  assert.deepEqual(ui.sent('machine_get').at(-1), { type: 'machine_get', refresh: true });
});

test('changes are listed with Revert only where a revert is possible', async () => {
  const ui = await open();
  ui.click(ui.$('windowsChip'));
  ui.deliver({ type: 'journal', entries: [
    entry({ id: 'a1' }),
    entry({ id: 'b2', summary: 'End notepad (id 42)', canUndo: false }),
    entry({ id: 'c3', summary: 'Disable start-up entry Chat', reverted: true }),
  ] });
  const rows = ui.qa('#winChanges .action-row');
  assert.equal(rows.length, 3);
  assert.ok(rows[0].querySelector('button'));
  assert.equal(rows[1].querySelector('button'), null, 'a change that cannot be undone has no button');
  assert.equal(rows[2].querySelector('button'), null, 'a reverted change has no button');
  assert.ok(rows[2].classList.contains('reverted'));
  assert.match(rows[2].textContent, /reverted/);
  assert.ok(ui.$('winChangesEmpty').hidden);

  ui.click(rows[0].querySelector('button'));
  assert.deepEqual(ui.sent('journal_revert'), [{ type: 'journal_revert', id: 'a1' }]);
});

test('an empty journal says nothing has changed', async () => {
  const ui = await open();
  ui.deliver({ type: 'journal', entries: [] });
  assert.ok(!ui.$('winChangesEmpty').hidden);
  assert.equal(ui.qa('#winChanges .action-row').length, 0);
});

test('a hostile change summary is shown as text and never as markup', async () => {
  const ui = await open();
  ui.deliver({ type: 'journal', entries: [entry({ summary: '<script>alert(1)</script><b>bold</b>' })] });
  assert.equal(ui.qa('#winChanges script, #winChanges b').length, 0);
  assert.equal(ui.q('#winChanges strong').textContent, '<script>alert(1)</script><b>bold</b>');
});

test('/windows is a view, not a chat message: the generic chip handler leaves it alone', async () => {
  const ui = await open();
  ui.click(ui.$('windowsChip'));
  assert.equal(ui.$('promptInput').value, '');
});
