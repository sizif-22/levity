using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Levity.Services.WindowsTools;
using Microsoft.Win32;
using Xunit;

namespace Levity.Tests;

public class ToolboxShapeTests
{
    [Fact]
    public void Every_tool_has_a_unique_name_a_description_and_a_strict_schema()
    {
        var box = Toolbox.Create(TestEnvironment.NewTempDir("box"));
        var tools = box.Registry.All;
        Assert.True(tools.Count >= 13);
        Assert.Equal(tools.Count, tools.Select(t => t.Name).Distinct().Count());
        foreach (var t in tools)
        {
            Assert.Matches("^[a-z][a-z0-9_]*$", t.Name);
            Assert.True(t.Description.Length > 40, t.Name + " needs a real description");
            Assert.Equal("object", (string?)t.InputSchema["type"]);
            Assert.False((bool?)t.InputSchema["additionalProperties"], t.Name + " must not accept unknown arguments silently");
            foreach (var kv in (JsonObject)t.InputSchema["properties"]!) Assert.False(string.IsNullOrWhiteSpace((string?)kv.Value!["description"]), $"{t.Name}.{kv.Key} needs a description");
        }
    }

    [Fact]
    public void Every_tool_that_changes_something_can_be_previewed_and_every_change_kind_can_be_undone()
    {
        var box = Toolbox.Create(TestEnvironment.NewTempDir("box"));
        foreach (var t in box.Registry.All.Where(t => t.Access == ToolAccess.Change))
            Assert.True(((JsonObject)t.InputSchema["properties"]!).ContainsKey("dry_run"), t.Name + " must offer dry_run");
        foreach (string kind in new[] { "wallpaper", "registry_values", "power_plan", "env_var", "none" })
            Assert.True(box.Journal.CanUndo(kind), kind);
    }

    [Fact]
    public async Task Tools_that_only_look_never_write_to_the_journal()
    {
        var box = Toolbox.Create(TestEnvironment.NewTempDir("box"));
        var ctx = ToolTestKit.Ctx(box.Journal);
        foreach (var (name, args) in new[]
        {
            ("settings_get", "{}"), ("startup_apps_list", "{}"), ("environment_get", "{\"scope\":\"process\",\"name\":\"PATH\"}"),
            ("path_check", "{}"), ("disk_usage", "{}"), ("network_info", "{\"action\":\"adapters\"}"), ("machine_profile", "{}"),
        })
        {
            var r = await box.Registry.CallAsync(name, ToolTestKit.Args(args), ctx);
            Assert.True(r.Ok, $"{name}: {r.Text}");
        }
        Assert.Empty(box.Journal.List());
    }

    [Fact]
    public async Task An_unknown_argument_name_is_not_silently_accepted_as_something_else()
    {
        var box = Toolbox.Create(TestEnvironment.NewTempDir("box"));
        var r = await box.Registry.CallAsync("settings_set", ToolTestKit.Args("{\"area\":\"theme\",\"value\":\"purple\"}"), ToolTestKit.Ctx(box.Journal));
        Assert.False(r.Ok);
        Assert.Empty(box.Journal.List());
    }
}

public class EnvironmentToolTests
{
    private static (FakeEnvStore store, ChangeJournal journal) Setup()
    {
        var store = new FakeEnvStore();
        var j = ToolTestKit.NewJournal();
        EnvironmentSetTool.RegisterUndo(j, store);
        return (store, j);
    }

    [Fact]
    public async Task Setting_a_user_variable_is_journaled_and_revert_restores_the_old_value()
    {
        var (store, j) = Setup();
        store.Set("user", "MY_TOOL_HOME", "C:\\old");
        var tool = new EnvironmentSetTool(store);

        var r = await tool.RunAsync(ToolTestKit.Args("{\"name\":\"MY_TOOL_HOME\",\"value\":\"C:\\\\new\"}"), ToolTestKit.Ctx(j));
        Assert.True(r.Ok, r.Text);
        Assert.Equal("C:\\new", store.Get("user", "MY_TOOL_HOME"));
        var entry = Assert.Single(j.List());

        Assert.Null(j.Revert(entry.Id));
        Assert.Equal("C:\\old", store.Get("user", "MY_TOOL_HOME"));
    }

    [Fact]
    public async Task A_new_variable_is_removed_again_by_revert_and_a_removed_one_comes_back()
    {
        var (store, j) = Setup();
        var tool = new EnvironmentSetTool(store);
        await tool.RunAsync(ToolTestKit.Args("{\"name\":\"BRAND_NEW\",\"value\":\"1\"}"), ToolTestKit.Ctx(j));
        Assert.Null(j.Revert(j.List()[0].Id));
        Assert.Null(store.Get("user", "BRAND_NEW"));

        store.Set("user", "KEEP_ME", "precious");
        await tool.RunAsync(ToolTestKit.Args("{\"name\":\"KEEP_ME\",\"remove\":true}"), ToolTestKit.Ctx(j));
        Assert.Null(store.Get("user", "KEEP_ME"));
        Assert.Null(j.Revert(j.List()[0].Id));
        Assert.Equal("precious", store.Get("user", "KEEP_ME"));
    }

