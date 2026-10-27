// The phone keeps its own copy of the desktop's Mermaid theme. They must never drift apart.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { diagramMermaidConfig } from '../src/lib/diagramTheme';

const desktopSource = readFileSync(join(__dirname, '..', '..', 'wwwroot', 'diagram-theme.js'), 'utf8');
const desktop = new Function(`${desktopSource}\nreturn diagramMermaidConfig;`)() as (dark: boolean) => unknown;

describe('diagram theme', () => {
  it.each([true, false])('is identical on the phone and the desktop (dark=%s)', (dark) => {
    expect(JSON.parse(JSON.stringify(diagramMermaidConfig(dark)))).toEqual(JSON.parse(JSON.stringify(desktop(dark))));
  });

  it('keeps Mermaid locked down and gives groups, titles and arrow labels explicit colours', () => {
    const cfg = diagramMermaidConfig(true) as { securityLevel: string; secure: string[]; themeVariables: Record<string, string>; fontFamily: string };
    expect(cfg.securityLevel).toBe('strict');
    for (const key of ['secure', 'securityLevel', 'startOnLoad', 'maxTextSize', 'htmlLabels', 'flowchart']) expect(cfg.secure).toContain(key);
    for (const key of ['clusterBkg', 'clusterBorder', 'titleColor', 'edgeLabelBackground', 'textColor']) expect(cfg.themeVariables[key]).toMatch(/^#/);
    expect(cfg.fontFamily).not.toBe('inherit');
  });
});
