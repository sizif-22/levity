// The /run and /git buttons, and answering questions from a running command, in the desktop sidebar.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { open } from './harness.mjs';

const actions = (over = {}) => ({
  type: 'actions', path: 'C:\\Work\\app', packageManager: 'bun',
  scripts: [{ name: 'dev', command: 'vite' }, { name: 'build', command: 'tsc && vite build' }, { name: 'test:unit', command: 'vitest' }],
  git: { repo: true, branch: 'main' }, ...over,
});

const job = (over = {}) => ({ id: 3, command: 'bun convex deploy', cwd: 'C:\\Work', state: 'running', exitCode: null, started: new Date().toISOString(), ended: null, interactive: true, tail: '? Project name', ...over });

test('the /run and /git chips stay hidden until the folder has scripts or a repository', async () => {
  const ui = await open();
  assert.ok(ui.$('runChip').hidden);
  assert.ok(ui.$('gitChip').hidden);

  ui.deliver(actions());
  assert.ok(!ui.$('runChip').hidden);
  assert.ok(!ui.$('gitChip').hidden);

  ui.deliver(actions({ scripts: [], git: { repo: false, branch: null } }));
  assert.ok(ui.$('runChip').hidden);
  assert.ok(ui.$('gitChip').hidden);
});

test('/run lists the scripts and sends only the script name when one is run', async () => {
  const ui = await open();
  ui.deliver(actions());
  ui.click(ui.$('runChip'));
  assert.ok(ui.$('runSheet').classList.contains('open'));
  assert.match(ui.$('runSub').textContent, /bun · 3 scripts/);
  const rows = ui.qa('#runList .action-row');
  assert.deepEqual(rows.map((r) => r.querySelector('strong').textContent), ['dev', 'build', 'test:unit']);
  assert.equal(rows[1].querySelector('code').textContent, 'tsc && vite build');

  ui.click(rows[2].querySelector('.action-go'));
  assert.deepEqual(ui.sent('run_script'), [{ type: 'run_script', name: 'test:unit', interactive: false }]);
  assert.ok(!ui.$('runSheet').classList.contains('open'), 'the dialog closes so the result is visible in the chat');
});

test('the interactive box on /run is passed along', async () => {
  const ui = await open();
  ui.deliver(actions());
  ui.click(ui.$('runChip'));
  ui.$('runInteractive').checked = true;
  ui.click(ui.qa('#runList .action-go')[0]);
  assert.equal(ui.sent('run_script')[0].interactive, true);
});

test('script names and commands are shown as text, never as markup', async () => {
  const ui = await open();
  ui.deliver(actions({ scripts: [{ name: '<img src=x onerror=alert(1)>', command: '<script>alert(2)</script>' }] }));
  ui.click(ui.$('runChip'));
  assert.equal(ui.qa('#runList img, #runList script').length, 0);
  assert.equal(ui.q('#runList strong').textContent, '<img src=x onerror=alert(1)>');
});

test('/git asks the PC for the repository state and shows branch, changes and what is waiting', async () => {
  const ui = await open();
  ui.deliver(actions());
  ui.click(ui.$('gitChip'));
  assert.ok(ui.$('gitSheet').classList.contains('open'));
  assert.equal(ui.sent('git_state').length, 1);

  ui.deliver({ type: 'git_state', path: 'C:\\Work\\app', ok: true, branch: 'feature/login', upstream: 'origin/feature/login', ahead: 2, behind: 1, changes: 4 });
  assert.equal(ui.$('gitSub').textContent, 'feature/login · 4 changes · 2 to push · 1 to pull');
  assert.ok(ui.$('gitStage').checked, 'with changes, staging first is the default');

  ui.deliver({ type: 'git_state', path: 'C:\\Work\\app', ok: true, branch: 'main', upstream: 'origin/main', ahead: 0, behind: 0, changes: 0 });
  assert.equal(ui.$('gitSub').textContent, 'main · clean');
  ui.deliver({ type: 'git_state', ok: false });
  assert.match(ui.$('gitSub').textContent, /Could not read/);
});

test('each simple git button sends its action name and closes the dialog', async () => {
  const ui = await open();
  ui.deliver(actions());
  for (const action of ['status', 'add', 'push', 'pull', 'fetch', 'log', 'diff', 'branches', 'stash', 'stash-pop']) {
    ui.click(ui.$('gitChip'));
    ui.click(ui.q(`#gitButtons [data-git="${action}"]`));
    assert.deepEqual(ui.sent('git_run').at(-1), { type: 'git_run', action });
    assert.ok(!ui.$('gitSheet').classList.contains('open'));
  }
});

test('Commit asks for a message first, and sends it with the stage choice', async () => {
  const ui = await open();
  ui.deliver(actions());
  ui.click(ui.$('gitChip'));
  ui.click(ui.$('gitCommitOpen'));
  assert.ok(!ui.$('gitCommitForm').hidden);
  assert.ok(ui.$('gitButtons').hidden);
  assert.ok(ui.$('gitCommitBtn').disabled, 'nothing to commit without a message');

  ui.click(ui.$('gitCommitBtn'));
  assert.equal(ui.sent('git_run').length, 0);

  ui.$('gitMessage').value = 'fix: login redirect loop';
  ui.$('gitMessage').dispatchEvent(new ui.window.Event('input', { bubbles: true }));
  assert.ok(!ui.$('gitCommitBtn').disabled);
  ui.$('gitStage').checked = true;
  ui.click(ui.$('gitCommitBtn'));
  assert.deepEqual(ui.sent('git_run'), [{ type: 'git_run', action: 'commit', message: 'fix: login redirect loop', stage: true }]);
  assert.ok(!ui.$('gitSheet').classList.contains('open'));
});

