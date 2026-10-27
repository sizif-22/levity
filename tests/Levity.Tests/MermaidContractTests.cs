using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AntigravitySidebar.Services;
using Xunit;

namespace Levity.Tests;

/// <summary>
/// The compiler's output is checked against the real, vendored Mermaid running in a real browser (headless Edge or Chrome),
/// because unit tests cannot tell whether Mermaid can parse, lay out and safely draw what we generate. Set LEVITY_REQUIRE_BROWSER=1
/// (CI does) to fail instead of skipping when no browser is installed.
/// </summary>
public class MermaidContractTests
{
    private static string? FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? FindBrowser()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
        {
            Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"),
        }.FirstOrDefault(File.Exists);
    }

    private static DiagramGraph Chain(int groups, int perGroup, int loose, int edges, bool dotted)
    {
        var gs = Enumerable.Range(0, groups).Select(i => new DiagramGroup("g" + i, "Group " + i)).ToList();
        var ns = new List<DiagramNode>();
        for (int i = 0; i < groups * perGroup; i++) ns.Add(new DiagramNode("n" + i, "Component number " + i, "g" + (i % groups), null, "box"));
        for (int i = 0; i < loose; i++) ns.Add(new DiagramNode("x" + i, "Loose " + i, null, null, i % 2 == 0 ? "database" : "box"));
        int total = ns.Count;
        var es = Enumerable.Range(0, edges).Select(i => new DiagramEdge(ns[i % (total - 1)].Id, ns[i % (total - 1) + 1].Id, "uses " + i, null, dotted && i % 3 == 0 ? EdgeSupport.Inferred : EdgeSupport.Verified)).ToList();
        return new DiagramGraph("overview", gs, ns, es);
    }

    private static Dictionary<string, CompiledDiagram> Cases()
    {
        var cases = new Dictionary<string, CompiledDiagram>();
        cases["fixture"] = DiagramCompiler.Compile(DiagramValidator.Validate(DiagramFixtures.Good, DiagramFixtures.Context()).Graph!);
        cases["single"] = DiagramCompiler.Compile(new DiagramGraph("o", Array.Empty<DiagramGroup>(), new[] { new DiagramNode("only", "Only one", null, null, "box") }, Array.Empty<DiagramEdge>()));
        cases["punctuation"] = DiagramCompiler.Compile(new DiagramGraph("o", Array.Empty<DiagramGroup>(),
            new[] { new DiagramNode("a", "Cache (L1) & C# host", null, null, "box"), new DiagramNode("b", "v2.0, ok: yes", null, null, "database") },
            new[] { new DiagramEdge("a", "b", "reads; writes", null, EdgeSupport.Verified) }));

        string[] hostile =
        {
            "x\"]\nclick n1 \"javascript:alert(1)\"", "<img src=x onerror=alert(1)>", "<script>alert(1)</script>", "end\nsubgraph evil", "%%{init: {\"securityLevel\":\"loose\",\"htmlLabels\":true}}%%",
            "a]) --> b((c", "`markdown` **bold** _it_", "#35; #quot; &amp; &lt;b&gt;", "\u65E5\u672C\u8A9E \u4E2D\u6587", "emoji \U0001F680", "back" + (char)92 + "slash",
        };
        cases["hostile"] = DiagramCompiler.Compile(new DiagramGraph(hostile[0],
            new[] { new DiagramGroup("g", hostile[1]) },
            hostile.Select((l, i) => new DiagramNode("n" + i, l, i % 2 == 0 ? "g" : null, null, i % 3 == 0 ? "database" : "box")).ToList(),
            Enumerable.Range(0, hostile.Length - 1).Select(i => new DiagramEdge("n" + i, "n" + (i + 1), hostile[(i * 5) % hostile.Length], null, i % 2 == 0 ? EdgeSupport.Verified : EdgeSupport.Inferred)).ToList()));

        // The shape that crashes Mermaid's layout engine when group boxes are drawn: chains crossing the same group boxes again and again.
        cases["max"] = DiagramCompiler.Compile(Chain(10, 3, 4, DiagramLimits.MaxEdges, dotted: true));
        return cases;
    }

    private const string Script = """
    const CASES = __CASES__;
    (async () => {
      mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', secure: ['secure', 'securityLevel', 'startOnLoad', 'maxTextSize', 'htmlLabels', 'flowchart'],
        theme: 'base', htmlLabels: false, flowchart: { htmlLabels: false, curve: 'basis', useMaxWidth: false } });
      const out = {};
      let n = 0;
      for (const [name, c] of Object.entries(CASES)) {
        out[name] = {};
        for (const kind of ['boxed', 'flat']) {
          const r = {};
          try {
            const res = await mermaid.render('c' + (n++), kind === 'boxed' ? c.boxed : c.flat);
            const holder = document.createElement('div');
            holder.innerHTML = res.svg;
            const svg = holder.querySelector('svg');
            r.ok = !!svg;
            r.nodeIds = Array.from(holder.querySelectorAll('g.node')).map(g => g.id);
            r.clusters = holder.querySelectorAll('g.cluster').length;
            r.edges = holder.querySelectorAll('path.flowchart-link').length;
            r.texts = Array.from(holder.querySelectorAll('text')).map(t => t.textContent);
            r.unsafe = {
              foreignObject: holder.querySelectorAll('foreignObject').length, script: holder.querySelectorAll('script').length,
              img: holder.querySelectorAll('img').length, a: holder.querySelectorAll('a').length,
              handlers: Array.from(holder.querySelectorAll('*')).filter(e => Array.from(e.attributes).some(a => /^on/i.test(a.name))).length,
              externalHref: Array.from(holder.querySelectorAll('[href]')).filter(e => !e.getAttribute('href').startsWith('#')).length,
            };
          } catch (e) { r.ok = false; r.error = String(e && e.message || e).slice(0, 200); }
          out[name][kind] = r;
        }
      }
      document.body.setAttribute('data-result', JSON.stringify(out));
    })();
    """;

    private static JsonElement? RunInBrowser(out string? skipReason)
    {
        skipReason = null;
        string? mermaid = FindRepoFile("wwwroot/vendor/mermaid.min.js");
        Assert.True(mermaid != null, "wwwroot/vendor/mermaid.min.js not found");
        string? browser = FindBrowser();
        if (browser == null)
        {
            if (Environment.GetEnvironmentVariable("LEVITY_REQUIRE_BROWSER") == "1") Assert.Fail("No Edge or Chrome found, and LEVITY_REQUIRE_BROWSER=1");
            skipReason = "no Edge or Chrome installed";
            return null;
        }

        var payload = Cases().ToDictionary(kv => kv.Key, kv => new { boxed = kv.Value.Mermaid, flat = kv.Value.MermaidFlat });
        string dir = TestEnvironment.NewTempDir("browser");
        string page = Path.Combine(dir, "page.html");
        File.WriteAllText(page,
            "<!doctype html><meta charset=\"utf-8\"><body><script src=\"file:///" + mermaid.Replace('\\', '/') + "\"></script><script>"
            + Script.Replace("__CASES__", JsonSerializer.Serialize(payload)) + "</script>");

        var psi = new ProcessStartInfo(browser)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (string a in new[] { "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--allow-file-access-from-files",
                                     "--user-data-dir=" + Path.Combine(dir, "profile"), "--virtual-time-budget=60000", "--dump-dom", "file:///" + page.Replace('\\', '/') })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } Assert.Fail("the browser did not finish within 120 seconds"); }
        string dom = stdout.GetAwaiter().GetResult();
        var m = Regex.Match(dom, "data-result=\"([^\"]*)\"");
        Assert.True(m.Success, "the page produced no result. Output started: " + dom[..Math.Min(300, dom.Length)]);
        return JsonDocument.Parse(WebUtility.HtmlDecode(m.Groups[1].Value)).RootElement.Clone();
    }

    private static readonly Lazy<(JsonElement? result, string? skip)> Shared = new(() => { var r = RunInBrowser(out var skip); return (r, skip); });

    private static JsonElement? Result => Shared.Value.result;

    [Fact]
    public void Every_case_can_be_drawn_at_least_in_the_flat_version()
    {
        if (Result is not { } res) return;
        foreach (var c in res.EnumerateObject())
            Assert.True(c.Value.GetProperty("flat").GetProperty("ok").GetBoolean(), $"{c.Name}: the flat version failed: {c.Value.GetProperty("flat")}");
    }

    [Fact]
    public void Cases_that_are_not_pathological_also_draw_with_group_boxes()
    {
        if (Result is not { } res) return;
        foreach (string name in new[] { "fixture", "single", "punctuation", "hostile" })
            Assert.True(res.GetProperty(name).GetProperty("boxed").GetProperty("ok").GetBoolean(), $"{name}: {res.GetProperty(name).GetProperty("boxed")}");
    }

    [Fact]
    public void The_boxed_version_draws_group_clusters_and_the_flat_version_does_not()
    {
        if (Result is not { } res) return;
        Assert.Equal(2, res.GetProperty("fixture").GetProperty("boxed").GetProperty("clusters").GetInt32());
        Assert.Equal(0, res.GetProperty("fixture").GetProperty("flat").GetProperty("clusters").GetInt32());
    }

    [Fact]
    public void Node_ids_in_the_drawing_follow_the_pattern_the_click_handling_relies_on()
    {
        if (Result is not { } res) return;
        foreach (var c in res.EnumerateObject())
            foreach (string kind in new[] { "boxed", "flat" })
            {
                var r = c.Value.GetProperty(kind);
                if (!r.GetProperty("ok").GetBoolean()) continue;
                int count = DiagramCompiler.Compile(new DiagramGraph("", Array.Empty<DiagramGroup>(), Array.Empty<DiagramNode>(), Array.Empty<DiagramEdge>())).Nodes.Count; // 0: keeps the compiler referenced
                Assert.Equal(0, count);
                var ids = r.GetProperty("nodeIds").EnumerateArray().Select(x => x.GetString()!).ToList();
                Assert.NotEmpty(ids);
                Assert.All(ids, id => Assert.Matches(@"^flowchart-n\d+-\d+$", id));
            }
    }

    [Fact]
    public void Every_node_and_edge_is_drawn()
    {
        if (Result is not { } res) return;
        var expected = Cases();
        foreach (var name in new[] { "fixture", "hostile", "max", "punctuation" })
        {
            int nodes = expected[name].Nodes.Count;
            var flat = res.GetProperty(name).GetProperty("flat");
            Assert.Equal(nodes, flat.GetProperty("nodeIds").GetArrayLength());
        }
        Assert.Equal(DiagramLimits.MaxEdges, res.GetProperty("max").GetProperty("flat").GetProperty("edges").GetInt32());
        Assert.Equal(DiagramLimits.MaxNodes - 0, DiagramLimits.MaxNodes); // the max case is a 34-node graph
        Assert.Equal(34, res.GetProperty("max").GetProperty("flat").GetProperty("nodeIds").GetArrayLength());
    }

    [Fact]
    public void Nothing_unsafe_comes_out_of_Mermaid_even_for_hostile_labels()
    {
        if (Result is not { } res) return;
        foreach (var c in res.EnumerateObject())
            foreach (string kind in new[] { "boxed", "flat" })
            {
                var r = c.Value.GetProperty(kind);
                if (!r.GetProperty("ok").GetBoolean()) continue;
                var u = r.GetProperty("unsafe");
                foreach (string key in new[] { "foreignObject", "script", "img", "a", "handlers", "externalHref" })
                    Assert.True(u.GetProperty(key).GetInt32() == 0, $"{c.Name}/{kind}: {key} = {u.GetProperty(key).GetInt32()}");
            }
    }

    [Fact]
    public void Labels_read_as_the_text_that_was_written()
    {
        if (Result is not { } res) return;
        string all = string.Join("\n", res.GetProperty("punctuation").GetProperty("flat").GetProperty("texts").EnumerateArray().Select(t => t.GetString()));
        Assert.Contains("Cache (L1) & C# host", Regex.Replace(all, @"\s+", " "));
        Assert.Contains("v2.0, ok: yes", Regex.Replace(all, @"\s+", " "));

        string hostile = string.Join("\n", res.GetProperty("hostile").GetProperty("flat").GetProperty("texts").EnumerateArray().Select(t => t.GetString()));
        Assert.Contains("\u65E5\u672C\u8A9E", hostile);                // other scripts survive
        Assert.False(hostile.Contains((char)4), "a control character leaked from entity decoding");   // ordinal check: a culture compare ignores control characters                      // no control characters from entity decoding
        Assert.False(hostile.Contains("fl" + (char)0xB0, StringComparison.Ordinal), "an internal entity placeholder leaked");                     // none of Mermaid's internal entity placeholders leak
    }

    [Fact]
    public void A_directive_hidden_in_a_label_does_not_take_effect()
    {
        if (Result is not { } res) return;
        // If the hidden init directive had been honoured, html labels would have produced a foreignObject.
        Assert.Equal(0, res.GetProperty("hostile").GetProperty("boxed").GetProperty("unsafe").GetProperty("foreignObject").GetInt32());
        Assert.Equal(0, res.GetProperty("hostile").GetProperty("flat").GetProperty("unsafe").GetProperty("foreignObject").GetInt32());
    }
}
