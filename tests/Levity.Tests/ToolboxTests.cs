using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Levity.Services.WindowsTools;
using Microsoft.Win32;
using Xunit;

namespace Levity.Tests;

internal sealed class FakeEnvStore : IEnvironmentStore
{
    public Dictionary<string, Dictionary<string, string>> Data { get; } = new() { ["user"] = new(StringComparer.OrdinalIgnoreCase), ["machine"] = new(StringComparer.OrdinalIgnoreCase), ["process"] = new(StringComparer.OrdinalIgnoreCase) };
    public string? Get(string scope, string name) => Data[scope].TryGetValue(name, out var v) ? v : null;
    public IDictionary<string, string> All(string scope) => new SortedDictionary<string, string>(Data[scope], StringComparer.OrdinalIgnoreCase);
    public void Set(string scope, string name, string? value) { if (value == null) Data[scope].Remove(name); else Data[scope][name] = value; }
}

internal static class ToolTestKit
{
    public static ChangeJournal NewJournal() => new(Path.Combine(TestEnvironment.NewTempDir("journal")));
    public static ToolContext Ctx(ChangeJournal j, bool dry = false) => new() { Journal = j, DryRun = dry, Cancel = CancellationToken.None };
    public static JsonObject Args(string json) => (JsonObject)JsonNode.Parse(json)!;

    /// <summary>A throw-away registry area under HKCU, removed afterwards.</summary>
    public sealed class RegistrySandbox : IDisposable
    {
        public string Path { get; } = @"Software\LevityTests\" + Guid.NewGuid().ToString("N");
        public void Dispose() { try { Registry.CurrentUser.DeleteSubKeyTree(Path, false); } catch { } }
    }
}

public class ChangeJournalTests
{
    [Fact]
    public void A_recorded_change_is_listed_newest_first_and_can_be_found()
    {
        var j = ToolTestKit.NewJournal();
        var a = j.Record("t", "first", "k", new JsonObject { ["x"] = 1 });
        Thread.Sleep(15);
        var b = j.Record("t", "second", "k", new JsonObject { ["x"] = 2 });

        var list = j.List();
        Assert.Equal(new[] { b.Id, a.Id }, list.Select(e => e.Id).ToArray());
        Assert.Equal("first", j.Find(a.Id)!.Summary);
        Assert.Equal(2, (int)j.Find(b.Id)!.UndoData["x"]!);
    }