    [Fact]
    public async Task Dry_run_changes_and_records_nothing()
    {
        var (store, j) = Setup();
        var r = await new EnvironmentSetTool(store).RunAsync(ToolTestKit.Args("{\"name\":\"X_Y\",\"value\":\"1\"}"), ToolTestKit.Ctx(j, dry: true));
        Assert.True(r.Ok);
        Assert.Contains("Dry run", r.Text);
        Assert.Null(store.Get("user", "X_Y"));
        Assert.Empty(j.List());
    }

    [Theory]
    [InlineData("{\"name\":\"1BAD\",\"value\":\"x\"}")]
    [InlineData("{\"name\":\"has space\",\"value\":\"x\"}")]
    [InlineData("{\"name\":\"A=B\",\"value\":\"x\"}")]
    [InlineData("{\"name\":\"\",\"value\":\"x\"}")]
    [InlineData("{\"name\":\"PATH\",\"value\":\"x\"}")]
    [InlineData("{\"name\":\"OK_NAME\"}")]
    [InlineData("{\"name\":\"OK_NAME\",\"value\":\"a\\u0000b\"}")]
    public async Task Bad_names_values_and_PATH_are_refused_without_a_journal_entry(string args)
    {
        var (store, j) = Setup();
        var r = await Run(new EnvironmentSetTool(store), args, j);
        Assert.False(r.Ok, r.Text);
        Assert.Empty(j.List());
    }

    private static async Task<ToolResult> Run(WindowsTool tool, string args, ChangeJournal j)
    {
        try { return await tool.RunAsync(ToolTestKit.Args(args), ToolTestKit.Ctx(j)); }
        catch (ToolInputException ex) { return ToolResult.Failure(ex.Message); }
    }

    [Fact]
    public async Task Machine_wide_changes_are_not_made_without_administrator_rights_but_can_be_previewed()
    {
        var (store, j) = Setup();
        var tool = new EnvironmentSetTool(store);
        var dry = await tool.RunAsync(ToolTestKit.Args("{\"name\":\"BIG_ONE\",\"value\":\"1\",\"scope\":\"machine\"}"), ToolTestKit.Ctx(j, dry: true));
        Assert.True(dry.Ok);
        var real = await tool.RunAsync(ToolTestKit.Args("{\"name\":\"BIG_ONE\",\"value\":\"1\",\"scope\":\"machine\"}"), ToolTestKit.Ctx(j));
        Assert.False(real.Ok);
        Assert.Contains("administrator", real.Text);
        Assert.Null(store.Get("machine", "BIG_ONE"));
        Assert.Empty(j.List());
    }

    [Fact]
    public async Task Reading_returns_values_and_reports_missing_ones()
    {
        var store = new FakeEnvStore();
        store.Set("user", "FOO", "bar");
        store.Set("user", "LONG", new string('z', 400));
        var tool = new EnvironmentGetTool(store);
        var j = ToolTestKit.NewJournal();
        Assert.Contains("FOO (user) = bar", (await tool.RunAsync(ToolTestKit.Args("{\"name\":\"FOO\"}"), ToolTestKit.Ctx(j))).Text);
        Assert.Contains("not set", (await tool.RunAsync(ToolTestKit.Args("{\"name\":\"NOPE\"}"), ToolTestKit.Ctx(j))).Text);
        var all = await tool.RunAsync(ToolTestKit.Args("{}"), ToolTestKit.Ctx(j));
        Assert.True(((string?)all.Data!["LONG"])!.Length < 200);
        Assert.False((await tool.RunAsync(ToolTestKit.Args("{\"name\":\"bad name\"}"), ToolTestKit.Ctx(j))).Ok);
    }
}

public class PathToolTests
{
    private static (FakeEnvStore store, ChangeJournal j, string real) Setup()
    {
        var store = new FakeEnvStore();
        var j = ToolTestKit.NewJournal();
        PathEditTool.RegisterUndo(j, store);
        string real = TestEnvironment.NewTempDir("pathdir");
        return (store, j, real);
    }

    [Fact]
    public async Task Check_finds_missing_folders_duplicates_files_and_over_long_paths()
    {
        var (store, j, real) = Setup();
        string file = Path.Combine(real, "a-file.txt");
        File.WriteAllText(file, "x");
        store.Set("user", "PATH", $"{real};{real}\\;C:\\definitely\\not\\here;{file}");
        store.Set("machine", "PATH", real + ";" + new string('a', 2100));

        var r = await new PathCheckTool(store).RunAsync(ToolTestKit.Args("{}"), ToolTestKit.Ctx(j));
        var problems = ((JsonArray)r.Data!).Select(f => (string?)f!["problem"]).ToList();
        Assert.Contains("duplicate", problems);
        Assert.Contains("missing", problems);
        Assert.Contains("is_a_file", problems);
        Assert.Contains("long", problems);
    }

