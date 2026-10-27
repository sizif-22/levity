// Mermaid settings for the architecture diagram. This is a copy of wwwroot/diagram-theme.js (the desktop sidebar uses that one);
// a test compares the two so the picture looks the same on the PC and on the phone.

/** Waits (at most 2.5 s) for the diagram font, so labels are measured with the real glyphs and the boxes fit their text. */
export async function loadDiagramFont(): Promise<void> {
  try {
    if (!document.fonts?.load) return;
    await Promise.race([document.fonts.load('400 18px "Plus Jakarta Sans"'), new Promise((r) => setTimeout(r, 2500))]);
  } catch { /* the fallback fonts are fine */ }
}

export function diagramMermaidConfig(dark: boolean): Record<string, unknown> {
  const c = dark
    ? { text: '#e5e5e5', line: '#a3a3a3', clusterFill: '#363636', clusterBorder: '#5a5a5a', labelFill: '#2b2b2b' }
    : { text: '#262626', line: '#525252', clusterFill: '#ececec', clusterBorder: '#a3a3a3', labelFill: '#fafafa' };
  return {
    startOnLoad: false,
    securityLevel: 'strict',
    // A directive hidden in a label (%%{init: ...}%%) must not be able to change any of these.
    secure: ['secure', 'securityLevel', 'startOnLoad', 'maxTextSize', 'htmlLabels', 'flowchart'],
    theme: 'base',
    htmlLabels: false,
    // Mermaid measures every label once while drawing, so the font must already be loaded then (see loadDiagramFont) and the
    // weight must not change afterwards. The system fonts are only the fallback if it cannot be loaded.
    fontFamily: '"Plus Jakarta Sans", system-ui, -apple-system, "Segoe UI", Roboto, sans-serif',
    flowchart: { htmlLabels: false, curve: 'basis', useMaxWidth: false, padding: 12, nodeSpacing: 40, rankSpacing: 48, wrappingWidth: 220 },
    themeVariables: {
      background: 'transparent',
      fontSize: '18px',
      textColor: c.text,
      titleColor: c.text,
      lineColor: c.line,
      clusterBkg: c.clusterFill,
      clusterBorder: c.clusterBorder,
      edgeLabelBackground: c.labelFill,
      mainBkg: c.clusterFill,
      secondaryColor: c.clusterFill,
      tertiaryColor: c.clusterFill,
      primaryTextColor: c.text,
      secondaryTextColor: c.text,
      tertiaryTextColor: c.text,
    },
  };
}
