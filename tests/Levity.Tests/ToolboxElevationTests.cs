using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Levity.Services;
using Levity.Services.WindowsTools;
using Xunit;

namespace Levity.Tests;

internal sealed class AdminEchoTool : WindowsTool
{
    public int Calls;
    public override string Name => "admin_echo";
    public override string Description => "A test tool that needs administrator rights to change things.";
    public override ToolAccess Access => ToolAccess.Change;
    public override bool NeedsAdminToChange => true;
    public override JsonObject InputSchema => Schema(new JsonObject { ["text"] = Prop("string", "Text."), ["dry_run"] = Prop("boolean", "Preview.") }, "text");
    public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
    {
        Calls++;
        if (ctx.DryRun) return Task.FromResult(ToolResult.Success("Dry run: would echo " + RequiredStr(args, "text")));
        ctx.Journal.Record("admin_echo", "echoed " + RequiredStr(args, "text"), "none", new JsonObject());
        return Task.FromResult(ToolResult.Success("echoed " + RequiredStr(args, "text")));
    }
}

internal sealed class PlainReadTool : WindowsTool
{
    public override string Name => "plain_read";
    public override string Description => "A read-only test tool.";
    public override JsonObject InputSchema => Schema(new JsonObject());
    public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx) => Task.FromResult(ToolResult.Success("read"));
}

public class ElevationTests
{
    private const string Safe = @"C:\Program Files\Levity\Levity.exe";

    [Theory]
    [InlineData(@"C:\Program Files\Levity\Levity.exe", true)]
    [InlineData(@"C:\Program Files (x86)\Levity\Levity.exe", true)]
    [InlineData(@"C:\Windows\System32\x.exe", true)]
    [InlineData(@"C:\Program Files Evil\Levity.exe", false)]
    [InlineData(@"C:\Users\sheri\source\repos\Levity\publish\Levity.exe", false)]
    [InlineData(@"C:\Program Files\..\Users\x\Levity.exe", false)]
    [InlineData(@"D:\Program Files\Levity.exe", false)]
    public void Elevation_is_only_offered_from_a_folder_ordinary_programs_cannot_write_to(string exe, bool allowed)
    {
        Assert.Equal(allowed, ElevationPolicy.WhyNotAvailable(exe, "0") == null);
    }

    [Fact]
    public void A_developer_can_turn_the_folder_check_off_and_only_with_exactly_1()
    {
        const string unsafeExe = @"C:\Users\x\Levity.exe";
        Assert.Null(ElevationPolicy.WhyNotAvailable(unsafeExe, "1"));
        Assert.NotNull(ElevationPolicy.WhyNotAvailable(unsafeExe, "true"));
        Assert.NotNull(ElevationPolicy.WhyNotAvailable(unsafeExe, ""));
    }

    private static (ToolRegistry reg, AdminEchoTool admin, string dataDir, ChangeJournal journal) Setup()
    {
        string dataDir = TestEnvironment.NewTempDir("elev");
        var reg = new ToolRegistry();
        var admin = new AdminEchoTool();
        reg.Add(admin);
        reg.Add(new PlainReadTool());
        return (reg, admin, dataDir, new ChangeJournal(Path.Combine(dataDir, "journal")));
    }

    private static string Write(string dataDir, string tool, JsonObject args, DateTime? created = null, string? folderOverride = null)
    {
        string folder = folderOverride ?? Path.Combine(dataDir, "elevation");
        Directory.CreateDirectory(folder);
        string id = Guid.NewGuid().ToString("N");
        string req = Path.Combine(folder, id + ".request.json");
        ElevatedRunner.WriteRequest(req, new ElevatedRequest(tool, args, dataDir, Path.Combine(folder, id + ".result.json"), created ?? DateTime.UtcNow));
        return req;
    }

    [Fact]
    public async Task A_valid_request_runs_that_one_tool_and_reports_back()
    {
        var (reg, _, dataDir, j) = Setup();
        string req = Write(dataDir, "admin_echo", ToolTestKit.Args("{\"text\":\"hi\"}"));
        int code = await new ElevatedRunner(reg).ExecuteAsync(req, j);
        Assert.Equal(0, code);
        var result = (JsonObject)JsonNode.Parse(File.ReadAllText(ElevatedRunner.ReadRequest(req)!.ResultFile))!;
        Assert.True((bool?)result["ok"]);
        Assert.Equal("echoed hi", (string?)result["text"]);
        Assert.Single(j.List());
    }