    [Fact]
    public async Task A_healthy_path_is_reported_as_healthy()
    {
        var (store, j, real) = Setup();
        store.Set("user", "PATH", real);
        var r = await new PathCheckTool(store).RunAsync(ToolTestKit.Args("{}"), ToolTestKit.Ctx(j));
        Assert.Contains("healthy", r.Text);
    }

    [Fact]
    public async Task Add_remove_and_clean_edit_the_user_path_and_each_is_revertible()
    {
        var (store, j, real) = Setup();
        string other = TestEnvironment.NewTempDir("pathdir2");
        store.Set("user", "PATH", real);
        var tool = new PathEditTool(store);

        Assert.True((await tool.RunAsync(ToolTestKit.Args($"{{\"action\":\"add\",\"entry\":{System.Text.Json.JsonSerializer.Serialize(other)}}}"), ToolTestKit.Ctx(j))).Ok);
        Assert.Equal($"{real};{other}", store.Get("user", "PATH"));
        Assert.Null(j.Revert(j.List()[0].Id));
        Assert.Equal(real, store.Get("user", "PATH"));

        store.Set("user", "PATH", $"{real};{real};C:\\gone\\folder;{other}");
        var clean = await tool.RunAsync(ToolTestKit.Args("{\"action\":\"clean\"}"), ToolTestKit.Ctx(j));
        Assert.True(clean.Ok, clean.Text);
        Assert.Equal($"{real};{other}", store.Get("user", "PATH"));
        Assert.Null(j.Revert(j.List()[0].Id));
        Assert.Equal($"{real};{real};C:\\gone\\folder;{other}", store.Get("user", "PATH"));

        Assert.True((await tool.RunAsync(ToolTestKit.Args($"{{\"action\":\"remove\",\"entry\":{System.Text.Json.JsonSerializer.Serialize(real)}}}"), ToolTestKit.Ctx(j))).Ok);
        Assert.DoesNotContain(real + ";", store.Get("user", "PATH") + ";");
    }

    [Fact]
    public async Task Adding_something_already_there_changes_nothing_and_records_nothing()
    {
        var (store, j, real) = Setup();
        store.Set("user", "PATH", real);
        var r = await new PathEditTool(store).RunAsync(ToolTestKit.Args($"{{\"action\":\"add\",\"entry\":{System.Text.Json.JsonSerializer.Serialize(real + "\\")}}}"), ToolTestKit.Ctx(j));
        Assert.True(r.Ok);
        Assert.Contains("already", r.Text);
        Assert.Empty(j.List());
    }

    [Theory]
    [InlineData("{\"action\":\"add\",\"entry\":\"C:\\\\a;C:\\\\b\"}")]
    [InlineData("{\"action\":\"add\",\"entry\":\"\"}")]
    [InlineData("{\"action\":\"add\"}")]
    [InlineData("{\"action\":\"destroy\"}")]
    public async Task Hostile_or_incomplete_requests_are_refused(string args)
    {
        var (store, j, _) = Setup();
        store.Set("user", "PATH", "C:\\keep");
        ToolResult r;
        try { r = await new PathEditTool(store).RunAsync(ToolTestKit.Args(args), ToolTestKit.Ctx(j)); }
        catch (ToolInputException ex) { r = ToolResult.Failure(ex.Message); }
        Assert.False(r.Ok);
        Assert.Equal("C:\\keep", store.Get("user", "PATH"));
        Assert.Empty(j.List());
    }

    [Fact]
    public async Task Dry_run_shows_the_plan_without_touching_anything()
    {
        var (store, j, real) = Setup();
        store.Set("user", "PATH", real);
        var r = await new PathEditTool(store).RunAsync(ToolTestKit.Args("{\"action\":\"add\",\"entry\":\"C:\\\\Tools\\\\bin\"}"), ToolTestKit.Ctx(j, dry: true));
        Assert.Contains("Dry run", r.Text);
        Assert.Equal(real, store.Get("user", "PATH"));
        Assert.Empty(j.List());
    }
}

public class StartupToolTests
{
    private static (StartupLocations loc, ToolTestKit.RegistrySandbox sb, string folder) Setup()
    {
        var sb = new ToolTestKit.RegistrySandbox();
        string userFolder = TestEnvironment.NewTempDir("startup");
        using (var k = Registry.CurrentUser.CreateSubKey(sb.Path + @"\RunUser")) { k.SetValue("Chat App", "\"C:\\Apps\\chat.exe\" --minimized"); k.SetValue("Updater", "C:\\Apps\\upd.exe"); }
        using (var k = Registry.CurrentUser.CreateSubKey(sb.Path + @"\RunMachine")) k.SetValue("Updater", "C:\\Program Files\\Vendor\\updater.exe");
        File.WriteAllText(Path.Combine(userFolder, "notes.lnk"), "x");
        File.WriteAllText(Path.Combine(userFolder, "desktop.ini"), "x");
        var loc = new StartupLocations
        {
            RunUser = ("HKCU", sb.Path + @"\RunUser"), RunMachine = ("HKCU", sb.Path + @"\RunMachine"),
            ApprovedRun = ("HKCU", sb.Path + @"\ApprovedRun"), ApprovedFolder = ("HKCU", sb.Path + @"\ApprovedFolder"),
            UserFolder = userFolder, CommonFolder = null,
        };
        return (loc, sb, userFolder);
    }

