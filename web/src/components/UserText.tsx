/** "/cmd{$cd ../..}" (aliases /cli /run /sh): the wrapper is dimmed and the command word coloured and bold. */
export const CMD_RE = /^(\/(?:cmd|cli|run|sh)(?:\{|\s+)\s*\$?\s*)([^\s}]+)([\s\S]*)$/i;

/** Renders message text; a command message gets its command word and "-b" background flag styled. */
export function UserText({ text }: { text: string }) {
  const m = CMD_RE.exec(text);
  if (!m) return <>{text}</>;
  const f = /^([\s\S]*?\s|)(-b)(\}?\s*)$/.exec(m[3]); // the background flag reads as secondary
  return <><span className="cmd-pre">{m[1]}</span><b className="cmd-word">{m[2]}</b>{f ? <>{f[1]}<span className="cmd-flag">{f[2]}</span>{f[3]}</> : m[3]}</>;
}
