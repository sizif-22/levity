// Fast Learner notices in the desktop sidebar: what the user sees when the agent learns something.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { open, reply, result } from './harness.mjs';

const fl = (over = {}) => ({
  type: 'fast_learner', phase: 'running', kind: 'run', detail: 'Wallpaper took 8 attempts', source: 'announce',
  text: 'Fast Learner is running: Wallpaper took 8 attempts', ...over,
});

test('Fast Learner starting shows a pulsing card in the reply and a toast', async () => {
  const ui = await open();
  ui.deliver({ type: 'user_message', text: 'change my wallpaper', source: 'desktop' });
  ui.deliver(fl());
  const card = ui.q('.message-row.agent .agent-content .fl-card');
  assert.ok(card, 'the card is inside the reply');
  assert.ok(card.classList.contains('running'));
  assert.equal(ui.q('.fl-title', card).textContent, 'Fast Learner · Learning');
  assert.equal(ui.q('.fl-detail', card).textContent.trim(), 'Wallpaper took 8 attempts');
  assert.equal(ui.$('toast').textContent, 'Fast Learner is running: Wallpaper took 8 attempts');
});

test('skill, docs and note notices each say what is being saved', async () => {
  const ui = await open();
  ui.deliver({ type: 'user_message', text: 'x', source: 'desktop' });
  ui.deliver(fl({ phase: 'saving', kind: 'skill', detail: 'windows-wallpaper-manager' }));
  ui.deliver(fl({ phase: 'saving', kind: 'docs', detail: 'Added the build command' }));
  ui.deliver(fl({ phase: 'saving', kind: 'notes', detail: 'yt-dlp needs cookies' }));
  const titles = ui.qa('.fl-card .fl-title').map((t) => t.textContent);
  assert.deepEqual(titles, ['Fast Learner · Saving a skill', 'Fast Learner · Updating docs', 'Fast Learner · Taking a note for later']);
  assert.ok(ui.qa('.fl-card').every((c) => !c.classList.contains('running')), 'one-shot notices are not pulsing');
});

test('"done" settles the running card and adds a Finished notice', async () => {
  const ui = await open();
  ui.deliver({ type: 'user_message', text: 'x', source: 'desktop' });
  ui.deliver(fl());
  ui.deliver(fl({ phase: 'done', kind: 'run', detail: 'Saved 1 skill', text: 'Fast Learner finished: Saved 1 skill' }));
  const cards = ui.qa('.fl-card');
  assert.equal(cards.length, 2);
  assert.ok(!cards[0].classList.contains('running'));
  assert.equal(ui.q('.fl-title', cards[0]).textContent, 'Fast Learner · Learned');
  assert.equal(ui.q('.fl-title', cards[1]).textContent, 'Fast Learner · Finished');
});

test('a skills-folder write without an announcement is labelled Skills', async () => {
  const ui = await open();
  ui.deliver({ type: 'user_message', text: 'x', source: 'desktop' });
  ui.deliver(fl({ phase: 'saving', kind: 'skill', detail: 'build-and-run', source: 'path' }));
  assert.equal(ui.q('.fl-title').textContent, 'Skills · Saving a skill');
});

test('notices survive the reply text re-rendering', async () => {
  const ui = await open();
  ui.deliver({ type: 'user_message', text: 'x', source: 'desktop' });
  ui.deliver(fl());
  ui.deliver(reply('Here is the result'));
  await new Promise((r) => setTimeout(r, 60));
  assert.equal(ui.qa('.fl-card').length, 1, 'the card is still there after the text rendered');
});

test('a reply that only produced a notice is not thrown away', async () => {
  const ui = await open();
  ui.deliver({ type: 'user_message', text: 'x', source: 'desktop' });
  ui.deliver(fl({ phase: 'saving', kind: 'notes', detail: 'a note' }));
  ui.deliver(result());
  assert.equal(ui.qa('.fl-card').length, 1);
});

test('when the turn ends, a notice that never said "done" stops pulsing', async () => {
  const ui = await open();
  ui.deliver({ type: 'user_message', text: 'x', source: 'desktop' });
  ui.deliver(fl());
  ui.deliver(reply('ok'));
  ui.deliver(result());
  assert.equal(ui.qa('.fl-card.running').length, 0);
  assert.equal(ui.q('.fl-title').textContent, 'Fast Learner · Learned');
});

test('notice text is shown literally and never as markup', async () => {
  const ui = await open();
  ui.deliver({ type: 'user_message', text: 'x', source: 'desktop' });
  ui.deliver(fl({ detail: '<img src=x onerror="window.__fl=1"><b>hi</b>' }));
  assert.equal(ui.q('.fl-card img'), null);
  assert.equal(ui.q('.fl-card b'), null);
  assert.equal(ui.window.__fl, undefined);
  assert.match(ui.q('.fl-detail').textContent, /<img src=x/);
});

test('the notice stays in the page when a saved chat is replayed', async () => {
  const ui = await open();
  ui.deliver({ type: 'user_message', text: 'x', source: 'desktop' });
  ui.deliver(fl({ phase: 'saving', kind: 'skill', detail: 'replayed-skill' }));
  ui.deliver(result());
  assert.match(ui.document.body.textContent, /replayed-skill/);
});
