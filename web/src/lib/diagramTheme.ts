// Mermaid settings for the architecture diagram. This is a copy of wwwroot/diagram-theme.js (the desktop sidebar uses that one);
// a test compares the two so the picture looks the same on the PC and on the phone.

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
    // A system font, not the web font the pages use: Mermaid measures every label once while drawing, and a web font that
    // arrives later (or a different weight) makes the text wider than the boxes it was measured for.
    fontFamily: 'system-ui, -apple-system, "Segoe UI", Roboto, sans-serif',
    flowchart: { htmlLabels: false, curve: 'basis', useMaxWidth: false, padding: 12, nodeSpacing: 40, rankSpacing: 48, wrappingWidth: 180 },
    themeVariables: {
      background: 'transparent',
      fontSize: '14px',
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
