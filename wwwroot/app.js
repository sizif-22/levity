// Levity web UI. Runs inside the WPF WebView2 host (desktop) and in a phone browser (remote).

const isHost = !!(window.chrome && window.chrome.webview);
if (!isHost) document.body.classList.add('remote');

let currentAssistantMessageElement = null;
let currentAssistantText = '';
let currentThinkingText = '';
let isGenerating = false;
let autoScroll = true;
let renderQueued = false;
let segmentDone = false;                 // the last text segment ended; the next one starts a new paragraph
const toolPills = new Map();             // step_index -> pill element, so ACTIVE -> DONE updates in place

const $ = (id) => document.getElementById(id);
const chatViewport = $('chatViewport');
const messagesList = $('messagesList');
const welcomeScreen = $('welcomeScreen');
const promptInput = $('promptInput');
const sendBtn = $('sendBtn');
const statusDot = $('statusDot');
const statusLabel = $('statusLabel');
const scrollBottomBtn = $('scrollBottomBtn');

if (window.marked && window.hljs) {
  marked.setOptions({
    highlight(code, lang) {
      if (lang && hljs.getLanguage(lang)) {
        try { return hljs.highlight(code, { language: lang }).value; } catch (e) {}
      }
      return hljs.highlightAuto(code).value;
    },
    breaks: true,
    gfm: true
  });
}

// ---------------------------------------------------------------------------
// Transport: WebView2 bridge to the WPF host (phones use the separate web/ app)
// ---------------------------------------------------------------------------

function postToHost(msg) {
  if (isHost) window.chrome.webview.postMessage(msg);
}

if (isHost) {
  window.chrome.webview.addEventListener('message', (event) => {
    try {
      handleHostMessage(typeof event.data === 'string' ? JSON.parse(event.data) : event.data);
    } catch (e) {
      console.error('Error handling host message:', e, event.data);
    }
  });
  postToHost({ type: 'ready' });
}

// ---------------------------------------------------------------------------
// Host -> UI
// ---------------------------------------------------------------------------

function handleHostMessage(msg) {
  switch (msg.type) {
    case 'agy_event': processAgyEvent(msg.payload); break;
    case 'user_message': appendUserMessage(msg.text, msg.source); ensureAssistantMessageRow(); break;
    case 'status': updateStatus(msg.text, msg.state); break;
    case 'config':
      if (msg.cwd) setCwd(msg.cwd);
      if (msg.isPinned !== undefined) updatePinState(msg.isPinned);
      break;
    case 'clear_chat': resetChatUI(); break;
    case 'focus_input':
      if (isHost) setTimeout(() => promptInput.focus(), 50);
      break;
    case 'toast':
      toast(msg.text);
      // A refused review start must not leave the optimistic "Starting…" view stuck.
      if (lastSync && lastSync.optimistic && lastSync.running) {
        lastSync = null; wasRunning = false; showSyncView = false; showView('list'); updateSyncBanner();
        requestScan(false); postToHost({ type: 'list_projects' });
      }
      break;
    case 'sessions': renderSessions(msg); break;
    case 'queue': renderQueue(msg); break;
    case 'jobs': renderJobs(msg); break;
    case 'projects': renderProjects(msg); break;
    case 'sync_state': renderSync(msg); break;
    case 'remote_info': renderRemote(msg); break;
    case 'relay_test_result': showRelayTest(msg); break;
    case 'open_panel': openSheet(msg.panel === 'mobile' ? 'mobileSheet' : 'projectsSheet'); break;
  }
}

function setCwd(path) {
  currentCwd = path;
  $('cwdText').innerHTML = `<bdi>${escapeHtml(path)}</bdi>`;
  $('cwdButton').title = `Current project: ${path}`;
  markCurrentProject();
}
let currentCwd = '';

function updatePinState(pinned) {
  $('pinBtn').classList.toggle('active', pinned);
  $('pinBtn').title = pinned ? 'Pinned (always on top)' : 'Unpinned (hides when you click away)';
}

function updateStatus(text, state) {
  statusLabel.textContent = text;
  statusDot.className = 'status-dot ' + (state || 'online');

  const lower = (text || '').toLowerCase();
  isGenerating = state === 'busy' || lower.includes('thinking') || lower.includes('respond');
  sendBtn.disabled = false; // messages sent while busy are queued by the PC
  $('stopBtn').hidden = !isGenerating;

  if (!isGenerating && currentAssistantMessageElement && !currentAssistantText && !currentThinkingText
      && !currentAssistantMessageElement.querySelector('.tool-pill')) {
    currentAssistantMessageElement.remove();
    currentAssistantMessageElement = null;
  }
}

// ---------------------------------------------------------------------------
// Streaming events from agy
// ---------------------------------------------------------------------------

function processAgyEvent(ev) {
  if (!ev) return;

  if (ev.event === 'init') {
    if (ev.init && ev.init.cwd) setCwd(ev.init.cwd);
    updateStatus('Ready', 'online');
  } else if (ev.event === 'step_update') {
    const su = ev.step_update || {};
    const delta = su.text_delta;

    // One reply row per turn: agy reports an empty agent_response between tool steps, which must not create rows.
    if (su.step_type === 'agent_response') {
      if (delta) {
        ensureAssistantMessageRow();
        if (segmentDone && currentAssistantText) currentAssistantText += '\n\n';
        currentAssistantText += delta;
        segmentDone = su.state === 'DONE';
        scheduleRender();
      } else if (su.state === 'DONE') {
        segmentDone = true;
      }
    } else if (su.step_type === 'thinking' || su.thought || su.thinking_delta) {
      const thoughtDelta = su.thought || su.thinking_delta || delta || '';
      if (thoughtDelta) {
        ensureAssistantMessageRow();
        currentThinkingText += thoughtDelta;
        scheduleRender();
      }
    } else if (su.step_type === 'tool' || su.step_type === 'tool_call' || su.step_type === 'tool_use') {
      ensureAssistantMessageRow();
      renderToolExecution(su);
    }
  } else if (ev.event === 'result') {
    const res = ev.result || {};
    if (res.status === 'ERROR') {
      ensureAssistantMessageRow();
      const el = currentAssistantMessageElement && currentAssistantMessageElement.querySelector('.agent-content');
      if (el) el.insertAdjacentHTML('beforeend', `<div class="error-banner">${escapeHtml(res.error || 'This turn ended with an error.')}</div>`);
    }
    finishCurrentAssistantMessage(res);
    updateStatus('Ready', 'online');
  }
}

