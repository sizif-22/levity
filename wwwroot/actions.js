// The /run and /git buttons: scripts from the folder's package.json, and the common git steps.
// The PC works out what is available (message "actions") and builds every command itself; this file only shows choices and sends
// names back ("run_script" with a script name, "git_run" with an action name and, for a commit, the message).
// Relies on helpers from app.js: $, postToHost, openSheet, closeSheet, toast.

const act = { data: null, git: null, gitAsked: 0 };
const win = { state: null, journal: [] };

function actionsOnMessage(msg) {
  if (msg.type === 'actions') {
    act.data = msg;
    updateActionChips();
    if ($('runSheet').classList.contains('open')) renderRunList();
    if ($('gitSheet').classList.contains('open') && !(msg.git && msg.git.repo)) closeSheet('gitSheet');
  } else if (msg.type === 'git_state') {
    act.git = msg;
    renderGitState();
  } else if (msg.type === 'tools_state') {
    win.state = msg;
    renderWindowsState();
  } else if (msg.type === 'journal') {
    win.journal = msg.entries || [];
    renderJournal();
  } else if (msg.type === 'machine') {
    $('winMachine').textContent = msg.summary || 'No details available.';
  } else if (msg.type === 'jobs_open') {
    openSheet('jobsSheet');
    setTimeout(() => {
      const box = document.querySelector(`#jobList [data-id="${msg.id}"] .job-input`);
      if (box) box.focus();
    }, 80);
  }
}

// Which start-screen buttons make sense here. Mirrored by web/src/lib/onboarding.ts (the phone), and tested on both sides.
function onboardingChips(d, projectCount) {
  d = d || {};
  const testScript = (d.scripts || []).find(s => /^test/i.test(s.name));
  const known = !!act.data;   // nothing is offered until the PC has said what the folder is
  return {
    reviewAll: known && projectCount >= 1,
    newProject: known && d.hasFiles === false,
    inspect: known && d.hasFiles !== false,
    git: known && !!(d.git && d.git.repo),
    tests: known && !!testScript,
    testScript: testScript ? testScript.name : null,
    overview: known && !!(d.isProject || d.hasReadme),
  };
}

function updateOnboarding() {
  const c = onboardingChips(act.data, typeof projects !== 'undefined' && projects ? projects.length : 0);
  $('chipReviewAll').hidden = !c.reviewAll;
  $('chipNew').hidden = !c.newProject;
  $('chipInspect').hidden = !c.inspect;
  $('chipGit').hidden = !c.git;
  $('chipTests').hidden = !c.tests;
  $('chipOverview').hidden = !c.overview;
  if (c.testScript) $('chipTests').dataset.prompt = `Run the project's "${c.testScript}" script and summarize any failures.`;
}

function updateActionChips() {
  const d = act.data || {};
  $('ideChip').hidden = !d.path;
  updateOnboarding();
  $('runChip').hidden = !(d.scripts && d.scripts.length);
  $('gitChip').hidden = !(d.git && d.git.repo);
  $('diagramChip').hidden = !(d.git && d.git.repo);   // the diagram is for projects: a folder that is not a git repository gets no chip
}

// ---- /run ----

function renderRunList() {
  const d = act.data || { scripts: [] };
  $('runSub').textContent = d.scripts.length
    ? `${d.packageManager || 'npm'} · ${d.scripts.length} script${d.scripts.length === 1 ? '' : 's'} in package.json`
    : 'No scripts in this folder.';
  const list = $('runList');
  list.textContent = '';
  d.scripts.forEach(s => {
    const li = document.createElement('li');
    li.className = 'action-row';
    const text = document.createElement('div');
    text.className = 'action-text';
    const name = document.createElement('strong');
    name.textContent = s.name;
    const cmd = document.createElement('code');
    cmd.textContent = s.command;
    text.append(name, cmd);
    const run = document.createElement('button');
    run.className = 'btn primary action-go';
    run.textContent = 'Run';
    run.addEventListener('click', () => {
      postToHost({ type: 'run_script', name: s.name, interactive: $('runInteractive').checked });
      closeSheet('runSheet');
    });
    li.append(text, run);
    list.appendChild(li);
  });
}

// ---- /git ----

function renderGitState() {
  const g = act.git;
  const sub = $('gitSub');
  if (!g || !g.ok) { sub.textContent = g ? 'Could not read the repository status.' : 'Checking...'; return; }
  const parts = [g.branch || 'detached HEAD'];
  parts.push(g.changes === 0 ? 'clean' : `${g.changes} change${g.changes === 1 ? '' : 's'}`);
  if (g.ahead) parts.push(`${g.ahead} to push`);
  if (g.behind) parts.push(`${g.behind} to pull`);
  if (g.upstream === null || g.upstream === undefined) parts.push('no upstream');
  sub.textContent = parts.join(' · ');
  $('gitStage').checked = g.changes > 0;
}

