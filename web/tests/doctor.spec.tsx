import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { DoctorSheet, type DoctorMsg } from '../src/components/DoctorSheet';

afterEach(cleanup);

const data = (): DoctorMsg => ({
  type: 'doctor', time: 't', skipped: ['event log'],
  findings: [
    { id: 'disk-C:', severity: 'critical', title: 'Drive C: is almost full', detail: '4 GB free', fixes: [{ index: 0, kind: 'tool', label: 'Find what is using it', changes: false }, { index: 1, kind: 'prompt', label: 'Help me free space', changes: false }] },
    { id: 'path', severity: 'info', title: 'PATH has problems', detail: 'x', fixes: [{ index: 0, kind: 'tool', label: 'Clean the user PATH', changes: true }] },
  ],
});

describe('DoctorSheet', () => {
  it('scans when it opens and says so until the report arrives', () => {
    const send = vi.fn();
    render(<DoctorSheet data={null} result={null} send={send} onClose={() => {}} onScan={() => {}} />);
    expect(send).toHaveBeenCalledWith({ type: 'doctor_scan' });
    expect(screen.getByText('Scanning...')).toBeTruthy();
  });

  it('shows findings with severity and fix buttons; a fix sends the finding id and index', () => {
    const send = vi.fn();
    render(<DoctorSheet data={data()} result={null} send={send} onClose={() => {}} onScan={() => {}} />);
    expect(screen.getByText('Fix now')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Help me free space' }));
    expect(send).toHaveBeenLastCalledWith({ type: 'doctor_fix', id: 'disk-C:', index: 1 });
  });

  it('never offers a change to Windows from the phone', () => {
    const send = vi.fn();
    render(<DoctorSheet data={data()} result={null} send={send} onClose={() => {}} onScan={() => {}} />);
    expect(screen.queryByRole('button', { name: 'Clean the user PATH' })).toBeNull();
    expect(screen.getByText(/Clean the user PATH: do this from the PC/)).toBeTruthy();
  });

  it('shows a tool answer under its finding as text and names what could not be checked', () => {
    const { container } = render(<DoctorSheet data={data()} result={{ type: 'doctor_result', id: 'disk-C:', index: 0, ok: true, text: '<img src=x onerror=alert(1)>' }} send={() => {}} onClose={() => {}} onScan={() => {}} />);
    expect(container.querySelector('img')).toBeNull();
    expect(container.querySelector('pre')?.textContent).toContain('<img');
    expect(screen.getByText(/Could not check: event log/)).toBeTruthy();
  });
});
