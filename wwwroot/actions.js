// The /run and /git buttons: scripts from the folder's package.json, and the common git steps.
// The PC works out what is available (message "actions") and builds every command itself; this file only shows choices and sends
// names back ("run_script" with a script name, "git_run" with an action name and, for a commit, the message).
// Relies on helpers from app.js: $, postToHost, openSheet, closeSheet, toast.

const act = { data: null, git: null, gitAsked: 0 };

function actionsOnMessage(msg) {
  if (msg.type === 'actions') {
    act.data = msg;
    updateActionChips();
    if ($('runSheet').classList.contains('open')) renderRunList();
    if ($('gitSheet').classList.contains('open') && !(msg.git && msg.git.repo)) closeSheet('gitSheet');
  } else if (msg.type === 'git_state') {
    act.git = msg;
    renderGitState();
  } else if (msg.type === 'jobs_open') {
    openSheet('jobsSheet');
    setTimeout(() => {
      const box = document.querySelector(`#jobList [data-id="${msg.id}"] .job-input`);
      if (box) box.focus();
    }, 80);
  }
}

function updateActionChips() {
  const d = act.data || {};
  $('runChip').hidden = !(d.scripts && d.scripts.length);
  $('gitChip').hidden = !(d.git && d.git.repo);
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