    [Fact]
    public async Task Listing_shows_every_source_and_hides_desktop_ini()
    {
        var (loc, sb, _) = Setup();
        using (sb)
        {
            var r = await new StartupAppsListTool(loc).RunAsync(ToolTestKit.Args("{}"), ToolTestKit.Ctx(ToolTestKit.NewJournal()));
            var rows = ((JsonArray)r.Data!).Select(x => ((string?)x!["name"], (string?)x["source"], (bool?)x["enabled"])).ToList();
            Assert.Contains(("Chat App", "run-user", (bool?)true), rows);
            Assert.Contains(("Updater", "run-machine", (bool?)true), rows);
            Assert.Contains(("notes.lnk", "folder-user", (bool?)true), rows);
            Assert.DoesNotContain(rows, x => x.Item1 == "desktop.ini");
        }
    }

    [Fact]
    public async Task Disabling_and_enabling_work_and_revert_puts_the_old_state_back()
    {
        var (loc, sb, _) = Setup();
        using (sb)
        {
            var j = ToolTestKit.NewJournal();
            j.RegisterUndo("registry_values", d => RegistryUndo.Restore(d, null));
            var set = new StartupAppsSetTool(loc);
            var list = new StartupAppsListTool(loc);
            bool Enabled(string name, string? source = null) => ((JsonArray)list.RunAsync(new JsonObject(), ToolTestKit.Ctx(j)).GetAwaiter().GetResult().Data!).OfType<JsonObject>().First(o => (string?)o["name"] == name && (source == null || (string?)o["source"] == source))["enabled"]!.GetValue<bool>();

            var off = await set.RunAsync(ToolTestKit.Args("{\"name\":\"Chat App\",\"enabled\":false}"), ToolTestKit.Ctx(j));
            Assert.True(off.Ok, off.Text);
            Assert.False(Enabled("Chat App"));
            var entry = Assert.Single(j.List());

            Assert.Null(j.Revert(entry.Id));
            Assert.True(Enabled("Chat App"));

            Assert.True((await set.RunAsync(ToolTestKit.Args("{\"name\":\"notes.lnk\",\"enabled\":false}"), ToolTestKit.Ctx(j))).Ok);
            Assert.False(Enabled("notes.lnk"));
        }
    }

    [Fact]
    public async Task A_name_shared_by_two_sources_needs_the_source_and_changes_only_that_one()
    {
        var (loc, sb, _) = Setup();
        using (sb)
        {
            var j = ToolTestKit.NewJournal();
            var set = new StartupAppsSetTool(loc);
            var ambiguous = await set.RunAsync(ToolTestKit.Args("{\"name\":\"Updater\",\"enabled\":false}"), ToolTestKit.Ctx(j));
            Assert.False(ambiguous.Ok);
            Assert.Contains("source", ambiguous.Text);

            var done = await set.RunAsync(ToolTestKit.Args("{\"name\":\"Updater\",\"source\":\"run-machine\",\"enabled\":false}"), ToolTestKit.Ctx(j));
            Assert.True(done.Ok, done.Text);
            Assert.Contains("share this switch", done.Text);   // Windows keeps one on/off choice per name, so both entries follow it
            var rows = ((JsonArray)(await new StartupAppsListTool(loc).RunAsync(new JsonObject(), ToolTestKit.Ctx(j))).Data!).OfType<JsonObject>().Where(o => (string?)o["name"] == "Updater").ToList();
            Assert.False(rows.First(o => (string?)o["source"] == "run-machine")["enabled"]!.GetValue<bool>());
        }
    }

    [Fact]
    public async Task Unknown_names_already_correct_states_and_previews_record_nothing()
    {
        var (loc, sb, _) = Setup();
        using (sb)
        {
            var j = ToolTestKit.NewJournal();
            var set = new StartupAppsSetTool(loc);
            Assert.False((await set.RunAsync(ToolTestKit.Args("{\"name\":\"Nope\",\"enabled\":false}"), ToolTestKit.Ctx(j))).Ok);
            Assert.Contains("already", (await set.RunAsync(ToolTestKit.Args("{\"name\":\"Chat App\",\"enabled\":true}"), ToolTestKit.Ctx(j))).Text);
            Assert.Contains("Dry run", (await set.RunAsync(ToolTestKit.Args("{\"name\":\"Chat App\",\"enabled\":false}"), ToolTestKit.Ctx(j, dry: true))).Text);
            Assert.Empty(j.List());
            Assert.True(((JsonArray)(await new StartupAppsListTool(loc).RunAsync(new JsonObject(), ToolTestKit.Ctx(j))).Data!).OfType<JsonObject>().First(o => (string?)o["name"] == "Chat App")["enabled"]!.GetValue<bool>());
        }
    }
}

