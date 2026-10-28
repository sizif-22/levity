using System.Text.Json.Nodes;
using Levity.Services.WindowsTools;
using Microsoft.Win32;
using Xunit;

namespace Levity.Tests;

public class ServiceToolTests
{
    private static ToolContext Ctx() => ToolTestKit.Ctx(ToolTestKit.NewJournal());

    [Fact]
    public async Task Services_are_listed_with_state_and_start_type_and_can_be_filtered()
    {
        var all = await new ServicesListTool().RunAsync(new JsonObject(), Ctx());
        Assert.True(all.Ok);
        var rows = ((JsonArray)all.Data!).OfType<JsonObject>().ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Contains((string?)r["state"], new[] { "running", "stopped", "paused", "startpending", "stoppending", "continuepending", "pausepending" }));

        var spooler = await new ServicesListTool().RunAsync(ToolTestKit.Args("{\"filter\":\"spooler\"}"), Ctx());
        Assert.Contains(((JsonArray)spooler.Data!).OfType<JsonObject>(), r => (string?)r["name"] == "Spooler");

        var running = await new ServicesListTool().RunAsync(ToolTestKit.Args("{\"state\":\"running\"}"), Ctx());
        Assert.All(((JsonArray)running.Data!).OfType<JsonObject>(), r => Assert.Equal("running", (string?)r["state"]));
    }

    [Theory]
    [InlineData("{\"name\":\"RpcSs\",\"action\":\"stop\"}")]
    [InlineData("{\"name\":\"WinDefend\",\"action\":\"set_start_type\",\"start_type\":\"disabled\"}")]
    [InlineData("{\"name\":\"bad;name&calc\",\"action\":\"start\"}")]
    [InlineData("{\"name\":\"NoSuchService12345\",\"action\":\"start\"}")]
    public async Task Core_services_odd_names_and_unknown_services_are_refused_before_anything_is_recorded(string args)
    {
        var j = ToolTestKit.NewJournal();
        var r = await new ServiceControlTool().RunAsync(ToolTestKit.Args(args), ToolTestKit.Ctx(j));
        Assert.False(r.Ok, r.Text);
        Assert.Empty(j.List());
    }

    [Fact]
    public async Task Without_administrator_rights_a_change_says_so_and_a_preview_still_works()
    {
        if (Elevation.IsElevated) return;
        var j = ToolTestKit.NewJournal();
        string args = "{\"name\":\"Spooler\",\"action\":\"restart\"}";
        var real = await new ServiceControlTool().RunAsync(ToolTestKit.Args(args), ToolTestKit.Ctx(j));
        Assert.False(real.Ok);
        Assert.Contains("administrator", real.Text);
        Assert.Empty(j.List());
        var dry = await new ServiceControlTool().RunAsync(ToolTestKit.Args(args), ToolTestKit.Ctx(j, dry: true));
        Assert.True(dry.Ok);
        Assert.Contains("Dry run", dry.Text);
    }

    [Fact]
    public async Task An_invalid_start_type_is_refused()
    {
        await Assert.ThrowsAsync<ToolInputException>(() => new ServiceControlTool().RunAsync(ToolTestKit.Args("{\"name\":\"Spooler\",\"action\":\"set_start_type\",\"start_type\":\"turbo\"}"), Ctx()));
    }
}

