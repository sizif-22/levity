using Levity.Services;
using Xunit;

namespace Levity.Tests;

/// <summary>Where settings, chats and diagrams live, and the one-time move from the folder the app used before it was called Levity.</summary>
public class DataFolderTests
{
    private static string AppData() => TestEnvironment.NewTempDir("appdata");
    private static string Old(string appData) => Path.Combine(appData, "AntigravitySidebar");
    private static string New(string appData) => Path.Combine(appData, "Levity");

    private static void Seed(string dir, params (string file, string text)[] files)
    {
        foreach (var (file, text) in files)
        {
            string path = Path.Combine(dir, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
    }

    [Fact]
    public void A_fresh_install_uses_the_new_folder_name_and_creates_nothing()
    {
        string appData = AppData();
        Assert.Equal(New(appData), AppSettings.ResolveDataDir(appData, null));
        Assert.False(Directory.Exists(New(appData)));
        Assert.False(Directory.Exists(Old(appData)));
    }

    [Fact]
    public void An_existing_old_folder_is_moved_with_everything_in_it()
    {
        string appData = AppData();
        Seed(Old(appData), ("settings.json", "{\"RelayUrl\":\"wss://x\"}"), ("sessions/abc.json", "chat"), ("diagrams/d.json", "diagram"), (".env", "A=1"));

        string dir = AppSettings.ResolveDataDir(appData, null);

        Assert.Equal(New(appData), dir);
        Assert.False(Directory.Exists(Old(appData)), "the old folder must be gone, not copied");
        Assert.Equal("{\"RelayUrl\":\"wss://x\"}", File.ReadAllText(Path.Combine(dir, "settings.json")));
        Assert.Equal("chat", File.ReadAllText(Path.Combine(dir, "sessions", "abc.json")));
        Assert.Equal("diagram", File.ReadAllText(Path.Combine(dir, "diagrams", "d.json")));
        Assert.Equal("A=1", File.ReadAllText(Path.Combine(dir, ".env")));
    }

    [Fact]
    public void Running_again_after_the_move_changes_nothing()
    {
        string appData = AppData();
        Seed(Old(appData), ("settings.json", "x"));
        AppSettings.ResolveDataDir(appData, null);
        Assert.Equal(New(appData), AppSettings.ResolveDataDir(appData, null));
        Assert.Equal("x", File.ReadAllText(Path.Combine(New(appData), "settings.json")));
    }

    [Fact]
    public void When_both_exist_the_new_folder_wins_and_the_old_one_is_left_alone()
    {
        string appData = AppData();
        Seed(Old(appData), ("settings.json", "old"));
        Seed(New(appData), ("settings.json", "new"));

        Assert.Equal(New(appData), AppSettings.ResolveDataDir(appData, null));
        Assert.Equal("new", File.ReadAllText(Path.Combine(New(appData), "settings.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Old(appData), "settings.json")));
    }

    [Fact]
    public void An_empty_leftover_new_folder_does_not_block_the_move()
    {
        string appData = AppData();
        Seed(Old(appData), ("settings.json", "precious"));
        Directory.CreateDirectory(New(appData));

        Assert.Equal(New(appData), AppSettings.ResolveDataDir(appData, null));
        Assert.Equal("precious", File.ReadAllText(Path.Combine(New(appData), "settings.json")));
    }

    [Fact]
    public void If_the_move_fails_because_a_file_is_in_use_the_old_folder_keeps_being_used_and_nothing_is_lost()
    {
        string appData = AppData();
        Seed(Old(appData), ("settings.json", "precious"), ("sessions/a.json", "chat"));
        using var held = new FileStream(Path.Combine(Old(appData), "sessions", "a.json"), FileMode.Open, FileAccess.Read, FileShare.None);

        string dir = AppSettings.ResolveDataDir(appData, null);

        Assert.Equal(Old(appData), dir);
        Assert.False(Directory.Exists(New(appData)), "no half-moved folder");
        Assert.Equal("precious", File.ReadAllText(Path.Combine(Old(appData), "settings.json")));
    }

    [Fact]
    public void An_explicit_settings_folder_is_used_as_given_and_nothing_is_moved()
    {
        string appData = AppData();
        Seed(Old(appData), ("settings.json", "old"));
        string custom = Path.Combine(TestEnvironment.NewTempDir("custom"), "mine");

        Assert.Equal(custom, AppSettings.ResolveDataDir(appData, custom));
        Assert.True(Directory.Exists(Old(appData)));
        Assert.False(Directory.Exists(custom));
        Assert.Equal(New(appData), AppSettings.ResolveDataDir(appData, "   "));   // blank counts as not set
    }

    [Fact]
    public void The_old_name_survives_only_as_the_constant_used_for_migration()
    {
        Assert.Equal("AntigravitySidebar", AppSettings.LegacyDataFolderName);
    }
}

public class AgentLaunchTests
{
    [Fact]
    public void The_assistant_starts_with_fast_effort_unless_told_otherwise()
    {
        Assert.Contains("--effort low", AgentLaunch.Arguments(null, null));
        Assert.Contains("--effort low", AgentLaunch.Arguments(null, "nonsense"));
        Assert.Contains("--effort high", AgentLaunch.Arguments(null, "HIGH"));
        Assert.Contains("--effort medium", AgentLaunch.Arguments(null, " medium "));
    }

    [Fact]
    public void A_conversation_is_resumed_only_with_a_plain_id()
    {
        Assert.Contains("--conversation 4b467c95-d05b-4092-b269-ec71a3e37a3f", AgentLaunch.Arguments("4b467c95-d05b-4092-b269-ec71a3e37a3f", "low"));
        Assert.DoesNotContain("--conversation", AgentLaunch.Arguments(null, "low"));
        Assert.DoesNotContain("--conversation", AgentLaunch.Arguments("  ", "low"));
        Assert.DoesNotContain("--conversation", AgentLaunch.Arguments("abc & calc", "low"));
        Assert.DoesNotContain("--conversation", AgentLaunch.Arguments("abc\" --x", "low"));
    }

    [Fact]
    public void The_permission_and_streaming_flags_are_kept()
    {
        string args = AgentLaunch.Arguments(null, "low");
        Assert.Contains("--dangerously-skip-permissions", args);
        Assert.Contains("--input-format=stream-json", args);
        Assert.Contains("--output-format=stream-json", args);
    }

    [Fact]
    public void The_efficiency_hint_goes_on_the_first_message_only_and_never_changes_the_message()
    {
        string first = AgentLaunch.WithHint("fix the bug", true);
        Assert.StartsWith(AgentLaunch.EfficiencyHint, first, StringComparison.Ordinal);
        Assert.EndsWith("fix the bug", first, StringComparison.Ordinal);
        Assert.Equal("and another thing", AgentLaunch.WithHint("and another thing", false));
    }

    [Fact]
    public void The_marker_a_shell_profile_looks_for_is_stable()
    {
        Assert.Equal("LEVITY_AGENT", AgentLaunch.EnvMarker);
    }
}
