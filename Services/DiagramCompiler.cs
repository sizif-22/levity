using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AntigravitySidebar.Services
{
    /// <param name="Index">1-based position; the UI sends this back when a node is clicked (never a path).</param>
    /// <param name="MermaidId">The id used in the Mermaid source (n1, n2, ...). Generated here, never taken from the model.</param>
    public sealed record CompiledNode(int Index, string Id, string Label, string? Group, string? Path, string MermaidId);

    public sealed record CompiledDiagram(string Mermaid, IReadOnlyList<CompiledNode> Nodes);

    /// <summary>
    /// Turns a validated graph into Mermaid source. Ids are generated, every piece of text is entity-escaped, and the output
    /// contains no click, link, style or comment directives, so nothing the model wrote can act as Mermaid syntax.
    /// </summary>
    public static class DiagramCompiler
    {
        private static readonly string[] Tones =
        {
            "fill:#dbeafe,stroke:#2563eb,stroke-width:1.5px,color:#172554",
            "fill:#fef3c7,stroke:#d97706,stroke-width:1.5px,color:#78350f",
            "fill:#dcfce7,stroke:#16a34a,stroke-width:1.5px,color:#14532d",
            "fill:#ffe4e6,stroke:#e11d48,stroke-width:1.5px,color:#881337",
            "fill:#e0e7ff,stroke:#4f46e5,stroke-width:1.5px,color:#312e81",
            "fill:#ccfbf1,stroke:#0f766e,stroke-width:1.5px,color:#134e4a",
        };
        private const string NeutralTone = "fill:#f8fafc,stroke:#334155,stroke-width:1.5px,color:#0f172a";

        public static CompiledDiagram Compile(DiagramGraph graph)
        {
            var nodes = graph.Nodes
                .Select((n, i) => new CompiledNode(i + 1, n.Id, n.Label, n.Group, n.Path, "n" + (i + 1)))
                .ToList();
            var mermaidId = nodes.ToDictionary(n => n.Id, n => n.MermaidId);
            var groupIndex = graph.Groups.Select((g, i) => (g.Id, i)).ToDictionary(x => x.Id, x => x.i);
            var shapeOf = graph.Nodes.ToDictionary(n => n.Id, n => n.Shape);

            var sb = new StringBuilder("flowchart TD\n");
            var toneMembers = new Dictionary<int, List<string>>(); // tone index -> mermaid ids; -1 = neutral

            void AddNode(CompiledNode n, string indent)
            {
                sb.Append(indent).Append(Render(n.MermaidId, n.Label, shapeOf[n.Id])).Append('\n');
                int tone = n.Group != null && groupIndex.TryGetValue(n.Group, out int gi) ? gi % Tones.Length : -1;
                if (!toneMembers.TryGetValue(tone, out var list)) toneMembers[tone] = list = new List<string>();
                list.Add(n.MermaidId);
            }

            for (int g = 0; g < graph.Groups.Count; g++)
            {
                var members = nodes.Where(n => n.Group == graph.Groups[g].Id).ToList();
                if (members.Count == 0) continue;
                sb.Append("\n  subgraph g").Append(g + 1).Append("[\"").Append(DiagramText.EscapeMermaidLabel(graph.Groups[g].Label)).Append("\"]\n");
                foreach (var n in members) AddNode(n, "    ");
                sb.Append("  end\n");
            }

            var loose = nodes.Where(n => n.Group == null).ToList();
            if (loose.Count > 0)
            {
                sb.Append('\n');
                foreach (var n in loose) AddNode(n, "  ");
            }

            if (graph.Edges.Count > 0) sb.Append('\n');
            foreach (var e in graph.Edges)
            {
                string arrow = e.Support == EdgeSupport.Inferred ? "-.->" : "-->";
                string label = string.IsNullOrEmpty(e.Label) ? "" : "|\"" + DiagramText.EscapeMermaidLabel(e.Label) + "\"|";
                sb.Append("  ").Append(mermaidId[e.From]).Append(' ').Append(arrow).Append(label).Append(' ').Append(mermaidId[e.To]).Append('\n');
            }

            sb.Append('\n');
            for (int t = 0; t < Tones.Length; t++) sb.Append("  classDef tone").Append(t).Append(' ').Append(Tones[t]).Append('\n');
            sb.Append("  classDef toneNeutral ").Append(NeutralTone).Append('\n');
            foreach (var (tone, ids) in toneMembers.OrderBy(kv => kv.Key))
                sb.Append("  class ").Append(string.Join(",", ids)).Append(tone < 0 ? " toneNeutral" : " tone" + tone).Append('\n');

            return new CompiledDiagram(sb.ToString().TrimEnd() + "\n", nodes);
        }

        private static string Render(string id, string label, string shape)
        {
            string text = DiagramText.EscapeMermaidLabel(label);
            return shape switch
            {
                "database" => $"{id}[(\"{text}\")]",
                "circle" => $"{id}((\"{text}\"))",
                "hexagon" => $"{id}{{{{\"{text}\"}}}}",
                _ => $"{id}[\"{text}\"]",
            };
        }
    }
}
