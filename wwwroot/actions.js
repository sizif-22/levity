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
  } else if (msg.type === 'checkpoint') {
    renderCheckpoint(msg);
  } else if (msg.type === 'speed') {
    renderSpeed(msg);
  } else if (msg.type === 'doctor') {
    renderDoctor(msg);
  } else if (msg.type === 'doctor_result') {
    renderDoctorResult(msg);
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
  $('settingsWindowsState').textContent = !s ? 'Let the assistant look at and change Windows, safely'
    : s.enabled ? `On: ${s.count} tools for the assistant` : 'Off: the assistant cannot see or change Windows settings';
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
  $('settingsWindows').addEventListener('click', () => {
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

// ---- /speed ----

const fmtSecs = (s) => s >= 90 ? `${Math.floor(s / 60)} min ${Math.round(s % 60)} s` : `${Math.round(s)} s`;
const fmtTokens = (n) => n >= 1000 ? `${(n / 1000).toFixed(n >= 100000 ? 0 : 1)}k` : String(n);

function fillList(id, wrapId, items) {
  const ul = $(id);
  ul.textContent = '';
  items.forEach(t => { const li = document.createElement('li'); li.textContent = t; ul.appendChild(li); });
  if (wrapId) $(wrapId).hidden = items.length === 0;
}

function renderSpeed(m) {
  const c = m.current || {};
  $('speedSummary').textContent = c.turns
    ? `${c.turns} answer${c.turns === 1 ? '' : 's'} · ${fmtSecs(c.seconds)} · ${fmtTokens(c.inputTokens)} tokens in, ${fmtTokens(c.outputTokens)} out (${fmtTokens(c.thinkingTokens)} thinking)`
    : 'Nothing to measure yet in this conversation.';
  fillList('speedFindings', 'speedFindingsWrap', c.findings || []);
  fillList('speedSlow', 'speedSlowWrap', (c.slow || []).map(s => `${Math.round(s.seconds)} s · ${s.tool}${s.what ? ' · ' + s.what : ''}`));
  fillList('speedTools', 'speedToolsWrap', (c.tools || []).map(t => `${t.name}: ${t.count} call${t.count === 1 ? '' : 's'}, ${fmtSecs(t.seconds)} in total (slowest ${fmtSecs(t.max)})`));
  fillList('speedRecent', 'speedRecentWrap', (m.recent || []).filter(r => r.turns > 0).map(r => `${r.title || 'Untitled'}: ${r.turns} answer${r.turns === 1 ? '' : 's'}, ${fmtSecs(r.seconds)}, ${fmtTokens(r.inputTokens)} tokens in`));
  const f = m.footprint || {};
  $('speedFootprint').textContent = f.files ? `${Math.round(f.totalMb)} MB in ${f.files.toLocaleString()} files${f.truncated ? ' (stopped early: very large)' : ''}. Only what the assistant loads costs tokens; this is a read-only look.` : 'No assistant folder found.';
  fillList('speedFolders', null, (f.folders || []).map(x => `${x.name}: ${Math.round(x.mb)} MB, ${x.files.toLocaleString()} files`));
  fillList('speedNotes', null, f.notes || []);
}

(function wireSpeed() {
  $('speedChip').addEventListener('click', () => {
    $('speedSummary').textContent = 'Working it out...';
    openSheet('speedSheet');
    postToHost({ type: 'speed_get' });
  });
})();

// ---- undo for the last answer ----

const ckpt = { id: null, armed: 0 };

function renderCheckpoint(m) {
  const bar = $('checkpointBar');
  clearTimeout(ckpt.armed);
  $('checkpointUndo').textContent = 'Undo';
  $('checkpointUndo').classList.remove('danger');
  if (m.none) { ckpt.id = null; bar.hidden = true; $('checkpointFiles').hidden = true; return; }
  ckpt.id = m.id;
  const n = m.added + m.modified + m.deleted;
  const parts = [];
  if (m.modified) parts.push(`${m.modified} changed`);
  if (m.added) parts.push(`${m.added} new`);
  if (m.deleted) parts.push(`${m.deleted} deleted`);
  $('checkpointText').textContent = `That answer changed ${n} file${n === 1 ? '' : 's'} (${parts.join(', ')}; +${m.insertions} −${m.deletions})`;
  const ul = $('checkpointFiles');
  ul.textContent = '';
  const label = { A: 'new', M: 'changed', D: 'deleted', T: 'changed' };
  (m.changes || []).forEach(c => { const li = document.createElement('li'); li.textContent = `${label[c.status] || c.status}  ${c.path}`; ul.appendChild(li); });
  if (m.more) { const li = document.createElement('li'); li.textContent = `... and ${m.more} more`; ul.appendChild(li); }
  ul.hidden = true;
  bar.hidden = false;
}

(function wireCheckpoint() {
  $('checkpointView').addEventListener('click', () => { const ul = $('checkpointFiles'); ul.hidden = !ul.hidden; });
  $('checkpointDismiss').addEventListener('click', () => { $('checkpointBar').hidden = true; });
  // Undo puts files back, so it takes a second press
  $('checkpointUndo').addEventListener('click', () => {
    const btn = $('checkpointUndo');
    if (ckpt.armed) { clearTimeout(ckpt.armed); ckpt.armed = 0; btn.textContent = 'Undo'; btn.classList.remove('danger'); postToHost({ type: 'checkpoint_undo', id: ckpt.id }); return; }
    btn.textContent = 'Sure?';
    btn.classList.add('danger');
    ckpt.armed = setTimeout(() => { ckpt.armed = 0; btn.textContent = 'Undo'; btn.classList.remove('danger'); }, 3000);
  });
})();

// ---- /doctor ----
const DOCTOR_LABEL = { critical: 'Fix now', warning: 'Worth fixing', info: 'For your information', ok: 'All good' };
const doctor = { armed: null };

function renderDoctor(m) {
  doctor.armed = null;
  const list = $('doctorList');
  list.textContent = '';
  $('doctorScan').disabled = false;
  $('doctorScan').textContent = 'Scan again';
  $('doctorStatus').textContent = m.time ? 'Scanned ' + new Date(m.time).toLocaleTimeString() + '.' : '';
  (m.findings || []).forEach(f => {
    const card = document.createElement('div');
    card.className = 'win-card doctor-' + f.severity;
    card.dataset.id = f.id;
    const sev = document.createElement('span'); sev.className = 'win-muted'; sev.textContent = DOCTOR_LABEL[f.severity] || f.severity;
    const title = document.createElement('strong'); title.textContent = f.title;
    const detail = document.createElement('p'); detail.className = 'win-muted'; detail.textContent = f.detail;
    card.append(sev, title, detail);
    if (f.fixes && f.fixes.length) {
      const row = document.createElement('div'); row.className = 'diag-actions';
      f.fixes.forEach(fx => {
        const b = document.createElement('button');
        b.className = 'btn ghost';
        b.textContent = fx.label;
        b.addEventListener('click', () => {
          if (fx.changes) {
            // A change to Windows needs a second press, like Undo.
            const key = f.id + ':' + fx.index;
            if (doctor.armed !== key) { doctor.armed = key; b.textContent = 'Sure?'; return; }
            doctor.armed = null; b.textContent = fx.label;
          }
          postToHost({ type: 'doctor_fix', id: f.id, index: fx.index });
        });
        row.appendChild(b);
      });
      card.appendChild(row);
    }
    list.appendChild(card);
  });
  const sk = $('doctorSkipped');
  sk.hidden = !(m.skipped && m.skipped.length);
  sk.textContent = sk.hidden ? '' : 'Could not check: ' + m.skipped.join(', ') + '.';
}

function renderDoctorResult(r) {
  const card = Array.from(document.querySelectorAll('#doctorList .win-card')).find(c => c.dataset.id === r.id);
  if (!card) return;
  let pre = card.querySelector('pre');
  if (!pre) { pre = document.createElement('pre'); pre.className = 'win-muted'; pre.style.cssText = 'white-space:pre-wrap;max-height:220px;overflow:auto;margin:0'; card.appendChild(pre); }
  pre.textContent = r.text;
}

(function wireDoctor() {
  const scan = () => {
    $('doctorScan').disabled = true;
    $('doctorScan').textContent = 'Scanning...';
    $('doctorStatus').textContent = 'Looking at disk space, memory, restarts, start-up, the network, the event log and PATH...';
    postToHost({ type: 'doctor_scan' });
  };
  $('doctorChip').addEventListener('click', () => { openSheet('doctorSheet'); scan(); });
  $('doctorScan').addEventListener('click', scan);
})();