    [Fact]
    public void Revert_runs_the_matching_undo_once_and_marks_the_entry()
    {
        var j = ToolTestKit.NewJournal();
        int calls = 0;
        string? seen = null;
        j.RegisterUndo("thing", d => { calls++; seen = (string?)d["old"]; return null; });
        var e = j.Record("t", "changed a thing", "thing", new JsonObject { ["old"] = "before" });

        Assert.Null(j.Revert(e.Id));
        Assert.Equal(1, calls);
        Assert.Equal("before", seen);
        Assert.True(j.Find(e.Id)!.Reverted);
        Assert.Contains("already", j.Revert(e.Id)!);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_failing_undo_leaves_the_entry_open_and_says_why()
    {
        var j = ToolTestKit.NewJournal();
        j.RegisterUndo("bad", _ => "the value is locked");
        var e = j.Record("t", "x", "bad", new JsonObject());
        Assert.Equal("the value is locked", j.Revert(e.Id));
        Assert.False(j.Find(e.Id)!.Reverted);
    }

    [Fact]
    public void An_undo_that_throws_is_reported_not_propagated()
    {
        var j = ToolTestKit.NewJournal();
        j.RegisterUndo("boom", _ => throw new InvalidOperationException("kaboom"));
        var e = j.Record("t", "x", "boom", new JsonObject());
        Assert.Equal("kaboom", j.Revert(e.Id));
    }

    [Fact]
    public void Unknown_ids_unknown_kinds_and_hostile_ids_are_refused()
    {
        var j = ToolTestKit.NewJournal();
        Assert.Contains("not in the journal", j.Revert("nope")!);
        Assert.Null(j.Find(@"..\..\evil"));
        Assert.Null(j.Find("a/b"));
        Assert.Null(j.Find(""));
        var e = j.Record("t", "x", "never-registered", new JsonObject());
        Assert.Contains("cannot undo", j.Revert(e.Id)!);
    }

    [Fact]
    public void Entries_older_than_thirty_days_are_dropped_and_damaged_files_are_skipped()
    {
        string dir = TestEnvironment.NewTempDir("journal");
        var j = new ChangeJournal(dir);
        j.Record("t", "fresh", "k", new JsonObject());
        File.WriteAllText(Path.Combine(dir, "old000000001.json"), "{\"id\":\"old000000001\",\"timeUtc\":\"" + DateTime.UtcNow.AddDays(-45).ToString("o") + "\",\"tool\":\"t\",\"summary\":\"old\",\"undoKind\":\"k\",\"undoData\":{},\"reverted\":false}");
        File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json");

        var list = j.List();
        Assert.Single(list);
        Assert.Equal("fresh", list[0].Summary);
        Assert.False(File.Exists(Path.Combine(dir, "old000000001.json")));
    }

    [Fact]
    public void Nothing_is_left_half_written()
    {
        string dir = TestEnvironment.NewTempDir("journal");
        var j = new ChangeJournal(dir);
        for (int i = 0; i < 5; i++) j.Record("t", "x" + i, "k", new JsonObject());
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }
}

public class RegistryUndoTests
{
    [Fact]
    public void A_snapshot_puts_back_values_of_every_kind_and_removes_the_ones_that_were_not_there()
    {
        using var sb = new ToolTestKit.RegistrySandbox();
        using (var k = Registry.CurrentUser.CreateSubKey(sb.Path))
        {
            k.SetValue("Number", 7, RegistryValueKind.DWord);
            k.SetValue("Text", "hello", RegistryValueKind.String);
            k.SetValue("Bytes", new byte[] { 1, 2, 3, 250 }, RegistryValueKind.Binary);
            k.SetValue("Many", new[] { "a", "b" }, RegistryValueKind.MultiString);
        }
        var snap = RegistryUndo.Snapshot("HKCU", sb.Path, new[] { "Number", "Text", "Bytes", "Many", "Missing" });
        using (var k = Registry.CurrentUser.OpenSubKey(sb.Path, true)!)
        {
            k.SetValue("Number", 99, RegistryValueKind.DWord);
            k.DeleteValue("Text");
            k.SetValue("Bytes", new byte[] { 9 }, RegistryValueKind.Binary);
            k.SetValue("Missing", "now present");
        }

        Assert.Null(RegistryUndo.Restore((JsonObject)JsonNode.Parse(snap.ToJsonString())!));   // through JSON, like a journal file

        using var after = Registry.CurrentUser.OpenSubKey(sb.Path)!;
        Assert.Equal(7, after.GetValue("Number"));
        Assert.Equal("hello", after.GetValue("Text"));
        Assert.Equal(new byte[] { 1, 2, 3, 250 }, (byte[])after.GetValue("Bytes")!);
        Assert.Equal(new[] { "a", "b" }, (string[])after.GetValue("Many")!);
        Assert.Null(after.GetValue("Missing"));
    }
}

public class McpServerTests
{
    private static (McpServer server, ToolRegistry reg, ChangeJournal journal) Make(string token = "secret-token")
    {
        var reg = new ToolRegistry();
        reg.Add(new EchoTool());
        reg.Add(new ChangeEchoTool());
        var j = ToolTestKit.NewJournal();
        return (new McpServer(reg, j, token), reg, j);
    }

    private sealed class EchoTool : WindowsTool
    {
        public override string Name => "echo";
        public override string Description => "Echo the text back.";
        public override JsonObject InputSchema => Schema(new JsonObject { ["text"] = Prop("string", "What to echo.") }, "text");
        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx) => Task.FromResult(ToolResult.Success("said: " + RequiredStr(args, "text"), new JsonObject { ["n"] = Int(args, "n", 1, 1, 5) }));
    }