public class ScheduledTaskToolTests
{
    [Fact]
    public async Task Tasks_are_listed_and_filtered_without_starting_a_program()
    {
        var all = await new ScheduledTasksListTool().RunAsync(new JsonObject(), ToolTestKit.Ctx(ToolTestKit.NewJournal()));
        Assert.True(all.Ok);
        var rows = ((JsonArray)all.Data!).OfType<JsonObject>().ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.StartsWith("\\", (string?)r["path"]));
        var ms = await new ScheduledTasksListTool().RunAsync(ToolTestKit.Args("{\"filter\":\"Microsoft\"}"), ToolTestKit.Ctx(ToolTestKit.NewJournal()));
        Assert.All(((JsonArray)ms.Data!).OfType<JsonObject>(), r => Assert.Contains("Microsoft", (string?)r["path"], StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("{\"path\":\"relative\",\"action\":\"run\"}")]
    [InlineData("{\"path\":\"\\\\..\\\\..\\\\x\",\"action\":\"run\"}")]
    [InlineData("{\"path\":\"\\\\a;calc\",\"action\":\"disable\"}")]
    [InlineData("{\"path\":\"\\\\No\\\\Such\\\\Task12345\",\"action\":\"disable\"}")]
    public async Task Bad_paths_and_missing_tasks_are_refused_without_a_journal_entry(string args)
    {
        var j = ToolTestKit.NewJournal();
        var r = await new ScheduledTaskControlTool().RunAsync(ToolTestKit.Args(args), ToolTestKit.Ctx(j));
        Assert.False(r.Ok, r.Text);
        Assert.Empty(j.List());
    }

    [Fact]
    public async Task A_real_task_can_be_previewed_without_being_touched()
    {
        var list = await new ScheduledTasksListTool().RunAsync(ToolTestKit.Args("{\"filter\":\"Microsoft\\\\Windows\"}"), ToolTestKit.Ctx(ToolTestKit.NewJournal()));
        var t = ((JsonArray)list.Data!).OfType<JsonObject>().FirstOrDefault(r => (bool?)r["enabled"] == true);
        if (t == null) return;
        var j = ToolTestKit.NewJournal();
        var dry = await new ScheduledTaskControlTool().RunAsync(ToolTestKit.Args($"{{\"path\":{System.Text.Json.JsonSerializer.Serialize((string?)t["path"])},\"action\":\"disable\"}}"), ToolTestKit.Ctx(j, dry: true));
        Assert.True(dry.Ok, dry.Text);
        Assert.Contains("Dry run", dry.Text);
        Assert.Empty(j.List());
    }
}

public class WingetToolTests
{
    [Theory]
    [InlineData("{\"action\":\"install\",\"id\":\"-h\"}")]
    [InlineData("{\"action\":\"install\",\"id\":\"Git.Git; calc\"}")]
    [InlineData("{\"action\":\"install\",\"id\":\"a b\"}")]
    [InlineData("{\"action\":\"install\",\"id\":\"--source winget\"}")]
    [InlineData("{\"action\":\"install\",\"id\":\"\"}")]
    public async Task Package_ids_that_could_be_read_as_options_or_commands_are_refused(string args)
    {
        var j = ToolTestKit.NewJournal();
        ToolResult r;
        try { r = await new WingetChangeTool().RunAsync(ToolTestKit.Args(args), ToolTestKit.Ctx(j)); }
        catch (ToolInputException ex) { r = ToolResult.Failure(ex.Message); }
        Assert.False(r.Ok);
        Assert.Empty(j.List());
    }

    [Fact]
    public async Task A_preview_of_an_install_changes_and_records_nothing()
    {
        if (Winget.Path == null) return;
        var j = ToolTestKit.NewJournal();
        var r = await new WingetChangeTool().RunAsync(ToolTestKit.Args("{\"action\":\"install\",\"id\":\"Git.Git\"}"), ToolTestKit.Ctx(j, dry: true));
        Assert.True(r.Ok, r.Text);
        Assert.Contains("Dry run", r.Text);
        Assert.Empty(j.List());
    }

    [Theory]
    [InlineData("{\"action\":\"search\",\"query\":\"-h\"}")]
    [InlineData("{\"action\":\"search\"}")]
    [InlineData("{\"action\":\"search\",\"query\":\"a\\nb\"}")]
    [InlineData("{\"action\":\"delete\"}")]
    public async Task Hostile_searches_are_refused(string args)
    {
        await Assert.ThrowsAsync<ToolInputException>(() => new WingetQueryTool().RunAsync(ToolTestKit.Args(args), ToolTestKit.Ctx(ToolTestKit.NewJournal())));
    }