const TYPING_HTML = '<div class="typing" aria-label="Levity is typing"><i></i><i></i><i></i></div>';

function ensureAssistantMessageRow() {
  if (currentAssistantMessageElement) return;
  welcomeScreen.style.display = 'none';

  const row = document.createElement('div');
  row.className = 'message-row agent';
  row.innerHTML = `
    <div class="agent-header">
      <div class="agent-avatar"><svg viewBox="0 0 24 24"><circle cx="12" cy="5" r="2"/><circle cx="12" cy="15" r="6"/></svg></div>
      <span class="agent-name">Levity</span>
    </div>
    <div class="agent-bubble">
      <div class="agent-content">${TYPING_HTML}</div>
    </div>`;
  messagesList.appendChild(row);

  currentAssistantMessageElement = row;
  currentAssistantText = '';
  currentThinkingText = '';
  segmentDone = false;
  toolPills.clear();
  scrollToBottom();
}

// Render at most once per frame: a replayed conversation sends thousands of tiny deltas.
function scheduleRender() {
  if (renderQueued) return;
  renderQueued = true;
  requestAnimationFrame(() => { renderQueued = false; renderAssistantContent(); });
}

function renderAssistantContent() {
  if (!currentAssistantMessageElement) return;
  const contentEl = currentAssistantMessageElement.querySelector('.agent-content');
  if (!contentEl) return;

  // Keep tool pills (they are appended to the content node) while the text re-renders.
  const pills = Array.from(contentEl.querySelectorAll(':scope > .tool-pill'));
  const wasThinkingOpen = contentEl.querySelector('.thinking-box') ? contentEl.querySelector('.thinking-box').open : true;

  let html = '';
  if (currentThinkingText.trim()) {
    html += `<details class="thinking-box" ${wasThinkingOpen ? 'open' : ''}>
      <summary class="thinking-summary">Reasoning</summary>
      <div class="thinking-content">${escapeHtml(currentThinkingText)}</div></details>`;
  }
  if (currentAssistantText) {
    try { html += window.marked ? marked.parse(currentAssistantText) : `<p>${escapeHtml(currentAssistantText)}</p>`; }
    catch (e) { html += `<p>${escapeHtml(currentAssistantText)}</p>`; }
  }

  contentEl.innerHTML = sanitizeHtml(html);
  pills.forEach(p => contentEl.appendChild(p));
  if (!currentAssistantText) contentEl.insertAdjacentHTML('beforeend', TYPING_HTML);

  contentEl.querySelectorAll('pre').forEach(pre => {
    if (pre.querySelector('.code-toolbar')) return;
    const code = pre.querySelector('code');
    const lang = code ? (code.className.match(/language-(\w+)/) || [])[1] || 'code' : 'code';
    const toolbar = document.createElement('div');
    toolbar.className = 'code-toolbar';
    toolbar.innerHTML = `<span>${escapeHtml(lang)}</span><button class="copy-code-btn">Copy</button>`;
    const btn = toolbar.querySelector('button');
    btn.onclick = () => copyText((code || pre).innerText).then(() => {
      btn.textContent = 'Copied';
      setTimeout(() => { btn.textContent = 'Copy'; }, 1500);
    });
    pre.insertBefore(toolbar, pre.firstChild);
  });

  if (autoScroll) scrollToBottom();
}

function toolSummary(su) {
  const p = (su.tool_info && su.tool_info.parameters) || su.args || su.parameters || {};
  let pick = typeof p === 'string' ? p
    : p.CommandLine || p.command || p.AbsolutePath || p.path || p.Query || p.query || p.Url || p.url
      || Object.values(p).find(v => typeof v === 'string') || '';
  pick = String(pick).replace(/\s+/g, ' ').trim();
  return pick.length > 70 ? pick.slice(0, 67) + '…' : pick;
}

function renderToolExecution(su) {
  if (!currentAssistantMessageElement) return;
  const contentEl = currentAssistantMessageElement.querySelector('.agent-content');
  if (!contentEl) return;

  const key = su.step_index !== undefined ? su.step_index : toolPills.size;
  const running = su.state === 'ACTIVE' || su.state === 'RUNNING';
  const name = su.tool_name || su.name || (su.tool_info && su.tool_info.name) || 'tool';

  let pill = toolPills.get(key);
  if (!pill) {
    pill = document.createElement('div');
    toolPills.set(key, pill);
    const typing = contentEl.querySelector('.typing');
    if (typing) contentEl.insertBefore(pill, typing); else contentEl.appendChild(pill);
  }
  pill.className = 'tool-pill' + (running ? ' running' : '');
  pill.innerHTML = `${running ? '<span class="spinner mini"></span>' : ''}<strong>${escapeHtml(name)}</strong> ${escapeHtml(toolSummary(su) || (pill.dataset.args || ''))}`;
  if (toolSummary(su)) pill.dataset.args = toolSummary(su);
  if (autoScroll) scrollToBottom();
}

