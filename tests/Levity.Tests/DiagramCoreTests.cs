using System.Text.RegularExpressions;
using AntigravitySidebar.Services;
using Xunit;

namespace Levity.Tests;

public class DiagramTextTests
{
    [Fact]
    public void Normalize_removes_control_zero_width_and_bidi_override_characters()
    {
        Assert.Equal("a b", DiagramText.Normalize("a\u0000\u0007 \u200B\u202Eb"));
        Assert.Equal("evil", DiagramText.Normalize("ev\u202Eil\u2066"));
        Assert.Equal("one two", DiagramText.Normalize("  one \n\t two\u2028"));
        Assert.Equal("", DiagramText.Normalize(" \u200B\u200C "));
        Assert.Equal("", DiagramText.Normalize(null));
    }

    [Fact]
    public void Normalize_uses_canonical_composition()
    {
        Assert.Equal("\u00E9", DiagramText.Normalize("e\u0301"));
    }

    [Fact]
    public void Limit_cuts_with_an_ellipsis()
    {
        Assert.Equal("short", DiagramText.Limit("short", 10));
        string cut = DiagramText.Limit(new string('x', 100), 10);
        Assert.Equal(10, cut.Length);
        Assert.EndsWith("\u2026", cut);
    }

    [Theory]
    [InlineData("src/App.tsx", "src/App.tsx")]
    [InlineData("./src/App.tsx", "src/App.tsx")]
    [InlineData(@"Services\JobManager.cs", "Services/JobManager.cs")]
    [InlineData("Services/", "Services")]
    [InlineData("  web/src/lib/chat.ts  ", "web/src/lib/chat.ts")]
    [InlineData(".github/workflows/ci.yml", ".github/workflows/ci.yml")]
    [InlineData("my file.md", "my file.md")]
    public void Accepts_plain_repo_relative_paths(string input, string expected)
    {
        Assert.Equal(expected, DiagramText.NormalizeRepoPath(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows\system32")]
    [InlineData("c:/x")]
    [InlineData(@"\\server\share\x")]
    [InlineData("../secret")]
    [InlineData("a/../../b")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("a/b*.cs")]
    [InlineData("a/b?.cs")]
    [InlineData("a/<b>.cs")]
    [InlineData("a/b|c")]
    [InlineData("a/\"b\"")]
    [InlineData("con")]
    [InlineData("a/NUL.txt")]
    [InlineData("a/com1")]
    [InlineData("a /b")]
    [InlineData("a/b ./c")]
    [InlineData("a/b.")]
    [InlineData("a/b./c")]
    [InlineData("https://evil.example/x")]
    [InlineData("a/b\u0000.cs")]
    [InlineData("a/b\u202Ecs.exe")]
    public void Rejects_anything_that_is_not_a_plain_relative_path(string input)
    {
        Assert.Null(DiagramText.NormalizeRepoPath(input));
    }

    [Fact]
    public void Rejects_overlong_paths()
    {
        Assert.Null(DiagramText.NormalizeRepoPath(new string('a', DiagramLimits.MaxPath + 1)));
        Assert.NotNull(DiagramText.NormalizeRepoPath(new string('a', DiagramLimits.MaxPath)));
    }

    [Fact]
    public void Mermaid_labels_have_no_syntax_characters_left()
    {
        string escaped = DiagramText.EscapeMermaidLabel("a\"b]c(d)e{f}g|h<i>j&k#l\\m;n%o`p");
        foreach (char c in "\"]()[{}|<>\\%`")   // ";" and "#" remain only as part of the entities themselves
            Assert.DoesNotContain(c, escaped);
        Assert.Contains("#34;", escaped);   // the quote became an entity
        Assert.Equal("plain text 123", DiagramText.EscapeMermaidLabel("plain text 123"));
    }

    [Fact]
    public void Mermaid_labels_cannot_contain_a_line_break()
    {
        Assert.DoesNotContain('\n', DiagramText.EscapeMermaidLabel("one\ntwo\r\nthree"));
    }
}

internal static class DiagramFixtures
{
    public static DiagramContext Context()
    {
        var ctx = new DiagramContext(@"C:\proj", new[]
        {
            "README.md", "Program.cs", "Services/A.cs", "Services/B.cs", "Services/C.cs", "web/src/App.tsx", "web/src/lib/relay.ts", "relay/src/index.ts", "docs/guide.md",
        });
        ctx.ReadmePath = "README.md";
        foreach (var f in new[] { "README.md", "Program.cs", "Services/A.cs", "Services/B.cs", "web/src/App.tsx", "web/src/lib/relay.ts" }) ctx.ReadPaths.Add(f);
        ctx.References["Program.cs"] = new[] { "Services/A.cs" };
        ctx.References["Services/A.cs"] = new[] { "Services/B.cs" };
        ctx.References["web/src/App.tsx"] = new[] { "web/src/lib/relay.ts" };
        return ctx;
    }

    public const string Good = """
    {
      "overview": "A small app.",
      "groups": [ { "id": "core", "label": "Core" }, { "id": "web", "label": "Web" } ],
      "nodes": [
        { "id": "entry", "label": "Entry point", "group": "core", "path": "Program.cs" },
        { "id": "a", "label": "Service A", "group": "core", "path": "Services/A.cs" },
        { "id": "b", "label": "Service B", "group": "core", "path": "Services/B.cs", "shape": "database" },
        { "id": "app", "label": "Phone app", "group": "web", "path": "web/src/App.tsx" },
        { "id": "ext", "label": "Cloud relay" }
      ],
      "edges": [
        { "from": "entry", "to": "a", "label": "starts", "evidencePath": "Program.cs" },
        { "from": "a", "to": "b", "label": "uses" },
        { "from": "app", "to": "ext", "label": "connects" }
      ]
    }
    """;
}

public class DiagramValidatorTests
{
    private static DiagramValidation Run(string json) => DiagramValidator.Validate(json, DiagramFixtures.Context());

    [Fact]
    public void A_good_graph_validates()
    {
        var v = Run(DiagramFixtures.Good);
        Assert.True(v.Ok, string.Join("; ", v.Issues.Select(i => i.Where + " " + i.Message)));
        Assert.Equal(5, v.Graph!.Nodes.Count);
        Assert.Equal(2, v.Graph.Groups.Count);
        Assert.Equal("database", v.Graph.Nodes.Single(n => n.Id == "b").Shape);
    }

    [Fact]
    public void Paths_are_canonicalised_to_the_real_spelling()
    {
        var v = Run(DiagramFixtures.Good.Replace("\"Services/A.cs\"", "\"services\\\\a.CS\""));
        Assert.Equal("Services/A.cs", v.Graph!.Nodes.Single(n => n.Id == "a").Path);
    }

    [Fact]
    public void A_node_may_point_at_a_folder()
    {
        var v = Run(DiagramFixtures.Good.Replace("\"Services/B.cs\"", "\"Services\""));
        Assert.Equal("Services", v.Graph!.Nodes.Single(n => n.Id == "b").Path);
    }

    [Theory]
    [InlineData("../../Windows/win.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("Services/DoesNotExist.cs")]
    [InlineData("Services/*.cs")]
    public void Paths_that_are_unsafe_or_not_in_the_project_are_removed_not_fatal(string bad)
    {
        var v = Run(DiagramFixtures.Good.Replace("\"Services/A.cs\"", $"\"{bad.Replace("\\", "\\\\")}\""));
        Assert.True(v.Ok);
        Assert.Null(v.Graph!.Nodes.Single(n => n.Id == "a").Path);
        Assert.Contains(v.Notes, n => n.Contains("link was removed"));
    }

    [Fact]
    public void Non_json_output_is_an_issue()
    {
        var v = Run("Sorry, I cannot help with that.");
        Assert.False(v.Ok);
        Assert.Contains("not valid JSON", v.Issues[0].Message);
    }

    [Fact]
    public void Json_in_a_code_fence_or_surrounded_by_prose_is_found()
    {
        Assert.True(Run("```json\n" + DiagramFixtures.Good + "\n```").Ok);
        Assert.True(Run("Here you go:\n" + DiagramFixtures.Good + "\nHope that helps {really}.").Ok);
    }

    [Fact]
    public void Braces_inside_strings_do_not_confuse_extraction()
    {
        string json = DiagramFixtures.Good.Replace("A small app.", "Uses {curly} \\\" braces } {");
        Assert.True(Run("prefix " + json + " suffix").Ok);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void Json_that_is_not_an_object_is_an_issue(string json)
    {
        Assert.False(Run(json).Ok);
    }

    [Fact]
    public void Duplicate_ids_are_issues()
    {
        Assert.Contains(Run(DiagramFixtures.Good.Replace("\"id\": \"a\"", "\"id\": \"entry\"")).Issues, i => i.Message.Contains("duplicate node id"));
        Assert.Contains(Run(DiagramFixtures.Good.Replace("\"id\": \"web\"", "\"id\": \"core\"")).Issues, i => i.Message.Contains("duplicate group id"));
    }

    [Fact]
    public void An_unknown_group_or_edge_endpoint_is_an_issue()
    {
        Assert.Contains(Run(DiagramFixtures.Good.Replace("\"group\": \"web\"", "\"group\": \"nope\"")).Issues, i => i.Message.Contains("unknown group"));
        Assert.Contains(Run(DiagramFixtures.Good.Replace("\"to\": \"a\"", "\"to\": \"ghost\"")).Issues, i => i.Message.Contains("unknown node"));
    }

    [Theory]
    [InlineData("Bad Id")]
    [InlineData("1abc")]
    [InlineData("UPPER")]
    [InlineData("has-dash")]
    [InlineData("a\\\"b")]
    public void Ids_must_match_the_pattern(string id)
    {
        Assert.False(Run(DiagramFixtures.Good.Replace("\"id\": \"entry\"", $"\"id\": \"{id}\"")).Ok);
    }

    [Fact]
    public void Too_many_nodes_edges_or_groups_are_issues()
    {
        string Nodes(int n) => string.Join(",", Enumerable.Range(0, n).Select(i => $"{{\"id\":\"n{i}\",\"label\":\"N{i}\"}}"));
        Assert.False(Run($"{{\"overview\":\"\",\"groups\":[],\"nodes\":[{Nodes(DiagramLimits.MaxNodes + 1)}],\"edges\":[]}}").Ok);
        string Groups(int n) => string.Join(",", Enumerable.Range(0, n).Select(i => $"{{\"id\":\"g{i}\",\"label\":\"G{i}\"}}"));
        Assert.False(Run($"{{\"overview\":\"\",\"groups\":[{Groups(DiagramLimits.MaxGroups + 1)}],\"nodes\":[{Nodes(2)}],\"edges\":[]}}").Ok);
        string Edges(int n) => string.Join(",", Enumerable.Range(0, n).Select(i => $"{{\"from\":\"n0\",\"to\":\"n1\",\"label\":\"e{i}\"}}"));
        Assert.False(Run($"{{\"overview\":\"\",\"groups\":[],\"nodes\":[{Nodes(2)}],\"edges\":[{Edges(DiagramLimits.MaxEdges + 1)}]}}").Ok);
    }

    [Fact]
    public void The_largest_allowed_graph_is_accepted()
    {
        string nodes = string.Join(",", Enumerable.Range(0, DiagramLimits.MaxNodes).Select(i => $"{{\"id\":\"n{i}\",\"label\":\"N{i}\"}}"));
        string edges = string.Join(",", Enumerable.Range(0, DiagramLimits.MaxEdges).Select(i => $"{{\"from\":\"n{i % 30}\",\"to\":\"n{(i % 30) + 1}\",\"label\":\"e{i}\"}}"));
        Assert.True(Run($"{{\"overview\":\"\",\"groups\":[],\"nodes\":[{nodes}],\"edges\":[{edges}]}}").Ok);
    }

    [Fact]
    public void No_nodes_is_an_issue()
    {
        Assert.False(Run("{\"overview\":\"x\",\"groups\":[],\"nodes\":[],\"edges\":[]}").Ok);
    }

    [Fact]
    public void Missing_arrays_and_wrong_types_are_issues_not_crashes()
    {
        Assert.False(Run("{\"overview\":\"x\"}").Ok);
        Assert.False(Run("{\"overview\":5,\"groups\":[],\"nodes\":[{\"id\":\"a\",\"label\":\"A\"}],\"edges\":[]}").Ok);
        Assert.False(Run("{\"overview\":\"\",\"groups\":[],\"nodes\":[{\"id\":\"a\",\"label\":7}],\"edges\":[]}").Ok);
        Assert.False(Run("{\"overview\":\"\",\"groups\":{},\"nodes\":[],\"edges\":[]}").Ok);
        Assert.False(Run("{\"overview\":\"\",\"groups\":[],\"nodes\":[1,2],\"edges\":[]}").Ok);
    }

    [Fact]
    public void Self_loops_and_duplicate_edges_are_removed_with_a_note()
    {
        string json = DiagramFixtures.Good.Replace("\"edges\": [", "\"edges\": [ { \"from\": \"a\", \"to\": \"a\" }, { \"from\": \"entry\", \"to\": \"a\", \"label\": \"starts\" },");
        var v = Run(json);
        Assert.True(v.Ok);
        Assert.Equal(3, v.Graph!.Edges.Count);
        Assert.Contains(v.Notes, n => n.Contains("self-loop"));
        Assert.Contains(v.Notes, n => n.Contains("duplicate edge"));
    }

    [Fact]
    public void Labels_are_cleaned_and_shortened_and_blank_labels_are_refused()
    {
        string json = DiagramFixtures.Good.Replace("Service A", "Serv\u202Eice\u200B A" + new string('x', 200));
        var v = Run(json);
        string label = v.Graph!.Nodes.Single(n => n.Id == "a").Label;
        Assert.True(label.Length <= DiagramLimits.MaxLabel);
        Assert.DoesNotContain('\u202E', label);

        Assert.False(Run(DiagramFixtures.Good.Replace("Service A", "\u200B \u200B")).Ok);
    }

    [Fact]
    public void An_unknown_shape_becomes_a_box()
    {
        var v = Run(DiagramFixtures.Good.Replace("\"database\"", "\"<script>\""));
        Assert.Equal("box", v.Graph!.Nodes.Single(n => n.Id == "b").Shape);
    }

    [Fact]
    public void Unknown_extra_fields_are_ignored()
    {
        Assert.True(Run(DiagramFixtures.Good.Replace("\"overview\"", "\"href\": \"javascript:alert(1)\", \"overview\"")).Ok);
    }

    [Fact]
    public void An_extremely_deep_document_is_refused_without_crashing()
    {
        string deep = string.Concat(Enumerable.Repeat("{\"a\":", 200)) + "1" + new string('}', 200);
        Assert.False(Run(deep).Ok);
    }

    // ---- edge support: only the code can make an edge Verified ----

    [Fact]
    public void An_edge_is_verified_when_a_file_on_one_side_references_the_other()
    {
        var edge = Run(DiagramFixtures.Good).Graph!.Edges.Single(e => e.From == "a" && e.To == "b");
        Assert.Equal(EdgeSupport.Verified, edge.Support);
        Assert.Equal("Services/A.cs", edge.EvidencePath); // found by the index, even though the model gave none
    }

    [Fact]
    public void The_models_own_citation_is_kept_when_it_checks_out()
    {
        var edge = Run(DiagramFixtures.Good).Graph!.Edges.Single(e => e.From == "entry");
        Assert.Equal((EdgeSupport.Verified, "Program.cs"), (edge.Support, edge.EvidencePath));
    }

    [Fact]
    public void A_confident_claim_without_a_real_reference_stays_inferred()
    {
        // Services/C.cs was never read and nothing references it: the model's claim cannot make this verified.
        string json = DiagramFixtures.Good.Replace("\"nodes\": [", "\"nodes\": [ { \"id\": \"c\", \"label\": \"Service C\", \"group\": \"core\", \"path\": \"Services/C.cs\" },")
            .Replace("\"edges\": [", "\"edges\": [ { \"from\": \"a\", \"to\": \"c\", \"label\": \"definitely calls\", \"evidencePath\": \"Services/A.cs\" },");
        var edge = Run(json).Graph!.Edges.Single(e => e.To == "c");
        Assert.Equal(EdgeSupport.Inferred, edge.Support);
        Assert.Null(edge.EvidencePath);
    }

    [Fact]
    public void Citing_a_file_that_does_not_exist_cannot_help_an_edge()
    {
        string json = DiagramFixtures.Good.Replace("\"evidencePath\": \"Program.cs\"", "\"evidencePath\": \"Nope/Made.Up.cs\"");
        var v = Run(json);
        Assert.True(v.Ok);
        Assert.Equal(EdgeSupport.Verified, v.Graph!.Edges.Single(e => e.From == "entry").Support); // still found via the index
        Assert.Contains(v.Notes, n => n.Contains("evidence"));
    }

    [Fact]
    public void Edges_to_nodes_without_a_path_are_inferred_unless_the_readme_says_so()
    {
        Assert.Equal(EdgeSupport.Inferred, Run(DiagramFixtures.Good).Graph!.Edges.Single(e => e.To == "ext").Support);

        string documented = DiagramFixtures.Good.Replace("\"label\": \"connects\"", "\"label\": \"connects\", \"evidencePath\": \"README.md\"");
        Assert.Equal(EdgeSupport.Documented, Run(documented).Graph!.Edges.Single(e => e.To == "ext").Support);
    }

    [Fact]
    public void A_wiring_file_that_references_both_sides_verifies_the_edge()
    {
        var ctx = DiagramFixtures.Context();
        ctx.ReadPaths.Add("Services/C.cs");
        ctx.References["Services/A.cs"] = Array.Empty<string>();   // A no longer references B itself: only the wiring file connects them
        ctx.References["Services/C.cs"] = new[] { "Services/A.cs", "Services/B.cs" };
        string json = DiagramFixtures.Good.Replace("\"label\": \"uses\"", "\"label\": \"uses\", \"evidencePath\": \"Services/C.cs\"");
        var edge = DiagramValidator.Validate(json, ctx).Graph!.Edges.Single(e => e.From == "a" && e.To == "b");
        Assert.Equal((EdgeSupport.Verified, "Services/C.cs"), (edge.Support, edge.EvidencePath));
    }

    [Fact]
    public void Support_is_found_in_either_direction()
    {
        // The model draws B -> A, but the code has A referencing B.
        string json = DiagramFixtures.Good.Replace("{ \"from\": \"a\", \"to\": \"b\", \"label\": \"uses\" }", "{ \"from\": \"b\", \"to\": \"a\", \"label\": \"uses\" }");
        Assert.Equal(EdgeSupport.Verified, Run(json).Graph!.Edges.Single(e => e.From == "b").Support);
    }

    [Fact]
    public void A_folder_node_is_supported_by_any_file_inside_it()
    {
        var ctx = DiagramFixtures.Context();
        string json = """
        { "overview":"", "groups":[], "nodes":[ {"id":"svc","label":"Services","path":"Services"}, {"id":"web","label":"Web","path":"web/src"} ],
          "edges":[ {"from":"svc","to":"web"} ] }
        """;
        ctx.References["Services/B.cs"] = new[] { "web/src/lib/relay.ts" };
        Assert.Equal(EdgeSupport.Verified, DiagramValidator.Validate(json, ctx).Graph!.Edges[0].Support);
    }

    // ---- stored graphs are never trusted ----

    [Fact]
    public void A_validated_graph_survives_a_round_trip_through_storage()
    {
        var g = Run(DiagramFixtures.Good).Graph!;
        var back = DiagramValidator.ValidateStored(DiagramValidator.ToJson(g));
        Assert.True(back.Ok);
        Assert.Equal(DiagramValidator.ToJson(g), DiagramValidator.ToJson(back.Graph!));
    }

    [Fact]
    public void A_tampered_stored_graph_is_cleaned_or_refused()
    {
        string stored = DiagramValidator.ToJson(Run(DiagramFixtures.Good).Graph!);

        var traversal = DiagramValidator.ValidateStored(stored.Replace("\"Services/A.cs\"", "\"../../Windows/win.ini\""));
        Assert.True(traversal.Ok);
        Assert.DoesNotContain(traversal.Graph!.Nodes, n => n.Path != null && n.Path.Contains(".."));

        var support = DiagramValidator.ValidateStored(stored.Replace("\"verified\"", "\"definitely\""));
        Assert.All(support.Graph!.Edges.Where(e => e.From == "entry"), e => Assert.Equal(EdgeSupport.Inferred, e.Support));

        string many = "{\"overview\":\"\",\"groups\":[],\"nodes\":[" + string.Join(",", Enumerable.Range(0, 500).Select(i => $"{{\"id\":\"n{i}\",\"label\":\"N\"}}")) + "],\"edges\":[]}";
        Assert.False(DiagramValidator.ValidateStored(many).Ok);
        Assert.False(DiagramValidator.ValidateStored("garbage").Ok);
    }

    [Fact]
    public void Feedback_lists_the_problems_for_the_retry()
    {
        var v = Run(DiagramFixtures.Good.Replace("\"to\": \"a\"", "\"to\": \"ghost\""));
        string text = DiagramValidator.FormatFeedback(v.Issues);
        Assert.Contains("ghost", text);
        Assert.Contains("Return the corrected JSON", text);
    }
}

public class DiagramCompilerTests
{
    private static CompiledDiagram Compile(string json = DiagramFixtures.Good) =>
        DiagramCompiler.Compile(DiagramValidator.Validate(json, DiagramFixtures.Context()).Graph!);

    [Fact]
    public void Produces_a_flowchart_with_generated_ids_and_groups()
    {
        var c = Compile();
        Assert.StartsWith("flowchart TD", c.Mermaid);
        Assert.Contains("subgraph g1[\"Core\"]", c.Mermaid);
        Assert.Contains("subgraph g2[\"Web\"]", c.Mermaid);
        Assert.Contains("n1[\"Entry point\"]", c.Mermaid);
        Assert.Contains("n3[(\"Service B\")]", c.Mermaid);          // database shape
        Assert.DoesNotContain("entry", c.Mermaid.Replace("Entry point", ""));  // the model's own ids never appear
    }

    [Fact]
    public void Inferred_edges_are_dashed_and_verified_edges_are_solid()
    {
        var c = Compile();
        Assert.Contains("n1 -->|\"starts\"| n2", c.Mermaid);
        Assert.Contains("n4 -.->|\"connects\"| n5", c.Mermaid);
    }

    [Fact]
    public void The_node_table_maps_clicks_back_without_paths_from_the_client()
    {
        var c = Compile();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, c.Nodes.Select(n => n.Index));
        Assert.Equal("Program.cs", c.Nodes[0].Path);
        Assert.Null(c.Nodes[4].Path);
        Assert.Equal("n5", c.Nodes[4].MermaidId);
    }

    [Fact]
    public void Ungrouped_nodes_are_drawn_and_use_the_neutral_style()
    {
        var c = Compile();
        Assert.Contains("class n5 toneNeutral", c.Mermaid);
    }

    [Fact]
    public void A_group_with_no_nodes_is_left_out()
    {
        string json = DiagramFixtures.Good.Replace("\"groups\": [", "\"groups\": [ { \"id\": \"empty\", \"label\": \"Nothing here\" },");
        Assert.DoesNotContain("Nothing here", Compile(json).Mermaid);
    }

    [Fact]
    public void Compiling_twice_gives_identical_output()
    {
        Assert.Equal(Compile().Mermaid, Compile().Mermaid);
    }

    public static IEnumerable<object[]> HostileLabels() => new[]
    {
        "x\"]\nclick n1 \"javascript:alert(1)\"",
        "x\"]\n    n9[\"injected\"]\n    n1 --> n9",
        "end\nsubgraph evil[\"x\"]",
        "%% a comment that eats the rest",
        "<img src=x onerror=alert(1)>",
        "<script>alert(1)</script>",
        "a]) --> b((c",
        "\"; style n1 fill:red; \"",
        "`markdown` **bold** _italic_",
        "x\r\nclassDef bad fill:url(javascript:alert(1))",
        "}}%%{init: {\"securityLevel\": \"loose\"}}%%",
        "linkStyle 0 stroke:#f00",
        "x\u2028click n1 href \"https://evil.example\"",
    }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(HostileLabels))]
    public void Hostile_text_can_never_become_mermaid_syntax(string hostile)
    {
        var graph = new DiagramGraph(hostile,
            new[] { new DiagramGroup("g", hostile) },
            new[] { new DiagramNode("a", hostile, "g", null, "box"), new DiagramNode("b", hostile, null, null, "database") },
            new[] { new DiagramEdge("a", "b", hostile, null, EdgeSupport.Inferred) });
        string mermaid = DiagramCompiler.Compile(graph).Mermaid;

        // Every line must be one the compiler itself wrote.
        foreach (string line in mermaid.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.Matches(@"^\s*(flowchart TD|subgraph g\d+\[""[^""]*""\]|end|n\d+(\[""[^""]*""\]|\[\(""[^""]*""\)\]|\(\(""[^""]*""\)\)|\{\{""[^""]*""\}\})|n\d+ (-->|-\.->)(\|""[^""]*""\|)? n\d+|classDef tone\w+ [a-z:#0-9.,\-]+|class n\d+(,n\d+)* tone\w+)$", line);
        }
        // The hostile words may appear as harmless displayed text, but never as a line of their own.
        foreach (string line in mermaid.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.False(line.TrimStart().StartsWith("click ") || line.TrimStart().StartsWith("style ") || line.TrimStart().StartsWith("linkStyle "), "an injected directive: " + line);
        Assert.DoesNotContain("%%{", mermaid);
    }
}
