using System.Text.Json;
using AntigravitySidebar.Services;
using Xunit;

namespace Levity.Tests;

public class FastLearnerDetectorTests
{
    private const string Script = @"C:\Users\me\.gemini\config\skills\fast-learner\scripts\announce.ps1";

    /// <summary>A tool-call event shaped like the real agy stream (see debug.log).</summary>
    private static string Tool(string name, object parameters, string state = "ACTIVE") => JsonSerializer.Serialize(new
    {
        @event = "step_update",
        step_update = new { conversation_id = "c1", step_index = 7, state, step_type = "tool", tool_name = name, tool_info = new { name, parameters } },
    });

    private static string Run(string commandLine, string state = "ACTIVE") => Tool("run_command", new { CommandLine = commandLine }, state);

    [Fact]
    public void An_announced_start_means_Fast_Learner_is_running()
    {
        var n = FastLearnerDetector.Inspect(Run($"& \"{Script}\" -Kind start -Message \"Wallpaper took 8 attempts\""));
        Assert.NotNull(n);
        Assert.Equal(("running", "run", "Wallpaper took 8 attempts", "announce"), (n!.Phase, n.Kind, n.Detail, n.Source));
        Assert.Equal("Fast Learner is running: Wallpaper took 8 attempts", n.Text);
    }

    [Fact]
    public void An_announced_skill_shows_the_skill_name()
    {
        var n = FastLearnerDetector.Inspect(Run($"& '{Script}' -Kind skill -Name windows-wallpaper-manager -Message 'Set wallpaper via COM'"));
        Assert.Equal(("saving", "skill", "windows-wallpaper-manager"), (n!.Phase, n.Kind, n.Detail));
        Assert.Equal("Fast Learner is saving a skill: windows-wallpaper-manager", n.Text);
    }

    [Fact]
    public void An_announced_docs_update_uses_the_message_and_keeps_the_path()
    {
        var n = FastLearnerDetector.Inspect(Run($"& $a -Kind docs -Path AGENTS.md -Message \"Added the build command\" # {Script}"));
        Assert.Equal(("saving", "docs", "Added the build command", "AGENTS.md"), (n!.Phase, n.Kind, n.Detail, n.Path));
        Assert.StartsWith("Fast Learner is updating docs:", n.Text);
    }

    [Fact]
    public void An_announced_note_says_it_is_for_later()
    {
        var n = FastLearnerDetector.Inspect(Run($"& \"{Script}\" -Kind notes -Message \"yt-dlp needs cookies, not verified\""));
        Assert.Equal(("saving", "notes", "yt-dlp needs cookies, not verified"), (n!.Phase, n.Kind, n.Detail));
        Assert.Equal("Fast Learner is taking a note for later: yt-dlp needs cookies, not verified", n.Text);
    }

    [Fact]
    public void An_announced_done_finishes_the_notice()
    {
        var n = FastLearnerDetector.Inspect(Run($"& \"{Script}\" -Kind done -Message \"Saved 1 skill\""));
        Assert.Equal(("done", "Saved 1 skill"), (n!.Phase, n.Detail));
        Assert.Equal("Fast Learner finished: Saved 1 skill", n.Text);
    }

    [Theory]
    [InlineData("-Kind START", "running")]
    [InlineData("-kind start", "running")]
    [InlineData("-Kind 'start'", "running")]
    [InlineData("-Kind \"start\"", "running")]
    public void Kind_is_case_insensitive_and_may_be_quoted(string kindArg, string phase)
    {
        Assert.Equal(phase, FastLearnerDetector.Inspect(Run($"& \"{Script}\" {kindArg}"))!.Phase);
    }

    [Fact]
    public void A_missing_message_gets_a_sensible_default()
    {
        Assert.Equal("learning from this task", FastLearnerDetector.Inspect(Run($"& \"{Script}\" -Kind start"))!.Detail);
        Assert.Equal("a note", FastLearnerDetector.Inspect(Run($"& \"{Script}\" -Kind notes"))!.Detail);
    }

    [Fact]
    public void Quoted_messages_may_contain_spaces_apostrophes_and_escaped_quotes()
    {
        Assert.Equal("it's slow", FastLearnerDetector.Inspect(Run($"& \"{Script}\" -Kind notes -Message \"it's slow\""))!.Detail);
        Assert.Equal("say \"hi\"", FastLearnerDetector.Inspect(Run($"& \"{Script}\" -Kind notes -Message \"say \\\"hi\\\"\""))!.Detail);
        Assert.Equal("it's", FastLearnerDetector.Inspect(Run($"& \"{Script}\" -Kind notes -Message 'it''s'"))!.Detail);
    }

