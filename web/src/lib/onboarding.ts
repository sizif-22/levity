import type { ActionsMsg } from './actions';

/** What the PC reports about the folder, beyond scripts and git (older PCs do not send these). */
export type FolderFacts = Partial<Pick<ActionsMsg, 'scripts' | 'git'>> & { hasFiles?: boolean; isProject?: boolean; hasReadme?: boolean };

export interface OnboardingChips {
  reviewAll: boolean;
  newProject: boolean;
  inspect: boolean;
  git: boolean;
  tests: boolean;
  testScript: string | null;
  overview: boolean;
}

/**
 * Which start-screen buttons make sense for this folder. A button that cannot do anything useful is not shown:
 * no git repository means no "Git status"; no projects on the PC means no "Review all my projects"; an empty folder offers
 * "Start a new project" instead of "Inspect workspace". Mirrored by onboardingChips() in wwwroot/actions.js (the PC's sidebar).
 */
export function onboardingChips(d: FolderFacts | null, projectCount: number): OnboardingChips {
  const known = d !== null;   // nothing is offered until the PC has said what the folder is
  const test = d?.scripts?.find((s) => /^test/i.test(s.name));
  return {
    reviewAll: known && projectCount >= 1,
    newProject: known && d!.hasFiles === false,
    inspect: known && d!.hasFiles !== false,
    git: known && !!d!.git?.repo,
    tests: known && !!test,
    testScript: test ? test.name : null,
    overview: known && !!(d!.isProject || d!.hasReadme),
  };
}
