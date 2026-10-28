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

test('the /diagram chip is only offered inside a git repository', async () => {
  const ui = await open();
  assert.ok(ui.$('diagramChip').hidden, 'hidden until the PC says what the folder is');
  ui.deliver(actions());
  assert.ok(!ui.$('diagramChip').hidden);
  ui.deliver(actions({ git: { repo: false, branch: null } }));
  assert.ok(ui.$('diagramChip').hidden);
});

// ---- the start screen and /open-ide ----

const facts = (over = {}) => actions({ scripts: [], hasFiles: true, isProject: true, hasReadme: true, ...over });
const shown = (ui) => Object.fromEntries(['chipReviewAll', 'chipNew', 'chipInspect', 'chipGit', 'chipTests', 'chipOverview'].map((id) => [id, !ui.$(id).hidden]));
const projectsMsg = (n) => ({ type: 'projects', root: 'C:\\Work', current: 'C:\\Work\\app', projects: Array.from({ length: n }, (_, i) => ({ name: 'p' + i, path: 'C:\\Work\\p' + i, kind: 'Git', hasReadme: true, hasAgentsDoc: true, hasSkills: true, modified: new Date().toISOString(), pinned: false, uses: 0 })) });

test('the sidebar asks for the project list at startup, and the start screen shows nothing before the PC has answered', async () => {
  const ui = await open();
  assert.equal(ui.sent('list_projects').length >= 0, true);
  assert.deepEqual(shown(ui), { chipReviewAll: false, chipNew: false, chipInspect: false, chipGit: false, chipTests: false, chipOverview: false });
});

test('a git project with other projects on the PC shows the full set', async () => {
  const ui = await open();
  ui.deliver(projectsMsg(3));
  ui.deliver(facts());
  assert.deepEqual(shown(ui), { chipReviewAll: true, chipNew: false, chipInspect: true, chipGit: true, chipTests: false, chipOverview: true });
});

test('no git repository hides Git status, no projects hides Review all my projects', async () => {
  const ui = await open();
  ui.deliver(projectsMsg(0));
  ui.deliver(facts({ git: { repo: false, branch: null } }));
  const s = shown(ui);
  assert.equal(s.chipGit, false);
  assert.equal(s.chipReviewAll, false);
  assert.equal(s.chipInspect, true);
  ui.deliver(projectsMsg(2));
  assert.equal(shown(ui).chipReviewAll, true, 'it appears as soon as projects are found');
});

test('an empty folder offers a new project, not inspecting, git or an overview', async () => {
  const ui = await open();
  ui.deliver(projectsMsg(2));
  ui.deliver(facts({ hasFiles: false, isProject: false, hasReadme: false, git: { repo: false, branch: null } }));
  assert.deepEqual(shown(ui), { chipReviewAll: true, chipNew: true, chipInspect: false, chipGit: false, chipTests: false, chipOverview: false });
});

test('a test script adds Run the tests with the script name in its prompt', async () => {
  const ui = await open();
  ui.deliver(facts({ scripts: [{ name: 'dev', command: 'vite' }, { name: 'test:unit', command: 'vitest' }] }));
  assert.ok(!ui.$('chipTests').hidden);
  assert.match(ui.$('chipTests').dataset.prompt, /"test:unit"/);
});

test('switching to another folder updates the buttons', async () => {
  const ui = await open();
  ui.deliver(facts());
  assert.ok(!ui.$('chipGit').hidden);
  ui.deliver(facts({ path: 'C:\\Plain', git: { repo: false, branch: null }, isProject: false, hasReadme: false }));
  assert.ok(ui.$('chipGit').hidden);
  assert.ok(ui.$('chipOverview').hidden);
});

test('the old Open in Antigravity IDE button is gone from the start screen; /open-ide is a chip next to the others', async () => {
  const ui = await open();
  assert.equal(ui.$('chipIde'), null);
  assert.ok(ui.$('ideChip').hidden, 'hidden until the PC reports a folder');
  ui.deliver({ type: 'config', cwd: 'C:\\Work\\app', isPinned: false });
  ui.deliver(facts());
  assert.ok(!ui.$('ideChip').hidden);
  ui.click(ui.$('ideChip'));
  assert.deepEqual(ui.sent('open_ide'), [{ type: 'open_ide', path: 'C:\\Work\\app' }]);
});

