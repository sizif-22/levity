import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { FastLearnerCard, learnLabel } from '../src/components/FastLearnerCard';
import type { Learn } from '../src/lib/chat';

afterEach(cleanup);

const learn = (o: Partial<Learn> = {}): Learn => ({ id: 1, phase: 'running', kind: 'run', detail: 'Wallpaper took 8 attempts', source: 'announce', ...o });

describe('FastLearnerCard', () => {
  it('shows a running notice with its detail and a pulse', () => {
    const { container } = render(<FastLearnerCard l={learn()} />);
    expect(container.querySelector('.fl-title')?.textContent).toBe('Fast Learner · Learning');
    expect(container.querySelector('.fl-detail')?.textContent?.trim()).toBe('Wallpaper took 8 attempts');
    expect(container.querySelector('.fl-card')?.classList.contains('running')).toBe(true);
  });

  it.each([
    [{ phase: 'saving', kind: 'skill' } as Partial<Learn>, 'Saving a skill'],
    [{ phase: 'saving', kind: 'docs' } as Partial<Learn>, 'Updating docs'],
    [{ phase: 'saving', kind: 'notes' } as Partial<Learn>, 'Taking a note for later'],
    [{ phase: 'done', kind: 'run' } as Partial<Learn>, 'Learned'],
    [{ phase: 'done', kind: 'run', final: true } as Partial<Learn>, 'Finished'],
  ])('%j reads "%s"', (over, label) => {
    expect(learnLabel(learn(over))).toBe(label);
  });

  it('labels a skills-folder write as Skills and does not pulse', () => {
    const { container } = render(<FastLearnerCard l={learn({ phase: 'saving', kind: 'skill', source: 'path', detail: 'x' })} />);
    expect(container.querySelector('.fl-title')?.textContent).toBe('Skills · Saving a skill');
    expect(container.querySelector('.fl-card')?.classList.contains('running')).toBe(false);
  });

  it('never renders the detail as markup', () => {
    const { container } = render(<FastLearnerCard l={learn({ detail: '<img src=x onerror=alert(1)>' })} />);
    expect(container.querySelector('img')).toBeNull();
  });
});
