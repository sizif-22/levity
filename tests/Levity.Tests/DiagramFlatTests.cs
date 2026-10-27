using System.Text.Json;
using System.Text.RegularExpressions;
using Levity.Services;
using Xunit;

namespace Levity.Tests;

public class DiagramFlatTests
{
    private static CompiledDiagram Compile(string json = DiagramFixtures.Good) =>
        DiagramCompiler.Compile(DiagramValidator.Validate(json, DiagramFixtures.Context()).Graph!);

    private static readonly Regex Grammar = new(@"^\s*(flowchart TD|subgraph g\d+\[""[^""]*""\]|end|n\d+(\[""[^""]*""\]|\[\(""[^""]*""\)\]|\(\(""[^""]*""\)\)|\{\{""[^""]*""\}\})|n\d+ (-->|-\.->)(\|""[^""]*""\|)? n\d+|classDef tone\w+ [a-z:#0-9.,\-]+|class n\d+(,n\d+)* tone\w+)$", RegexOptions.Compiled);

    [Fact]
    public void The_flat_version_has_no_group_boxes_but_the_same_nodes_edges_and_colours()
    {
        var c = Compile();
        Assert.Contains("subgraph", c.Mermaid);
        Assert.DoesNotContain("subgraph", c.MermaidFlat);
        Assert.DoesNotMatch(@"(?m)^\s*end\s*$", c.MermaidFlat);

        string[] Pick(string text, string prefix) => text.Split('\n').Where(l => l.TrimStart().StartsWith(prefix)).Select(l => l.Trim()).OrderBy(l => l, StringComparer.Ordinal).ToArray();
        Assert.Equal(Pick(c.Mermaid, "n"), Pick(c.MermaidFlat, "n"));          // node declarations, edges and class lines
        Assert.Equal(Pick(c.Mermaid, "class"), Pick(c.MermaidFlat, "class"));   // the same colours
    }

    [Fact]
    public void Both_versions_use_the_same_generated_ids_the_ui_maps_clicks_with()
    {
        var c = Compile();
        foreach (var node in c.Nodes)
        {
            Assert.Contains(node.MermaidId + "[", c.Mermaid.Replace("(", "[").Replace("{", "["));
            Assert.Matches("\\b" + node.MermaidId + "\\b", c.MermaidFlat);
        }
    }

    [Fact]
    public void A_diagram_with_no_groups_is_the_same_in_both_versions_apart_from_spacing()
    {
        string json = "{\"overview\":\"\",\"groups\":[],\"nodes\":[{\"id\":\"a\",\"label\":\"A\"},{\"id\":\"b\",\"label\":\"B\"}],\"edges\":[{\"from\":\"a\",\"to\":\"b\"}]}";
        var c = Compile(json);
        Assert.Equal(Regex.Replace(c.Mermaid, @"\s+", " "), Regex.Replace(c.MermaidFlat, @"\s+", " "));
    }

    public static IEnumerable<object[]> Hostile() => new[]
    {
        "x\"]\nclick n1 \"javascript:alert(1)\"", "<img src=x onerror=alert(1)>", "end\nsubgraph evil[\"x\"]", "%%{init: {\"securityLevel\": \"loose\"}}%%",
        "`markdown` **bold** _it_", "a]) --> b((c", "x\r\nclassDef bad fill:url(javascript:alert(1))", "#35; #quot; &amp; &lt;b&gt;",
    }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Hostile_text_cannot_become_syntax_in_either_version(string hostile)
    {
        var graph = new DiagramGraph(hostile,
            new[] { new DiagramGroup("g", hostile) },
            new[] { new DiagramNode("a", hostile, "g", null, "box"), new DiagramNode("b", hostile, null, null, "database") },
            new[] { new DiagramEdge("a", "b", hostile, null, EdgeSupport.Inferred) });
        var c = DiagramCompiler.Compile(graph);
        foreach (string text in new[] { c.Mermaid, c.MermaidFlat })
        {
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Assert.Matches(Grammar, line);
            Assert.DoesNotContain("%%", text);
            Assert.DoesNotMatch("#[A-Za-z0-9]+;", text);
        }
    }

    [Fact]
    public void Labels_keep_their_punctuation_so_they_read_naturally()
    {
        string json = DiagramFixtures.Good.Replace("Service A", "Cache (L1) & store: v2.0, C# host");
        var c = Compile(json);
        Assert.Contains("Cache (L1) & store: v2.0, C# host", c.Mermaid);
        Assert.Contains("Cache (L1) & store: v2.0, C# host", c.MermaidFlat);
    }

    [Fact]
    public void The_payload_carries_the_flat_version_and_each_groups_colour_slot()
    {
        var graph = DiagramValidator.Validate(DiagramFixtures.Good, DiagramFixtures.Context()).Graph!;
        var result = new DiagramResult(@"C:\proj", graph, DiagramCompiler.Compile(graph), DateTime.UtcNow, null, false, null, new List<string>(), 0);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(DiagramPayload.Build(result)));
        var root = doc.RootElement;

        Assert.DoesNotContain("subgraph", root.GetProperty("mermaidFlat").GetString());
        Assert.Contains("subgraph", root.GetProperty("mermaid").GetString());
        Assert.Equal(new[] { 0, 1 }, root.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("tone").GetInt32()));
    }

    [Fact]
    public void The_colour_slots_wrap_after_the_palette_runs_out()
    {
        Assert.Equal(0, DiagramCompiler.ToneOf(0));
        Assert.Equal(5, DiagramCompiler.ToneOf(5));
        Assert.Equal(0, DiagramCompiler.ToneOf(DiagramCompiler.Palette.Length));
        Assert.Equal(3, DiagramCompiler.ToneOf(DiagramCompiler.Palette.Length * 2 + 3));
    }

    [Fact]
    public void The_saved_docs_use_the_version_that_always_lays_out_and_list_the_group_colours()
    {
        var graph = DiagramValidator.Validate(DiagramFixtures.Good, DiagramFixtures.Context()).Graph!;
        var result = new DiagramResult(@"C:\proj", graph, DiagramCompiler.Compile(graph), DateTime.UtcNow, null, false, null, new List<string>(), 0);
        string md = DiagramDocs.Render(result);
        string block = Regex.Match(md, "```mermaid[\\s\\S]*?\n```").Value;
        Assert.DoesNotContain("subgraph", block);
        Assert.Contains("Colours: Core; Web", md);
    }
}
