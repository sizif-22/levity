import { describe, expect, it } from 'vitest';
import { sanitizeSvg, svgSize } from '../src/lib/svg';
import { sameFolder } from '../src/lib/diagram';

const wrap = (inner: string, attrs = 'viewBox="0 0 100 50"') => `<svg xmlns="http://www.w3.org/2000/svg" ${attrs}>${inner}</svg>`;

describe('sanitizeSvg', () => {
  it('keeps an ordinary drawing', () => {
    const svg = sanitizeSvg(wrap('<defs><marker id="m"><path d="M0 0"/></marker></defs><g class="node" id="flowchart-n1-0"><rect width="10" height="10" fill="#fff"/><text>Hello</text></g>'))!;
    expect(svg).not.toBeNull();
    expect(svg.querySelector('g.node')?.id).toBe('flowchart-n1-0');
    expect(svg.querySelector('text')?.textContent).toBe('Hello');
    expect(svg.querySelector('marker')).not.toBeNull();
    expect(svg.getAttribute('viewBox')).toBe('0 0 100 50');
  });

  it.each(['script', 'a', 'image', 'use', 'animate', 'set', 'animateTransform', 'iframe', 'object', 'audio', 'video', 'canvas', 'link', 'form'])(
    'removes <%s> and everything inside it',
    (tag) => {
      const svg = sanitizeSvg(wrap(`<${tag} href="https://evil.example/x"><rect id="inside" width="1" height="1"/></${tag}><rect id="outside" width="2" height="2"/>`))!;
      expect(svg.querySelector(tag)).toBeNull();
      expect(svg.querySelector('#inside')).toBeNull();
      expect(svg.querySelector('#outside')).not.toBeNull();
    },
  );

  // A browser's parser lets these tags break out of an SVG, so the drawing is either refused outright or cleaned: never kept as is.
  it.each(['foreignObject', 'embed', 'meta'])('never lets <%s> through (the drawing is refused or the element is removed)', (tag) => {
    const svg = sanitizeSvg(wrap(`<${tag} href="https://evil.example/x"><rect id="inside" width="1" height="1"/></${tag}><rect id="outside" width="2" height="2"/>`));
    if (svg) {
      expect(svg.querySelector(tag)).toBeNull();
      expect(svg.querySelector('#inside')).toBeNull();
    }
  });

  it('removes event-handler attributes everywhere, including on the root', () => {
    const svg = sanitizeSvg(wrap('<g onclick="x()" ONMOUSEOVER="y()"><rect onload="z()" width="1" height="1"/></g>', 'viewBox="0 0 1 1" onload="w()"'))!;
    for (const el of [svg, ...Array.from(svg.querySelectorAll('*'))]) {
      expect(Array.from(el.attributes).some((a) => a.name.toLowerCase().startsWith('on'))).toBe(false);
    }
  });

  it('removes links that leave the document but keeps in-document references', () => {
    const svg = sanitizeSvg(wrap('<g><rect href="https://evil.example/x"/><rect id="a" xlink:href="javascript:alert(1)"/><rect id="b" href="#target"/><rect src="x.png"/></g>'))!;
    const rects = Array.from(svg.querySelectorAll('rect'));
    expect(rects[0].hasAttribute('href')).toBe(false);
    expect(rects[1].hasAttribute('xlink:href')).toBe(false);
    expect(rects[2].getAttribute('href')).toBe('#target');
    expect(rects[3].hasAttribute('src')).toBe(false);
  });

  it('drops stylesheets that load remote or scripted resources and keeps harmless ones', () => {
    const svg = sanitizeSvg(wrap(
      '<style>@import url(https://evil.example/a.css);</style><style>.a{background:url(https://evil.example/t.png)}</style><style>.b{fill:url(javascript:alert(1))}</style><style>.c{filter:url(#blur);fill:red}</style><rect width="1" height="1"/>',
    ))!;
    const texts = Array.from(svg.querySelectorAll('style')).map((s) => s.textContent);
    expect(texts).toEqual(['.c{filter:url(#blur);fill:red}']);
  });

  it('strips style attributes that load remote or scripted resources', () => {
    const svg = sanitizeSvg(wrap('<rect id="a" style="fill:url(https://evil.example/x)"/><rect id="b" style="fill:red"/><rect id="c" style="width:expression(alert(1))"/>'))!;
    expect(svg.querySelector('#a')!.hasAttribute('style')).toBe(false);
    expect(svg.querySelector('#b')!.getAttribute('style')).toBe('fill:red');
    expect(svg.querySelector('#c')!.hasAttribute('style')).toBe(false);
  });

  it.each([
    ['not svg', '<div onclick="alert(1)">hi</div>'],
    ['plain text', 'just text'],
    ['empty', ''],
    ['two roots', '<svg></svg><svg></svg>'],
    ['svg followed by a script', '<svg></svg><script>alert(1)</script>'],
    ['an html page', '<html><body><svg></svg><p>x</p></body></html>'],
    ['a math element', '<math><mi>x</mi></math>'],
  ])('refuses %s', (_name, input) => {
    expect(sanitizeSvg(input)).toBeNull();
  });

  it('ignores text around the svg because only the svg element is ever taken', () => {
    const svg = sanitizeSvg('hello <svg viewBox="0 0 1 1"><g/></svg> trailing')!;
    expect(svg).not.toBeNull();
    expect(svg.outerHTML).not.toContain('hello');
    expect(svg.outerHTML).not.toContain('trailing');
  });

  it('copes with small slips in the markup the way a browser would', () => {
    expect(sanitizeSvg('<svg viewBox="0 0 10 10"><g><rect width="1" height="1"></svg>')).not.toBeNull();
    expect(sanitizeSvg('<?xml version="1.0"?><svg viewBox="0 0 10 10"></svg>')).not.toBeNull();
  });

  it('never runs anything while it works', () => {
    (window as unknown as { __pwned?: number }).__pwned = undefined;
    sanitizeSvg(wrap('<script>window.__pwned=1</script><g onload="window.__pwned=2"/><foreignObject><img src=x onerror="window.__pwned=3"></foreignObject>'));
    expect((window as unknown as { __pwned?: number }).__pwned).toBeUndefined();
  });

  it('hands back elements owned by the target document', () => {
    const other = document.implementation.createHTMLDocument('x');
    expect(sanitizeSvg(wrap('<g/>'), other)!.ownerDocument).toBe(other);
  });
});