function finishCurrentAssistantMessage(resultData) {
  renderAssistantContent();
  const target = currentAssistantMessageElement || messagesList.querySelector('.message-row.agent:last-child');
  if (target) {
    target.querySelectorAll('.typing').forEach(t => t.remove());
    // A turn that produced nothing visible (only empty steps) leaves no empty row behind.
    if (!currentAssistantText && !currentThinkingText && !target.querySelector('.tool-pill, .error-banner')) {
      target.remove();
      currentAssistantMessageElement = null;
      currentAssistantText = '';
      currentThinkingText = '';
      isGenerating = false;
      sendBtn.disabled = false;
      return;
    }
    target.querySelectorAll('.tool-pill.running').forEach(p => { p.classList.remove('running'); const sp = p.querySelector('.spinner'); if (sp) sp.remove(); });

    if (resultData && (resultData.duration_seconds || resultData.usage) && !target.querySelector('.turn-stats')) {
      const parts = [];
      if (resultData.duration_seconds) parts.push(`${resultData.duration_seconds.toFixed(1)}s`);
      if (resultData.usage && resultData.usage.total_tokens) parts.push(`${resultData.usage.total_tokens.toLocaleString()} tokens`);
      const bubble = target.querySelector('.agent-bubble');
      if (parts.length && bubble) {
        const stats = document.createElement('div');
        stats.className = 'turn-stats';
        stats.textContent = parts.join(' · ');
        bubble.appendChild(stats);
      }
    }
  }
  currentAssistantMessageElement = null;
  currentAssistantText = '';
  currentThinkingText = '';
  isGenerating = false;
  sendBtn.disabled = false;
}

// "/cmd{$cd ../..}": dim the wrapper, colour and embolden the command word.
const CMD_RE = /^(\/(?:cmd|cli|run|sh)(?:\{|\s+)\s*\$?\s*)([^\s}]+)([\s\S]*)$/i;
function userBubbleHtml(text) {
  const m = CMD_RE.exec(text);
  if (!m) return escapeHtml(text);
  const rest = escapeHtml(m[3]).replace(/(^|\s)(-b)(?=\}?\s*$)/, '$1<span class="cmd-flag">$2</span>'); // the background flag reads as secondary
  return `<span class="cmd-pre">${escapeHtml(m[1])}</span><b class="cmd-word">${escapeHtml(m[2])}</b>${rest}`;
}

// Colour the command word inside the message box itself: a transparent-text textarea over a styled mirror.
function syncPromptHl() {
  const isCmd = CMD_RE.test(promptInput.value);
  promptInput.classList.toggle('hl-on', isCmd);
  promptInput.spellcheck = !isCmd;
  $('promptHl').innerHTML = isCmd ? userBubbleHtml(promptInput.value) + '&#8203;' : '';
  $('promptHl').scrollTop = promptInput.scrollTop;
}

function appendUserMessage(text, source) {
  welcomeScreen.style.display = 'none';
  const row = document.createElement('div');
  row.className = 'message-row user';
  row.innerHTML = `<div class="user-bubble">${userBubbleHtml(text)}</div>`;
  // Show where a message came from when it was not typed on this device.
  const fromOther = (source === 'mobile' && isHost) || (source === 'desktop' && !isHost);
  if (fromOther) row.insertAdjacentHTML('beforeend', `<span class="via">from ${source === 'mobile' ? 'your phone' : 'your PC'}</span>`);
  messagesList.appendChild(row);
  scrollToBottom();
}

function sendPrompt(text) {
  text = (text || '').trim();
  if (!text) return;
  postToHost({ type: 'send_prompt', text });
}

function sendCurrentPrompt() {
  const text = promptInput.value;
  if (!text.trim()) return;
  sendPrompt(text);
  promptInput.value = '';
  promptInput.style.height = 'auto';
  syncPromptHl();
}

function resetChatUI() {
  messagesList.innerHTML = '';
  welcomeScreen.style.display = 'flex';
  currentAssistantMessageElement = null;
  currentAssistantText = '';
  currentThinkingText = '';
  isGenerating = false;
  sendBtn.disabled = false;
}

// ---------------------------------------------------------------------------
// Input
// ---------------------------------------------------------------------------

promptInput.addEventListener('keydown', (e) => {
  // On phones Enter inserts a newline (the send button sends); on desktop Enter sends.
  if (e.key === 'Enter' && !e.shiftKey && isHost) {
    e.preventDefault();
    sendCurrentPrompt();
  }
});
promptInput.addEventListener('scroll', () => { $('promptHl').scrollTop = promptInput.scrollTop; });
promptInput.addEventListener('input', () => {
  syncPromptHl();
  promptInput.style.height = 'auto';
  promptInput.style.height = Math.min(promptInput.scrollHeight, 180) + 'px';
});
sendBtn.addEventListener('click', sendCurrentPrompt);

document.querySelectorAll('.chip[data-prompt]').forEach(chip => {
  chip.addEventListener('click', () => sendPrompt(chip.dataset.prompt));
});
document.querySelectorAll('.cmd-chip').forEach(cmd => {
  cmd.addEventListener('click', () => {
    const c = cmd.dataset.cmd;
    promptInput.value = c.endsWith('}') ? c : c + ' ';
    promptInput.focus();
    syncPromptHl();
    if (c.endsWith('}')) promptInput.setSelectionRange(c.length - 1, c.length - 1); // caret between the braces
  });
});

$('newChatBtn').addEventListener('click', () => { postToHost({ type: 'new_chat' }); });
$('pinBtn').addEventListener('click', () => postToHost({ type: 'toggle_pin' }));
$('closeBtn').addEventListener('click', () => postToHost({ type: 'close' }));

chatViewport.addEventListener('scroll', () => {
  const atBottom = chatViewport.scrollHeight - chatViewport.scrollTop <= chatViewport.clientHeight + 60;
  autoScroll = atBottom;
  scrollBottomBtn.style.display = atBottom ? 'none' : 'grid';
});
scrollBottomBtn.addEventListener('click', () => scrollToBottom(true));

function scrollToBottom(force = false) {
  chatViewport.scrollTo({ top: chatViewport.scrollHeight, behavior: force ? 'smooth' : 'auto' });
}

// ---------------------------------------------------------------------------
// Sheets
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// Queue and background jobs
// ---------------------------------------------------------------------------

