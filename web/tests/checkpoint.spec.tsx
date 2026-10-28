import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { CheckpointBar, checkpointSummary, type CheckpointMsg } from '../src/components/CheckpointBar';

afterEach(cleanup);

const msg = (over: Partial<CheckpointMsg> = {}): CheckpointMsg => ({
  type: 'checkpoint', id: 'c1', added: 1, modified: 2, deleted: 1, insertions: 12, deletions: 4,
  changes: [{ status: 'M', path: 'src\\a.ts' }, { status: 'A', path: 'new.txt' }], more: 0, ...over,
});

describe('checkpointSummary', () => {
  it('counts files and lines and uses the singular for one file', () => {
    expect(checkpointSummary(msg())).toBe('That answer changed 4 files (2 changed, 1 new, 1 deleted; +12 −4)');
    expect(checkpointSummary(msg({ added: 0, modified: 1, deleted: 0, insertions: 1, deletions: 0 }))).toBe('That answer changed 1 file (1 changed; +1 −0)');
  });
});

describe('CheckpointBar', () => {
  it('shows nothing without a checkpoint or when nothing changed', () => {
    const { container, rerender } = render(<CheckpointBar data={null} send={() => {}} onDismiss={() => {}} />);
    expect(container.firstChild).toBeNull();
    rerender(<CheckpointBar data={{ type: 'checkpoint', none: true }} send={() => {}} onDismiss={() => {}} />);
    expect(container.firstChild).toBeNull();
  });

  it('View toggles the file list, with new, changed and deleted labels and a "more" line', () => {
    render(<CheckpointBar data={msg({ more: 5 })} send={() => {}} onDismiss={() => {}} />);
    expect(screen.queryByText(/new\s+new\.txt/)).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'View' }));
    expect(screen.getByText(/new\s+new\.txt/)).toBeTruthy();
    expect(screen.getByText(/changed\s+src\\a\.ts/)).toBeTruthy();
    expect(screen.getByText('... and 5 more')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'View' }));
    expect(screen.queryByText(/new\s+new\.txt/)).toBeNull();
  });

  it('Undo needs a second press and then sends the checkpoint id', () => {
    const send = vi.fn();
    render(<CheckpointBar data={msg()} send={send} onDismiss={() => {}} />);
    fireEvent.click(screen.getByRole('button', { name: 'Undo' }));
    expect(send).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Sure?' }));
    expect(send).toHaveBeenCalledWith({ type: 'checkpoint_undo', id: 'c1' });
    expect(screen.getByRole('button', { name: 'Undo' })).toBeTruthy();
  });

  it('Dismiss calls back, and a new checkpoint resets the open list and the armed Undo', () => {
    const dismiss = vi.fn();
    const { rerender } = render(<CheckpointBar data={msg()} send={() => {}} onDismiss={dismiss} />);
    fireEvent.click(screen.getByRole('button', { name: 'View' }));
    fireEvent.click(screen.getByRole('button', { name: 'Undo' }));
    rerender(<CheckpointBar data={msg({ id: 'c2' })} send={() => {}} onDismiss={dismiss} />);
    expect(screen.queryByText(/new\s+new\.txt/)).toBeNull();
    expect(screen.getByRole('button', { name: 'Undo' })).toBeTruthy();
    fireEvent.click(screen.getByTitle('Dismiss'));
    expect(dismiss).toHaveBeenCalled();
  });

  it('never renders file names as markup', () => {
    const { container } = render(<CheckpointBar data={msg({ changes: [{ status: 'A', path: '<img src=x onerror=alert(1)>.txt' }] })} send={() => {}} onDismiss={() => {}} />);
    fireEvent.click(screen.getByRole('button', { name: 'View' }));
    expect(container.querySelector('img')).toBeNull();
  });
});
