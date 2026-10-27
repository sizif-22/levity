using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AntigravitySidebar.Services
{
    public sealed record DiagramIssue(string Where, string Message);

    public sealed class DiagramValidation
    {
        public DiagramGraph? Graph { get; init; }
        /// <summary>Problems that make the output unusable (shown to the model when it retries).</summary>
        public List<DiagramIssue> Issues { get; } = new();
        /// <summary>Things that were quietly fixed or removed (a link to a file that does not exist, a duplicate edge).</summary>
        public List<string> Notes { get; } = new();
        public bool Ok => Graph != null && Issues.Count == 0;
    }

    /// <summary>
    /// The only way to obtain a <see cref="DiagramGraph"/>. Treats model output as hostile: parses strictly, enforces limits,
    /// cleans text, checks every path against the real project, and decides edge support from Levity's own reference index.
    /// </summary>
    public static class DiagramValidator
    {
        private static readonly Regex IdPattern = new(@"^[a-z][a-z0-9_]{0,31}$", RegexOptions.Compiled);
        private static readonly string[] Shapes = { "box", "database", "circle", "hexagon" };

        /// <summary>Validates fresh model output against the project.</summary>
        public static DiagramValidation Validate(string? raw, DiagramContext ctx) => Run(raw, ctx, stored: false);

        /// <summary>Validates a graph read back from disk. Never trusts the file: same limits and cleaning, stored support values, no project needed.</summary>
        public static DiagramValidation ValidateStored(string? json) => Run(json, null, stored: true);

        private static DiagramValidation Run(string? raw, DiagramContext? ctx, bool stored)
        {
            var result = new DiagramValidation();
            string? json = ExtractJson(raw);
            if (json == null) { result.Issues.Add(new("output", "The output was not valid JSON. Return only the JSON object.")); return Wrap(result, null); }

            JsonDocument doc;
            try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); }
            catch (JsonException) { result.Issues.Add(new("output", "The output was not valid JSON. Return only the JSON object.")); return Wrap(result, null); }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { result.Issues.Add(new("output", "The top level must be a JSON object.")); return Wrap(result, null); }

                string overview = DiagramText.Limit(Str(root, "overview", result, "overview"), DiagramLimits.MaxOverview);

                var groups = ReadGroups(root, result);
                var groupIds = groups.Select(g => g.Id).ToHashSet();
                var nodes = ReadNodes(root, groupIds, ctx, result);
                var nodeById = nodes.ToDictionary(n => n.Id);
                var edges = ReadEdges(root, nodeById, ctx, stored, result);

                if (nodes.Count == 0) result.Issues.Add(new("nodes", "There must be at least one node."));
                if (result.Issues.Count > 0) return Wrap(result, null);

                var graph = new DiagramGraph(overview, groups, nodes, edges);
                return Wrap(result, graph);
            }
        }

        private static DiagramValidation Wrap(DiagramValidation v, DiagramGraph? graph)
        {
            var wrapped = new DiagramValidation { Graph = v.Issues.Count == 0 ? graph : null };
            wrapped.Issues.AddRange(v.Issues);
            wrapped.Notes.AddRange(v.Notes);
            return wrapped;
        }

        private static List<DiagramGroup> ReadGroups(JsonElement root, DiagramValidation res)
        {
            var list = new List<DiagramGroup>();
            if (!root.TryGetProperty("groups", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                res.Issues.Add(new("groups", "groups must be an array (it may be empty)."));
                return list;
            }
            if (arr.GetArrayLength() > DiagramLimits.MaxGroups) res.Issues.Add(new("groups", $"At most {DiagramLimits.MaxGroups} groups are allowed."));
            var seen = new HashSet<string>();
            int i = 0;
            foreach (var g in arr.EnumerateArray().Take(DiagramLimits.MaxGroups * 2))
            {
                string where = $"groups[{i++}]";
                if (g.ValueKind != JsonValueKind.Object) { res.Issues.Add(new(where, "must be an object.")); continue; }
                string? id = Str(g, "id", res, where + ".id");
                string label = DiagramText.Limit(Str(g, "label", res, where + ".label"), DiagramLimits.MaxLabel);
                if (id == null || !IdPattern.IsMatch(id)) { res.Issues.Add(new(where + ".id", "must match ^[a-z][a-z0-9_]{0,31}$.")); continue; }
                if (!seen.Add(id)) { res.Issues.Add(new(where + ".id", $"duplicate group id '{id}'.")); continue; }
                if (label.Length == 0) { res.Issues.Add(new(where + ".label", "must contain visible text.")); continue; }
                list.Add(new DiagramGroup(id, label));
            }
            return list;
        }

        private static List<DiagramNode> ReadNodes(JsonElement root, HashSet<string> groupIds, DiagramContext? ctx, DiagramValidation res)
        {
            var list = new List<DiagramNode>();
            if (!root.TryGetProperty("nodes", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                res.Issues.Add(new("nodes", "nodes must be an array."));
                return list;
            }
            if (arr.GetArrayLength() > DiagramLimits.MaxNodes) res.Issues.Add(new("nodes", $"At most {DiagramLimits.MaxNodes} nodes are allowed; merge related components."));
            var seen = new HashSet<string>();
            int i = 0;
            foreach (var n in arr.EnumerateArray().Take(DiagramLimits.MaxNodes * 2))
            {
                string where = $"nodes[{i++}]";
                if (n.ValueKind != JsonValueKind.Object) { res.Issues.Add(new(where, "must be an object.")); continue; }
                string? id = Str(n, "id", res, where + ".id");
                string label = DiagramText.Limit(Str(n, "label", res, where + ".label"), DiagramLimits.MaxLabel);
                if (id == null || !IdPattern.IsMatch(id)) { res.Issues.Add(new(where + ".id", "must match ^[a-z][a-z0-9_]{0,31}$.")); continue; }
                if (!seen.Add(id)) { res.Issues.Add(new(where + ".id", $"duplicate node id '{id}'.")); continue; }
                if (label.Length == 0) { res.Issues.Add(new(where + ".label", "must contain visible text.")); continue; }

                string? group = Str(n, "group", res, where + ".group");
                if (string.IsNullOrWhiteSpace(group)) group = null;
                else if (!groupIds.Contains(group)) { res.Issues.Add(new(where + ".group", $"unknown group '{group}'. Use a group id from groups.")); continue; }

                string shape = Str(n, "shape", res, where + ".shape") ?? "box";
                if (Array.IndexOf(Shapes, shape) < 0) shape = "box";

                string? path = ResolvePath(Str(n, "path", res, where + ".path"), ctx, $"node '{id}'", res);
                list.Add(new DiagramNode(id, label, group, path, shape));
            }
            return list;
        }

        private static List<DiagramEdge> ReadEdges(JsonElement root, Dictionary<string, DiagramNode> nodes, DiagramContext? ctx, bool stored, DiagramValidation res)
        {
            var list = new List<DiagramEdge>();
            if (!root.TryGetProperty("edges", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                res.Issues.Add(new("edges", "edges must be an array (it may be empty)."));
                return list;
            }
            if (arr.GetArrayLength() > DiagramLimits.MaxEdges) res.Issues.Add(new("edges", $"At most {DiagramLimits.MaxEdges} edges are allowed; keep the important relationships."));
            var seen = new HashSet<string>();
            int i = 0;
            foreach (var e in arr.EnumerateArray().Take(DiagramLimits.MaxEdges * 2))
            {
                string where = $"edges[{i++}]";
                if (e.ValueKind != JsonValueKind.Object) { res.Issues.Add(new(where, "must be an object.")); continue; }
                string? from = Str(e, "from", res, where + ".from"), to = Str(e, "to", res, where + ".to");
                if (from == null || !nodes.ContainsKey(from)) { res.Issues.Add(new(where + ".from", $"unknown node '{from}'. Use a node id from nodes.")); continue; }
                if (to == null || !nodes.ContainsKey(to)) { res.Issues.Add(new(where + ".to", $"unknown node '{to}'. Use a node id from nodes.")); continue; }
                if (from == to) { res.Notes.Add($"{where}: a self-loop on '{from}' was removed."); continue; }

                string? label = DiagramText.Limit(Str(e, "label", res, where + ".label"), DiagramLimits.MaxLabel);
                if (label.Length == 0) label = null;
                if (!seen.Add($"{from}\u0001{to}\u0001{label}")) { res.Notes.Add($"{where}: a duplicate edge {from} -> {to} was removed."); continue; }

                string? claimed = ResolvePath(Str(e, "evidencePath", res, where + ".evidencePath"), ctx, $"edge {from} -> {to} evidence", res);

                DiagramEdge edge;
                if (stored)
                {
                    var support = Str(e, "support", res, where + ".support")?.ToLowerInvariant() switch
                    {
                        "verified" => EdgeSupport.Verified,
                        "documented" => EdgeSupport.Documented,
                        _ => EdgeSupport.Inferred,
                    };
                    edge = new DiagramEdge(from, to, label, claimed, support);
                }
                else
                {
                    var (support, evidence) = Classify(nodes[from], nodes[to], claimed, ctx!);
                    edge = new DiagramEdge(from, to, label, evidence, support);
                }
                list.Add(edge);
            }
            return list;
        }

        /// <summary>A path the model gave, if it is a safe, real part of the project. Anything else is dropped with a note (never fatal).</summary>
        private static string? ResolvePath(string? value, DiagramContext? ctx, string what, DiagramValidation res)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (ctx == null) return DiagramText.NormalizeRepoPath(value); // stored: syntax only; existence is checked when it is used
            string? resolved = ctx.Resolve(value);
            if (resolved == null) res.Notes.Add($"{what}: '{DiagramText.Limit(value, 80)}' is not a path in this project; the link was removed.");
            return resolved;
        }

        /// <summary>
        /// Decides how well the code backs an edge. The model's claim is only a hint: an edge is Verified solely when a file
        /// Levity read on one side references the other side (or one wiring file references both).
        /// </summary>
        public static (EdgeSupport support, string? evidence) Classify(DiagramNode a, DiagramNode b, string? claimed, DiagramContext ctx)
        {
            if (a.Path != null && b.Path != null)
            {
                bool Refs(string file, string target) =>
                    ctx.References.TryGetValue(file, out var refs) && refs.Any(r => DiagramContext.Covers(target, r));

                // The model's own citation, when it checks out.
                if (claimed != null && ctx.ReadPaths.Contains(claimed))
                {
                    if ((DiagramContext.Covers(a.Path, claimed) && Refs(claimed, b.Path)) || (DiagramContext.Covers(b.Path, claimed) && Refs(claimed, a.Path))
                        || (Refs(claimed, a.Path) && Refs(claimed, b.Path)))
                        return (EdgeSupport.Verified, claimed);
                }

                // Otherwise look for support ourselves, in a stable order.
                foreach (var (x, y) in new[] { (a.Path, b.Path), (b.Path, a.Path) })
                {
                    string? file = ctx.References.Keys
                        .Where(k => DiagramContext.Covers(x, k) && Refs(k, y))
                        .OrderBy(k => k.Length).ThenBy(k => k, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (file != null) return (EdgeSupport.Verified, file);
                }
            }

            if (claimed != null && ctx.ReadmePath != null && string.Equals(claimed, ctx.ReadmePath, StringComparison.OrdinalIgnoreCase))
                return (EdgeSupport.Documented, claimed);

            return (EdgeSupport.Inferred, null);
        }

        private static string? Str(JsonElement obj, string name, DiagramValidation res, string where)
        {
            if (!obj.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind != JsonValueKind.String) { res.Issues.Add(new(where, "must be a string.")); return null; }
            return v.GetString();
        }

        /// <summary>The JSON object inside <paramref name="raw"/>: the whole text, or the first balanced {...} (models sometimes wrap it in a code fence or prose).</summary>
        public static string? ExtractJson(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string text = raw.Trim();
            if (text.StartsWith('{') && IsJson(text)) return text;

            int start = text.IndexOf('{');
            while (start >= 0)
            {
                int end = MatchingBrace(text, start);
                if (end > start)
                {
                    string candidate = text.Substring(start, end - start + 1);
                    if (IsJson(candidate)) return candidate;
                }
                start = text.IndexOf('{', start + 1);
            }
            return null;
        }

        private static bool IsJson(string text)
        {
            try { using var _ = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 }); return true; }
            catch (JsonException) { return false; }
        }

        private static int MatchingBrace(string s, int start)
        {
            int depth = 0;
            bool inString = false, escaped = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>Plain-language list of problems to give the model on a retry.</summary>
        public static string FormatFeedback(IEnumerable<DiagramIssue> issues)
        {
            var sb = new StringBuilder("Your previous output had these problems. Return the corrected JSON object only, changing nothing else:\n");
            foreach (var issue in issues.Take(12)) sb.Append("- ").Append(issue.Where).Append(": ").AppendLine(issue.Message);
            return sb.ToString();
        }

        /// <summary>Serializes a validated graph for the cache and for the UI (the same shape <see cref="ValidateStored"/> reads).</summary>
        public static string ToJson(DiagramGraph g)
        {
            using var stream = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteString("overview", g.Overview);
                w.WriteStartArray("groups");
                foreach (var x in g.Groups) { w.WriteStartObject(); w.WriteString("id", x.Id); w.WriteString("label", x.Label); w.WriteEndObject(); }
                w.WriteEndArray();
                w.WriteStartArray("nodes");
                foreach (var n in g.Nodes)
                {
                    w.WriteStartObject();
                    w.WriteString("id", n.Id); w.WriteString("label", n.Label);
                    if (n.Group != null) w.WriteString("group", n.Group);
                    if (n.Path != null) w.WriteString("path", n.Path);
                    w.WriteString("shape", n.Shape);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("edges");
                foreach (var e in g.Edges)
                {
                    w.WriteStartObject();
                    w.WriteString("from", e.From); w.WriteString("to", e.To);
                    if (e.Label != null) w.WriteString("label", e.Label);
                    if (e.EvidencePath != null) w.WriteString("evidencePath", e.EvidencePath);
                    w.WriteString("support", e.Support.ToString().ToLowerInvariant());
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