    [Fact]
    public void An_unknown_kind_is_ignored()
    {
        Assert.Null(FastLearnerDetector.Inspect(Run($"& \"{Script}\" -Kind explode")));
        Assert.Null(FastLearnerDetector.Inspect(Run($"& \"{Script}\"")));
    }

    [Fact]
    public void Ordinary_commands_are_not_Fast_Learner()
    {
        Assert.Null(FastLearnerDetector.Inspect(Run("git status")));
        Assert.Null(FastLearnerDetector.Inspect(Run("Get-ChildItem -Path .")));
    }

    [Fact]
    public void Only_the_start_of_a_tool_call_is_reported_so_each_call_counts_once()
    {
        string cmd = $"& \"{Script}\" -Kind start";
        Assert.NotNull(FastLearnerDetector.Inspect(Run(cmd, "ACTIVE")));
        Assert.Null(FastLearnerDetector.Inspect(Run(cmd, "DONE")));
    }

    [Theory]
    [InlineData(@"C:\Users\me\.gemini\config\skills\windows-wallpaper-manager\SKILL.md", "windows-wallpaper-manager")]
    [InlineData(@"C:\Users\me\.gemini\config\skills\x-tool\scripts\run.ps1", "x-tool")]
    [InlineData("C:/Users/me/.gemini/config/skills/fwd-slash/SKILL.md", "fwd-slash")]
    [InlineData(@"D:\proj\.agent\skills\build-and-run\SKILL.md", "build-and-run")]
    [InlineData(@"D:\proj\.agents\skills\other\SKILL.md", "other")]
    public void Writing_into_a_skills_folder_is_reported_even_without_an_announcement(string path, string skill)
    {
        var n = FastLearnerDetector.Inspect(Tool("write_file", new { Path = path }));
        Assert.Equal(("saving", "skill", skill, "path", path), (n!.Phase, n.Kind, n.Detail, n.Source, n.Path));
    }

    [Theory]
    [InlineData("replace_file_content", "TargetFile")]
    [InlineData("multi_replace_file_content", "TargetFile")]
    [InlineData("write_file", "AbsolutePath")]
    public void Other_edit_tools_and_path_names_are_understood(string tool, string argName)
    {
        var parameters = new Dictionary<string, string> { [argName] = @"C:\Users\me\.gemini\config\skills\abc\SKILL.md" };
        Assert.Equal("abc", FastLearnerDetector.Inspect(Tool(tool, parameters))!.Detail);
    }

    [Theory]
    [InlineData(@"D:\proj\README.md")]
    [InlineData(@"D:\proj\src\Program.cs")]
    [InlineData(@"C:\Users\me\GEMINI.md")]
    [InlineData(@"C:\Users\me\.gemini\config\skills\SKILL.md")]   // not inside a named skill folder
    [InlineData(@"D:\proj\skills\x\SKILL.md")]                    // a skills folder that is not the agent's
    public void Writing_other_files_is_not_Fast_Learner(string path)
    {
        Assert.Null(FastLearnerDetector.Inspect(Tool("write_file", new { Path = path })));
    }

    [Fact]
    public void Reading_a_skill_is_not_reported()
    {
        Assert.Null(FastLearnerDetector.Inspect(Tool("read_file", new { Path = @"C:\Users\me\.gemini\config\skills\abc\SKILL.md" })));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"event\":\"result\",\"result\":{}}")]
    [InlineData("{\"event\":\"step_update\",\"step_update\":{\"step_type\":\"agent_response\",\"text_delta\":\"announce.ps1 -Kind start\"}}")]
    [InlineData("{\"event\":\"step_update\",\"step_update\":{\"step_type\":\"tool\",\"state\":\"ACTIVE\",\"tool_name\":\"run_command\"}}")]
    public void Malformed_or_unrelated_events_are_ignored_without_throwing(string json)
    {
        Assert.Null(FastLearnerDetector.Inspect(json));
    }

    [Fact]
    public void The_agent_merely_mentioning_the_script_in_its_text_does_not_trigger_a_notice()
    {
        // Only tool calls count; prose that quotes the command must not light up the notice.
        var prose = JsonSerializer.Serialize(new { @event = "step_update", step_update = new { step_type = "agent_response", state = "ACTIVE", text_delta = $"I will run {Script} -Kind start" } });
        Assert.Null(FastLearnerDetector.Inspect(prose));
    }
}
