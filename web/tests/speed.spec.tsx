import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { SpeedSheet, fmtSecs, fmtTokens, type SpeedMsg } from '../src/components/SpeedSheet';

afterEach(cleanup);

const data = (over: Partial<SpeedMsg> = {}): SpeedMsg => ({
  type: 'speed',
  current: {
    turns: 5, seconds: 769, inputTokens: 327392, outputTokens: 25158, thinkingTokens: 11274,
    tools: [{ name: 'run_command', count: 29, seconds: 235.6, max: 71.3 }],
    slow: [{ tool: 'run_command', seconds: 71.3, what: 'Get-Process' }],
    findings: ['29 shell commands took 8.1 seconds each.'],
  },
  recent: [{ title: 'wallpaper', turns: 5, seconds: 769, inputTokens: 327392 }, { title: 'empty', turns: 0, seconds: 0, inputTokens: 0 }],
  footprint: { totalMb: 1450, files: 18413, truncated: false, folders: [{ name: 'antigravity-ide', mb: 900, files: 6464 }], notes: ['looks like a backup'] },
  ...over,
});

describe('formatting', () => {
  it('shows seconds under a minute and a half, minutes above, and thousands of tokens as k', () => {
    expect(fmtSecs(45)).toBe('45 s');
    expect(fmtSecs(769)).toBe('12 min 49 s');
    expect(fmtTokens(950)).toBe('950');
    expect(fmtTokens(25158)).toBe('25.2k');
    expect(fmtTokens(327392)).toBe('327k');
  });
});

describe('SpeedSheet', () => {
  it('asks the PC for the report when it opens and shows a waiting text until it arrives', () => {
    const send = vi.fn();
    render(<SpeedSheet data={null} send={send} onClose={() => {}} />);
    expect(send).toHaveBeenCalledWith({ type: 'speed_get' });
    expect(screen.getByText('Working it out...')).toBeTruthy();
    expect(screen.getByText('Measuring...')).toBeTruthy();
  });

  it('shows the summary, findings, slow steps, tool times, recent chats and folder sizes', () => {
    render(<SpeedSheet data={data()} send={() => {}} onClose={() => {}} />);
    expect(screen.getByText(/5 answers · 12 min 49 s · 327k tokens in, 25\.2k out/)).toBeTruthy();
    expect(screen.getByText(/29 shell commands took/)).toBeTruthy();
    expect(screen.getByText(/71 s · run_command · Get-Process/)).toBeTruthy();
    expect(screen.getByText(/run_command: 29 calls, 3 min 56 s in total/)).toBeTruthy();
    expect(screen.getByText(/wallpaper: 5 answers/)).toBeTruthy();
    expect(screen.queryByText(/empty: 0 answers/)).toBeNull();
    expect(screen.getByText(/1450 MB in 18,413 files/)).toBeTruthy();
    expect(screen.getByText(/antigravity-ide: 900 MB/)).toBeTruthy();
  });

  it('says so when there is nothing to measure and never renders text as markup', () => {
    const empty = data({ current: { turns: 0, seconds: 0, inputTokens: 0, outputTokens: 0, thinkingTokens: 0, tools: [], slow: [], findings: [] }, recent: [] });
    const { container, rerender } = render(<SpeedSheet data={empty} send={() => {}} onClose={() => {}} />);
    expect(screen.getByText('Nothing to measure yet in this conversation.')).toBeTruthy();
    expect(container.querySelector('.speed-list li')).not.toBeNull();   // the folder section still shows
    rerender(<SpeedSheet data={data({ current: { ...data().current, findings: ['<b>x</b><script>alert(1)</script>'] } })} send={() => {}} onClose={() => {}} />);
    expect(container.querySelector('.speed-list b, .speed-list script')).toBeNull();
  });
});