    [Fact]
    public void Package_ids_follow_a_strict_pattern()
    {
        Assert.Matches(Winget.Id, "Git.Git");
        Assert.Matches(Winget.Id, "Microsoft.VisualStudioCode");
        Assert.Matches(Winget.Id, "7zip.7zip");
        Assert.DoesNotMatch(Winget.Id, "-rf");
        Assert.DoesNotMatch(Winget.Id, "Git.Git --force");
    }
}

public class HostsFileToolTests
{
    private static (HostsFileTool tool, string path, ChangeJournal j) Setup()
    {
        string path = Path.Combine(TestEnvironment.NewTempDir("hosts"), "hosts");
        File.WriteAllText(path, "# comment\r\n127.0.0.1\tlocalhost\r\n::1 localhost\r\n10.0.0.5 intranet.local # work\r\n");
        var j = ToolTestKit.NewJournal();
        HostsFileTool.RegisterUndo(j);
        return (new HostsFileTool(path), path, j);
    }

    [Fact]
    public async Task Listing_shows_the_entries_and_skips_comments()
    {
        var (tool, _, j) = Setup();
        var r = await tool.RunAsync(ToolTestKit.Args("{\"action\":\"list\"}"), ToolTestKit.Ctx(j));
        var rows = ((JsonArray)r.Data!).OfType<JsonObject>().ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal("intranet.local", (string?)rows[2]["hosts"]);
    }

    [Fact]
    public async Task Add_then_revert_restores_the_file_byte_for_byte()
    {
        var (tool, path, j) = Setup();
        byte[] original = File.ReadAllBytes(path);
        var add = await tool.RunAsync(ToolTestKit.Args("{\"action\":\"add\",\"address\":\"192.168.1.20\",\"host\":\"nas.home\"}"), ToolTestKit.Ctx(j));
        Assert.True(add.Ok, add.Text);
        Assert.Contains("192.168.1.20\tnas.home", File.ReadAllText(path));
        Assert.Contains("10.0.0.5 intranet.local", File.ReadAllText(path));

        Assert.Null(j.Revert(j.List()[0].Id));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Remove_takes_out_only_that_hosts_line_and_revert_brings_it_back()
    {
        var (tool, path, j) = Setup();
        byte[] original = File.ReadAllBytes(path);
        var r = await tool.RunAsync(ToolTestKit.Args("{\"action\":\"remove\",\"host\":\"intranet.local\"}"), ToolTestKit.Ctx(j));
        Assert.True(r.Ok, r.Text);
        string after = File.ReadAllText(path);
        Assert.DoesNotContain("intranet.local", after);
        Assert.Contains("localhost", after);
        Assert.Contains("# comment", after);
        Assert.Null(j.Revert(j.List()[0].Id));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("{\"action\":\"add\",\"address\":\"not-an-ip\",\"host\":\"x.test\"}")]
    [InlineData("{\"action\":\"add\",\"address\":\"1.2.3.4\",\"host\":\"bad host\"}")]
    [InlineData("{\"action\":\"add\",\"address\":\"1.2.3.4\",\"host\":\"evil.com\\n9.9.9.9 bank.com\"}")]
    [InlineData("{\"action\":\"add\",\"address\":\"1.2.3.4\\n6.6.6.6 x\",\"host\":\"ok.test\"}")]
    [InlineData("{\"action\":\"add\",\"address\":\"1.2.3.4\",\"host\":\"intranet.local\"}")]
    [InlineData("{\"action\":\"remove\",\"host\":\"; calc\"}")]
    public async Task Hostile_or_duplicate_entries_are_refused_and_the_file_is_untouched(string args)
    {
        var (tool, path, j) = Setup();
        byte[] original = File.ReadAllBytes(path);
        ToolResult r;
        try { r = await tool.RunAsync(ToolTestKit.Args(args), ToolTestKit.Ctx(j)); }
        catch (ToolInputException ex) { r = ToolResult.Failure(ex.Message); }
        Assert.False(r.Ok, r.Text);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(j.List());
    }

    [Fact]
    public async Task Removing_a_name_that_is_not_there_changes_nothing_and_a_preview_writes_nothing()
    {
        var (tool, path, j) = Setup();
        byte[] original = File.ReadAllBytes(path);
        Assert.Contains("Nothing changed", (await tool.RunAsync(ToolTestKit.Args("{\"action\":\"remove\",\"host\":\"ghost.test\"}"), ToolTestKit.Ctx(j))).Text);
        Assert.Contains("Dry run", (await tool.RunAsync(ToolTestKit.Args("{\"action\":\"add\",\"address\":\"1.1.1.1\",\"host\":\"new.test\"}"), ToolTestKit.Ctx(j, dry: true))).Text);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(j.List());
    }

    [Fact]
    public async Task The_real_hosts_file_cannot_be_written_without_administrator_rights_and_is_left_alone()
    {
        if (Elevation.IsElevated) return;
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        byte[] before = File.Exists(real) ? File.ReadAllBytes(real) : Array.Empty<byte>();
        var j = ToolTestKit.NewJournal();
        var r = await new HostsFileTool().RunAsync(ToolTestKit.Args("{\"action\":\"add\",\"address\":\"203.0.113.9\",\"host\":\"levity-test.invalid\"}"), ToolTestKit.Ctx(j));
        Assert.False(r.Ok);
        Assert.Contains("administrator", r.Text);
        Assert.Equal(before, File.Exists(real) ? File.ReadAllBytes(real) : Array.Empty<byte>());
        Assert.Empty(j.List());
    }
}

public class ReadOnlyToolTests
{
    private static ToolContext Ctx() => ToolTestKit.Ctx(ToolTestKit.NewJournal());

