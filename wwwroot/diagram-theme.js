// Mermaid settings for the architecture diagram, in one place so the picture reads the same everywhere.
// The compiler gives every component its own fill and text colour; everything else (group boxes, titles, arrows, arrow labels)
// comes from here, in a light and a dark variant that match the sidebar. The phone app keeps an identical copy in
// web/src/lib/diagramTheme.ts, and a test fails if the two ever differ.

function diagramMermaidConfig(dark) {
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
    fontFamily: 'Montserrat, "Segoe UI", ui-sans-serif, system-ui, sans-serif',
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
