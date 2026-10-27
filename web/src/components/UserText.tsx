/** "/cmd$cd ../.." or "/cmd{$cd ../..}" (aliases /cli /run /sh): the wrapper is dimmed and the command word coloured and bold. */
export const CMD_RE = /^(\/(?:cmd|cli|run|sh)(?:\{|\s+|(?=\$))\s*\$?\s*)((?!\$)[^\s};]+)([\s\S]*)$/i;

/** Renders message text; a command message gets its wrapper dimmed and its command word styled. */
export function UserText({ text }: { text: string }) {
  const m = CMD_RE.exec(text);
  if (!m) return <>{text}</>;
  return <><span className="cmd-pre">{m[1]}</span><b className="cmd-word">{m[2]}</b>{m[3]}</>;
}
