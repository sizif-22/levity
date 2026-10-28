using Levity.Services;
using Xunit;

namespace Levity.Tests;

public class SessionStatsTests
{
    private static string Result(double secs, long input, long output = 100, long thinking = 10, long cache = 0) =>
        "{\"type\":\"agy_event\",\"payload\":{\"event\":\"result\",\"result\":{\"status\":\"SUCCESS\",\"duration_seconds\":" + secs.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ",\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output + ",\"thinking_tokens\":" + thinking + ",\"cache_read_tokens\":" + cache + "}}}}";

    private static string Tool(string name, double secs, string? commandLine = null, string state = "DONE") =>
        "{\"type\":\"agy_event\",\"payload\":{\"event\":\"step_update\",\"step_update\":{\"step_type\":\"tool\",\"state\":\"" + state + "\",\"tool_name\":\"" + name + "\",\"duration_seconds\":" +
        secs.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"tool_info\":{\"name\":\"" + name + "\",\"parameters\":{" +
        (commandLine != null ? "\"CommandLine\":" + System.Text.Json.JsonSerializer.Serialize(commandLine) : "") + "}}}}}";

    [Fact]
    public void Running_totals_become_per_answer_figures()
    {
        // the assistant reports time and tokens for the whole conversation so far
        var s = SessionStats.Compute(new[] { Result(112, 132_000), Result(440, 222_000), Result(506, 282_000) });
        Assert.Equal(3, s.Turns);
        Assert.Equal(new[] { 112.0, 328.0, 66.0 }, s.PerTurn.Select(t => t.Seconds).ToArray());
        Assert.Equal(new long[] { 132_000, 90_000, 60_000 }, s.PerTurn.Select(t => t.InputTokens).ToArray());
        Assert.Equal(506, s.TotalSeconds);
        Assert.Equal(282_000, s.InputTokens);
    }

    [Fact]
    public void Figures_that_are_not_running_totals_are_added_up()
    {
        var s = SessionStats.Compute(new[] { Result(100, 50_000), Result(30, 20_000) });
        Assert.Equal(new[] { 100.0, 30.0 }, s.PerTurn.Select(t => t.Seconds).ToArray());
        Assert.Equal(130, s.TotalSeconds);
        Assert.Equal(70_000, s.InputTokens);
    }

    [Fact]
    public void Tools_are_counted_timed_and_ranked_and_only_finished_steps_count()
    {
        var s = SessionStats.Compute(new[] { Tool("run_command", 8, "ls"), Tool("run_command", 20, "git status"), Tool("read_file", 0.2), Tool("run_command", 0, state: "ACTIVE"), Result(60, 10_000) });
        var run = s.Tools.First(t => t.Name == "run_command");
        Assert.Equal(2, run.Count);
        Assert.Equal(28, run.TotalSeconds);
        Assert.Equal(20, run.MaxSeconds);
        Assert.Equal("run_command", s.Tools[0].Name);
    }

    [Fact]
    public void The_slowest_steps_say_what_they_were_running()
    {
        var s = SessionStats.Compute(new[] { Tool("run_command", 71, "Get-Process | Where-Object { $_.Name -match 'wallpaper' }"), Tool("read_file", 1), Tool("run_command", 9, "dir") });
        Assert.Equal(2, s.SlowSteps.Count);
        Assert.Equal(71, s.SlowSteps[0].Seconds);
        Assert.Contains("Get-Process", s.SlowSteps[0].What);
    }

    [Fact]
    public void Slow_shell_starts_are_pointed_out_with_the_profile_fix()
    {
        var lines = new List<string>();
        for (int i = 0; i < 6; i++) lines.Add(Tool("run_command", 8.5, "echo " + i));
        lines.Add(Result(200, 50_000));
        var s = SessionStats.Compute(lines);
        Assert.Contains(s.Findings, f => f.Contains("shell commands took") && f.Contains("LEVITY_AGENT"));
    }

    [Fact]
    public void Quick_commands_and_few_commands_are_not_blamed()
    {
        var quick = SessionStats.Compute(Enumerable.Range(0, 6).Select(i => Tool("run_command", 0.4)).Append(Result(30, 5_000)));
        Assert.DoesNotContain(quick.Findings, f => f.Contains("shell commands"));
        var few = SessionStats.Compute(new[] { Tool("run_command", 30), Tool("run_command", 30), Result(100, 5_000) });
        Assert.DoesNotContain(few.Findings, f => f.Contains("shell commands"));
    }