function gitForm(which) {
  $('gitCommitForm').hidden = which !== 'commit';
  $('gitBranchForm').hidden = which !== 'branch';
  $('gitButtons').hidden = !!which;
  if (which === 'commit') setTimeout(() => $('gitMessage').focus(), 30);
  if (which === 'branch') setTimeout(() => $('gitBranchName').focus(), 30);
}

function openGit() {
  act.git = null;
  gitForm(null);
  $('gitMessage').value = '';
  $('gitBranchName').value = '';
  $('gitCommitBtn').disabled = true;
  renderGitState();
  openSheet('gitSheet');
  postToHost({ type: 'git_state' });
}

function gitRun(action, extra) {
  postToHost(Object.assign({ type: 'git_run', action }, extra || {}));
  closeSheet('gitSheet');
}

(function wireActions() {
  $('runChip').addEventListener('click', () => { renderRunList(); openSheet('runSheet'); });
  $('gitChip').addEventListener('click', openGit);

  document.querySelectorAll('#gitButtons [data-git]').forEach(b => b.addEventListener('click', () => gitRun(b.dataset.git)));
  $('gitCommitOpen').addEventListener('click', () => gitForm('commit'));
  $('gitBranchOpen').addEventListener('click', () => gitForm('branch'));
  $('gitCommitCancel').addEventListener('click', () => gitForm(null));
  $('gitBranchCancel').addEventListener('click', () => gitForm(null));

  $('gitMessage').addEventListener('input', () => { $('gitCommitBtn').disabled = !$('gitMessage').value.trim(); });
  const commit = () => {
    const message = $('gitMessage').value.trim();
    if (!message) return;
    gitRun('commit', { message, stage: $('gitStage').checked });
  };
  $('gitCommitBtn').addEventListener('click', commit);
  $('gitMessage').addEventListener('keydown', e => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); commit(); } });

  const branch = () => {
    const name = $('gitBranchName').value.trim();
    if (!name) return;
    gitRun('switch-new', { name });
  };
  $('gitBranchBtn').addEventListener('click', branch);
  $('gitBranchName').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); branch(); } });
})();

// ---- /windows ----

function renderWindowsState() {
  const s = win.state;
  const btn = $('winToggle');
  if (!s) { btn.disabled = true; return; }
  btn.disabled = false;
  btn.textContent = s.enabled ? 'Turn off' : 'Turn on';
  btn.classList.toggle('primary', !s.enabled);
  btn.classList.toggle('ghost', !!s.enabled);
  $('winStateText').textContent = s.enabled
    ? (s.running ? `On: ${s.count} tools are available to the assistant (they appear with its next message).` : 'On, but the tools are not running right now.')
    : 'Off. The assistant cannot see or change Windows settings until you turn this on.';
  const note = $('winElevateNote');
  note.hidden = !(s.enabled && s.elevateNote);
  note.textContent = s.elevateNote || '';
}

function renderJournal() {
  const list = $('winChanges');
  list.textContent = '';
  $('winChangesEmpty').hidden = win.journal.length > 0;
  win.journal.forEach(e => {
    const li = document.createElement('li');
    li.className = 'action-row' + (e.reverted ? ' reverted' : '');
    const text = document.createElement('div');
    text.className = 'action-text';
    const what = document.createElement('strong');
    what.textContent = e.summary;
    const when = document.createElement('code');
    when.textContent = new Date(e.time).toLocaleString() + (e.reverted ? ' · reverted' : '');
    text.append(what, when);
    li.appendChild(text);
    if (!e.reverted && e.canUndo) {
      const b = document.createElement('button');
      b.className = 'btn ghost action-go';
      b.textContent = 'Revert';
      b.addEventListener('click', () => postToHost({ type: 'journal_revert', id: e.id }));
      li.appendChild(b);
    }
    list.appendChild(li);
  });
}

(function wireWindows() {
  $('windowsChip').addEventListener('click', () => {
    openSheet('windowsSheet');
    postToHost({ type: 'tools_get' });
    postToHost({ type: 'journal_list' });
    postToHost({ type: 'machine_get' });
  });
  $('winToggle').addEventListener('click', () => {
    if (!win.state) return;
    $('winToggle').disabled = true;   // enabled again by the answer
    postToHost({ type: 'tools_set', on: !win.state.enabled });
  });
  $('winMachineRefresh').addEventListener('click', () => { $('winMachine').textContent = 'Reading...'; postToHost({ type: 'machine_get', refresh: true }); });
})();