test('a message of only spaces cannot be committed, and Back returns to the buttons', async () => {
  const ui = await open();
  ui.deliver(actions());
  ui.click(ui.$('gitChip'));
  ui.click(ui.$('gitCommitOpen'));
  ui.$('gitMessage').value = '   ';
  ui.$('gitMessage').dispatchEvent(new ui.window.Event('input', { bubbles: true }));
  assert.ok(ui.$('gitCommitBtn').disabled);
  ui.click(ui.$('gitCommitCancel'));
  assert.ok(ui.$('gitCommitForm').hidden);
  assert.ok(!ui.$('gitButtons').hidden);
});

test('New branch sends the name', async () => {
  const ui = await open();
  ui.deliver(actions());
  ui.click(ui.$('gitChip'));
  ui.click(ui.$('gitBranchOpen'));
  ui.$('gitBranchName').value = 'feature/login';
  ui.click(ui.$('gitBranchBtn'));
  assert.deepEqual(ui.sent('git_run'), [{ type: 'git_run', action: 'switch-new', name: 'feature/login' }]);
});

test('reopening the git dialog starts fresh, and it closes itself if the folder is no longer a repository', async () => {
  const ui = await open();
  ui.deliver(actions());
  ui.click(ui.$('gitChip'));
  ui.click(ui.$('gitCommitOpen'));
  ui.$('gitMessage').value = 'old';
  ui.window.closeSheet('gitSheet');
  ui.click(ui.$('gitChip'));
  assert.equal(ui.$('gitMessage').value, '');
  assert.ok(ui.$('gitCommitForm').hidden);

  ui.deliver(actions({ git: { repo: false, branch: null } }));
  assert.ok(!ui.$('gitSheet').classList.contains('open'));
});

// ---- answering questions from a running command ----

test('a running interactive job shows an answer box and key buttons; a finished or ordinary one does not', async () => {
  const ui = await open();
  ui.deliver({ type: 'jobs', jobs: [job(), job({ id: 4, interactive: false, command: 'bun run dev' }), job({ id: 5, state: 'done', exitCode: 0 })] });
  const ask = (id) => ui.q(`#jobList [data-id="${id}"] .job-ask`);
  assert.ok(!ask(3).hidden);
  assert.ok(ask(4).hidden);
  assert.ok(ask(5).hidden);

  ui.deliver({ type: 'jobs', jobs: [job({ state: 'done', exitCode: 0 })] });
  assert.ok(ask(3).hidden, 'once it has finished nothing can be typed');
});

test('typing an answer sends it to that job, and Enter in the box sends it too', async () => {
  const ui = await open();
  ui.deliver({ type: 'jobs', jobs: [job()] });
  const box = ui.q('#jobList [data-id="3"] .job-input');
  box.value = 'my-app';
  ui.click(ui.q('#jobList [data-id="3"] .job-send'));
  assert.deepEqual(ui.sent('job_input'), [{ type: 'job_input', id: 3, text: 'my-app', enter: true }]);
  assert.equal(box.value, '', 'the box is cleared after sending');

  box.value = 'second';
  box.dispatchEvent(new ui.window.KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
  assert.deepEqual(ui.sent('job_input').at(-1), { type: 'job_input', id: 3, text: 'second', enter: true });
});

test('the arrow, Space, Enter and Ctrl+C buttons press those keys in that job', async () => {
  const ui = await open();
  ui.deliver({ type: 'jobs', jobs: [job()] });
  for (const key of ['up', 'down', 'left', 'right', 'space', 'tab', 'esc', 'enter', 'ctrl-c']) {
    ui.click(ui.q(`#jobList [data-id="3"] .key-btn[data-key="${key}"]`));
    assert.deepEqual(ui.sent('job_key').at(-1), { type: 'job_key', id: 3, key });
  }
});

test('an update to the output keeps what the user was typing', async () => {
  const ui = await open();
  ui.deliver({ type: 'jobs', jobs: [job()] });
  const box = ui.q('#jobList [data-id="3"] .job-input');
  box.value = 'half-typed';
  ui.deliver({ type: 'jobs', jobs: [job({ tail: '? Project name\n> typing' })] });
  assert.equal(ui.q('#jobList [data-id="3"] .job-input').value, 'half-typed');
  assert.match(ui.q('#jobList [data-id="3"] .job-out').textContent, /typing/);
});

test('when the PC says a command is waiting for an answer the Jobs panel opens', async () => {
  const ui = await open();
  ui.deliver({ type: 'jobs', jobs: [job()] });
  ui.deliver({ type: 'jobs_open', id: 3 });
  assert.ok(ui.$('jobsSheet').classList.contains('open'));
});