    [Fact]
    public void Many_single_file_reads_a_huge_first_message_and_many_searches_are_called_out()
    {
        var lines = new List<string>();
        for (int i = 0; i < 14; i++) lines.Add(Tool("read_file", 1));
        for (int i = 0; i < 9; i++) lines.Add(Tool("search_web", 4));
        lines.Add(Result(60, 133_000));
        var s = SessionStats.Compute(lines);
        Assert.Contains(s.Findings, f => f.Contains("14 files were read one at a time"));
        Assert.Contains(s.Findings, f => f.Contains("9 separate web searches"));
        Assert.Contains(s.Findings, f => f.Contains("133,000 input tokens") || f.Contains("133" ));
    }

    [Fact]
    public void Most_of_the_time_going_to_the_model_is_reported()
    {
        var s = SessionStats.Compute(new[] { Tool("read_file", 5), Result(400, 20_000) });
        Assert.Contains(s.Findings, f => f.Contains("model thinking between steps"));
    }

    [Fact]
    public void Garbage_other_events_and_empty_input_never_throw()
    {
        var s = SessionStats.Compute(new[] { "not json", "{\"type\":\"user_message\",\"text\":\"hi\"}", "{\"type\":\"agy_event\",\"payload\":null}", "{\"type\":\"agy_event\"", "" });
        Assert.Equal(0, s.Turns);
        Assert.Empty(s.Findings);
        Assert.Equal(0, SessionStats.Compute(Array.Empty<string>()).Turns);
    }

    [Fact]
    public void The_real_shape_of_a_stored_chat_is_understood()
    {
        string line = "{\"type\":\"agy_event\",\"payload\":{\"event\":\"result\",\"result\":{\"conversation_id\":\"x\",\"status\":\"SUCCESS\",\"response\":\"done\",\"duration_seconds\":769.2235199,\"num_turns\":5,\"usage\":{\"input_tokens\":327392,\"output_tokens\":25158,\"thinking_tokens\":11274,\"cache_read_tokens\":2312462,\"total_tokens\":352550}}}}";
        var s = SessionStats.Compute(new[] { line });
        Assert.Equal(1, s.Turns);
        Assert.Equal(769.2, s.TotalSeconds);
        Assert.Equal(327_392, s.InputTokens);
        Assert.Equal(11_274, s.ThinkingTokens);
    }
}

public class GeminiFootprintTests
{
    private static string Make(params (string path, int kb)[] files)
    {
        string root = TestEnvironment.NewTempDir("gemini");
        foreach (var (path, kb) in files)
        {
            string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[kb * 1024]);
        }
        return root;
    }

    [Fact]
    public void Folders_are_sized_counted_and_ranked()
    {
        string root = Make(("brain/a.bin", 2048), ("brain/deep/b.bin", 1024), ("skills/s.md", 4), ("config.json", 1));
        var r = GeminiFootprint.Scan(root);
        Assert.Equal("brain", r.Folders[0].Name);
        Assert.Equal(3.0, r.Folders[0].Megabytes);
        Assert.Equal(2, r.Folders[0].Files);
        Assert.Equal(4, r.TotalFiles);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void A_missing_folder_is_reported_not_thrown()
    {
        var r = GeminiFootprint.Scan(Path.Combine(TestEnvironment.Root, "nope-" + Guid.NewGuid().ToString("N")));
        Assert.Empty(r.Folders);
        Assert.NotEmpty(r.Notes);
    }

    [Fact]
    public void A_big_backup_folder_gets_a_note_and_nothing_is_ever_changed()
    {
        string root = Make(("antigravity-backup/x.bin", 60 * 1024), ("tiny/t.txt", 1));
        string before = string.Join("|", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f));
        var r = GeminiFootprint.Scan(root);
        Assert.Contains(r.Notes, n => n.Contains("antigravity-backup") && n.Contains("backup"));
        Assert.Equal(before, string.Join("|", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f)));
    }

    [Fact]
    public void The_scan_stops_at_its_limits_and_says_so()
    {
        string root = Make(("a/1.bin", 1), ("a/2.bin", 1), ("a/3.bin", 1), ("b/4.bin", 1), ("b/5.bin", 1), ("b/6.bin", 1));
        var r = GeminiFootprint.Scan(root, maxSeconds: 8, maxFiles: 3);
        Assert.True(r.Truncated);
        Assert.True(r.TotalFiles <= 6);
    }

    [Fact]
    public void A_junction_is_not_followed_out_of_the_folder()
    {
        string root = Make(("real/a.bin", 10));
        string outside = Make(("huge/x.bin", 5 * 1024));
        string link = Path.Combine(root, "link");
        var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        mk.WaitForExit();
        if (!Directory.Exists(link)) return;
        var r = GeminiFootprint.Scan(root);
        Assert.DoesNotContain(r.Folders, f => f.Name == "link");
        Assert.True(r.TotalMegabytes < 1);
    }
}
