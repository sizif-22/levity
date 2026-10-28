import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { WindowsSheet, type JournalEntryMsg, type ToolsState } from '../src/components/WindowsSheet';

afterEach(cleanup);

const state = (over: Partial<ToolsState> = {}): ToolsState => ({ type: 'tools_state', enabled: true, running: true, count: 34, canElevate: false, elevateNote: null, ...over });
const entry = (over: Partial<JournalEntryMsg> = {}): JournalEntryMsg => ({ id: 'a1', time: '2026-10-28T12:00:00Z', tool: 'settings_set', summary: 'Set the wallpaper', reverted: false, canUndo: true, ...over });

describe('WindowsSheet', () => {
  it('asks the PC for everything it shows when it opens', () => {
    const send = vi.fn();
    render(<WindowsSheet state={null} journal={[]} machine={null} send={send} onClose={() => {}} />);
    expect(send).toHaveBeenCalledWith({ type: 'tools_get' });
    expect(send).toHaveBeenCalledWith({ type: 'journal_list' });
    expect(send).toHaveBeenCalledWith({ type: 'machine_get' });
  });

  it('describes off, on and not-running honestly and points to the PC for the switch', () => {
    const { rerender } = render(<WindowsSheet state={state({ enabled: false, running: false })} journal={[]} machine={null} send={() => {}} onClose={() => {}} />);
    expect(screen.getByText(/Turn it on from Levity on your PC/)).toBeTruthy();
    rerender(<WindowsSheet state={state()} journal={[]} machine={null} send={() => {}} onClose={() => {}} />);
    expect(screen.getByText(/34 tools are available/)).toBeTruthy();
    rerender(<WindowsSheet state={state({ running: false })} journal={[]} machine={null} send={() => {}} onClose={() => {}} />);
    expect(screen.getByText(/not running right now/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Turn on' })).toBeNull();
  });

  it('shows the machine summary as text and refreshes on request', () => {
    const send = vi.fn();
    const { container } = render(<WindowsSheet state={state()} journal={[]} machine={'This PC: Windows 11 <img src=x onerror=alert(1)>'} send={send} onClose={() => {}} />);
    expect(container.querySelector('img')).toBeNull();
    expect(container.textContent).toContain('<img src=x onerror=alert(1)>');
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(send).toHaveBeenLastCalledWith({ type: 'machine_get', refresh: true });
  });

  it('lists changes, offers Revert only where it can work, and sends the id', () => {
    const send = vi.fn();
    render(<WindowsSheet state={state()} journal={[entry(), entry({ id: 'b2', summary: 'End notepad', canUndo: false }), entry({ id: 'c3', summary: 'Disabled Chat', reverted: true })]} machine={null} send={send} onClose={() => {}} />);
    const buttons = screen.getAllByRole('button', { name: 'Revert' });
    expect(buttons).toHaveLength(1);
    fireEvent.click(buttons[0]);
    expect(send).toHaveBeenLastCalledWith({ type: 'journal_revert', id: 'a1' });
    expect(screen.getByText(/· reverted/)).toBeTruthy();
  });

  it('says so when nothing has changed and never renders a summary as markup', () => {
    const { container, rerender } = render(<WindowsSheet state={state()} journal={[]} machine={null} send={() => {}} onClose={() => {}} />);
    expect(screen.getByText('Nothing has been changed yet.')).toBeTruthy();
    rerender(<WindowsSheet state={state()} journal={[entry({ summary: '<b>bold</b><script>alert(1)</script>' })]} machine={null} send={() => {}} onClose={() => {}} />);
    expect(container.querySelector('.action-text b, .action-text script')).toBeNull();
  });
});
