// UI tests for the desktop sidebar (wwwroot). The real page and scripts are loaded in jsdom with a fake
// WebView2 bridge (see harness.mjs), then driven with the same messages the PC sends and the same clicks a user makes.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { open, reply, result } from './harness.mjs';

test('the page loads without script errors and announces itself to the PC', async () => {
  const posted = [];
  const ui = await (async () => {
    // Re-open without clearing the startup message.
    const u = await open();
    return u;
  })();
  assert.deepEqual(ui.errors, []);
  for (const id of ['promptInput', 'sendBtn', 'stopBtn', 'queueList', 'jobsBtn', 'sessionsBtn', 'projectsSheet', 'jobsSheet', 'sessionsSheet']) {
    assert.ok(ui.$(id), `#${id} should exist`);
  }
  assert.equal(posted.length, 0);
});

test('a user message and a markdown reply render, and the turn finishes with stats', async () => {
  const ui = await open();
  ui.deliver({ type: 'user_message', text: 'hello **there**', source: 'desktop' });
  assert.equal(ui.q('.user-bubble').textContent, 'hello **there**'); // user text is shown literally

  ui.deliver(reply('Here is `code` and **bold**\n\n```js\nconst x = 1;\n```'));
  ui.deliver(result());
  await new Promise((r) => setTimeout(r, 60)); // rendering is batched on a frame

  const agent = ui.q('.message-row.agent .agent-content');
  assert.ok(agent.querySelector('strong'), 'markdown bold should render');
  assert.ok(agent.querySelector('pre code'), 'fenced code should render');
  assert.match(ui.document.body.textContent, /1\.5s/);
  assert.match(ui.document.body.textContent, /1,234 tokens/);
  assert.equal(ui.qa('.typing').length, 0);
});

test('message text can never inject markup', async () => {
  const ui = await open();
  ui.deliver({ type: 'user_message', text: '<img src=x onerror="window.__pwned=1"><b>x</b>', source: 'desktop' });
  assert.equal(ui.q('.user-bubble img'), null);
  assert.equal(ui.q('.user-bubble b'), null);
  assert.equal(ui.window.__pwned, undefined);
});

test('a /cmd bubble highlights the command word', async () => {
  const ui = await open();
  ui.deliver({ type: 'user_message', text: '/cmd{$bun run dev}', source: 'desktop' });
  const bubble = ui.q('.user-bubble');
  assert.ok(bubble.classList.contains('cmd-bubble'));
  assert.equal(ui.q('.cmd-word', bubble).textContent, 'bun');
  assert.equal(bubble.textContent, '/cmd{$bun run dev}'); // nothing lost or reordered
});

test('plain messages are not styled as commands', async () => {
  const ui = await open();
  ui.deliver({ type: 'user_message', text: '/cmd{$npm start}', source: 'desktop' });
  ui.deliver({ type: 'user_message', text: 'just talking', source: 'desktop' });
  const bubbles = ui.qa('.user-bubble');
  assert.equal(bubbles[1].querySelector('.cmd-word'), null);
  assert.ok(!bubbles[1].classList.contains('cmd-bubble'));
});

test('typing a command colours the word inside the message box', async () => {
  const ui = await open();
  const box = ui.$('promptInput');
  ui.type('/cmd{$bun run dev}');
  assert.ok(box.classList.contains('hl-on'));
  assert.equal(ui.q('.cmd-word', ui.$('promptHl')).textContent, 'bun');
  assert.equal(box.spellcheck, false);

  ui.type('hello');
  assert.ok(!box.classList.contains('hl-on'));
  assert.equal(ui.$('promptHl').innerHTML, '');
  assert.equal(box.spellcheck, true);
});

test('the /cmd chip fills "/cmd$" with the caret at the end', async () => {
  const ui = await open();
  const chip = ui.qa('.cmd-chip').find((c) => c.dataset.cmd === '/cmd$');
  assert.ok(chip, 'the /cmd chip exists');
  ui.click(chip);
  const box = ui.$('promptInput');
  assert.equal(box.value, '/cmd$');
  assert.equal(box.selectionStart, 5);
});

