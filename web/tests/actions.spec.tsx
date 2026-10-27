import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { GitSheet, RunSheet } from '../src/components/ActionSheets';
import { JobsSheet, type Job } from '../src/components/JobsSheet';
import { gitSummary, JOB_KEYS, type ActionsMsg } from '../src/lib/actions';

afterEach(cleanup);

const actions = (over: Partial<ActionsMsg> = {}): ActionsMsg => ({
  type: 'actions', path: 'C:\\Work\\app', packageManager: 'bun',
  scripts: [{ name: 'dev', command: 'vite' }, { name: 'test:unit', command: 'vitest' }],
  git: { repo: true, branch: 'main' }, ...over,
});

const job = (over: Partial<Job> = {}): Job => ({ id: 3, command: 'bun convex deploy', cwd: 'C:\\Work', state: 'running', exitCode: null, started: new Date().toISOString(), ended: null, interactive: true, tail: '? Project name', ...over });

describe('gitSummary', () => {
  it('says where the repository stands in one line', () => {
    expect(gitSummary(null)).toBe('Checking...');
    expect(gitSummary({ type: 'git_state', ok: false })).toMatch(/Could not read/);
    expect(gitSummary({ type: 'git_state', ok: true, branch: 'feature/login', upstream: 'origin/feature/login', ahead: 2, behind: 1, changes: 4 })).toBe('feature/login · 4 changes · 2 to push · 1 to pull');
    expect(gitSummary({ type: 'git_state', ok: true, branch: 'main', upstream: 'origin/main', ahead: 0, behind: 0, changes: 0 })).toBe('main · clean');
    expect(gitSummary({ type: 'git_state', ok: true, branch: null, upstream: null, changes: 1 })).toBe('detached HEAD · 1 change · no upstream');
  });
});

describe('RunSheet', () => {
  it('lists the scripts and sends only the name of the one that is run', () => {
    const send = vi.fn(); const onClose = vi.fn();
    render(<RunSheet actions={actions()} send={send} onClose={onClose} />);
    expect(screen.getByText(/bun · 2 scripts/)).toBeTruthy();
    fireEvent.click(screen.getAllByText('Run')[1]);
    expect(send).toHaveBeenCalledWith({ type: 'run_script', name: 'test:unit', interactive: false });
    expect(onClose).toHaveBeenCalled();
  });

  it('passes the interactive choice along', () => {
    const send = vi.fn();
    render(<RunSheet actions={actions()} send={send} onClose={() => {}} />);
    fireEvent.click(screen.getByLabelText(/may ask me questions/));
    fireEvent.click(screen.getAllByText('Run')[0]);
    expect(send).toHaveBeenCalledWith({ type: 'run_script', name: 'dev', interactive: true });
  });

  it('shows script names as text, never as markup', () => {
    const { container } = render(<RunSheet actions={actions({ scripts: [{ name: '<img src=x onerror=alert(1)>', command: '<b>x</b>' }] })} send={() => {}} onClose={() => {}} />);
    expect(container.querySelector('img')).toBeNull();
    expect(container.querySelector('.action-text b')).toBeNull();
  });

  it('says so when there are no scripts', () => {
    render(<RunSheet actions={actions({ scripts: [] })} send={() => {}} onClose={() => {}} />);
    expect(screen.getByText('No scripts in this folder.')).toBeTruthy();
  });
});