    private sealed class ChangeEchoTool : WindowsTool
    {
        public override string Name => "change_echo";
        public override string Description => "A tool that claims to change something.";
        public override ToolAccess Access => ToolAccess.Change;
        public override bool NeedsAdminToChange => true;
        public override JsonObject InputSchema => Schema(new JsonObject());
        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx) => Task.FromResult(ctx.DryRun ? ToolResult.Success("dry") : throw new InvalidOperationException("disk on fire"));
    }

    private static JsonObject Obj(string? json) => (JsonObject)JsonNode.Parse(json!)!;

    [Fact]
    public async Task Initialize_returns_the_server_info_and_echoes_a_supported_protocol_version()
    {
        var (s, _, _) = Make();
        var r = Obj(await s.HandleRpcAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\"}}"));
        Assert.Equal("2025-03-26", (string?)r["result"]!["protocolVersion"]);
        Assert.Equal("levity-windows", (string?)r["result"]!["serverInfo"]!["name"]);
        Assert.NotNull(r["result"]!["capabilities"]!["tools"]);
        var odd = Obj(await s.HandleRpcAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"1999-01-01\"}}"));
        Assert.Contains((string?)odd["result"]!["protocolVersion"], McpServer.SupportedProtocols);
    }

    [Fact]
    public async Task The_tool_list_has_names_descriptions_schemas_and_read_only_hints()
    {
        var (s, _, _) = Make();
        var r = Obj(await s.HandleRpcAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\"}"));
        var tools = (JsonArray)r["result"]!["tools"]!;
        Assert.Equal(new[] { "change_echo", "echo" }, tools.Select(t => (string?)t!["name"]).ToArray());
        var echo = tools.First(t => (string?)t!["name"] == "echo")!;
        Assert.True((bool?)echo["annotations"]!["readOnlyHint"]);
        Assert.Equal("object", (string?)echo["inputSchema"]!["type"]);
        Assert.Equal("text", (string?)((JsonArray)echo["inputSchema"]!["required"]!)[0]);
        var change = tools.First(t => (string?)t!["name"] == "change_echo")!;
        Assert.False((bool?)change["annotations"]!["readOnlyHint"]);
        Assert.Contains("administrator", (string?)change["description"]);
    }

    [Fact]
    public async Task A_tool_call_returns_text_and_data_and_a_bad_call_is_an_error_result_not_a_crash()
    {
        var (s, _, _) = Make();
        var ok = Obj(await s.HandleRpcAsync("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{\"text\":\"hi\",\"n\":3}}}"));
        Assert.False((bool?)ok["result"]!["isError"]);
        string text = (string?)ok["result"]!["content"]![0]!["text"]!;
        Assert.Contains("said: hi", text);
        Assert.Contains("\"n\": 3", text);

        var missing = Obj(await s.HandleRpcAsync("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{}}}"));
        Assert.True((bool?)missing["result"]!["isError"]);
        Assert.Contains("'text' is required", (string?)missing["result"]!["content"]![0]!["text"]);

        var range = Obj(await s.HandleRpcAsync("{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{\"text\":\"x\",\"n\":99}}}"));
        Assert.True((bool?)range["result"]!["isError"]);

        var unknown = Obj(await s.HandleRpcAsync("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"format_c\",\"arguments\":{}}}"));
        Assert.True((bool?)unknown["result"]!["isError"]);
        Assert.Contains("Unknown tool", (string?)unknown["result"]!["content"]![0]!["text"]);

        var boom = Obj(await s.HandleRpcAsync("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/call\",\"params\":{\"name\":\"change_echo\",\"arguments\":{}}}"));
        Assert.True((bool?)boom["result"]!["isError"]);
        Assert.Contains("disk on fire", (string?)boom["result"]!["content"]![0]!["text"]);

        var dry = Obj(await s.HandleRpcAsync("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"change_echo\",\"arguments\":{\"dry_run\":true}}}"));
        Assert.False((bool?)dry["result"]!["isError"]);
    }

    [Fact]
    public async Task Notifications_get_no_reply_garbage_gets_an_error_and_batches_are_answered_in_order()
    {
        var (s, _, _) = Make();
        Assert.Null(await s.HandleRpcAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
        Assert.Contains("-32700", await s.HandleRpcAsync("not json {"));
        Assert.Contains("-32601", await s.HandleRpcAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"does/not/exist\"}"));
        Assert.Contains("-32602", await s.HandleRpcAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{}}"));
        var batch = (JsonArray)JsonNode.Parse((await s.HandleRpcAsync("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"},{\"jsonrpc\":\"2.0\",\"method\":\"notifications/x\"},{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}]"))!)!;
        Assert.Equal(new[] { 1, 2 }, batch.Select(b => (int)b!["id"]!).ToArray());
    }

    [Fact]
    public async Task A_huge_reply_is_cut()
    {
        var reg = new ToolRegistry();
        reg.Add(new BigTool());
        var s = new McpServer(reg, ToolTestKit.NewJournal(), "t");
        var r = Obj(await s.HandleRpcAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"big\",\"arguments\":{}}}"));
        string text = (string?)r["result"]!["content"]![0]!["text"]!;
        Assert.True(text.Length < McpServer.MaxReplyChars + 100);
        Assert.Contains("cut", text);
    }

    private sealed class BigTool : WindowsTool
    {
        public override string Name => "big";
        public override string Description => "big";
        public override JsonObject InputSchema => Schema(new JsonObject());
        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx) => Task.FromResult(ToolResult.Success(new string('x', 200_000)));
    }

    // ---- who may talk to it ----

    private static Dictionary<string, string> Headers(string? auth = "Bearer secret-token", string? origin = null, string host = "127.0.0.1")
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = host };
        if (auth != null) h["Authorization"] = auth;
        if (origin != null) h["Origin"] = origin;
        return h;
    }

    [Fact]
    public async Task Requests_without_the_token_or_with_the_wrong_one_are_refused()
    {
        var (s, _, _) = Make();
        const string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}";
        Assert.Equal(401, (await s.HandleHttpAsync("POST", "/mcp", Headers(auth: null), body)).Status);
        Assert.Equal(401, (await s.HandleHttpAsync("POST", "/mcp", Headers(auth: "Bearer wrong"), body)).Status);
        Assert.Equal(401, (await s.HandleHttpAsync("POST", "/mcp", Headers(auth: "secret-token"), body)).Status);
        Assert.Equal(401, (await s.HandleHttpAsync("POST", "/mcp", Headers(auth: "Bearer "), body)).Status);
        Assert.Equal(200, (await s.HandleHttpAsync("POST", "/mcp", Headers(), body)).Status);
    }

    [Fact]
    public async Task A_web_page_cannot_use_it_even_with_the_token()
    {
        var (s, _, _) = Make();
        Assert.Equal(403, (await s.HandleHttpAsync("POST", "/mcp", Headers(origin: "https://evil.example"), "{}")).Status);
        Assert.Equal(403, (await s.HandleHttpAsync("POST", "/mcp", Headers(origin: "null"), "{}")).Status);
        Assert.Equal(403, (await s.HandleHttpAsync("POST", "/mcp", Headers(host: "evil.example:80"), "{}")).Status);   // DNS rebinding
    }

    [Fact]
    public async Task Only_post_to_the_mcp_path_is_served_and_notifications_get_202()
    {
        var (s, _, _) = Make();
        Assert.Equal(405, (await s.HandleHttpAsync("GET", "/mcp", Headers(), "")).Status);
        Assert.Equal(404, (await s.HandleHttpAsync("POST", "/admin", Headers(), "{}")).Status);
        Assert.Equal(404, (await s.HandleHttpAsync("POST", "/", Headers(), "{}")).Status);
        Assert.Equal(202, (await s.HandleHttpAsync("POST", "/mcp?x=1", Headers(), "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}")).Status);
    }

    // ---- over a real socket ----

    private static async Task<(int status, string body)> Raw(int port, string request)
    {
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, port);
        var stream = c.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        var ms = new MemoryStream();
        var buf = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int n;
        while ((n = await stream.ReadAsync(buf, cts.Token)) > 0) ms.Write(buf, 0, n);
        string text = Encoding.UTF8.GetString(ms.ToArray());
        int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        int status = int.Parse(text.Split(' ')[1]);
        return (status, split >= 0 ? text[(split + 4)..] : "");
    }

    private static string Post(int port, string body, string token = "secret-token", string extra = "") =>
        $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nAuthorization: Bearer {token}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n{extra}\r\n{body}";

    [Fact]
    public async Task A_real_connection_gets_a_real_answer_and_the_server_listens_on_loopback_only()
    {
        var (s, _, _) = Make();
        using (s)
        {
            Assert.True(s.Start());
            Assert.True(s.Port > 0);
            var (status, body) = await Raw(s.Port, Post(s.Port, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}"));
            Assert.Equal(200, status);
            Assert.Contains("\"echo\"", body);

            var (bad, _) = await Raw(s.Port, Post(s.Port, "{}", token: "nope"));
            Assert.Equal(401, bad);

            var (garbage, _) = await Raw(s.Port, "GARBAGE\r\n\r\n");
            Assert.Equal(400, garbage);

            var (big, _) = await Raw(s.Port, $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{s.Port}\r\nAuthorization: Bearer secret-token\r\nContent-Length: {McpServer.MaxBodyBytes + 1}\r\n\r\n");
            Assert.Equal(400, big);

            var (chunked, _) = await Raw(s.Port, Post(s.Port, "{}", extra: "Transfer-Encoding: chunked\r\n"));
            Assert.Equal(400, chunked);

            // anything that is not on loopback could not connect: the listener is bound to 127.0.0.1
            var lan = Dns.GetHostAddresses(Dns.GetHostName()).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (lan != null)
            {
                using var c = new TcpClient();
                await Assert.ThrowsAnyAsync<Exception>(async () => await c.ConnectAsync(lan, s.Port).WaitAsync(TimeSpan.FromSeconds(3)));
            }
        }
    }

    [Fact]
    public void It_falls_back_to_another_port_when_the_preferred_one_is_taken_and_stops_cleanly()
    {
        var (s, _, _) = Make();
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        int taken = ((IPEndPoint)blocker.LocalEndpoint).Port;
        try
        {
            Assert.True(s.Start(taken));
            Assert.NotEqual(taken, s.Port);
            s.Stop();
            Assert.False(s.IsRunning);
        }
        finally { blocker.Stop(); s.Dispose(); }
    }

    [Fact]
    public void Tokens_are_long_and_different_every_time()
    {
        string a = McpServer.NewToken(), b = McpServer.NewToken();
        Assert.True(a.Length >= 48);
        Assert.NotEqual(a, b);
    }
}