    [Fact]
    public async Task Tools_that_do_not_need_admin_unknown_tools_and_read_only_tools_are_refused()
    {
        var (reg, admin, dataDir, j) = Setup();
        foreach (string tool in new[] { "plain_read", "no_such_tool", "" })
        {
            string req = Write(dataDir, tool, new JsonObject());
            Assert.Equal(5, await new ElevatedRunner(reg).ExecuteAsync(req, j));
        }
        Assert.Equal(0, admin.Calls);
    }

    [Fact]
    public async Task Old_future_and_unreadable_requests_are_refused()
    {
        var (reg, admin, dataDir, j) = Setup();
        Assert.Equal(4, await new ElevatedRunner(reg).ExecuteAsync(Write(dataDir, "admin_echo", ToolTestKit.Args("{\"text\":\"x\"}"), DateTime.UtcNow.AddMinutes(-10)), j));
        Assert.Equal(4, await new ElevatedRunner(reg).ExecuteAsync(Write(dataDir, "admin_echo", ToolTestKit.Args("{\"text\":\"x\"}"), DateTime.UtcNow.AddHours(2)), j));
        string junk = Path.Combine(dataDir, "elevation", "junk.request.json");
        Directory.CreateDirectory(Path.GetDirectoryName(junk)!);
        File.WriteAllText(junk, "{ not json");
        Assert.Equal(2, await new ElevatedRunner(reg).ExecuteAsync(junk, j));
        Assert.Equal(0, admin.Calls);
    }

    [Fact]
    public async Task A_request_outside_levitys_elevation_folder_is_refused_and_results_are_never_written_elsewhere()
    {
        var (reg, admin, dataDir, j) = Setup();
        string elsewhere = TestEnvironment.NewTempDir("elsewhere");
        string req = Write(dataDir, "admin_echo", ToolTestKit.Args("{\"text\":\"x\"}"), folderOverride: elsewhere);
        Assert.Equal(3, await new ElevatedRunner(reg).ExecuteAsync(req, j));
        Assert.Equal(0, admin.Calls);

        string victim = Path.Combine(elsewhere, "victim.txt");
        File.WriteAllText(victim, "keep");
        string good = Path.Combine(dataDir, "elevation", "x.request.json");
        Directory.CreateDirectory(Path.GetDirectoryName(good)!);
        ElevatedRunner.WriteRequest(good, new ElevatedRequest("admin_echo", ToolTestKit.Args("{\"text\":\"x\"}"), dataDir, victim, DateTime.UtcNow));
        await new ElevatedRunner(reg).ExecuteAsync(good, j);
        Assert.Equal("keep", File.ReadAllText(victim));
    }

    [Fact]
    public async Task The_whole_round_trip_works_through_the_elevator_with_a_stand_in_for_the_windows_prompt()
    {
        if (Elevation.IsElevated) return;
        var (reg, _, dataDir, _) = Setup();
        var journal = new ChangeJournal(Path.Combine(dataDir, "journal"));
        var elevator = new Elevator(dataDir, Safe, async file => { await new ElevatedRunner(reg).ExecuteAsync(file, journal); return null; });

        var r = await elevator.RunAsync(reg, "admin_echo", ToolTestKit.Args("{\"text\":\"via prompt\"}"), CancellationToken.None);
        Assert.True(r.Ok, r.Text);
        Assert.Equal("echoed via prompt", r.Text);
        Assert.Single(journal.List());
        Assert.Empty(Directory.GetFiles(Path.Combine(dataDir, "elevation")));
    }

    [Fact]
    public async Task A_declined_prompt_and_a_silent_failure_are_reported_and_a_tool_that_needs_no_admin_is_not_elevated()
    {
        if (Elevation.IsElevated) return;
        var (reg, admin, dataDir, _) = Setup();
        var declined = new Elevator(dataDir, Safe, _ => Task.FromResult<string?>("The Windows prompt was declined, so nothing was changed."));
        var r1 = await declined.RunAsync(reg, "admin_echo", ToolTestKit.Args("{\"text\":\"x\"}"), CancellationToken.None);
        Assert.False(r1.Ok);
        Assert.Contains("declined", r1.Text);

        var silent = new Elevator(dataDir, Safe, _ => Task.FromResult<string?>(null)) { WaitSeconds = 1 };
        var r2 = await silent.RunAsync(reg, "admin_echo", ToolTestKit.Args("{\"text\":\"x\"}"), CancellationToken.None);
        Assert.False(r2.Ok);
        Assert.Contains("did not report back", r2.Text);

        bool launched = false;
        var spy = new Elevator(dataDir, Safe, _ => { launched = true; return Task.FromResult<string?>(null); });
        var r3 = await spy.RunAsync(reg, "plain_read", new JsonObject(), CancellationToken.None);
        Assert.False(r3.Ok);
        Assert.False(launched);
        Assert.Equal(0, admin.Calls);
    }