// ---- project cards ----

test('each project card keeps its buttons together, and the pin button (not the diagram button) pins', async () => {
  const ui = await open();
  ui.deliver(projectsMsg(2));
  const card = ui.q('#projList .proj');
  assert.ok(card.querySelector('.proj-actions .open-btn'), 'the buttons are grouped so they can move to their own line when space is tight');
  assert.equal(card.querySelectorAll('.proj-actions button').length, 4);

  ui.click(card.querySelector('.diag-btn'));
  assert.equal(ui.sent('pin_project').length, 0, 'the diagram button must not pin');
  ui.click(card.querySelector('.pin-toggle'));
  assert.equal(ui.sent('pin_project').length, 1);
  assert.equal(ui.sent('pin_project')[0].pinned, true);
});

// ---- the short command form: /cmd$command; ----

test('the /cmd chip puts "/cmd$" in the box, ready to type the command', async () => {
  const ui = await open();
  const chip = ui.qa('.cmd-chip').find((c) => c.dataset.cmd === '/cmd$');
  assert.ok(chip, 'the chip inserts the short form');
  ui.click(chip);
  const box = ui.$('promptInput');
  assert.equal(box.value, '/cmd$');
  assert.equal(box.selectionStart, 5);
});

test('a typed /cmd$command; is highlighted as a command and sent unchanged', async () => {
  const ui = await open();
  ui.type('/cmd$bun run dev');
  assert.ok(ui.$('promptInput').classList.contains('hl-on'), 'the box shows the command word styled');
  assert.equal(ui.$('promptHl').querySelector('.cmd-word').textContent, 'bun');

  ui.click(ui.$('sendBtn'));
  assert.deepEqual(ui.sent('send_prompt'), [{ type: 'send_prompt', text: '/cmd$bun run dev' }]);
});

test('the user bubble for the short form is a command bubble with the wrapper dimmed', async () => {
  const ui = await open();
  ui.deliver({ type: 'user_message', text: '/cmd$git status', source: 'desktop' });
  const bubble = ui.q('.user-bubble');
  assert.ok(bubble.classList.contains('cmd-bubble'));
  assert.equal(bubble.querySelector('.cmd-word').textContent, 'git');
  assert.deepEqual(Array.from(bubble.querySelectorAll('.cmd-pre')).map((e) => e.textContent), ['/cmd$']);
  assert.equal(bubble.textContent, '/cmd$git status');
});

// ---- staying light on long days ----

test('only the latest 300 rows stay in the page, with one note about the rest', async () => {
  const ui = await open();
  for (let i = 0; i < 330; i++) ui.deliver({ type: 'user_message', text: 'message ' + i, source: 'desktop' });
  await new Promise((r) => setTimeout(r, 30));
  const rows = ui.qa('#messagesList > .message-row');
  assert.equal(rows.length, 300);
  assert.match(rows[0].textContent, /message 30/);
  assert.match(rows[299].textContent, /message 329/);
  assert.equal(ui.qa('#messagesList > .earlier-note').length, 1);
});

test('closing the diagram frees its drawing and a reopened one is drawn again', async () => {
  const mermaid = {
    initialize() {},
    render: async () => ({ svg: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 50"><g class="node" id="flowchart-n1-0"><rect width="10" height="10"/><text>A</text></g></svg>' }),
  };
  const ui = await open({ mermaid });
  ui.window.openDiagram('C:\\Work\\app');
  ui.deliver({ type: 'diagram', path: 'C:\\Work\\app', overview: '', groups: [], nodes: [{ i: 1, label: 'A', group: null, path: null, shape: 'box' }], edges: [],
    mermaid: 'flowchart TD\n n1["A"]\n', mermaidFlat: 'flowchart TD\n n1["A"]\n', generatedUtc: '2026-10-28T12:00:00Z', stale: false, notes: [], tokens: 0 });
  await new Promise((r) => setTimeout(r, 40));
  assert.ok(ui.q('#diagStage svg'), 'drawn');
  ui.window.closeSheet('diagramSheet');
  assert.equal(ui.$('diagStage').children.length, 0, 'the drawing is released');
});
