import { describe, expect, it } from 'vitest';
import { onboardingChips, type FolderFacts } from '../src/lib/onboarding';

const repo: FolderFacts = { scripts: [], git: { repo: true, branch: 'main' }, hasFiles: true, isProject: true, hasReadme: true };

describe('start-screen buttons', () => {
  it('offers nothing until the PC has said what the folder is', () => {
    expect(onboardingChips(null, 5)).toEqual({ reviewAll: false, newProject: false, inspect: false, git: false, tests: false, testScript: null, overview: false });
  });

  it('a git project with projects on the PC gets the full set', () => {
    const c = onboardingChips(repo, 3);
    expect(c).toMatchObject({ reviewAll: true, inspect: true, git: true, overview: true, newProject: false, tests: false });
  });

  it('no git repository means no Git status', () => {
    expect(onboardingChips({ ...repo, git: { repo: false, branch: null } }, 3).git).toBe(false);
  });

  it('no projects on the PC means no Review all my projects', () => {
    expect(onboardingChips(repo, 0).reviewAll).toBe(false);
    expect(onboardingChips(repo, 1).reviewAll).toBe(true);
  });

  it('a folder that is not a project and has no README gets no Project overview', () => {
    const c = onboardingChips({ scripts: [], git: { repo: false, branch: null }, hasFiles: true, isProject: false, hasReadme: false }, 2);
    expect(c.overview).toBe(false);
    expect(c.inspect).toBe(true);
    expect(onboardingChips({ scripts: [], git: { repo: false, branch: null }, hasFiles: true, isProject: false, hasReadme: true }, 2).overview).toBe(true);
  });

  it('an empty folder offers Start a new project instead of inspecting or explaining', () => {
    const c = onboardingChips({ scripts: [], git: { repo: false, branch: null }, hasFiles: false, isProject: false, hasReadme: false }, 2);
    expect(c).toMatchObject({ newProject: true, inspect: false, overview: false, git: false });
  });

  it('a test script adds Run the tests, naming the script', () => {
    expect(onboardingChips({ ...repo, scripts: [{ name: 'dev', command: 'vite' }, { name: 'test:unit', command: 'vitest' }] }, 1)).toMatchObject({ tests: true, testScript: 'test:unit' });
    expect(onboardingChips({ ...repo, scripts: [{ name: 'latest', command: 'x' }, { name: 'attest', command: 'y' }] }, 1).tests).toBe(false);
  });

  it('an older PC that does not report file facts still gets the basic buttons', () => {
    const c = onboardingChips({ scripts: [], git: { repo: true, branch: 'main' } }, 1);
    expect(c.inspect).toBe(true);
    expect(c.newProject).toBe(false);
    expect(c.git).toBe(true);
  });
});
