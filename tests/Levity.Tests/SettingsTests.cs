using AntigravitySidebar.Services;
using Xunit;

namespace Levity.Tests;

public class DotEnvTests
{
    private static string Key(string name) => "LEVITY_TEST_" + name + "_" + Guid.NewGuid().ToString("N")[..6];

    [Fact]
    public void Parses_plain_quoted_exported_and_commented_values()
    {
        string a = Key("A"), b = Key("B"), c = Key("C"), d = Key("D"), e = Key("E");
        DotEnv.Apply(new[]
        {
            "# a comment",
            "",
            $"{a}=plain",
            $"{b}=\"quoted value\"",
            $"{c}='single quoted'",
            $"export {d}=exported",
            $"{e}=value  # trailing comment",
        });
        Assert.Equal("plain", Environment.GetEnvironmentVariable(a));
        Assert.Equal("quoted value", Environment.GetEnvironmentVariable(b));
        Assert.Equal("single quoted", Environment.GetEnvironmentVariable(c));
        Assert.Equal("exported", Environment.GetEnvironmentVariable(d));
        Assert.Equal("value", Environment.GetEnvironmentVariable(e));
    }

    [Fact]
    public void A_value_already_in_the_environment_wins_over_the_file()
    {
        string k = Key("WIN");
        Environment.SetEnvironmentVariable(k, "from-shell");
        DotEnv.Apply(new[] { $"{k}=from-file" });
        Assert.Equal("from-shell", Environment.GetEnvironmentVariable(k));
    }

    [Fact]
    public void Empty_values_and_junk_lines_are_ignored()
    {
        string k = Key("EMPTY");
        DotEnv.Apply(new[] { $"{k}=", "no equals sign", "=novalue" });
        Assert.Null(Environment.GetEnvironmentVariable(k));
        Assert.Null(DotEnv.Get(k));
    }

    [Fact]
    public void A_hash_inside_a_value_without_a_leading_space_is_kept()
    {
        string k = Key("HASH");
        DotEnv.Apply(new[] { $"{k}=https://example.com/#frag" });
        Assert.Equal("https://example.com/#frag", Environment.GetEnvironmentVariable(k));
    }

    [Fact]
    public void GetDouble_falls_back_for_missing_invalid_and_non_positive_values()
    {
        string k = Key("NUM");
        Assert.Equal(460, DotEnv.GetDouble(k, 460));
        Environment.SetEnvironmentVariable(k, "abc");
        Assert.Equal(460, DotEnv.GetDouble(k, 460));
        Environment.SetEnvironmentVariable(k, "-5");
        Assert.Equal(460, DotEnv.GetDouble(k, 460));
        Environment.SetEnvironmentVariable(k, "520.5");
        Assert.Equal(520.5, DotEnv.GetDouble(k, 460));
    }

    [Fact]
    public void Load_reads_the_file_named_by_LEVITY_ENV_FILE()
    {
        string k = Key("FILE");
        string file = Path.Combine(TestEnvironment.NewTempDir("env"), "my.env");
        File.WriteAllText(file, $"{k}=loaded\n");
        Environment.SetEnvironmentVariable("LEVITY_ENV_FILE", file);
        try { DotEnv.Load(); }
        finally { Environment.SetEnvironmentVariable("LEVITY_ENV_FILE", null); }
        Assert.Equal("loaded", Environment.GetEnvironmentVariable(k));
    }
}

public class AppSettingsTests
{
    [Fact]
    public void New_settings_use_safe_defaults_not_personal_values()
    {
        var s = new AppSettings();
        Assert.Null(s.MobileAppUrl);
        Assert.Null(s.RelayUrl);
        Assert.Equal(AppSettings.HomeFolder, s.ProjectsRoot);
        Assert.Equal(460, s.SidebarWidth);
    }