function renderQueue(msg) {
  const items = msg.items || [];
  const host = $('queueList');
  host.hidden = items.length === 0;
  host.innerHTML = '';
  items.forEach(q => {
    const row = document.createElement('div');
    row.className = 'queue-item';
    row.innerHTML = `<span class="queue-tag">Queued</span><span class="queue-text">${escapeHtml(q.text)}</span>
      <button class="icon-btn" title="Remove from queue"><svg viewBox="0 0 24 24"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg></button>`;
    row.querySelector('button').addEventListener('click', () => postToHost({ type: 'queue_remove', id: q.id }));
    host.appendChild(row);
  });
}

function renderSessions(msg) {
  const list = $('sessList');
  const items = msg.sessions || [];
  $('sessSub').textContent = items.length ? `${items.length} saved on this PC` : 'No saved chats yet. Chats are saved automatically.';
  list.innerHTML = '';
  items.forEach(s => {
    const folder = (s.cwd || '').split(/[\\/]/).filter(Boolean).pop() || s.cwd || '';
    const isCurrent = s.id === msg.current;
    const li = document.createElement('li');
    li.className = 'sess' + (isCurrent ? ' current' : '');
    li.innerHTML = `<div class="sess-main"><div class="sess-title"></div><div class="sess-meta"></div></div>
      <button class="btn ghost sess-open"></button><button class="btn ghost sess-del">Delete</button>`;
    li.querySelector('.sess-title').textContent = s.title || '(untitled)';
    li.querySelector('.sess-meta').textContent = `${folder} · ${s.messages} message${s.messages === 1 ? '' : 's'} · ${ago(s.updated)}`;
    const open = li.querySelector('.sess-open');
    open.textContent = isCurrent ? 'Open now' : 'Open';
    open.disabled = isCurrent;
    const doOpen = () => { if (!isCurrent) { postToHost({ type: 'session_open', id: s.id }); closeSheet('sessionsSheet'); } };
    open.addEventListener('click', doOpen);
    li.querySelector('.sess-main').addEventListener('click', doOpen);
    const del = li.querySelector('.sess-del');
    let armed = null;
    del.addEventListener('click', () => {
      if (armed) { clearTimeout(armed); postToHost({ type: 'session_delete', id: s.id }); return; }
      del.textContent = 'Sure?';
      del.classList.add('danger');
      armed = setTimeout(() => { armed = null; del.textContent = 'Delete'; del.classList.remove('danger'); }, 3000);
    });
    list.appendChild(li);
  });
}

function jobElapsed(j) {
  const s = Math.max(0, Math.round(((j.ended ? new Date(j.ended) : new Date()) - new Date(j.started)) / 1000));
  return s < 60 ? `${s}s` : s < 3600 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m`;
}

function renderJobs(msg) {
  const jobs = msg.jobs || [];
  const running = jobs.filter(j => j.state === 'running').length;
  $('jobsBadge').hidden = running === 0;
  $('jobsBadge').textContent = running;
  $('jobsSub').textContent = jobs.length ? `${running} running · ${jobs.length - running} finished` : 'Nothing running. Start one with /cmd{$bun run dev}.';

  const list = $('jobList');
  const keep = new Set(jobs.map(j => String(j.id)));
  Array.from(list.children).forEach(li => { if (!keep.has(li.dataset.id)) li.remove(); });

  jobs.forEach(j => {
    let li = list.querySelector(`[data-id="${j.id}"]`);
    if (!li) {
      li = document.createElement('li');
      li.className = 'job';
      li.dataset.id = j.id;
      li.innerHTML = `<div class="job-head"><i class="job-dot"></i><code class="job-cmd"></code><span class="job-meta"></span>
        <button class="btn danger job-stop">Stop</button></div><pre class="job-out"></pre>`;
      li.querySelector('.job-cmd').textContent = `#${j.id} ${j.command}`;
      li.querySelector('.job-stop').addEventListener('click', () => postToHost({ type: 'job_stop', id: j.id }));
      list.prepend(li);
    }
    li.dataset.state = j.state;
    li.querySelector('.job-meta').textContent = j.state === 'running' ? `running ${jobElapsed(j)}`
      : `${j.state}${j.exitCode != null ? ` (exit ${j.exitCode})` : ''} · ${jobElapsed(j)}`;
    li.querySelector('.job-stop').hidden = j.state !== 'running';
    const pre = li.querySelector('.job-out');
    const atBottom = pre.scrollHeight - pre.scrollTop - pre.clientHeight < 24;
    if (pre.textContent !== j.tail) {
      pre.textContent = j.tail || '(no output yet)';
      if (atBottom) pre.scrollTop = pre.scrollHeight;
    }
  });
}

$('sessionsBtn').addEventListener('click', () => openSheet('sessionsSheet'));
$('jobsBtn').addEventListener('click', () => openSheet('jobsSheet'));
$('jobsClearBtn').addEventListener('click', () => postToHost({ type: 'job_clear' }));
$('stopBtn').addEventListener('click', () => postToHost({ type: 'stop_response' }));

function openSheet(id) {
  const sheet = $(id);
  sheet.classList.add('open');
  sheet.setAttribute('aria-hidden', 'false');
  if (id === 'projectsSheet') { requestScan(false); postToHost({ type: 'list_projects' }); syncSeen = true; updateSyncBanner(); }
  if (id === 'mobileSheet') postToHost({ type: 'remote_info' });
  if (id === 'sessionsSheet') postToHost({ type: 'list_sessions' });
}
function closeSheet(id) {
  const sheet = $(id);
  sheet.classList.remove('open');
  sheet.setAttribute('aria-hidden', 'true');
  if (id === 'projectsSheet') updateSyncBanner();
}
document.querySelectorAll('[data-close-sheet]').forEach(b => b.addEventListener('click', () => closeSheet(b.dataset.closeSheet)));
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') { if (!$('confirmOverlay').hidden) closeConfirm(false); else document.querySelectorAll('.sheet.open').forEach(s => closeSheet(s.id)); }
});