    [Fact]
    public async Task The_registry_reader_reads_values_and_sub_keys_and_refuses_anything_else()
    {
        var r = await new RegistryReadTool().RunAsync(ToolTestKit.Args("{\"key\":\"HKCU\\\\Software\\\\Microsoft\\\\Windows\\\\CurrentVersion\\\\Run\"}"), Ctx());
        Assert.True(r.Ok, r.Text);
        Assert.NotNull(r.Data!["values"]);

        var cv = await new RegistryReadTool().RunAsync(ToolTestKit.Args("{\"key\":\"HKLM\\\\SOFTWARE\\\\Microsoft\\\\Windows NT\\\\CurrentVersion\"}"), Ctx());
        Assert.Contains(((JsonArray)cv.Data!["values"]!).OfType<JsonObject>(), v => (string?)v["name"] == "ProductName");

        foreach (string bad in new[] { "HKEY_USERS\\x", "HKCU\\..\\..\\x", "C:\\Windows", "HKLM", "HKCU\\Software\\a;b", "" })
        {
            ToolResult res;
            try { res = await new RegistryReadTool().RunAsync(ToolTestKit.Args($"{{\"key\":{System.Text.Json.JsonSerializer.Serialize(bad)}}}"), Ctx()); }
            catch (ToolInputException ex) { res = ToolResult.Failure(ex.Message); }
            Assert.False(res.Ok, bad);
        }
        Assert.False((await new RegistryReadTool().RunAsync(ToolTestKit.Args("{\"key\":\"HKCU\\\\Software\\\\NoSuchKey12345\"}"), Ctx())).Ok);
    }

    [Fact]
    public async Task Certificates_are_listed_and_the_expiry_filter_works()
    {
        var root = await new CertificatesTool().RunAsync(ToolTestKit.Args("{\"location\":\"machine\",\"store\":\"Root\"}"), Ctx());
        Assert.True(root.Ok);
        Assert.NotEmpty((JsonArray)root.Data!);
        var expiring = await new CertificatesTool().RunAsync(ToolTestKit.Args("{\"location\":\"machine\",\"store\":\"Root\",\"expiring_within_days\":0}"), Ctx());
        Assert.All(((JsonArray)expiring.Data!).OfType<JsonObject>(), c => Assert.True((int?)c["days_left"] <= 0));
        await Assert.ThrowsAsync<ToolInputException>(() => new CertificatesTool().RunAsync(ToolTestKit.Args("{\"store\":\"Disallowed\"}"), Ctx()));
    }

