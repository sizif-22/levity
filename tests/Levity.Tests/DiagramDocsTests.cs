using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AntigravitySidebar.Services;
using Xunit;

namespace Levity.Tests;

public class DiagramDocsTests
{
    private static DiagramResult Result(string json = DiagramFixtures.Good, string? head = "abc1234def5678")
    {
        var graph = DiagramValidator.Validate(json, DiagramFixtures.Context()).Graph!;
        return new DiagramResult(@"C:\proj", graph, DiagramCompiler.Compile(graph), new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc), head, false, null, new List<string>(), 0);
    }

    [Fact]
    public void The_section_has_markers_a_mermaid_block_a_legend_and_a_component_list()
    {
        string s = DiagramDocs.Render(Result());
        Assert.StartsWith(DiagramDocs.StartMarker, s);
        Assert.EndsWith(DiagramDocs.EndMarker, s);
        Assert.Contains("```mermaid\nflowchart TD", s.Replace("\r\n", "\n"));
        Assert.Contains("Dashed arrows are inferred", s);
        Assert.Contains("- **Service A**: [`Services/A.cs`](../Services/A.cs)", s);
        Assert.Contains("- **Cloud relay**", s);                  // a node without a path has no link
        Assert.Contains("from commit `abc1234`", s);
        Assert.Contains("2026-10-27", s);
    }

    [Fact]
    public void Hostile_text_cannot_become_html_or_a_live_markdown_link()
    {
        string json = DiagramFixtures.Good
            .Replace("A small app.", "<script>alert(1)</script> & <img src=x onerror=alert(1)>")
            .Replace("Service A", "[click](javascript:alert(1)) **bold** `code` <b>x</b>");
        string s = DiagramDocs.Render(Result(json));

        Assert.DoesNotContain("<script>", s);
        Assert.DoesNotContain("<img", s);
        Assert.DoesNotContain("<b>", s);
        Assert.DoesNotMatch(@"\]\(javascript", s);
        Assert.Contains("&lt;script&gt;", s);
        Assert.Contains("\\[click\\]\\(javascript", s);
    }

    [Fact]
    public void Paths_with_spaces_are_encoded_in_links()
    {
        var ctx = new DiagramContext(@"C:\proj", new[] { "my docs/read me.md", "a.cs" });
        var graph = DiagramValidator.Validate("{\"overview\":\"\",\"groups\":[],\"nodes\":[{\"id\":\"d\",\"label\":\"Docs\",\"path\":\"my docs/read me.md\"}],\"edges\":[]}", ctx).Graph!;
        var r = new DiagramResult(@"C:\proj", graph, DiagramCompiler.Compile(graph), DateTime.UtcNow, null, false, null, new List<string>(), 0);
        Assert.Contains("(../my%20docs/read%20me.md)", DiagramDocs.Render(r));
    }

    [Fact]
    public void Without_a_commit_the_footer_says_so_by_leaving_it_out()
    {
        Assert.DoesNotContain("from commit", DiagramDocs.Render(Result(head: null)));
    }

    [Fact]
    public void A_missing_or_empty_file_becomes_a_new_document()
    {
        string section = DiagramDocs.Render(Result());
        foreach (string? existing in new string?[] { null, "", "  \n " })
        {
            string doc = DiagramDocs.Merge(existing, section);
            Assert.StartsWith("# Architecture", doc);
            Assert.Contains(DiagramDocs.StartMarker, doc);
        }
    }

    [Fact]
    public void An_existing_document_keeps_everything_and_gets_the_section_appended()
    {
        string existing = "# My project\n\nHand-written notes.\n\n## Setup\nrun it\n";
        string doc = DiagramDocs.Merge(existing, DiagramDocs.Render(Result()));
        Assert.StartsWith(existing.TrimEnd(), doc);
        Assert.EndsWith(DiagramDocs.EndMarker + "\n", doc);
    }

    [Fact]
    public void A_previous_generated_section_is_replaced_and_the_text_around_it_is_untouched()
    {
        string before = "# Title\n\nIntro text.\n\n", after = "\n\n## Other section\nStays exactly as written.\n";
        string old = DiagramDocs.StartMarker + "\nOLD CONTENT\n" + DiagramDocs.EndMarker;
        string doc = DiagramDocs.Merge(before + old + after, DiagramDocs.Render(Result()));

        Assert.StartsWith(before, doc);
        Assert.EndsWith(after, doc);
        Assert.DoesNotContain("OLD CONTENT", doc);
        Assert.Single(Regex.Matches(doc, Regex.Escape(DiagramDocs.StartMarker)));
    }

    [Fact]
    public void Running_it_twice_changes_nothing_the_second_time()
    {
        string section = DiagramDocs.Render(Result());
        string once = DiagramDocs.Merge("# T\n\ntext\n", section);
        Assert.Equal(once, DiagramDocs.Merge(once, section));
    }

    [Fact]
    public void Windows_line_endings_in_an_existing_file_are_kept()
    {
        string doc = DiagramDocs.Merge("# T\r\n\r\ntext\r\n", DiagramDocs.Render(Result()));
        Assert.DoesNotMatch("(?<!\r)\n", doc);
    }

    [Fact]
    public void A_start_marker_without_an_end_marker_does_not_eat_the_rest_of_the_file()
    {
        string existing = "# T\n" + DiagramDocs.StartMarker + "\nsomeone's text with no end marker\n## Important\nkeep me\n";
        string doc = DiagramDocs.Merge(existing, DiagramDocs.Render(Result()));
        Assert.Contains("keep me", doc);
        Assert.Contains("someone's text with no end marker", doc);
    }

    [Fact]
    public void Markdown_escaping_covers_html_emphasis_links_and_headings()
    {
        Assert.Equal("a &lt;b&gt; &amp; \\*c\\* \\[d\\]\\(e\\) \\# f", DiagramDocs.EscapeMarkdown("a <b> & *c* [d](e) # f"));
    }
}