$('projectsBtn').addEventListener('click', () => openSheet('projectsSheet'));
$('cwdButton').addEventListener('click', () => openSheet('projectsSheet'));
$('pickFolderBtn').addEventListener('click', () => { closeSheet('projectsSheet'); postToHost({ type: 'pick_folder' }); });
$('noProjectBtn').addEventListener('click', () => { postToHost({ type: 'no_project' }); closeSheet('projectsSheet'); });
$('chipReviewAll').addEventListener('click', () => openSheet('projectsSheet'));
$('chipIde').addEventListener('click', () => postToHost({ type: 'open_ide', path: currentCwd }));
$('mobileBtn').addEventListener('click', () => openSheet('mobileSheet'));

// ---------------------------------------------------------------------------
// Projects & review
// ---------------------------------------------------------------------------

let projects = [];
let syncMode = 'update';
let lastSync = null;
let showSyncView = false;

function dot(has, label) { return `<span title="${label}: ${has ? 'present' : 'missing'}"><i class="d ${has ? 'ok' : 'miss'}"></i></span>`; }

const IDE_SVG = '<svg viewBox="0 0 24 24"><polyline points="16 18 22 12 16 6"/><polyline points="8 6 2 12 8 18"/></svg>';
const PIN_SVG = '<svg viewBox="0 0 24 24"><line x1="12" y1="17" x2="12" y2="22"/><path d="M5 17h14v-2l-2-2V5a1 1 0 0 0-1-1H8a1 1 0 0 0-1 1v8l-2 2v2z"/></svg>';
const DAY = 86400000;

// Pinned first, then most-used, then most recently active, then everything else A-Z.
function groupProjects(list) {
  const used = new Set();
  const take = (arr) => arr.filter(p => !used.has(p.path) && used.add(p.path));
  const pinned = take(list.filter(p => p.pinned).sort((a, b) => a.name.localeCompare(b.name)));
  const frequent = take(list.filter(p => p.uses >= 2)
    .sort((a, b) => b.uses - a.uses || new Date(b.lastUsed || 0) - new Date(a.lastUsed || 0)).slice(0, 5));
  const recent = take(list.filter(p => Date.now() - new Date(p.modified) < 30 * DAY)
    .sort((a, b) => new Date(b.modified) - new Date(a.modified)).slice(0, 6));
  const rest = list.filter(p => !used.has(p.path));
  return [['Pinned', pinned], ['Frequently used', frequent], ['Recently active', recent], ['All projects', rest]].filter(g => g[1].length);
}

function ago(iso) {
  const d = (Date.now() - new Date(iso)) / DAY;
  if (d < 1) return 'today';
  if (d < 2) return 'yesterday';
  if (d < 30) return `${Math.floor(d)}d ago`;
  if (d < 365) return `${Math.floor(d / 30)}mo ago`;
  return `${Math.floor(d / 365)}y ago`;
}

let scanUserInitiated = false;
function setScanning(on, label) {
  const btn = $('rescanBtn');
  btn.classList.toggle('busy', on);
  btn.querySelector('.spinner').hidden = !on;
  btn.querySelector('.lbl').textContent = on ? 'Scanning…' : 'Scan';
  $('projList').classList.toggle('loading', on);
  if (on) $('projSub').textContent = label || 'Scanning…';
}
function requestScan(userInitiated) {
  scanUserInitiated = !!userInitiated;
  setScanning(true, `Scanning ${$('rootInput').value || 'projects'}…`);
  return true;
}

function renderProjects(msg) {
  setScanning(false);
  projects = msg.projects || [];
  if (msg.current) currentCwd = msg.current;
  $('rootInput').value = msg.root || '';
  const missing = projects.filter(p => !p.hasReadme || !p.hasAgentsDoc || !p.hasSkills).length;
  $('projSub').textContent = `${projects.length} projects found · ${missing} missing docs or skills`;

  // Re-rendering after a pin must not reset what the user already unchecked.
  const unchecked = new Set(Array.from(document.querySelectorAll('#projList .cb')).filter(c => !c.checked).map(c => c.dataset.path));
  const host = $('projList');
  host.innerHTML = '';

  groupProjects(projects).forEach(([title, items]) => {
    const h = document.createElement('div');
    h.className = 'grp-title';
    h.textContent = title;
    host.appendChild(h);
    const ul = document.createElement('ul');
    ul.className = 'proj-list';
    items.forEach(p => ul.appendChild(projectRow(p, !unchecked.has(p.path))));
    host.appendChild(ul);
  });

  markCurrentProject();
  updateSyncButton();
  if (!showSyncView) showView('list');
  if (scanUserInitiated) { toast(`Scan complete: ${projects.length} projects found`); scanUserInitiated = false; }
}

function projectRow(p, checked) {
  const i = projects.indexOf(p);
  const li = document.createElement('li');
  li.className = 'proj';
  li.dataset.path = p.path;
  const meta = [p.uses ? `${p.uses} prompt${p.uses === 1 ? '' : 's'}` : '', ago(p.modified)].filter(Boolean).join(' · ');
  li.innerHTML = `
    <input type="checkbox" class="cb" data-i="${i}" data-path="${escapeHtml(p.path)}" ${checked ? 'checked' : ''} aria-label="Select ${escapeHtml(p.name)}">
    <div class="proj-main">
      <div class="proj-name">${escapeHtml(p.name)}</div>
      <div class="proj-meta"><span class="kind">${escapeHtml(p.kind)}</span>
        ${dot(p.hasReadme, 'README')}${dot(p.hasAgentsDoc, 'AGENTS')}${dot(p.hasSkills, 'Skills')}
        <span>${escapeHtml(meta)}</span></div>
    </div>
    <button class="pin-btn ide-btn" title="Open in Antigravity IDE">${IDE_SVG}</button>
    <button class="pin-btn ${p.pinned ? 'on' : ''}" title="${p.pinned ? 'Unpin' : 'Pin to top'}">${PIN_SVG}</button>
    <button class="open-btn">Open</button>`;
  li.querySelector('.proj-main').addEventListener('click', () => { const cb = li.querySelector('.cb'); cb.checked = !cb.checked; updateSyncButton(); });
  li.querySelector('.cb').addEventListener('change', updateSyncButton);
  li.querySelector('.ide-btn').addEventListener('click', () => postToHost({ type: 'open_ide', path: p.path }));
  li.querySelector('.pin-btn').addEventListener('click', () => postToHost({ type: 'pin_project', path: p.path, pinned: !p.pinned }));
  li.querySelector('.open-btn').addEventListener('click', () => {
    postToHost({ type: 'set_cwd', path: p.path });
    closeSheet('projectsSheet');
  });
  return li;
}