    [Fact]
    public void Defaults_come_from_the_environment_on_first_run()
    {
        Environment.SetEnvironmentVariable("LEVITY_RELAY_URL", "wss://relay.example.workers.dev");
        Environment.SetEnvironmentVariable("LEVITY_APP_URL", "https://app.example.com");
        Environment.SetEnvironmentVariable("LEVITY_SIDEBAR_WIDTH", "520");
        try
        {
            var s = new AppSettings();
            Assert.Equal("wss://relay.example.workers.dev", s.RelayUrl);
            Assert.Equal("https://app.example.com", s.MobileAppUrl);
            Assert.Equal(520, s.SidebarWidth);
        }
        finally
        {
            foreach (string k in new[] { "LEVITY_RELAY_URL", "LEVITY_APP_URL", "LEVITY_SIDEBAR_WIDTH" })
                Environment.SetEnvironmentVariable(k, null);
        }
    }

    [Fact]
    public void ProjectKey_is_case_and_trailing_slash_insensitive()
    {
        Assert.Equal(AppSettings.ProjectKey(@"D:\Work\App\"), AppSettings.ProjectKey(@"d:\work\app"));
    }

    [Fact]
    public void RecordProjectUse_counts_prompts_and_stamps_the_time()
    {
        var s = new AppSettings();
        s.RecordProjectUse(@"D:\Work\App");
        s.RecordProjectUse(@"d:\work\app\");
        string key = AppSettings.ProjectKey(@"D:\Work\App");
        Assert.Equal(2, s.ProjectUses[key]);
        Assert.True(s.ProjectLastUsed[key] > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Pinning_adds_and_removes_without_duplicates()
    {
        var s = new AppSettings();
        s.SetPinned(@"D:\Work\App", true);
        s.SetPinned(@"D:\WORK\APP", true);
        Assert.Single(s.PinnedProjects);
        s.SetPinned(@"D:\Work\App", false);
        Assert.Empty(s.PinnedProjects);
    }

    [Fact]
    public void Recent_folders_dedupe_newest_first_cap_at_12_and_skip_home()
    {
        var s = new AppSettings();
        s.AddRecentFolder(AppSettings.HomeFolder);
        Assert.Empty(s.RecentFolders);

        string a = TestEnvironment.NewTempDir("ra"), b = TestEnvironment.NewTempDir("rb");
        s.AddRecentFolder(a);
        s.AddRecentFolder(b);
        s.AddRecentFolder(a.ToUpperInvariant());
        Assert.Equal(2, s.RecentFolders.Count);
        Assert.Equal(a.ToUpperInvariant(), s.RecentFolders[0]);

        for (int i = 0; i < 20; i++) s.AddRecentFolder(TestEnvironment.NewTempDir("rc"));
        Assert.Equal(12, s.RecentFolders.Count);
    }

    [Fact]
    public void Settings_survive_a_save_and_load_round_trip()
    {
        var s = AppSettings.Load();
        s.WorkingDirectory = TestEnvironment.NewTempDir("wd");
        s.RelayUrl = "wss://roundtrip.example";
        s.SetPinned(@"D:\Pinned", true);
        s.Save();

        var again = AppSettings.Load();
        Assert.Equal(s.WorkingDirectory, again.WorkingDirectory);
        Assert.Equal("wss://roundtrip.example", again.RelayUrl);
        Assert.Contains(AppSettings.ProjectKey(@"D:\Pinned"), again.PinnedProjects);
    }

    [Fact]
    public void Load_replaces_a_missing_working_folder_and_projects_root_with_the_home_folder()
    {
        var s = AppSettings.Load();
        s.WorkingDirectory = Path.Combine(TestEnvironment.Root, "does-not-exist-" + Guid.NewGuid().ToString("N"));
        s.ProjectsRoot = Path.Combine(TestEnvironment.Root, "also-missing-" + Guid.NewGuid().ToString("N"));
        s.Save();

        var again = AppSettings.Load();
        Assert.Equal(AppSettings.HomeFolder, again.WorkingDirectory);
        Assert.Equal(AppSettings.HomeFolder, again.ProjectsRoot);
    }

    [Fact]
    public void Load_drops_recent_folders_that_no_longer_exist()
    {
        var s = AppSettings.Load();
        string real = TestEnvironment.NewTempDir("keep");
        s.RecentFolders = new() { real, Path.Combine(TestEnvironment.Root, "gone-" + Guid.NewGuid().ToString("N")) };
        s.Save();
        Assert.Equal(new[] { real }, AppSettings.Load().RecentFolders);
    }
}