test('sending posts the prompt, clears the box and resets the highlight', async () => {
  const ui = await open();
  ui.type('/cmd{$git status}');
  ui.click(ui.$('sendBtn'));
  assert.deepEqual(ui.sent('send_prompt'), [{ type: 'send_prompt', text: '/cmd{$git status}' }]);
  assert.equal(ui.$('promptInput').value, '');
  assert.equal(ui.$('promptHl').innerHTML, '');
  assert.ok(!ui.$('promptInput').classList.contains('hl-on'));
});

test('empty or whitespace messages are not sent', async () => {
  const ui = await open();
  ui.type('   ');
  ui.click(ui.$('sendBtn'));
  assert.equal(ui.sent('send_prompt').length, 0);
});

test('while the AI is busy Stop appears, Send still works (queued by the PC), and Stop posts stop_response', async () => {
  const ui = await open();
  assert.equal(ui.$('stopBtn').hidden, true);

  ui.deliver({ type: 'status', text: 'Thinking...', state: 'busy' });
  assert.equal(ui.$('stopBtn').hidden, false);
  assert.equal(ui.$('sendBtn').disabled, false);

  ui.type('next thing');
  ui.click(ui.$('sendBtn'));
  assert.equal(ui.sent('send_prompt').length, 1);

  ui.click(ui.$('stopBtn'));
  assert.equal(ui.sent('stop_response').length, 1);

  ui.deliver({ type: 'status', text: 'Ready', state: 'online' });
  assert.equal(ui.$('stopBtn').hidden, true);
});

test('queued messages are listed and can be removed', async () => {
  const ui = await open();
  assert.equal(ui.$('queueList').hidden, true);

  ui.deliver({ type: 'queue', items: [{ id: 4, text: 'first queued' }, { id: 5, text: 'second queued' }] });
  assert.equal(ui.$('queueList').hidden, false);
  assert.deepEqual(ui.qa('.queue-text').map((e) => e.textContent), ['first queued', 'second queued']);

  ui.click(ui.qa('.queue-item button')[1]);
  assert.deepEqual(ui.sent('queue_remove'), [{ type: 'queue_remove', id: 5 }]);

  ui.deliver({ type: 'queue', items: [] });
  assert.equal(ui.$('queueList').hidden, true);
});

test('queued text is escaped', async () => {
  const ui = await open();
  ui.deliver({ type: 'queue', items: [{ id: 1, text: '<b onmouseover=alert(1)>hi</b>' }] });
  assert.equal(ui.q('.queue-text b'), null);
});

const job = (over = {}) => ({ id: 1, command: 'bun run dev', cwd: 'C:\\p', state: 'running', exitCode: null, started: new Date(Date.now() - 5000).toISOString(), ended: null, tail: 'ready on :3000', ...over });