public class SettingsToolTests
{
    [Fact]
    public async Task Reading_settings_returns_wallpaper_theme_power_plans_and_display()
    {
        var r = await new SettingsGetTool().RunAsync(ToolTestKit.Args("{}"), ToolTestKit.Ctx(ToolTestKit.NewJournal()));
        Assert.True(r.Ok);
        var d = (JsonObject)r.Data!;
        Assert.NotNull(d["wallpaper"]!["style"]);
        Assert.Contains((string?)d["theme"]!["apps"], new[] { "light", "dark" });
        Assert.NotEmpty((JsonArray)d["power_plans"]!);
        Assert.Matches(@"^\d+x\d+$", (string?)d["display"]!["primary_resolution"]);
        Assert.True((int?)d["display"]!["scale_percent"] >= 100);
    }

    [Fact]
    public async Task One_area_can_be_read_alone_and_a_bad_area_is_refused()
    {
        var j = ToolTestKit.NewJournal();
        var r = await new SettingsGetTool().RunAsync(ToolTestKit.Args("{\"area\":\"theme\"}"), ToolTestKit.Ctx(j));
        Assert.Single(((JsonObject)r.Data!).Select(x => x.Key));
        await Assert.ThrowsAsync<ToolInputException>(() => new SettingsGetTool().RunAsync(ToolTestKit.Args("{\"area\":\"registry\"}"), ToolTestKit.Ctx(j)));
    }

    [Theory]
    [InlineData("{\"area\":\"wallpaper\",\"value\":\"relative\\\\pic.png\"}")]
    [InlineData("{\"area\":\"wallpaper\",\"value\":\"\\\\\\\\server\\\\share\\\\pic.png\"}")]
    [InlineData("{\"area\":\"wallpaper\",\"value\":\"C:\\\\no\\\\such\\\\pic.png\"}")]
    [InlineData("{\"area\":\"wallpaper\",\"value\":\"C:\\\\Windows\\\\System32\\\\notepad.exe\"}")]
    [InlineData("{\"area\":\"theme\",\"value\":\"purple\"}")]
    [InlineData("{\"area\":\"power_plan\",\"value\":\"No Such Plan\"}")]
    public async Task Invalid_changes_are_refused_before_anything_is_recorded(string args)
    {
        var j = ToolTestKit.NewJournal();
        SettingsSetTool.RegisterUndo(j);
        var r = await new SettingsSetTool().RunAsync(ToolTestKit.Args(args), ToolTestKit.Ctx(j));
        Assert.False(r.Ok, r.Text);
        Assert.Empty(j.List());
    }

    [Fact]
    public async Task A_wallpaper_picture_that_is_too_big_or_the_wrong_style_is_refused()
    {
        var j = ToolTestKit.NewJournal();
        string dir = TestEnvironment.NewTempDir("pics");
        string pic = Path.Combine(dir, "ok.png");
        File.WriteAllBytes(pic, new byte[] { 1, 2, 3 });
        var tool = new SettingsSetTool();
        var bad = await tool.RunAsync(ToolTestKit.Args($"{{\"area\":\"wallpaper\",\"value\":{System.Text.Json.JsonSerializer.Serialize(pic)},\"style\":\"sideways\"}}"), ToolTestKit.Ctx(j));
        Assert.False(bad.Ok);
        var dry = await tool.RunAsync(ToolTestKit.Args($"{{\"area\":\"wallpaper\",\"value\":{System.Text.Json.JsonSerializer.Serialize(pic)},\"style\":\"fill\"}}"), ToolTestKit.Ctx(j, dry: true));
        Assert.True(dry.Ok);
        Assert.Contains("Dry run", dry.Text);
        Assert.Empty(j.List());
    }

    [Fact]
    public async Task Setting_the_theme_to_what_it_already_is_is_journaled_and_revert_restores_the_registry_exactly()
    {
        var j = ToolTestKit.NewJournal();
        SettingsSetTool.RegisterUndo(j);
        string current = (string)((JsonObject)(await new SettingsGetTool().RunAsync(ToolTestKit.Args("{\"area\":\"theme\"}"), ToolTestKit.Ctx(j))).Data!)["theme"]!["apps"]!;
        string before = SnapshotTheme();

        var r = await new SettingsSetTool().RunAsync(ToolTestKit.Args($"{{\"area\":\"theme\",\"value\":\"{current}\"}}"), ToolTestKit.Ctx(j));
        Assert.True(r.Ok, r.Text);
        var entry = Assert.Single(j.List());
        Assert.Null(j.Revert(entry.Id));
        Assert.Equal(before, SnapshotTheme());
    }