    [Fact]
    public async Task Nothing_is_launched_from_an_unprotected_folder()
    {
        if (Elevation.IsElevated || Environment.GetEnvironmentVariable(ElevationPolicy.OverrideVariable) == "1") return;
        var (reg, _, dataDir, _) = Setup();
        bool launched = false;
        var e = new Elevator(dataDir, @"C:\Users\someone\Levity.exe", _ => { launched = true; return Task.FromResult<string?>(null); });
        var r = await e.RunAsync(reg, "admin_echo", ToolTestKit.Args("{\"text\":\"x\"}"), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("Program Files", r.Text);
        Assert.False(launched);
    }

    [Fact]
    public async Task The_run_elevated_tool_lists_only_admin_tools_and_previews_without_launching()
    {
        var (reg, _, dataDir, j) = Setup();
        bool launched = false;
        var elevator = new Elevator(dataDir, Safe, _ => { launched = true; return Task.FromResult<string?>(null); });
        var tool = new RunElevatedTool(elevator, reg);
        reg.Add(tool);

        var allowed = ((JsonArray)tool.InputSchema["properties"]!["tool"]!["enum"]!).Select(n => (string?)n).ToArray();
        Assert.Equal(new[] { "admin_echo" }, allowed);
        Assert.Contains("admin_echo", tool.Description);

        var dry = await reg.CallAsync("run_elevated", ToolTestKit.Args("{\"tool\":\"admin_echo\",\"arguments\":{\"text\":\"hello\"},\"dry_run\":true}"), ToolTestKit.Ctx(j));
        Assert.True(dry.Ok, dry.Text);
        Assert.Contains("Dry run", dry.Text);
        Assert.False(launched);
        Assert.Empty(j.List());
    }
}

public class ToolHostTests
{
    private static (ToolHost host, AppSettings settings, List<string> calls) Make(int exit = 0)
    {
        var settings = new AppSettings();
        var calls = new List<string>();
        var host = new ToolHost(settings, TestEnvironment.NewTempDir("host"), @"C:\Program Files\Levity\Levity.exe")
        {
            RunAgy = args => { calls.Add(string.Join(" ", args)); return Task.FromResult(new Proc.Output(exit, exit == 0 ? "ok" : "no thanks", false)); },
        };
        return (host, settings, calls);
    }

    [Fact]
    public void The_register_command_puts_every_flag_before_the_name_as_agy_requires()
    {
        var args = ToolHost.AddArguments("http://127.0.0.1:5000/mcp", "tok");
        Assert.Equal(new[] { "mcp", "add", "--header", "Authorization: Bearer tok", "levity-windows", "http://127.0.0.1:5000/mcp" }, args);
        Assert.True(args.ToList().IndexOf("--header") < args.ToList().IndexOf("levity-windows"));
        Assert.Equal(new[] { "mcp", "remove", "levity-windows" }, ToolHost.RemoveArguments());
    }

