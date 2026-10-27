// Mermaid settings for the architecture diagram. This is a copy of wwwroot/diagram-theme.js (the desktop sidebar uses that one);
// a test compares the two so the picture looks the same on the PC and on the phone.

/** Waits (at most 2.5 s) for the diagram font, so labels are measured with the real glyphs and the boxes fit their text. */
export async function loadDiagramFont(): Promise<void> {
  try {
    if (!document.fonts?.load) return;
    await Promise.race([document.fonts.load('400 18px "Plus Jakarta Sans"'), new Promise((r) => setTimeout(r, 2500))]);
  } catch { /* the fallback fonts are fine */ }
}

// Mermaid places each arrow label on its own, so two labels on nearby arrows can land on top of each other. After drawing, any
// labels that overlap are pushed apart vertically (a few passes, always downwards so the result is stable). Needs real layout.
export function separateEdgeLabels(svg: SVGSVGElement): number {
  const labels = Array.from(svg.querySelectorAll('g.edgeLabel')).filter((g) => (g.textContent || '').trim());
  const ctm = svg.getScreenCTM && svg.getScreenCTM();
  const scale = ctm && ctm.d ? Math.abs(ctm.d) : 0;
  if (labels.length < 2 || !scale) return 0;
  let moved = 0;
  for (let pass = 0; pass < 12; pass++) {
    let changed = false;
    const boxes = labels.map((g) => ({ g, r: g.getBoundingClientRect() })).sort((a, b) => a.r.top - b.r.top);
    for (let i = 0; i < boxes.length; i++) for (let j = i + 1; j < boxes.length; j++) {
      const a = boxes[i].r, b = boxes[j].r;
      const w = Math.min(a.right, b.right) - Math.max(a.left, b.left), h = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
      if (w > 1 && h > 1) {
        const dy = (h + 4) / scale;
        const g = boxes[j].g;
        const m = /translate\(\s*(-?[\d.]+)[ ,]+(-?[\d.]+)\s*\)/.exec(g.getAttribute('transform') || '');
        if (!m) continue;
        g.setAttribute('transform', `translate(${m[1]}, ${Number(m[2]) + dy})`);
        changed = true; moved++;
        break;
      }
    }
    if (!changed) break;
  }
  return moved;
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
    flowchart: { htmlLabels: false, curve: 'basis', useMaxWidth: false, padding: 14, nodeSpacing: 60, rankSpacing: 80, wrappingWidth: 220 },
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