public class DiagramPayloadTests
{
    [Fact]
    public void The_payload_refers_to_nodes_by_number_and_carries_what_the_ui_needs()
    {
        var graph = DiagramValidator.Validate(DiagramFixtures.Good, DiagramFixtures.Context()).Graph!;
        var result = new DiagramResult(@"C:\proj", graph, DiagramCompiler.Compile(graph), DateTime.UtcNow, "abc", true, new DiagramStats(9, 3, 100, 4, 0, false), new[] { "a note" }, 777);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(DiagramPayload.Build(result)));
        var root = doc.RootElement;

        Assert.Equal("diagram", root.GetProperty("type").GetString());
        Assert.Equal(@"C:\proj", root.GetProperty("path").GetString());
        Assert.True(root.GetProperty("stale").GetBoolean());
        Assert.StartsWith("flowchart TD", root.GetProperty("mermaid").GetString());

        var nodes = root.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, nodes.Select(n => n.GetProperty("i").GetInt32()));
        Assert.Equal("Services/A.cs", nodes[1].GetProperty("path").GetString());
        Assert.All(nodes, n => Assert.False(n.TryGetProperty("id", out _)));   // the model's ids never leave the PC

        var edges = root.GetProperty("edges").EnumerateArray().ToList();
        Assert.Equal((1, 2, "verified"), (edges[0].GetProperty("from").GetInt32(), edges[0].GetProperty("to").GetInt32(), edges[0].GetProperty("support").GetString()));
        Assert.Equal("inferred", edges[2].GetProperty("support").GetString());
        Assert.Equal("a note", root.GetProperty("notes")[0].GetString());
        Assert.Equal(777, root.GetProperty("tokens").GetInt64());
    }

    [Fact]
    public void The_largest_diagram_stays_far_below_the_relay_frame_limit()
    {
        string nodes = string.Join(",", Enumerable.Range(0, DiagramLimits.MaxNodes).Select(i => $"{{\"id\":\"n{i}\",\"label\":\"{new string('L', 70)}\"}}"));
        string edges = string.Join(",", Enumerable.Range(0, DiagramLimits.MaxEdges).Select(i => $"{{\"from\":\"n{i % 30}\",\"to\":\"n{i % 30 + 1}\",\"label\":\"{new string('e', 70)}{i}\"}}"));
        var graph = DiagramValidator.Validate($"{{\"overview\":\"{new string('o', 900)}\",\"groups\":[],\"nodes\":[{nodes}],\"edges\":[{edges}]}}", DiagramFixtures.Context()).Graph!;
        var result = new DiagramResult(@"C:\proj", graph, DiagramCompiler.Compile(graph), DateTime.UtcNow, null, false, null, new List<string>(), 0);
        Assert.True(JsonSerializer.Serialize(DiagramPayload.Build(result)).Length < 200_000);
    }
}

public class SafeFullPathTests
{
    [Fact]
    public void Paths_inside_the_project_resolve_even_when_the_target_does_not_exist_yet()
    {
        string root = Proj.Make(("a/b.txt", "x"));
        Assert.Equal(Path.Combine(root, "a", "b.txt"), DiagramIngest.SafeFullPath(root, "a/b.txt"));
        Assert.Equal(Path.Combine(root, "docs", "ARCHITECTURE.md"), DiagramIngest.SafeFullPath(root, "docs/ARCHITECTURE.md"));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("/etc/x")]
    [InlineData("C:/Windows/x")]
    [InlineData("a//b")]
    [InlineData("")]
    public void Paths_that_leave_the_project_are_refused(string rel)
    {
        Assert.Null(DiagramIngest.SafeFullPath(Proj.Make(("a.txt", "x")), rel));
    }

    [Fact]
    public void A_path_through_a_junction_is_refused_even_though_it_is_textually_inside()
    {
        string root = Proj.Make(("a.txt", "x"));
        string outside = TestEnvironment.NewTempDir("outside");
        var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(root, "docs")}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        mk.WaitForExit();
        Assert.True(Directory.Exists(Path.Combine(root, "docs")));

        Assert.Null(DiagramIngest.SafeFullPath(root, "docs/ARCHITECTURE.md"));   // writing here would land outside the project
        Assert.Null(DiagramIngest.SafeFullPath(root, "docs"));
        Assert.NotNull(DiagramIngest.SafeFullPath(root, "a.txt"));
    }
}