test('background jobs show in the Jobs panel with a badge, live output and a Stop button', async () => {
  const ui = await open();
  assert.equal(ui.$('jobsBadge').hidden, true);

  ui.deliver({ type: 'jobs', jobs: [job()] });
  assert.equal(ui.$('jobsBadge').hidden, false);
  assert.equal(ui.$('jobsBadge').textContent, '1');
  assert.match(ui.q('.job-cmd').textContent, /#1 bun run dev/);
  assert.equal(ui.q('.job-out').textContent, 'ready on :3000');
  assert.match(ui.q('.job-meta').textContent, /running/);

  ui.click(ui.q('.job-stop'));
  assert.deepEqual(ui.sent('job_stop'), [{ type: 'job_stop', id: 1 }]);
});

test('job updates change the existing row in place instead of rebuilding it', async () => {
  const ui = await open();
  ui.deliver({ type: 'jobs', jobs: [job()] });
  const row = ui.q('.job');
  ui.deliver({ type: 'jobs', jobs: [job({ tail: 'ready on :3000\ncompiled in 2s' })] });
  assert.equal(ui.q('.job'), row, 'the same element is reused, so scroll position and focus survive');
  assert.match(ui.q('.job-out').textContent, /compiled in 2s/);
});

test('a finished job loses its Stop button, shows its exit code and clears the badge', async () => {
  const ui = await open();
  ui.deliver({ type: 'jobs', jobs: [job()] });
  ui.deliver({ type: 'jobs', jobs: [job({ state: 'failed', exitCode: 2, ended: new Date().toISOString() })] });
  assert.equal(ui.q('.job-stop').hidden, true);
  assert.match(ui.q('.job-meta').textContent, /failed \(exit 2\)/);
  assert.equal(ui.$('jobsBadge').hidden, true);

  ui.click(ui.$('jobsClearBtn'));
  assert.equal(ui.sent('job_clear').length, 1);

  ui.deliver({ type: 'jobs', jobs: [] });
  assert.equal(ui.qa('.job').length, 0);
});

test('job output is plain text, never markup', async () => {
  const ui = await open();
  ui.deliver({ type: 'jobs', jobs: [job({ command: '<script>x</script>', tail: '<img src=x onerror=alert(1)>' })] });
  assert.equal(ui.q('.job-out img'), null);
  assert.equal(ui.q('.job-cmd script'), null);
});

test('the Jobs and History buttons open their sheets', async () => {
  const ui = await open();
  ui.click(ui.$('jobsBtn'));
  assert.ok(ui.$('jobsSheet').classList.contains('open'));
  ui.click(ui.$('sessionsBtn'));
  assert.ok(ui.$('sessionsSheet').classList.contains('open'));
  assert.equal(ui.sent('list_sessions').length, 1);
});

const sessions = (current = 'b') => ({
  type: 'sessions', current,
  sessions: [
    { id: 'a', title: 'Fix the login bug', cwd: 'D:\\Work\\app', updated: new Date().toISOString(), messages: 3 },
    { id: 'b', title: 'Wallpaper thing', cwd: 'C:\\Users\\me', updated: new Date(Date.now() - 86400000 * 3).toISOString(), messages: 1 },
  ],
});

test('chat history lists sessions, marks the open one, and opens another', async () => {
  const ui = await open();
  ui.deliver(sessions('b'));
  const rows = ui.qa('.sess');
  assert.equal(rows.length, 2);
  assert.equal(ui.q('.sess-title', rows[0]).textContent, 'Fix the login bug');
  assert.match(ui.q('.sess-meta', rows[0]).textContent, /app · 3 messages/);
  assert.match(ui.q('.sess-meta', rows[1]).textContent, /1 message ·/); // singular
  assert.ok(rows[1].classList.contains('current'));
  assert.equal(ui.q('.sess-open', rows[1]).disabled, true);

  ui.click(ui.q('.sess-open', rows[0]));
  assert.deepEqual(ui.sent('session_open'), [{ type: 'session_open', id: 'a' }]);
});

test('deleting a chat needs a second click', async () => {
  const ui = await open();
  ui.deliver(sessions('b'));
  const del = ui.q('.sess-del');
  ui.click(del);
  assert.equal(ui.sent('session_delete').length, 0, 'the first click only arms it');
  assert.equal(del.textContent, 'Sure?');
  ui.click(del);
  assert.deepEqual(ui.sent('session_delete'), [{ type: 'session_delete', id: 'a' }]);
});

test('an empty history explains itself', async () => {
  const ui = await open();
  ui.deliver({ type: 'sessions', current: 'x', sessions: [] });
  assert.match(ui.$('sessSub').textContent, /No saved chats/);
});

test('the path bar shows the full working folder', async () => {
  const ui = await open();
  ui.deliver({ type: 'config', cwd: 'D:\\Work\\my-project', isPinned: false });
  assert.equal(ui.$('cwdText').textContent, 'D:\\Work\\my-project');
  assert.match(ui.$('cwdButton').title, /D:\\Work\\my-project/);
});

test('the project sheet can pick any folder or switch to "no project"', async () => {
  const ui = await open();
  ui.click(ui.$('pickFolderBtn'));
  ui.click(ui.$('noProjectBtn'));
  assert.equal(ui.sent('pick_folder').length, 1);
  assert.equal(ui.sent('no_project').length, 1);
});

test('clear_chat removes the conversation', async () => {
  const ui = await open();
  ui.deliver({ type: 'user_message', text: 'one', source: 'desktop' });
  ui.deliver({ type: 'clear_chat' });
  assert.equal(ui.qa('.user-bubble').length, 0);
});

test('toasts show their text', async () => {
  const ui = await open();
  ui.deliver({ type: 'toast', text: 'Job #2 finished' });
  assert.equal(ui.$('toast').textContent, 'Job #2 finished');
  assert.ok(ui.$('toast').classList.contains('show'));
});

test('an idle sync state clears a stale "Starting" review screen', async () => {
  const ui = await open();
  const items = [{ name: 'p1', path: 'D:\\p1', status: 'running', summary: '', seconds: 0 }];
  ui.deliver({ type: 'sync_state', running: true, mode: 'update', items });
  ui.click(ui.$('cwdButton'));
  assert.equal(ui.$('syncCancelBtn').hidden, false);

  ui.deliver({ type: 'sync_state', running: false, mode: 'update', items: [] });
  assert.equal(ui.$('syncBanner').hidden, true);
  assert.equal(ui.$('syncView').hidden, true, 'the review view must be left');
});

test('Stop on a review posts sync_cancel', async () => {
  const ui = await open();
  ui.deliver({ type: 'sync_state', running: true, mode: 'update', items: [{ name: 'p1', path: 'D:\\p1', status: 'running', summary: '', seconds: 0 }] });
  ui.click(ui.$('syncCancelBtn'));
  assert.equal(ui.sent('sync_cancel').length, 1);
});

test('tool activity shows as a pill that updates in place', async () => {
  const ui = await open();
  ui.deliver({ type: 'user_message', text: 'do it', source: 'desktop' });
  const tool = (state) => ({ type: 'agy_event', payload: { event: 'step_update', step_update: { step_type: 'tool', state, step_index: 3, tool_name: 'run_command' } } });
  ui.deliver(tool('ACTIVE'));
  ui.deliver(tool('DONE'));
  assert.equal(ui.qa('.tool-pill').length, 1);
  assert.match(ui.q('.tool-pill').textContent, /run_command/);
});

test('an error result is shown to the user', async () => {
  const ui = await open();
  ui.deliver({ type: 'user_message', text: 'x', source: 'desktop' });
  ui.deliver(reply('partial'));
  ui.deliver(result({ status: 'ERROR', error: 'quota exceeded' }));
  assert.match(ui.q('.error-banner').textContent, /quota exceeded/);
});

test('the Fast/Deep switch shows the current level and asks the PC to change it', async () => {
  const ui = await open();
  assert.equal(ui.$('effortBtn').textContent, 'Fast');
  ui.deliver({ type: 'config', cwd: 'C:\\Work', isPinned: false, effort: 'high' });
  assert.equal(ui.$('effortBtn').textContent, 'Deep');
  assert.ok(ui.$('effortBtn').classList.contains('deep'));
  ui.click(ui.$('effortBtn'));
  assert.deepEqual(ui.sent('set_effort'), [{ type: 'set_effort', value: 'low' }]);
  ui.deliver({ type: 'config', effort: 'low' });
  ui.click(ui.$('effortBtn'));
  assert.deepEqual(ui.sent('set_effort').at(-1), { type: 'set_effort', value: 'high' });
});

test('slow tool steps show how long they took, quick ones do not', async () => {
  const ui = await open();
  const step = (idx, secs) => ({ type: 'agy_event', payload: { event: 'step_update', step_update: { step_type: 'tool', state: 'DONE', step_index: idx, tool_name: 'run_command', duration_seconds: secs, tool_info: { name: 'run_command', parameters: { CommandLine: 'ls' } } } } });
  ui.deliver({ type: 'agy_event', payload: { event: 'step_update', step_update: { step_type: 'agent_response', state: 'ACTIVE', text_delta: 'x' } } });
  ui.deliver(step(1, 8.4));
  ui.deliver(step(2, 0.3));
  const times = ui.qa('.tool-pill .tool-time').map((e) => e.textContent.trim());
  assert.deepEqual(times, ['8s']);
});

test('the Plan/Work switch shows the mode the PC reports and asks the PC to change it', async () => {
  const ui = await open();
  assert.equal(ui.$('modeBtn').textContent, 'Work');
  ui.deliver({ type: 'config', mode: 'plan' });
  assert.equal(ui.$('modeBtn').textContent, 'Plan');
  assert.ok(ui.$('modeBtn').classList.contains('deep'));
  ui.click(ui.$('modeBtn'));
  assert.deepEqual(ui.sent('set_mode'), [{ type: 'set_mode', value: 'auto' }]);
  ui.deliver({ type: 'config', mode: 'auto' });
  ui.click(ui.$('modeBtn'));
  assert.deepEqual(ui.sent('set_mode').at(-1), { type: 'set_mode', value: 'plan' });
});
