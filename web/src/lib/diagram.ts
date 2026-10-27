/** What the PC sends for an architecture diagram. Nodes are numbered; only the PC ever turns a number into a file path. */
export interface DiagramNode { i: number; label: string; group: string | null; path: string | null; shape: string }
export interface DiagramEdge { from: number; to: number; label: string | null; support: 'verified' | 'documented' | 'inferred'; evidence: string | null }
export interface DiagramGroup { id: string; label: string; tone: number }

export interface DiagramMsg {
  type: 'diagram';
  path: string;
  overview: string;
  groups: DiagramGroup[];
  nodes: DiagramNode[];
  edges: DiagramEdge[];
  mermaid: string;
  mermaidFlat?: string;
  generatedUtc?: string;
  head?: string | null;
  stale?: boolean;
  notes?: string[];
  tokens?: number;
}

export interface DiagramPrepared {
  path: string; files: number; excerpts: number; indexed: number; redactions: number; truncated: boolean; kb: number;
}

export interface DiagramState { path: string; phase: 'ingest' | 'generating' | 'validating' | 'done' | 'error' | 'cancelled'; message: string }

const norm = (p: string) => p.replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase();

/** The PC reports paths with backslashes and no trailing slash; compare folders the way Windows does. */
export const sameFolder = (a?: string | null, b?: string | null): boolean => !!a && !!b && norm(a) === norm(b);

export const SUPPORT_TEXT: Record<DiagramEdge['support'], string> = {
  verified: 'Backed by a reference in the code',
  documented: 'Stated in the README',
  inferred: 'Inferred: not found in the code that was read',
};

/** Tests use Number(g.tone) || 0 as the legend colour slot. */
export const toneClass = (tone: number | undefined) => `diag-sw diag-sw${Number(tone) || 0}`;