    private static string SnapshotTheme()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return $"{k?.GetValue("AppsUseLightTheme")}|{k?.GetValue("SystemUsesLightTheme")}";
    }

    [Fact]
    public async Task Setting_the_active_power_plan_again_is_journaled_and_reverts_without_harm()
    {
        var j = ToolTestKit.NewJournal();
        SettingsSetTool.RegisterUndo(j);
        var plans = (JsonArray)((JsonObject)(await new SettingsGetTool().RunAsync(ToolTestKit.Args("{\"area\":\"power_plan\"}"), ToolTestKit.Ctx(j))).Data!)["power_plans"]!;
        var active = plans.OfType<JsonObject>().FirstOrDefault(p => (bool?)p["active"] == true);
        if (active == null) return;   // a PC with no readable plan list: nothing to exercise
        var r = await new SettingsSetTool().RunAsync(ToolTestKit.Args($"{{\"area\":\"power_plan\",\"value\":\"{(string?)active["name"]}\"}}"), ToolTestKit.Ctx(j));
        Assert.True(r.Ok, r.Text);
        Assert.Null(j.Revert(j.List()[0].Id));
        var after = (JsonArray)((JsonObject)(await new SettingsGetTool().RunAsync(ToolTestKit.Args("{\"area\":\"power_plan\"}"), ToolTestKit.Ctx(j))).Data!)["power_plans"]!;
        Assert.Equal((string?)active["id"], (string?)after.OfType<JsonObject>().First(p => (bool?)p["active"] == true)["id"]);
    }

    [Fact]
    public void Power_plan_output_is_parsed()
    {
        var plans = PowerPlans.Parse("Existing Power Schemes (* Active)\n-----------------------------------\nPower Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *\nPower Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance)\n");
        Assert.Equal(2, plans.Count);
        Assert.True(plans[0].Active);
        Assert.Equal("High performance", plans[1].Name);
        Assert.Empty(PowerPlans.Parse("garbage"));
    }
}

public class SystemToolTests
{
    private static ToolContext Ctx() => ToolTestKit.Ctx(ToolTestKit.NewJournal());

    [Fact]
    public async Task Disk_usage_without_a_path_lists_the_drives()
    {
        var r = await new DiskUsageTool().RunAsync(new JsonObject(), Ctx());
        Assert.True(r.Ok);
        Assert.NotEmpty((JsonArray)r.Data!);
        Assert.True((double?)((JsonArray)r.Data!)[0]!["total_gb"] > 0);
    }

    [Fact]
    public async Task Disk_usage_of_a_folder_ranks_the_biggest_items_with_everything_below_them()
    {
        string root = TestEnvironment.NewTempDir("du");
        Directory.CreateDirectory(Path.Combine(root, "big", "deeper"));
        Directory.CreateDirectory(Path.Combine(root, "small"));
        File.WriteAllBytes(Path.Combine(root, "big", "deeper", "a.bin"), new byte[3 * 1048576]);
        File.WriteAllBytes(Path.Combine(root, "big", "b.bin"), new byte[1048576]);
        File.WriteAllBytes(Path.Combine(root, "small", "c.bin"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(root, "loose.bin"), new byte[2 * 1048576]);

        var r = await new DiskUsageTool().RunAsync(ToolTestKit.Args($"{{\"path\":{System.Text.Json.JsonSerializer.Serialize(root)},\"top\":2}}"), Ctx());
        var rows = ((JsonArray)r.Data!).OfType<JsonObject>().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("big", (string?)rows[0]["name"]);
        Assert.Equal(4.0, (double?)rows[0]["size_mb"]);
        Assert.Equal("loose.bin", (string?)rows[1]["name"]);
        Assert.Contains("6", r.Text);
    }

    [Fact]
    public async Task Disk_usage_refuses_relative_and_missing_paths_and_never_deletes()
    {
        string root = TestEnvironment.NewTempDir("du");
        File.WriteAllText(Path.Combine(root, "keep.txt"), "x");
        Assert.False((await new DiskUsageTool().RunAsync(ToolTestKit.Args("{\"path\":\"relative\"}"), Ctx())).Ok);
        Assert.False((await new DiskUsageTool().RunAsync(ToolTestKit.Args("{\"path\":\"C:\\\\no\\\\such\\\\folder\"}"), Ctx())).Ok);
        await new DiskUsageTool().RunAsync(ToolTestKit.Args($"{{\"path\":{System.Text.Json.JsonSerializer.Serialize(root)}}}"), Ctx());
        Assert.True(File.Exists(Path.Combine(root, "keep.txt")));
    }

    [Fact]
    public async Task Disk_usage_does_not_follow_a_junction_out_of_the_folder()
    {
        string root = TestEnvironment.NewTempDir("du");
        string outside = TestEnvironment.NewTempDir("outside");
        File.WriteAllBytes(Path.Combine(outside, "huge.bin"), new byte[5 * 1048576]);
        string link = Path.Combine(root, "link");
        var mk = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        mk.WaitForExit();
        if (!Directory.Exists(link)) return;   // junction creation not possible here
        File.WriteAllBytes(Path.Combine(root, "real.bin"), new byte[1048576]);
        var r = await new DiskUsageTool().RunAsync(ToolTestKit.Args($"{{\"path\":{System.Text.Json.JsonSerializer.Serialize(root)}}}"), Ctx());
        Assert.DoesNotContain(((JsonArray)r.Data!).OfType<JsonObject>(), o => (string?)o["name"] == "link");
        Assert.Contains("1 MB", r.Text);
    }

    [Fact]
    public async Task Network_adapters_dns_ping_and_port_owner_work()
    {
        var net = new NetworkInfoTool();
        Assert.True((await net.RunAsync(ToolTestKit.Args("{\"action\":\"adapters\"}"), Ctx())).Ok);
        var dns = await net.RunAsync(ToolTestKit.Args("{\"action\":\"dns\",\"host\":\"localhost\"}"), Ctx());
        Assert.True(dns.Ok, dns.Text);
        var ping = await net.RunAsync(ToolTestKit.Args("{\"action\":\"ping\",\"host\":\"127.0.0.1\"}"), Ctx());
        Assert.True(ping.Ok, ping.Text);
        Assert.Contains("4 of 4", ping.Text);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var owner = await net.RunAsync(ToolTestKit.Args($"{{\"action\":\"port_owner\",\"port\":{port}}}"), Ctx());
            Assert.Contains("in use", owner.Text);
            var row = (JsonObject)((JsonArray)owner.Data!)[0]!;
            Assert.Equal(Environment.ProcessId, (int?)row["pid"]);
        }
        finally { listener.Stop(); }
        Assert.Contains("Nothing is using", (await net.RunAsync(ToolTestKit.Args("{\"action\":\"port_owner\",\"port\":1}"), Ctx())).Text);
    }

