import { memo, useMemo } from 'react';
import { Marked } from 'marked';
import DOMPurify from 'dompurify';
import hljs from 'highlight.js/lib/common';

const escapeHtml = (s: string) =>
  s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

const marked = new Marked({
  gfm: true,
  breaks: true,
  renderer: {
    code(code: string, infostring: string | undefined) {
      const lang = (infostring ?? '').match(/\S*/)?.[0] ?? '';
      const html = lang && hljs.getLanguage(lang)
        ? hljs.highlight(code, { language: lang }).value
        : hljs.highlightAuto(code).value;
      return `<pre><div class="code-toolbar"><span>${escapeHtml(lang || 'code')}</span><button class="copy-code-btn" data-copy>Copy</button></div><code class="hljs">${html}</code></pre>`;
    },
  },
});

DOMPurify.addHook('afterSanitizeAttributes', (node) => {
  if (node.tagName === 'A') {
    node.setAttribute('target', '_blank');
    node.setAttribute('rel', 'noopener noreferrer');
  }
});

/** Agent output may echo web content, so it is always sanitized before it reaches the DOM. */
export const Markdown = memo(function Markdown({ text }: { text: string }) {
  const html = useMemo(
    () => DOMPurify.sanitize(marked.parse(text) as string, { ADD_ATTR: ['data-copy'], FORBID_TAGS: ['style', 'form', 'iframe'] }),
    [text],
  );

  return (
    <div
      onClick={(e) => {
        const btn = (e.target as HTMLElement).closest('[data-copy]');
        if (!btn) return;
        const code = btn.closest('pre')?.querySelector('code')?.textContent ?? '';
        void navigator.clipboard?.writeText(code);
        btn.textContent = 'Copied';
        window.setTimeout(() => { btn.textContent = 'Copy'; }, 1500);
      }}
      dangerouslySetInnerHTML={{ __html: html }}
    />
  );
});