    [Fact]
    public async Task Default_apps_are_read_and_open_settings_only_knows_its_own_pages()
    {
        Assert.True((await new DefaultAppsTool().RunAsync(new JsonObject(), Ctx())).Ok);
        var dry = await new OpenSettingsTool().RunAsync(ToolTestKit.Args("{\"page\":\"defaultapps\"}"), ToolTestKit.Ctx(ToolTestKit.NewJournal(), dry: true));
        Assert.Contains("Dry run", dry.Text);
        foreach (string bad in new[] { "cmd", "ms-settings:defaultapps", "https://evil.example", "..", "defaultapps; calc" })
            await Assert.ThrowsAsync<ToolInputException>(() => new OpenSettingsTool().RunAsync(ToolTestKit.Args($"{{\"page\":{System.Text.Json.JsonSerializer.Serialize(bad)}}}"), Ctx()));
    }

    [Fact]
    public async Task Printers_are_listed_with_one_default_at_most()
    {
        var r = await new PrintersListTool().RunAsync(new JsonObject(), Ctx());
        Assert.True(r.Ok);
        Assert.True(((JsonArray)r.Data!).OfType<JsonObject>().Count(p => (bool?)p["default"] == true) <= 1);
    }

    [Fact]
    public async Task Printer_changes_need_an_exact_existing_name_and_a_preview_changes_nothing()
    {
        var j = ToolTestKit.NewJournal();
        Assert.False((await new PrinterControlTool().RunAsync(ToolTestKit.Args("{\"name\":\"No Such Printer 12345\",\"action\":\"set_default\"}"), ToolTestKit.Ctx(j))).Ok);
        var printers = Spooler.List();
        if (printers.Count > 0)
        {
            string before = Spooler.DefaultName();
            var dry = await new PrinterControlTool().RunAsync(ToolTestKit.Args($"{{\"name\":{System.Text.Json.JsonSerializer.Serialize(printers[0].Name)},\"action\":\"clear_jobs\"}}"), ToolTestKit.Ctx(j, dry: true));
            Assert.True(dry.Ok, dry.Text);
            Assert.Equal(before, Spooler.DefaultName());
        }
        Assert.Empty(j.List());
    }

    [Fact]
    public void The_event_log_query_is_built_from_numbers_only()
    {
        Assert.Equal("*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= 86400000]]]", EventLogTool.Query(24, false, null));
        Assert.Equal("*[System[(Level=1 or Level=2 or Level=3) and (EventID=41) and TimeCreated[timediff(@SystemTime) <= 3600000]]]", EventLogTool.Query(1, true, 41));
    }

    [Fact]
    public async Task The_event_log_summarises_recent_problems_and_rejects_odd_requests()
    {
        var r = await new EventLogTool().RunAsync(ToolTestKit.Args("{\"log\":\"Application\",\"hours\":168,\"level\":\"warnings_and_errors\"}"), Ctx());
        Assert.True(r.Ok, r.Text);
        Assert.All(((JsonArray)r.Data!).OfType<JsonObject>(), g => Assert.True((int?)g["count"] >= 1));
        await Assert.ThrowsAsync<ToolInputException>(() => new EventLogTool().RunAsync(ToolTestKit.Args("{\"log\":\"Security\"}"), Ctx()));
        await Assert.ThrowsAsync<ToolInputException>(() => new EventLogTool().RunAsync(ToolTestKit.Args("{\"hours\":100000}"), Ctx()));
        await Assert.ThrowsAsync<ToolInputException>(() => new EventLogTool().RunAsync(ToolTestKit.Args("{\"event_id\":99999999}"), Ctx()));
    }