describe('GitSheet', () => {
  it('asks the PC for the repository state when it opens', () => {
    const send = vi.fn();
    render(<GitSheet state={null} send={send} onClose={() => {}} />);
    expect(send).toHaveBeenCalledWith({ type: 'git_state' });
  });

  it('each simple button sends its action and closes', () => {
    for (const [title, action] of [['Status', 'status'], ['Stage all', 'add'], ['Push', 'push'], ['Pull', 'pull'], ['Fetch', 'fetch'], ['Log', 'log'], ['Diff', 'diff'], ['Branches', 'branches'], ['Stash', 'stash'], ['Stash pop', 'stash-pop']]) {
      const send = vi.fn(); const onClose = vi.fn();
      const { unmount } = render(<GitSheet state={null} send={send} onClose={onClose} />);
      fireEvent.click(screen.getByText(title).closest('button')!);
      expect(send).toHaveBeenLastCalledWith({ type: 'git_run', action });
      expect(onClose).toHaveBeenCalled();
      unmount();
    }
  });

  it('Commit asks for the message first and sends it with the stage choice', () => {
    const send = vi.fn(); const onClose = vi.fn();
    render(<GitSheet state={{ type: 'git_state', ok: true, branch: 'main', upstream: 'origin/main', ahead: 0, behind: 0, changes: 3 }} send={send} onClose={onClose} />);
    fireEvent.click(screen.getByText('Commit…').closest('button')!);
    const commit = screen.getByRole('button', { name: 'Commit' }) as HTMLButtonElement;
    expect(commit.disabled).toBe(true);
    fireEvent.change(screen.getByPlaceholderText('What changed and why?'), { target: { value: '  fix: login loop  ' } });
    expect(commit.disabled).toBe(false);
    fireEvent.click(commit);
    expect(send).toHaveBeenLastCalledWith({ type: 'git_run', action: 'commit', message: 'fix: login loop', stage: true });
    expect(onClose).toHaveBeenCalled();
  });

  it('with nothing changed, staging first is off by default', () => {
    const send = vi.fn();
    render(<GitSheet state={{ type: 'git_state', ok: true, branch: 'main', upstream: 'origin/main', ahead: 1, behind: 0, changes: 0 }} send={send} onClose={() => {}} />);
    fireEvent.click(screen.getByText('Commit…').closest('button')!);
    fireEvent.change(screen.getByPlaceholderText('What changed and why?'), { target: { value: 'msg' } });
    fireEvent.click(screen.getByRole('button', { name: 'Commit' }));
    expect(send).toHaveBeenLastCalledWith({ type: 'git_run', action: 'commit', message: 'msg', stage: false });
  });

  it('a message of only spaces cannot be committed, and Back returns to the buttons', () => {
    render(<GitSheet state={null} send={() => {}} onClose={() => {}} />);
    fireEvent.click(screen.getByText('Commit…').closest('button')!);
    fireEvent.change(screen.getByPlaceholderText('What changed and why?'), { target: { value: '   ' } });
    expect((screen.getByRole('button', { name: 'Commit' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Back' }));
    expect(screen.getByText('Status')).toBeTruthy();
  });

  it('New branch sends the name', () => {
    const send = vi.fn();
    render(<GitSheet state={null} send={send} onClose={() => {}} />);
    fireEvent.click(screen.getByText('New branch…').closest('button')!);
    fireEvent.change(screen.getByPlaceholderText('feature/login'), { target: { value: 'feature/login' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create and switch' }));
    expect(send).toHaveBeenLastCalledWith({ type: 'git_run', action: 'switch-new', name: 'feature/login' });
  });
});

describe('JobsSheet: answering a running command', () => {
  it('shows the answer box and keys only for a running interactive job', () => {
    const { container } = render(<JobsSheet jobs={[job(), job({ id: 4, interactive: false }), job({ id: 5, state: 'done', exitCode: 0 })]} send={() => {}} onClose={() => {}} />);
    expect(container.querySelectorAll('.job-ask')).toHaveLength(1);
  });

  it('sends a typed answer to that job, and clears the box', () => {
    const send = vi.fn();
    render(<JobsSheet jobs={[job()]} send={send} onClose={() => {}} />);
    const box = screen.getByPlaceholderText('Type an answer, then Enter') as HTMLInputElement;
    fireEvent.change(box, { target: { value: 'my-app' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    expect(send).toHaveBeenCalledWith({ type: 'job_input', id: 3, text: 'my-app', enter: true });
    expect(box.value).toBe('');
    fireEvent.change(box, { target: { value: 'again' } });
    fireEvent.keyDown(box, { key: 'Enter' });
    expect(send).toHaveBeenLastCalledWith({ type: 'job_input', id: 3, text: 'again', enter: true });
  });

  it('every key button presses its key in that job', () => {
    const send = vi.fn();
    const { container } = render(<JobsSheet jobs={[job()]} send={send} onClose={() => {}} />);
    const buttons = container.querySelectorAll('.key-btn');
    expect(buttons).toHaveLength(JOB_KEYS.length);
    JOB_KEYS.forEach((k, i) => {
      fireEvent.click(buttons[i]);
      expect(send).toHaveBeenLastCalledWith({ type: 'job_key', id: 3, key: k.key });
    });
  });
});