    [Theory]
    [InlineData("{\"action\":\"dns\",\"host\":\"a b\"}")]
    [InlineData("{\"action\":\"dns\",\"host\":\"evil.com; calc\"}")]
    [InlineData("{\"action\":\"ping\",\"host\":\"-t\"}")]
    [InlineData("{\"action\":\"ping\"}")]
    [InlineData("{\"action\":\"traceroute\",\"host\":\"x\"}")]
    [InlineData("{\"action\":\"port_owner\",\"port\":70000}")]
    public async Task Hostile_network_requests_are_refused(string args)
    {
        await Assert.ThrowsAsync<ToolInputException>(() => new NetworkInfoTool().RunAsync(ToolTestKit.Args(args), Ctx()));
    }

    [Fact]
    public async Task Processes_find_this_test_program_and_rank_by_memory()
    {
        var found = await new ProcessesTool().RunAsync(ToolTestKit.Args("{\"action\":\"find\",\"name\":\"testhost\",\"top\":5}"), Ctx());
        Assert.True(found.Ok);
        Assert.Contains(((JsonArray)found.Data!).OfType<JsonObject>(), o => (int?)o["pid"] == Environment.ProcessId);

        var top = await new ProcessesTool().RunAsync(ToolTestKit.Args("{\"top\":3}"), Ctx());
        var mem = ((JsonArray)top.Data!).Select(o => (double)o!["memory_mb"]!).ToList();
        Assert.Equal(mem.OrderByDescending(m => m).ToList(), mem);
        await Assert.ThrowsAsync<ToolInputException>(() => new ProcessesTool().RunAsync(ToolTestKit.Args("{\"action\":\"find\"}"), Ctx()));
    }

    [Fact]
    public async Task Ending_a_program_needs_the_right_name_refuses_essential_processes_and_works_on_a_child()
    {
        var j = ToolTestKit.NewJournal();
        ProcessEndTool.RegisterUndo(j);
        var tool = new ProcessEndTool();
        var ctx = ToolTestKit.Ctx(j);

        Assert.False((await tool.RunAsync(ToolTestKit.Args($"{{\"pid\":{Environment.ProcessId},\"name\":\"definitely-not-me\"}}"), ctx)).Ok);
        var self = await tool.RunAsync(ToolTestKit.Args($"{{\"pid\":{Environment.ProcessId},\"name\":\"{Process.GetCurrentProcess().ProcessName}\"}}"), ctx);
        Assert.True(self.Ok == false || Process.GetCurrentProcess().ProcessName != "levity");   // never the test host's own protected names
        Assert.False((await tool.RunAsync(ToolTestKit.Args("{\"pid\":99999999,\"name\":\"x\"}"), ctx)).Ok);
        var sys = Process.GetProcessesByName("csrss").FirstOrDefault();
        if (sys != null) Assert.False((await tool.RunAsync(ToolTestKit.Args($"{{\"pid\":{sys.Id},\"name\":\"csrss\"}}"), ctx)).Ok);

        var child = Process.Start(new ProcessStartInfo("ping", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            var dry = await tool.RunAsync(ToolTestKit.Args($"{{\"pid\":{child.Id},\"name\":\"PING\"}}"), ToolTestKit.Ctx(j, dry: true));
            Assert.True(dry.Ok, dry.Text);
            Assert.Contains("Dry run", dry.Text);
            Assert.False(child.HasExited, "ping exited early, code " + (child.HasExited ? child.ExitCode : -1) + " dry: " + dry.Text);
            Assert.Empty(j.List());

            var real = await tool.RunAsync(ToolTestKit.Args($"{{\"pid\":{child.Id},\"name\":\"ping\"}}"), ctx);
            Assert.True(real.Ok, real.Text);
            Assert.True(child.WaitForExit(5000));
            var entry = Assert.Single(j.List());
            Assert.Contains("cannot be undone", j.Revert(entry.Id)!);
        }
        finally { try { if (!child.HasExited) child.Kill(); } catch { } }
    }
}

public class MachineProfileTests
{
    [Fact]
    public void The_fast_facts_describe_this_pc()
    {
        var p = MachineProfile.BuildFast();
        Assert.Contains("Windows", (string?)p["windows"]!["edition"]);
        Assert.True((int?)p["cpu"]!["logical_cores"] > 0);
        Assert.True((double?)p["ram_gb"] > 1);
        Assert.NotEmpty((JsonArray)p["drives"]!);
    }