    [Fact]
    public async Task The_journal_tools_list_changes_and_revert_one_by_id()
    {
        var j = ToolTestKit.NewJournal();
        string? undone = null;
        j.RegisterUndo("test", d => { undone = (string?)d["v"]; return null; });
        var e = j.Record("t", "did a thing", "test", new JsonObject { ["v"] = "old" });
        var ctx = ToolTestKit.Ctx(j);

        var list = await new ChangesListTool().RunAsync(new JsonObject(), ctx);
        Assert.Equal(e.Id, (string?)((JsonArray)list.Data!)[0]!["id"]);
        Assert.Contains("Dry run", (await new ChangesRevertTool().RunAsync(ToolTestKit.Args($"{{\"id\":\"{e.Id}\"}}"), ToolTestKit.Ctx(j, dry: true))).Text);
        Assert.Null(undone);
        Assert.True((await new ChangesRevertTool().RunAsync(ToolTestKit.Args($"{{\"id\":\"{e.Id}\"}}"), ctx)).Ok);
        Assert.Equal("old", undone);
        Assert.False((await new ChangesRevertTool().RunAsync(ToolTestKit.Args("{\"id\":\"missing\"}"), ctx)).Ok);
    }
}

public class SecurityToolTests
{
    private static ToolContext Ctx(ChangeJournal j, bool dry = false) => ToolTestKit.Ctx(j, dry);