function markCurrentProject() {
  document.querySelectorAll('#projList .proj').forEach(li => {
    const cur = currentCwd && li.dataset.path.toLowerCase() === currentCwd.toLowerCase();
    li.classList.toggle('current', !!cur);
    const btn = li.querySelector('.open-btn');
    btn.disabled = !!cur;
    btn.textContent = cur ? 'Active' : 'Open';
  });
}

function selectedPaths() {
  return Array.from(document.querySelectorAll('#projList .cb'))
    .filter(cb => cb.checked).map(cb => projects[+cb.dataset.i].path);
}

function updateSyncButton() {
  const n = selectedPaths().length;
  const all = n === projects.length && n > 0;
  const btn = $('startSyncBtn');
  btn.disabled = n === 0;
  btn.textContent = n === 0 ? 'Select projects' : all ? `Review all ${n} projects` : `Review ${n} project${n === 1 ? '' : 's'}`;
  $('selectAll').checked = all;
}

$('selectAll').addEventListener('change', (e) => {
  document.querySelectorAll('#projList .cb').forEach(cb => { cb.checked = e.target.checked; });
  updateSyncButton();
});
$('rescanBtn').addEventListener('click', () => {
  const path = $('rootInput').value.trim();
  if (path) { requestScan(true); postToHost({ type: 'set_root', path }); }
});
$('rootInput').addEventListener('keydown', (e) => { if (e.key === 'Enter') $('rescanBtn').click(); });

document.querySelectorAll('#modeSeg .seg-btn').forEach(b => b.addEventListener('click', () => {
  syncMode = b.dataset.mode;
  document.querySelectorAll('#modeSeg .seg-btn').forEach(x => x.classList.toggle('active', x === b));
}));

$('startSyncBtn').addEventListener('click', async () => {
  const paths = selectedPaths();
  if (!paths.length) return;
  const n = paths.length;
  const text = syncMode === 'update'
    ? `Levity will create or edit README, AGENTS.md and skill files in ${n} project${n === 1 ? '' : 's'}, one at a time. Source code is not touched and nothing is committed.`
    : `Levity will read ${n} project${n === 1 ? '' : 's'} one at a time and report suggested changes. No files are written.`;
  if (!(await confirmDialog(syncMode === 'update' ? `Update docs in ${n} project${n === 1 ? '' : 's'}?` : `Review ${n} project${n === 1 ? '' : 's'}?`, text, 'Start'))) return;

  // Show progress immediately; the first real state arrives from the PC a moment later.
  showSyncView = true;
  syncSeen = true;
  renderSync({
    running: true, mode: syncMode,
    items: paths.map(p => ({ name: (projects.find(x => x.path === p) || {}).name || p, path: p, status: 'pending', summary: '', seconds: 0 })),
    optimistic: true
  });
  postToHost({ type: 'sync_start', paths, mode: syncMode });
});
$('syncCancelBtn').addEventListener('click', () => postToHost({ type: 'sync_cancel' }));
$('syncBackBtn').addEventListener('click', () => { showSyncView = false; showView('list'); requestScan(false); postToHost({ type: 'list_projects' }); });

function showView(which) {
  const sync = which === 'sync';
  $('projListView').hidden = sync;
  $('projFoot').hidden = sync;
  $('syncView').hidden = !sync;
  $('syncFoot').hidden = !sync;
}

const SYNC_ICON = { pending: '○', running: '◌', done: '✓', error: '✕', cancelled: '–' };

let syncSeen = true;
let wasRunning = false;

function syncCounts(items) {
  return {
    finished: items.filter(i => ['done', 'error', 'cancelled'].includes(i.status)).length,
    ok: items.filter(i => i.status === 'done').length,
    failed: items.filter(i => i.status === 'error').length,
    cancelled: items.filter(i => i.status === 'cancelled').length
  };
}

function updateSyncBanner() {
  const banner = $('syncBanner');
  const items = (lastSync && lastSync.items) || [];
  const sheetOpen = $('projectsSheet').classList.contains('open');
  if (!items.length || sheetOpen || (!lastSync.running && syncSeen)) { banner.hidden = true; return; }

  const c = syncCounts(items);
  banner.hidden = false;
  banner.className = 'sync-banner' + (lastSync.running ? '' : c.failed ? ' failed' : ' done');
  $('syncBannerSpin').hidden = !lastSync.running;
  const running = items.find(i => i.status === 'running');
  $('syncBannerText').textContent = lastSync.running
    ? `Reviewing ${Math.min(c.finished + 1, items.length)} of ${items.length}${running ? ' · ' + running.name : ''}`
    : `Review finished · ${c.ok} done${c.failed ? `, ${c.failed} failed` : ''}${c.cancelled ? `, ${c.cancelled} stopped` : ''}`;
}
$('syncBanner').addEventListener('click', () => { showSyncView = true; openSheet('projectsSheet'); renderSync(lastSync); });