    [Fact]
    public async Task The_profile_is_built_saved_reused_and_rebuilt_on_request()
    {
        string file = Path.Combine(TestEnvironment.NewTempDir("profile"), "machine.json");
        var profile = new MachineProfile(file);
        Assert.Null(profile.Peek());

        var first = await profile.GetAsync();
        Assert.True(File.Exists(file));
        Assert.NotNull(first["dev_tools"]);

        File.WriteAllText(file, ((JsonObject)JsonNode.Parse(first.ToJsonString())!).ToJsonString());   // same content: reuse must not rebuild
        var second = await new MachineProfile(file).GetAsync();
        Assert.Equal((string?)first["built_utc"], (string?)second["built_utc"]);

        var forced = await new MachineProfile(file).GetAsync(refresh: true);
        Assert.NotEqual((string?)first["built_utc"], (string?)forced["built_utc"]);
    }

    [Fact]
    public async Task An_old_profile_is_not_reused()
    {
        string file = Path.Combine(TestEnvironment.NewTempDir("profile"), "machine.json");
        var old = MachineProfile.BuildFast();
        old["built_utc"] = DateTime.UtcNow.AddDays(-3).ToString("o");
        File.WriteAllText(file, old.ToJsonString());
        Assert.Null(new MachineProfile(file).Peek());
        var rebuilt = await new MachineProfile(file).GetAsync();
        Assert.True(DateTime.Parse((string?)rebuilt["built_utc"]!, null, System.Globalization.DateTimeStyles.RoundtripKind) > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task A_damaged_cache_file_is_ignored()
    {
        string file = Path.Combine(TestEnvironment.NewTempDir("profile"), "machine.json");
        File.WriteAllText(file, "{ not json");
        var p = await new MachineProfile(file).GetAsync();
        Assert.NotNull(p["windows"]);
    }

    [Fact]
    public async Task The_summary_is_short_and_names_the_essentials()
    {
        var p = await MachineProfile.BuildAsync();
        string s = MachineProfile.Summary(p);
        Assert.True(s.Length < 1600, $"{s.Length} characters is too long to send with every conversation");
        Assert.Contains("This PC: Windows", s);
        Assert.Contains("GB RAM", s);
        Assert.Contains("Drives:", s);
    }

    [Fact]
    public void Summary_copes_with_a_nearly_empty_profile()
    {
        string s = MachineProfile.Summary(new JsonObject());
        Assert.StartsWith("This PC:", s);
    }

    [Fact]
    public void Finding_a_program_on_PATH_finds_the_shell_and_does_not_invent_others()
    {
        Assert.NotNull(MachineProfile.FindOnPath("cmd"));
        Assert.Null(MachineProfile.FindOnPath("definitely-not-a-real-program-xyz"));
    }
}

public class DryRunEnforcementTests
{
    [Fact]
    public async Task Dry_run_in_the_arguments_prevents_the_change_even_when_the_caller_did_not_set_the_context_flag()
    {
        var store = new FakeEnvStore();
        var box = Toolbox.Create(TestEnvironment.NewTempDir("box"));
        var reg = new ToolRegistry();
        reg.Add(new EnvironmentSetTool(store));
        var j = ToolTestKit.NewJournal();

        var r = await reg.CallAsync("environment_set", ToolTestKit.Args("{\"name\":\"SHOULD_NOT_EXIST\",\"value\":\"1\",\"dry_run\":true}"), ToolTestKit.Ctx(j));   // ctx.DryRun is false
        Assert.True(r.Ok);
        Assert.Contains("Dry run", r.Text);
        Assert.Null(store.Get("user", "SHOULD_NOT_EXIST"));
        Assert.Empty(j.List());
        Assert.NotNull(box);
    }

    [Fact]
    public async Task Dry_run_false_or_missing_still_makes_the_change()
    {
        var store = new FakeEnvStore();
        var reg = new ToolRegistry();
        reg.Add(new EnvironmentSetTool(store));
        var j = ToolTestKit.NewJournal();
        EnvironmentSetTool.RegisterUndo(j, store);
        await reg.CallAsync("environment_set", ToolTestKit.Args("{\"name\":\"A_ONE\",\"value\":\"1\",\"dry_run\":false}"), ToolTestKit.Ctx(j));
        await reg.CallAsync("environment_set", ToolTestKit.Args("{\"name\":\"A_TWO\",\"value\":\"2\"}"), ToolTestKit.Ctx(j));
        Assert.Equal("1", store.Get("user", "A_ONE"));
        Assert.Equal("2", store.Get("user", "A_TWO"));
    }
}
