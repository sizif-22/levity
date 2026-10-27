import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { JobsSheet, type Job } from '../src/components/JobsSheet';
import { SessionsSheet, type SessionsMsg } from '../src/components/SessionsSheet';
import { CMD_RE, UserText } from '../src/components/UserText';

afterEach(cleanup);

describe('UserText', () => {
  it('styles the command word and dims the -b flag after the brace', () => {
    const { container } = render(<UserText text="/cmd{$bun run dev} -b" />);
    expect(container.querySelector('.cmd-word')?.textContent).toBe('bun');
    expect(container.querySelector('.cmd-flag')?.textContent).toBe('-b');
    expect(container.textContent).toBe('/cmd{$bun run dev} -b');
  });

  it('dims -b inside the braces too', () => {
    const { container } = render(<UserText text="/cmd{$npm start -b}" />);
    expect(container.querySelector('.cmd-flag')?.textContent).toBe('-b');
  });

  it('leaves ordinary text alone and never interprets it as HTML', () => {
    const { container } = render(<UserText text={'<img src=x onerror=alert(1)> hello'} />);
    expect(container.querySelector('img')).toBeNull();
    expect(container.querySelector('.cmd-word')).toBeNull();
    expect(container.textContent).toBe('<img src=x onerror=alert(1)> hello');
  });

  it.each(['/cmd{$ls}', '/cli ls', '/run{dir}', '/SH{$whoami}'])('%s is a command message', (t) => {
    expect(CMD_RE.test(t)).toBe(true);
  });

  it.each(['hello', '/plan x', '/cmdx{ls}', '/cmd'])('%s is not a command message', (t) => {
    expect(CMD_RE.test(t)).toBe(false);
  });
});

const job = (over: Partial<Job> = {}): Job => ({
  id: 1, command: 'bun run dev', cwd: 'C:\\p', state: 'running', exitCode: null,
  started: new Date(Date.now() - 5000).toISOString(), ended: null, tail: 'ready on :3000', ...over,
});

describe('JobsSheet', () => {
  it('shows a running job with its output and a Stop button that posts job_stop', () => {
    const send = vi.fn();
    render(<JobsSheet jobs={[job()]} send={send} onClose={() => {}} />);
    expect(screen.getByText(/#1 bun run dev/)).toBeTruthy();
    expect(screen.getByText('ready on :3000')).toBeTruthy();
    expect(screen.getByText(/1 running/)).toBeTruthy();
    fireEvent.click(screen.getByText('Stop'));
    expect(send).toHaveBeenCalledWith({ type: 'job_stop', id: 1 });
  });

  it('a finished job has no Stop button and shows its exit code', () => {
    render(<JobsSheet jobs={[job({ state: 'failed', exitCode: 2, ended: new Date().toISOString() })]} send={() => {}} onClose={() => {}} />);
    expect(screen.queryByText('Stop')).toBeNull();
    expect(screen.getByText(/failed \(exit 2\)/)).toBeTruthy();
  });

  it('Clear finished posts job_clear and the empty state explains how to start a job', () => {
    const send = vi.fn();
    render(<JobsSheet jobs={[]} send={send} onClose={() => {}} />);
    expect(screen.getByText(/Nothing running/)).toBeTruthy();
    fireEvent.click(screen.getByText('Clear finished'));
    expect(send).toHaveBeenCalledWith({ type: 'job_clear' });
  });

  it('shows newest jobs first and never renders output as markup', () => {
    render(<JobsSheet jobs={[job({ id: 1 }), job({ id: 2, command: 'second', tail: '<b>bold?</b>' })]} send={() => {}} onClose={() => {}} />);
    const names = screen.getAllByText(/^#\d/).map((e) => e.textContent);
    expect(names[0]).toMatch(/#2/);
    expect(screen.getByText('<b>bold?</b>')).toBeTruthy();
  });

  it('Close calls onClose', () => {
    const onClose = vi.fn();
    render(<JobsSheet jobs={[]} send={() => {}} onClose={onClose} />);
    fireEvent.click(screen.getByTitle('Close'));
    expect(onClose).toHaveBeenCalled();
  });
});

const sessions = (current = 'b'): SessionsMsg => ({
  current,
  sessions: [
    { id: 'a', title: 'Fix the login bug', cwd: 'D:\\Work\\app', updated: new Date().toISOString(), messages: 3 },
    { id: 'b', title: 'Wallpaper thing', cwd: 'C:\\Users\\me', updated: new Date(Date.now() - 86_400_000 * 3).toISOString(), messages: 1 },
  ],
});

describe('SessionsSheet', () => {
  it('lists saved chats with folder, message count and age', () => {
    render(<SessionsSheet data={sessions()} send={() => {}} onClose={() => {}} />);
    expect(screen.getByText('Fix the login bug')).toBeTruthy();
    expect(screen.getByText(/app · 3 messages/)).toBeTruthy();
    expect(screen.getByText(/me · 1 message ·/)).toBeTruthy();
    expect(screen.getByText(/2 saved on your PC/)).toBeTruthy();
  });

  it('opening a chat posts session_open and closes the sheet; the open chat cannot be reopened', () => {
    const send = vi.fn();
    const onClose = vi.fn();
    render(<SessionsSheet data={sessions('b')} send={send} onClose={onClose} />);
    expect((screen.getByText('Open now') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByText('Open'));
    expect(send).toHaveBeenCalledWith({ type: 'session_open', id: 'a' });
    expect(onClose).toHaveBeenCalled();
  });

  it('deleting needs a second tap', () => {
    const send = vi.fn();
    render(<SessionsSheet data={sessions('b')} send={send} onClose={() => {}} />);
    const del = screen.getAllByText('Delete')[0];
    fireEvent.click(del);
    expect(send).not.toHaveBeenCalled();
    fireEvent.click(screen.getByText('Sure?'));
    expect(send).toHaveBeenCalledWith({ type: 'session_delete', id: 'a' });
  });

  it('an empty or not-yet-loaded list says so', () => {
    render(<SessionsSheet data={null} send={() => {}} onClose={() => {}} />);
    expect(screen.getByText(/No saved chats yet/)).toBeTruthy();
  });
});