    [Fact]
    public async Task Firewall_rules_can_be_listed_and_filtered()
    {
        var r = await new FirewallRulesTool().RunAsync(new JsonObject(), Ctx(ToolTestKit.NewJournal()));
        Assert.True(r.Ok, r.Text);
        Assert.NotEmpty((JsonArray)r.Data!);
        var f = await new FirewallRulesTool().RunAsync(ToolTestKit.Args("{\"filter\":\"Core Networking\"}"), Ctx(ToolTestKit.NewJournal()));
        Assert.All(((JsonArray)f.Data!).OfType<JsonObject>(), x => Assert.Contains("Core Networking", ((string?)x["name"] ?? "") + ((string?)x["program"] ?? ""), StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("{\"action\":\"remove\",\"name\":\"Core Networking - DNS (UDP-Out)\"}")]
    [InlineData("{\"action\":\"remove\",\"name\":\"\"}")]
    [InlineData("{\"action\":\"allow_program\",\"program\":\"notepad.exe\"}")]
    [InlineData("{\"action\":\"allow_program\",\"program\":\"C:\\\\Windows\\\\win.ini\"}")]
    [InlineData("{\"action\":\"allow_program\",\"program\":\"\\\\\\\\server\\\\share\\\\x.exe\"}")]
    [InlineData("{\"action\":\"allow_program\",\"program\":\"C:\\\\no\\\\such.exe\"}")]
    public async Task Only_levitys_own_rules_can_be_removed_and_programs_must_be_real_executables(string args)
    {
        var j = ToolTestKit.NewJournal();
        ToolResult r;
        try { r = await new FirewallChangeTool().RunAsync(ToolTestKit.Args(args), Ctx(j)); }
        catch (ToolInputException ex) { r = ToolResult.Failure(ex.Message); }
        Assert.False(r.Ok, r.Text);
        Assert.Empty(j.List());
    }

    [Theory]
    [InlineData("{\"action\":\"allow_port\",\"port\":0}")]
    [InlineData("{\"action\":\"allow_port\",\"port\":70000}")]
    [InlineData("{\"action\":\"allow_port\"}")]
    public async Task Port_numbers_are_checked(string args)
    {
        await Assert.ThrowsAsync<ToolInputException>(() => new FirewallChangeTool().RunAsync(ToolTestKit.Args(args), Ctx(ToolTestKit.NewJournal())));
    }

    [Fact]
    public async Task Firewall_changes_need_administrator_rights_and_a_preview_is_free()
    {
        if (Elevation.IsElevated) return;
        var j = ToolTestKit.NewJournal();
        var real = await new FirewallChangeTool().RunAsync(ToolTestKit.Args("{\"action\":\"allow_port\",\"port\":48123}"), Ctx(j));
        Assert.False(real.Ok);
        Assert.Contains("administrator", real.Text);
        var dry = await new FirewallChangeTool().RunAsync(ToolTestKit.Args("{\"action\":\"allow_port\",\"port\":48123}"), Ctx(j, dry: true));
        Assert.Contains("Dry run", dry.Text);
        Assert.Empty(j.List());
        Assert.DoesNotContain(FirewallPolicy.List("Levity: allow TCP 48123"), r => r.Name == "Levity: allow TCP 48123");
    }

    [Theory]
    [InlineData("{\"action\":\"add_exclusion\",\"path\":\"C:\\\\\"}")]
    [InlineData("{\"action\":\"add_exclusion\",\"path\":\"C:\"}")]
    [InlineData("{\"action\":\"add_exclusion\",\"path\":\"C:\\\\Users\\\\*\"}")]
    [InlineData("{\"action\":\"add_exclusion\",\"path\":\"relative\\\\dir\"}")]
    [InlineData("{\"action\":\"add_exclusion\",\"path\":\"\\\\\\\\server\\\\share\"}")]
    [InlineData("{\"action\":\"add_exclusion\"}")]
    public async Task Dangerous_exclusions_are_refused_before_anything_is_recorded(string args)
    {
        var j = ToolTestKit.NewJournal();
        ToolResult r;
        try { r = await new DefenderChangeTool().RunAsync(ToolTestKit.Args(args), Ctx(j)); }
        catch (ToolInputException ex) { r = ToolResult.Failure(ex.Message); }
        Assert.False(r.Ok, r.Text);
        Assert.Empty(j.List());
    }

    [Fact]
    public async Task The_windows_folder_is_never_excluded_and_a_normal_exclusion_needs_administrator_rights()
    {
        var j = ToolTestKit.NewJournal();
        var win = await new DefenderChangeTool().RunAsync(ToolTestKit.Args($"{{\"action\":\"add_exclusion\",\"path\":{System.Text.Json.JsonSerializer.Serialize(Environment.GetFolderPath(Environment.SpecialFolder.Windows))}}}"), Ctx(j));
        Assert.False(win.Ok);
        if (Elevation.IsElevated) return;
        string dir = TestEnvironment.NewTempDir("excl");
        var r = await new DefenderChangeTool().RunAsync(ToolTestKit.Args($"{{\"action\":\"add_exclusion\",\"path\":{System.Text.Json.JsonSerializer.Serialize(dir)}}}"), Ctx(j));
        Assert.False(r.Ok);
        Assert.Contains("administrator", r.Text);
        Assert.Empty(j.List());
        Assert.Contains("Dry run", (await new DefenderChangeTool().RunAsync(ToolTestKit.Args($"{{\"action\":\"add_exclusion\",\"path\":{System.Text.Json.JsonSerializer.Serialize(dir)}}}"), Ctx(j, dry: true))).Text);
    }

    [Fact]
    public async Task Defender_status_reports_or_explains_why_it_cannot()
    {
        var r = await new DefenderStatusTool().RunAsync(new JsonObject(), Ctx(ToolTestKit.NewJournal()));
        Assert.False(string.IsNullOrWhiteSpace(r.Text));
        if (r.Ok) Assert.NotNull(r.Data!["status"]);
    }

    [Fact]
    public void Text_given_to_a_script_travels_as_an_environment_variable_never_inside_the_script()
    {
        Assert.Contains("$env:LEVITY_ARG", Defender.AddExclusionScript);
        Assert.Contains("$env:LEVITY_ARG", Defender.RemoveExclusionScript);
        Assert.DoesNotContain("{", Defender.AddExclusionScript);
    }
}