describe('svgSize', () => {
  const el = (attrs: string) => new DOMParser().parseFromString(`<svg xmlns="http://www.w3.org/2000/svg" ${attrs}></svg>`, 'image/svg+xml').documentElement;

  it('reads the viewBox, or width and height as a fallback', () => {
    expect(svgSize(el('viewBox="0 0 300 200"'))).toEqual({ w: 300, h: 200 });
    expect(svgSize(el('viewBox="0,0,50,25"'))).toEqual({ w: 50, h: 25 });
    expect(svgSize(el('width="120" height="80"'))).toEqual({ w: 120, h: 80 });
  });

  it.each(['', 'viewBox="0 0 0 0"', 'viewBox="a b c d"', 'viewBox="1 2 3"', 'width="10"'])('gives null for %j', (attrs) => {
    expect(svgSize(el(attrs))).toBeNull();
  });
});

describe('sameFolder', () => {
  it('compares folders the way Windows does', () => {
    expect(sameFolder('C:\\Work\\app', 'c:/work/APP/')).toBe(true);
    expect(sameFolder('C:\\Work\\app\\', 'C:\\Work\\app')).toBe(true);
    expect(sameFolder('C:\\Work\\app', 'C:\\Work\\app2')).toBe(false);
    expect(sameFolder('C:\\Work\\app', 'C:\\Work')).toBe(false);
    expect(sameFolder('', 'C:\\x')).toBe(false);
    expect(sameFolder(undefined, 'C:\\x')).toBe(false);
    expect(sameFolder(null, null)).toBe(false);
  });
});
