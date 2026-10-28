// The /speed sheet in the desktop sidebar.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { open } from './harness.mjs';

const speed = (over = {}) => ({
  type: 'speed',
  current: {
    turns: 5, seconds: 769, inputTokens: 327392, outputTokens: 25158, thinkingTokens: 11274,
    perTurn: [], tools: [{ name: 'run_command', count: 29, seconds: 235.6, max: 71.3 }, { name: 'read_file', count: 5, seconds: 0.6, max: 0.2 }],
    slow: [{ tool: 'run_command', seconds: 71.3, what: 'Get-Process | Where-Object { $_.Name -match "wallpaper" }' }],
    findings: ['29 shell commands took 8.1 seconds each. Add the profile guard.'],
  },
  recent: [{ title: 'download a wallpaper', turns: 5, seconds: 769, inputTokens: 327392 }, { title: 'empty chat', turns: 0, seconds: 0, inputTokens: 0 }],
  footprint: { totalMb: 1450, files: 18413, truncated: false, folders: [{ name: 'antigravity-ide', mb: 900, files: 6464 }], notes: ['antigravity-backup looks like a backup.'] },
  ...over,
});

test('the /speed chip opens the sheet and asks the PC for the report', async () => {
  const ui = await open();
  ui.click(ui.$('speedChip'));
  assert.ok(ui.$('speedSheet').classList.contains('open'));
  assert.equal(ui.sent('speed_get').length, 1);
  assert.match(ui.$('speedSummary').textContent, /Working it out/);
  assert.equal(ui.$('promptInput').value, '', 'it is a view, not a chat message');
});

test('the summary, findings, slow steps, tool times, recent chats and folder sizes are all shown', async () => {
  const ui = await open();
  ui.click(ui.$('speedChip'));
  ui.deliver(speed());
  assert.match(ui.$('speedSummary').textContent, /5 answers · 12 min 49 s · 327k tokens in, 25\.2k out/);
  assert.match(ui.q('#speedFindings li').textContent, /29 shell commands/);
  assert.match(ui.q('#speedSlow li').textContent, /71 s · run_command · Get-Process/);
  assert.equal(ui.qa('#speedTools li').length, 2);
  assert.match(ui.q('#speedTools li').textContent, /run_command: 29 calls, 3 min 56 s in total \(slowest 71 s\)/);
  assert.equal(ui.qa('#speedRecent li').length, 1, 'a conversation with no answers is not listed');
  assert.match(ui.$('speedFootprint').textContent, /1450 MB in 18,413 files/);
  assert.match(ui.q('#speedFolders li').textContent, /antigravity-ide: 900 MB, 6,464 files/);
  assert.match(ui.q('#speedNotes li').textContent, /looks like a backup/);
});

test('with nothing measured yet it says so and hides the empty sections', async () => {
  const ui = await open();
  ui.deliver(speed({ current: { turns: 0, seconds: 0, inputTokens: 0, outputTokens: 0, thinkingTokens: 0, perTurn: [], tools: [], slow: [], findings: [] }, recent: [], footprint: { totalMb: 0, files: 0, truncated: false, folders: [], notes: [] } }));
  assert.match(ui.$('speedSummary').textContent, /Nothing to measure/);
  for (const id of ['speedFindingsWrap', 'speedSlowWrap', 'speedToolsWrap', 'speedRecentWrap']) assert.ok(ui.$(id).hidden, id);
  assert.match(ui.$('speedFootprint').textContent, /No assistant folder/);
});

test('a hostile command in a slow step is shown as text, never as markup', async () => {
  const ui = await open();
  ui.deliver(speed({ current: { ...speed().current, slow: [{ tool: 'run_command', seconds: 12, what: '<img src=x onerror=alert(1)>' }], findings: ['<script>alert(2)</script>'] } }));
  assert.equal(ui.qa('#speedSlow img, #speedFindings script').length, 0);
  assert.equal(ui.q('#speedSlow li').textContent.includes('<img src=x onerror=alert(1)>'), true);
});