function renderSync(state) {
  lastSync = state;
  const items = state.items || [];
  if (!items.length) return;

  if (wasRunning && !state.running) {
    const c = syncCounts(items);
    toast(`Review finished: ${c.ok} done${c.failed ? `, ${c.failed} failed` : ''}`);
    syncSeen = $('projectsSheet').classList.contains('open');
  }
  wasRunning = !!state.running;
  updateSyncBanner();
  updateStatusFromSync(state);

  if (state.running) showSyncView = true;
  if (!showSyncView) return;

  showView('sync');
  const { finished, ok, failed, cancelled } = syncCounts(items);
  $('syncBar').style.width = `${Math.round((finished / items.length) * 100)}%`;
  $('projSub').textContent = state.running
    ? `${state.optimistic ? 'Starting' : state.mode === 'report' ? 'Reading' : 'Updating'} ${Math.min(finished + 1, items.length)} of ${items.length}…`
    : `Finished: ${ok} done${failed ? `, ${failed} failed` : ''}${cancelled ? `, ${cancelled} stopped` : ''}`;

  const res = $('syncResult');
  res.hidden = !!state.running;
  if (!state.running) {
    const allOk = failed === 0 && cancelled === 0;
    res.className = 'sync-result ' + (allOk ? 'done' : 'failed');
    res.innerHTML = `<span class="big">${allOk ? '✓' : '!'}</span><span><strong>${allOk ? 'Review complete' : cancelled && !failed ? 'Review stopped' : 'Review finished with errors'}</strong>` +
      `<small>${ok} of ${items.length} projects ${state.mode === 'report' ? 'reported on' : 'updated'}${failed ? ` · ${failed} failed` : ''}. Tap a project for details.</small></span>`;
  }
  $('syncSummary').textContent = state.mode === 'report' ? 'Report only — no files were written.' : 'Docs and skills only — source code is never modified.';

  const openIds = new Set(Array.from(document.querySelectorAll('#syncList details[open]')).map(d => d.dataset.path));
  $('syncList').innerHTML = items.map(i => `
    <li><details class="sync-item" data-path="${escapeHtml(i.path)}" ${openIds.has(i.path) ? 'open' : ''}>
      <summary class="sync-row">
        <span class="sync-ico ${i.status}">${SYNC_ICON[i.status] || '○'}</span>
        <span class="sync-name">${escapeHtml(i.name)}</span>
        <span class="sync-time">${i.seconds ? i.seconds + 's' : i.status === 'running' ? 'running' : ''}</span>
      </summary>
      ${i.summary ? `<div class="sync-detail">${escapeHtml(i.summary)}</div>` : ''}
    </details></li>`).join('');

  $('syncCancelBtn').hidden = !state.running;
  $('syncBackBtn').hidden = !!state.running;
}

// ---------------------------------------------------------------------------
// Mobile pairing (desktop only)
// ---------------------------------------------------------------------------

let remoteInfo = null;
let editingConfig = false;
let pairTimerId = null;
const handledPairRequests = new Set();

function renderRemote(info) {
  remoteInfo = info;
  $('remoteToggle').checked = info.enabled;
  const phones = (info.devices || []).filter(d => d.connected).length;
  $('mobileState').textContent = !info.configured ? 'Needs setup'
    : info.enabled ? `On · ${(info.devices || []).length} paired · ${phones} online` : 'Off · nothing is connected';

  const badge = $('mobileBadge');
  const online = (info.devices || []).reduce((n, d) => n + (d.phones || 0), 0);
  badge.hidden = !online;
  badge.textContent = online;

  renderRelayStatus(info);

  const showSetup = !info.configured || editingConfig;
  $('setupBox').hidden = !showSetup;
  if (showSetup && document.activeElement !== $('relayInput') && document.activeElement !== $('appInput')) {
    $('relayInput').value = info.relayUrl || '';
    $('appInput').value = info.appUrl || '';
  }

  $('devicesBox').hidden = showSetup || !info.enabled;
  if (!$('devicesBox').hidden) {
    const list = $('deviceList');
    const devices = info.devices || [];
    $('noDevices').hidden = devices.length > 0;
    list.hidden = devices.length === 0;
    list.innerHTML = devices.map(d => `
      <li class="dev" data-id="${escapeHtml(d.id)}">
        <i class="d ${d.connected ? 'ok' : 'off'}" title="${d.connected ? 'Connected to relay' : 'Waiting'}"></i>
        <div class="dev-main">
          <div class="dev-name">${escapeHtml(d.name)}</div>
          <div class="dev-meta">${d.phones ? 'Online now' : d.lastSeen ? 'Last seen ' + ago(d.lastSeen) : 'Never connected'} · paired ${ago(d.created)}</div>
        </div>
        <button class="revoke-btn">Revoke</button>
      </li>`).join('');
    list.querySelectorAll('.dev').forEach(li => li.querySelector('.revoke-btn').addEventListener('click', async () => {
      const name = li.querySelector('.dev-name').textContent;
      if (await confirmDialog(`Revoke "${name}"?`, 'This phone is disconnected now and can only come back by pairing again.', 'Revoke')) {
        postToHost({ type: 'device_revoke', id: li.dataset.id });
      }
    }));
  }

  // Pairing QR + countdown
  const pairing = info.pairing;
  $('pairBox').hidden = !pairing;
  $('pairBtn').hidden = !!pairing;
  clearInterval(pairTimerId);
  if (pairing) {
    $('qrBox').innerHTML = pairing.qr;
    const tick = () => {
      const left = Math.max(0, Math.round((new Date(pairing.expiresAt) - Date.now()) / 1000));
      $('pairTimer').textContent = `${Math.floor(left / 60)}:${String(left % 60).padStart(2, '0')}`;
    };
    tick();
    pairTimerId = setInterval(tick, 1000);
  }

  (info.pending || []).forEach(askToApprove);
}

const RELAY_TEXT = {
  off: ['Relay off', 'Turn on Mobile access to connect.', 'off'],
  idle: ['Relay ready', 'The link opens when you pair a phone. Use Test to check it now.', 'off'],
  connecting: ['Connecting to relay…', 'Trying to reach the relay.', 'wait'],
  connected: ['Relay connected', 'End-to-end encrypted. Phones can reach this PC.', 'ok'],
  error: ['Cannot reach relay', '', 'err'],
};
let relayTestNote = '';

