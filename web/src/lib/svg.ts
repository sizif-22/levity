/**
 * Makes a drawing that came out of a diagram library safe to put on the page. The compiler already turns every label into plain
 * text and Mermaid runs in strict mode; this is the second wall. The drawing is parsed like a browser would parse it, then
 * rebuilt element by element from an allow-list. Nothing is serialised and parsed again, so there is no second parse to exploit.
 */
const SVG_NS = 'http://www.w3.org/2000/svg';

const DROP = new Set([
  'script', 'foreignobject', 'iframe', 'object', 'embed', 'link', 'meta', 'base', 'form', 'image', 'use', 'a', 'audio', 'video', 'canvas',
  'animate', 'set', 'animatetransform',
]);

const UNSAFE_CSS = /@import|expression\s*\(|javascript:|url\(\s*['"]?\s*(?!#)/i;

/** The sanitised <svg> element, or null if the text is not exactly one SVG. */
export function sanitizeSvg(svgText: string, doc: Document = document): SVGSVGElement | null {
  let parsed: Document;
  try {
    parsed = new DOMParser().parseFromString('<!doctype html><body>' + String(svgText), 'text/html');
  } catch {
    return null;
  }
  const root = parsed.body?.firstElementChild;
  if (!root || parsed.body.children.length !== 1 || root.localName.toLowerCase() !== 'svg' || root.namespaceURI !== SVG_NS) return null;

  const walk = (el: Element) => {
    for (const child of Array.from(el.children)) {
      const tag = child.localName.toLowerCase();
      if (DROP.has(tag)) { child.remove(); continue; }
      if (tag === 'style' && UNSAFE_CSS.test(child.textContent ?? '')) { child.remove(); continue; }
      for (const attr of Array.from(child.attributes)) {
        const name = attr.name.toLowerCase();
        const val = attr.value.trim().toLowerCase();
        if (name.startsWith('on')) child.removeAttribute(attr.name);
        else if ((name === 'href' || name === 'xlink:href' || name === 'src') && !val.startsWith('#')) child.removeAttribute(attr.name);
        else if (name === 'style' && UNSAFE_CSS.test(val)) child.removeAttribute(attr.name);
      }
      walk(child);
    }
  };
  for (const attr of Array.from(root.attributes)) if (attr.name.toLowerCase().startsWith('on')) root.removeAttribute(attr.name);
  walk(root);
  return doc.importNode(root, true) as unknown as SVGSVGElement;
}

/** Natural size of a drawing from its viewBox (or width and height), or null. */
export function svgSize(svg: Element): { w: number; h: number } | null {
  const vb = (svg.getAttribute('viewBox') ?? '').trim().split(/[\s,]+/).map(Number);
  const hasBox = vb.length === 4 && vb.every(Number.isFinite);
  const w = (hasBox && vb[2]) || parseFloat(svg.getAttribute('width') ?? '') || 0;
  const h = (hasBox && vb[3]) || parseFloat(svg.getAttribute('height') ?? '') || 0;
  return w > 0 && h > 0 ? { w, h } : null;
}