    [Fact]
    public async Task Turning_it_on_starts_the_server_registers_it_and_it_answers_with_the_token()
    {
        var (host, settings, calls) = Make();
        using (host)
        {
            var (ok, message) = await host.EnableAsync();
            Assert.True(ok, message);
            Assert.True(host.Running);
            Assert.True(settings.WindowsToolsEnabled);
            Assert.NotEmpty(settings.WindowsToolsToken!);
            Assert.Equal(host.Port, settings.WindowsToolsPort);
            Assert.Equal(new[] { string.Join(" ", ToolHost.AddArguments(host.Url, settings.WindowsToolsToken!)) }, calls);

            using var http = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Post, host.Url) { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json") };
            request.Headers.Add("Authorization", "Bearer " + settings.WindowsToolsToken);
            var reply = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            var tools = (JsonArray)JsonNode.Parse(await reply.Content.ReadAsStringAsync())!["result"]!["tools"]!;
            Assert.True(tools.Count >= 30, $"{tools.Count} tools");
            Assert.Contains(tools.Select(t => (string?)t!["name"]), n => n == "run_elevated");
            Assert.Contains(tools.Select(t => (string?)t!["name"]), n => n == "settings_get");

            using var bad = new HttpRequestMessage(HttpMethod.Post, host.Url) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(bad)).StatusCode);
        }
    }

    [Fact]
    public async Task If_the_assistant_refuses_the_server_it_is_switched_off_again_and_the_reason_is_shown()
    {
        var (host, settings, _) = Make(exit: 1);
        using (host)
        {
            var (ok, message) = await host.EnableAsync();
            Assert.False(ok);
            Assert.Contains("no thanks", message);
            Assert.False(host.Running);
            Assert.False(settings.WindowsToolsEnabled);
        }
    }

    [Fact]
    public async Task Turning_it_off_stops_the_server_and_removes_the_entry()
    {
        var (host, settings, calls) = Make();
        using (host)
        {
            await host.EnableAsync();
            var (ok, _) = await host.DisableAsync();
            Assert.True(ok);
            Assert.False(host.Running);
            Assert.False(settings.WindowsToolsEnabled);
            Assert.Equal("mcp remove levity-windows", calls.Last());
        }
    }

    [Fact]
    public async Task At_start_up_the_server_comes_back_on_the_same_port_without_registering_again()
    {
        var first = Make();
        int port;
        using (first.host)
        {
            await first.host.EnableAsync();
            port = first.host.Port;
        }
        var calls = new List<string>();
        using var again = new ToolHost(first.settings, TestEnvironment.NewTempDir("host"), @"C:\Program Files\Levity\Levity.exe")
        {
            RunAgy = args => { calls.Add(string.Join(" ", args)); return Task.FromResult(new Proc.Output(0, "", false)); },
        };
        await again.ResumeAsync();
        Assert.True(again.Running);
        Assert.Equal(port, again.Port);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task If_the_saved_port_is_taken_it_moves_and_updates_the_assistants_entry()
    {
        var first = Make();
        using (first.host) await first.host.EnableAsync();
        var settings = first.settings;
        var blocker = new System.Net.Sockets.TcpListener(IPAddress.Loopback, settings.WindowsToolsPort);
        try { blocker.Start(); } catch { return; }   // the port was reused already: nothing to test
        try
        {
            var calls = new List<string>();
            using var again = new ToolHost(settings, TestEnvironment.NewTempDir("host"), @"C:\Program Files\Levity\Levity.exe")
            {
                RunAgy = args => { calls.Add(string.Join(" ", args)); return Task.FromResult(new Proc.Output(0, "", false)); },
            };
            await again.ResumeAsync();
            Assert.True(again.Running);
            Assert.NotEqual(((IPEndPoint)blocker.LocalEndpoint).Port, again.Port);
            Assert.Single(calls);
            Assert.Contains(again.Url, calls[0]);
        }
        finally { blocker.Stop(); }
    }

    [Fact]
    public async Task Resume_does_nothing_when_the_switch_is_off()
    {
        var (host, settings, calls) = Make();
        using (host)
        {
            settings.WindowsToolsEnabled = false;
            await host.ResumeAsync();
            Assert.False(host.Running);
            Assert.Empty(calls);
        }
    }
}

public class ToolboxDocsAndFactsTests
{
    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Levity.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? "";
    }

    [Fact]
    public void The_windows_tools_page_lists_every_tool_and_says_nothing_about_tools_that_do_not_exist()
    {
        string docs = Path.Combine(Repo(), "docs", "WINDOWS.md");
        if (!File.Exists(docs)) return;
        string text = File.ReadAllText(docs);
        var box = Toolbox.Create(TestEnvironment.NewTempDir("box"), new Elevator(TestEnvironment.NewTempDir("e"), @"C:\Program Files\Levity\Levity.exe"));
        foreach (var t in box.Registry.All) Assert.Contains($"`{t.Name}`", text);
        var documented = System.Text.RegularExpressions.Regex.Matches(text, @"^\| `([a-z_]+)` \|", System.Text.RegularExpressions.RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(box.Registry.All.Select(t => t.Name).OrderBy(n => n).ToList(), documented.OrderBy(n => n).ToList());
    }

    [Fact]
    public void Facts_about_this_pc_go_in_front_of_the_first_message_only_and_never_change_the_message()
    {
        string first = AgentLaunch.WithHint("fix the bug", true, "This PC: Windows 11 Pro, 32 GB RAM.");
        Assert.StartsWith(AgentLaunch.EfficiencyHint, first, StringComparison.Ordinal);
        Assert.Contains("[This PC: Windows 11 Pro, 32 GB RAM.]", first);
        Assert.EndsWith("fix the bug", first, StringComparison.Ordinal);
        Assert.Equal("later message", AgentLaunch.WithHint("later message", false, "This PC: x"));
        Assert.DoesNotContain("[]", AgentLaunch.WithHint("hi", true, "  "));
        Assert.Equal(AgentLaunch.EfficiencyHint + "hi", AgentLaunch.WithHint("hi", true, null));
    }
}