function renderRelayStatus(info) {
  const box = $('relayStatus');
  box.hidden = !info.configured;
  if (!info.configured) return;
  const st = info.relay || { state: 'off', error: '' };
  const [title, detail, cls] = RELAY_TEXT[st.state] || RELAY_TEXT.off;
  $('relayDot').className = 'd ' + cls;
  $('relayTitle').textContent = title;
  $('relayDetail').textContent = relayTestNote || st.error || detail;
}

function showRelayTest(res) {
  const btn = $('relayTestBtn');
  btn.classList.remove('busy');
  btn.querySelector('.spinner').hidden = true;
  btn.querySelector('.lbl').textContent = 'Test';
  relayTestNote = res.ok ? `Test passed in ${res.ms} ms.` : `Test failed: ${res.error}`;
  $('relayDot').className = 'd ' + (res.ok ? 'ok' : 'err');
  $('relayTitle').textContent = res.ok ? 'Relay reachable' : 'Cannot reach relay';
  $('relayDetail').textContent = relayTestNote;
  setTimeout(() => { relayTestNote = ''; if (remoteInfo) renderRelayStatus(remoteInfo); }, 15000);
}

$('relayTestBtn').addEventListener('click', () => {
  const btn = $('relayTestBtn');
  btn.classList.add('busy');
  btn.querySelector('.spinner').hidden = false;
  btn.querySelector('.lbl').textContent = 'Testing…';
  relayTestNote = '';
  postToHost({ type: 'relay_test' });
});

// A phone that scanned the QR shows a 4-digit code; approving here confirms it is the phone in your hand.
async function askToApprove(req) {
  if (handledPairRequests.has(req.id) || !$('confirmOverlay').hidden) return;
  handledPairRequests.add(req.id);
  openSheet('mobileSheet');
  const ok = await confirmDialog(`Connect "${req.name}"?`,
    `Only approve if the code on your phone is ${req.code}.\n\nThis phone will be able to chat with Levity and run commands on this PC.`, 'Approve');
  postToHost({ type: ok ? 'pair_approve' : 'pair_deny', id: req.id });
  if (ok) toast(`${req.name} paired`);
}

$('remoteToggle').addEventListener('change', (e) => postToHost({ type: 'remote_toggle', enabled: e.target.checked }));
$('saveConfigBtn').addEventListener('click', () => {
  editingConfig = false;
  postToHost({ type: 'remote_config', relayUrl: $('relayInput').value, appUrl: $('appInput').value });
});
$('editConfigBtn').addEventListener('click', () => { editingConfig = true; renderRemote(remoteInfo); });
$('pairBtn').addEventListener('click', () => postToHost({ type: 'pair_begin' }));
$('pairCancelBtn').addEventListener('click', () => postToHost({ type: 'pair_cancel' }));

// ---------------------------------------------------------------------------
// Utilities
// ---------------------------------------------------------------------------

let confirmResolve = null;
function confirmDialog(title, text, okLabel = 'Continue') {
  $('confirmTitle').textContent = title;
  $('confirmText').textContent = text;
  $('confirmOk').textContent = okLabel;
  $('confirmOverlay').hidden = false;
  $('confirmOk').focus();
  return new Promise(res => { confirmResolve = res; });
}
function closeConfirm(result) {
  $('confirmOverlay').hidden = true;
  if (confirmResolve) { confirmResolve(result); confirmResolve = null; }
}
$('confirmOk').addEventListener('click', () => closeConfirm(true));
$('confirmCancel').addEventListener('click', () => closeConfirm(false));

let toastTimer = null;
function toast(text) {
  const el = $('toast');
  el.textContent = text;
  el.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.remove('show'), 3200);
}

function copyText(text) {
  if (navigator.clipboard && window.isSecureContext) return navigator.clipboard.writeText(text);
  // Phones reach this page over plain http, where the async clipboard API is unavailable.
  return new Promise((resolve) => {
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    try { document.execCommand('copy'); } catch (e) {}
    ta.remove();
    resolve();
  });
}

function escapeHtml(str) {
  return String(str ?? '')
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#039;');
}

// Agent output can echo web content, and the phone origin holds the pairing key, so strip anything active.
function sanitizeHtml(html) {
  const tpl = document.createElement('template');
  tpl.innerHTML = html;
  tpl.content.querySelectorAll('script,iframe,object,embed,style,link,meta,base,form').forEach(n => n.remove());
  tpl.content.querySelectorAll('*').forEach(el => {
    for (const attr of Array.from(el.attributes)) {
      const name = attr.name.toLowerCase();
      const val = attr.value.trim().toLowerCase();
      if (name.startsWith('on') || ((name === 'href' || name === 'src' || name === 'xlink:href') && /^(javascript|data|vbscript):/.test(val))) {
        el.removeAttribute(attr.name);
      }
    }
    if (el.tagName === 'A') { el.setAttribute('target', '_blank'); el.setAttribute('rel', 'noopener noreferrer'); }
  });
  const div = document.createElement('div');
  div.appendChild(tpl.content);
  return div.innerHTML;
}

if (!isHost && 'visualViewport' in window) {
  // iOS Safari does not resize the layout when the keyboard opens: shrink the app to the visible area then.
  const appEl = document.getElementById('app');
  window.visualViewport.addEventListener('resize', () => {
    const keyboardOpen = window.innerHeight - window.visualViewport.height > 120;
    appEl.style.height = keyboardOpen ? window.visualViewport.height + 'px' : '';
    if (keyboardOpen && autoScroll) scrollToBottom();
  });
}

function updateStatusFromSync(state) {
  const items = state.items || [];
  if (state.running) {
    const c = syncCounts(items);
    statusLabel.textContent = `Reviewing ${Math.min(c.finished + 1, items.length)}/${items.length}`;
    statusDot.className = 'status-dot busy';
  } else if (statusLabel.textContent.startsWith('Reviewing') && !isGenerating) {
    updateStatus('Ready', 'online');
  }
}
