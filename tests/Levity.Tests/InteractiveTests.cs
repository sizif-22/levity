using System.Diagnostics;
using System.Text.Json;
using Levity.Services;
using Xunit;

namespace Levity.Tests;

/// <summary>The terminal screen that turns a program's redrawing output into the text a person would see.</summary>
public class VtScreenTests
{
    private static string Show(string output) { var s = new VtScreen(40, 6); s.Feed(output); return s.Text(); }

    [Fact]
    public void Plain_lines_come_out_as_they_were_printed()
    {
        Assert.Equal("hello\nworld", Show("hello\r\nworld\r\n"));
    }

    [Fact]
    public void A_carriage_return_overwrites_the_line_like_a_progress_counter()
    {
        Assert.Equal("done 100%", Show("downloading 10%\rdownloading 50%\rdone 100%\x1b[K"));
    }

    [Fact]
    public void Erase_line_clears_what_was_to_the_right_of_the_cursor()
    {
        Assert.Equal("abc", Show("abcdef\r\x1b[3C\x1b[K"));
        Assert.Equal("ab", Show("abcdef\x1b[4D\x1b[K"));
    }

    [Fact]
    public void Colours_and_titles_are_ignored()
    {
        Assert.Equal("ok", Show("\x1b[32mok\x1b[0m\x1b]0;window title\a"));
        Assert.Equal("ok", Show("\x1b]0;title\x1b\\ok"));
    }

    [Fact]
    public void A_menu_that_redraws_itself_shows_only_its_latest_state()
    {
        string menu1 = "? Pick one\r\n> first\r\n  second\r\n";
        string redraw = "\x1b[3A\x1b[J? Pick one\r\n  first\r\n> second\r\n";   // up three lines, erase, draw again
        Assert.Equal("? Pick one\n  first\n> second", Show(menu1 + redraw));
    }

    [Fact]
    public void Cursor_positioning_places_text_on_the_right_row_and_column()
    {
        Assert.Equal("top\n\n   x", Show("\x1b[1;1Htop\x1b[3;4Hx"));
    }

    [Fact]
    public void An_escape_sequence_split_across_two_chunks_still_works()
    {
        var s = new VtScreen(40, 6);
        s.Feed("abc\x1b[");
        s.Feed("2Kdef");
        Assert.Equal("def", s.Text().TrimStart());
    }

    [Fact]
    public void Lines_that_scroll_off_the_top_are_kept()
    {
        var s = new VtScreen(40, 3);
        for (int i = 1; i <= 8; i++) s.Feed($"line {i}\r\n");
        string text = s.Text();
        Assert.StartsWith("line 1", text);
        Assert.EndsWith("line 8", text);
    }

    [Fact]
    public void Long_lines_wrap_at_the_screen_width()
    {
        var s = new VtScreen(10, 5);
        s.Feed("0123456789ABCDE");
        Assert.Equal("0123456789\nABCDE", s.Text());
    }

    [Fact]
    public void Hostile_or_garbage_output_never_throws()
    {
        var s = new VtScreen(20, 4);
        s.Feed("\x1b[999999999A\x1b[;;;;H\x1b[?1049h\x1b[" + new string('9', 500) + "m\x1b]" + new string('x', 5000) + "\x1b[0;0;0Z\x1b[-5Cabc\0\x01\x7f");
        s.Feed(new string('z', 100000));
        Assert.True(s.Text().Length > 0);
    }

    [Fact]
    public void The_scrollback_is_capped()
    {
        var s = new VtScreen(20, 4, maxLines: 50);
        for (int i = 0; i < 5000; i++) s.Feed("some output line\r\n");
        Assert.True(s.Text().Split('\n').Length <= 55);
    }
}

public class DirectShellFlagTests
{
    [Theory]
    [InlineData("-i bun convex deploy", "bun convex deploy", true)]
    [InlineData("-i -i npm start", "npm start", true)]
    [InlineData("-b npm run dev", "-b npm run dev", false)]
    [InlineData("npm run dev -b", "npm run dev -b", false)]
    [InlineData("grep -i foo file.txt", "grep -i foo file.txt", false)]
    [InlineData("sed -i s/a/b/ f", "sed -i s/a/b/ f", false)]
    [InlineData("git status", "git status", false)]
    public void Only_a_leading_dash_i_is_a_flag_and_dash_b_is_no_longer_one(string input, string expected, bool interactive)
    {
        DirectShell.StripFlags(input, out string stripped, out bool inter);
        Assert.Equal(expected, stripped);
        Assert.Equal(interactive, inter);
    }

    [Fact]
    public void A_flag_with_no_command_after_it_is_left_alone()
    {
        DirectShell.StripFlags("-i", out string stripped, out bool inter);
        Assert.Equal("-i", stripped);
        Assert.False(inter);
    }

    [Theory]
    [InlineData("bun convex deploy", true)]
    [InlineData("bunx convex dev", true)]
    [InlineData("npx convex deploy", true)]
    [InlineData("npm create vite@latest", true)]
    [InlineData("npx create-next-app", true)]
    [InlineData("gh auth login", true)]
    [InlineData("cd app; npm init", true)]
    [InlineData("git status", false)]
    [InlineData("bun run dev", false)]
    [InlineData("npm install", false)]
    [InlineData("echo create", false)]
    [InlineData("Get-ChildItem", false)]
    public void Commands_that_usually_ask_questions_are_recognised(string command, bool expected)
    {
        Assert.Equal(expected, InteractiveCommands.LooksInteractive(command));
    }
}

public class ProjectActionsTests
{
    private static string Folder(params (string file, string text)[] files)
    {
        string dir = TestEnvironment.NewTempDir("proj");
        foreach (var (f, t) in files) File.WriteAllText(Path.Combine(dir, f), t);
        return dir;
    }

    [Fact]
    public void Scripts_are_read_from_package_json_in_file_order()
    {
        string dir = Folder(("package.json", "{ \"name\":\"x\", \"scripts\": { \"dev\": \"vite\", \"build\": \"tsc && vite build\", \"test:unit\": \"vitest\" } }"));
        var scripts = ProjectActions.ReadScripts(dir);
        Assert.Equal(new[] { "dev", "build", "test:unit" }, scripts.Select(s => s.Name).ToArray());
        Assert.Equal("tsc && vite build", scripts[1].Command);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{ \"scripts\": [1,2] }")]
    [InlineData("{ \"scripts\": \"x\" }")]
    [InlineData("{ \"name\": \"no scripts\" }")]
    [InlineData("[]")]
    public void Missing_or_broken_package_json_gives_no_scripts_and_never_throws(string json)
    {
        Assert.Empty(ProjectActions.ReadScripts(Folder(("package.json", json))));
        Assert.Empty(ProjectActions.ReadScripts(Folder()));
    }

    [Fact]
    public void Non_string_script_values_and_odd_names_are_skipped_and_long_commands_are_shortened()
    {
        string json = "{ \"scripts\": { \"ok\": \"echo\", \"num\": 5, \"obj\": {}, \"\": \"x\", \"long\": \"" + new string('a', 500) + "\" } }";
        var scripts = ProjectActions.ReadScripts(Folder(("package.json", json)));
        Assert.Equal(new[] { "ok", "long" }, scripts.Select(s => s.Name).ToArray());
        Assert.True(scripts[1].Command.Length < 320);
    }

    [Theory]
    [InlineData("bun.lockb", "bun")]
    [InlineData("bun.lock", "bun")]
    [InlineData("pnpm-lock.yaml", "pnpm")]
    [InlineData("yarn.lock", "yarn")]
    [InlineData("package-lock.json", "npm")]
    [InlineData("nothing.txt", "npm")]
    public void The_package_manager_comes_from_the_lock_file(string lockFile, string expected)
    {
        Assert.Equal(expected, ProjectActions.DetectPackageManager(Folder((lockFile, ""))));
    }

    [Fact]
    public void Only_a_real_script_can_be_run_and_its_name_is_quoted()
    {
        string dir = Folder(("package.json", "{ \"scripts\": { \"dev\": \"vite\", \"it's\": \"x\", \"a;b\": \"y\" } }"), ("bun.lock", ""));
        Assert.Equal("bun run 'dev'", ProjectActions.RunCommand(dir, "dev"));
        Assert.Equal("bun run 'it''s'", ProjectActions.RunCommand(dir, "it's"));
        Assert.Equal("bun run 'a;b'", ProjectActions.RunCommand(dir, "a;b"));
        Assert.Null(ProjectActions.RunCommand(dir, "dev; calc"));
        Assert.Null(ProjectActions.RunCommand(dir, "DEV"));
        Assert.Null(ProjectActions.RunCommand(dir, ""));
    }

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("a\u2019b", "'a\u2019\u2019b'")]
    [InlineData("\u2018x\u2019", "'\u2018\u2018x\u2019\u2019'")]
    public void PowerShell_quoting_doubles_every_kind_of_single_quote(string input, string expected)
    {
        Assert.Equal(expected, ProjectActions.PsQuote(input));
    }

    [Fact]
    public void A_git_repository_is_found_in_the_folder_or_above_it_and_the_branch_is_read()
    {
        string root = TestEnvironment.NewTempDir("repo");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/feature/login\n");
        string sub = Path.Combine(root, "src", "deep");
        Directory.CreateDirectory(sub);

        var info = ProjectActions.Detect(sub);
        Assert.True(info.IsGitRepo);
        Assert.Equal("feature/login", info.Branch);
        Assert.False(ProjectActions.Detect(TestEnvironment.NewTempDir("norepo")).IsGitRepo);
    }

    [Fact]
    public void A_detached_head_has_no_branch_and_a_worktree_file_is_followed()
    {
        string root = TestEnvironment.NewTempDir("repo");
        string real = Path.Combine(root, "real-git");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "HEAD"), "3f2a9c1d5e7b\n");
        Assert.Null(ProjectActions.ReadBranch(real));

        File.WriteAllText(Path.Combine(real, "HEAD"), "ref: refs/heads/main\n");
        string work = Path.Combine(root, "work");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, ".git"), "gitdir: ../real-git\n");
        Assert.True(ProjectActions.Detect(work).IsGitRepo);
        Assert.Equal("main", ProjectActions.Detect(work).Branch);
    }

    [Fact]
    public void A_missing_folder_gives_nothing_to_offer()
    {
        var info = ProjectActions.Detect(Path.Combine(TestEnvironment.Root, "nope-" + Guid.NewGuid().ToString("N")));
        Assert.Empty(info.Scripts);
        Assert.False(info.IsGitRepo);
        Assert.Empty(ProjectActions.Detect("").Scripts);
        Assert.Empty(ProjectActions.Detect(null).Scripts);
    }
}

public class GitActionTests
{
    private static string Tmp => Path.GetTempPath();

    [Theory]
    [InlineData("status", "git status -sb")]
    [InlineData("add", "git add .")]
    [InlineData("push", "git push")]
    [InlineData("pull", "git pull")]
    [InlineData("fetch", "git fetch --all --prune")]
    [InlineData("log", "git log --oneline --graph --decorate -n 15")]
    [InlineData("diff", "git diff --stat")]
    [InlineData("branches", "git branch -vv")]
    [InlineData("stash", "git stash")]
    [InlineData("stash-pop", "git stash pop")]
    public void Each_button_builds_a_fixed_command(string action, string expected)
    {
        var b = GitActions.Build(action, null, null, false, Tmp);
        Assert.NotNull(b);
        Assert.Equal(expected, b!.Command);
        Assert.Null(b.TempFile);
    }

    [Theory]
    [InlineData("")]
    [InlineData("rm -rf /")]
    [InlineData("push --force")]
    [InlineData("reset --hard")]
    [InlineData("status; calc")]
    public void Anything_that_is_not_on_the_list_is_refused(string action)
    {
        Assert.Null(GitActions.Build(action, "m", "n", true, Tmp));
    }

    [Theory]
    [InlineData("feature/login")]
    [InlineData("fix-123")]
    [InlineData("release_1.2")]
    public void A_sensible_branch_name_is_accepted_and_quoted(string name)
    {
        Assert.Equal($"git switch -c '{name}'", GitActions.Build("switch-new", null, name, false, Tmp)!.Command);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-f")]
    [InlineData("--detach")]
    [InlineData("a b")]
    [InlineData("a;calc")]
    [InlineData("a'b")]
    [InlineData("a$(calc)")]
    [InlineData("a`b")]
    public void A_branch_name_that_could_be_read_as_anything_else_is_refused(string name)
    {
        Assert.Null(GitActions.Build("switch-new", null, name, false, Tmp));
    }

    [Fact]
    public void A_commit_needs_a_message_and_the_message_never_appears_on_the_command_line()
    {
        Assert.Null(GitActions.Build("commit", "", null, false, Tmp));
        Assert.Null(GitActions.Build("commit", "   \n ", null, false, Tmp));
        Assert.Null(GitActions.Build("commit", new string('x', GitActions.MaxMessage + 1), null, false, Tmp));

        string nasty = "fix: it's \"broken\" $(calc) `x`; & | > <";
        var b = GitActions.Build("commit", nasty, null, false, Tmp)!;
        Assert.DoesNotContain("calc", b.Command);
        Assert.DoesNotContain("broken", b.Command);
        Assert.StartsWith("git commit -F '", b.Command);
        Assert.NotNull(b.TempFile);
        Assert.StartsWith(Tmp, b.TempFile!);
        Assert.Contains("fix:", b.Typed);
    }

    [Fact]
    public void The_message_is_written_to_the_file_as_plain_utf8_without_a_byte_order_mark()
    {
        var b = GitActions.Build("commit", "caf\u00e9 \u2013 d\u00e9j\u00e0 vu\r\n\r\nsecond paragraph", null, false, Tmp)!;
        try
        {
            GitActions.WriteMessage(b, "caf\u00e9 \u2013 d\u00e9j\u00e0 vu\r\n\r\nsecond paragraph");
            byte[] bytes = File.ReadAllBytes(b.TempFile!);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.Equal("caf\u00e9 \u2013 d\u00e9j\u00e0 vu\n\nsecond paragraph\n", System.Text.Encoding.UTF8.GetString(bytes));
        }
        finally { try { File.Delete(b.TempFile!); } catch { } }
    }

    [Fact]
    public void Staging_first_adds_everything_and_stops_if_that_fails()
    {
        var plain = GitActions.Build("commit", "msg", null, false, Tmp)!;
        var staged = GitActions.Build("commit", "msg", null, true, Tmp)!;
        Assert.DoesNotContain("git add", plain.Command);
        Assert.StartsWith("git add .;", staged.Command);
        Assert.Contains("exit $LASTEXITCODE", staged.Command);
        Assert.Contains("git commit -F", staged.Command);
        Assert.StartsWith("/git add . && commit", staged.Typed);
    }

    [Fact]
    public void Git_status_output_is_summarised()
    {
        var s = GitStatusParser.Parse("## main...origin/main [ahead 2, behind 1]\n M a.txt\n?? b.txt\nA  c.txt\n");
        Assert.Equal("main", s.Branch);
        Assert.Equal("origin/main", s.Upstream);
        Assert.Equal(2, s.Ahead);
        Assert.Equal(1, s.Behind);
        Assert.Equal(3, s.Changes);

        var clean = GitStatusParser.Parse("## dev\n");
        Assert.Equal("dev", clean.Branch);
        Assert.Null(clean.Upstream);
        Assert.Equal(0, clean.Changes);

        Assert.Equal(3, GitStatusParser.Parse("## main...origin/main [behind 3]\n").Behind);
        Assert.Equal("main", GitStatusParser.Parse("## No commits yet on main\n").Branch);
        Assert.Equal(0, GitStatusParser.Parse("").Changes);
    }
}

/// <summary>Real pseudo-console runs: a program that asks questions gets a terminal and the answers typed from the app.</summary>
public class InteractiveJobTests
{
    private static async Task<bool> Until(Func<bool> condition, int ms = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (condition()) return true; await Task.Delay(50); }
        return condition();
    }

    [Fact]
    public void Arguments_are_quoted_so_the_command_survives_the_command_line()
    {
        Assert.Equal("\"plain\"", JobManager.QuoteArg("plain"));
        Assert.Equal("\"say \\\"hi\\\"\"", JobManager.QuoteArg("say \"hi\""));
        Assert.Equal("\"C:\\dir\\\\\"", JobManager.QuoteArg("C:\\dir\\"));
        Assert.Equal("\"a\\\\\\\"b\"", JobManager.QuoteArg("a\\\"b"));
    }

    [Fact]
    public async Task A_program_that_asks_a_question_gets_the_typed_answer()
    {
        using var jobs = new JobManager();
        string dir = TestEnvironment.NewTempDir("pty");
        var job = jobs.StartInteractive("$n = Read-Host 'Your name'; Write-Output \"Hello $n!\"", dir);

        Assert.True(await Until(() => job.Tail(4000).Contains("Your name")), "the prompt should appear: " + job.Tail(2000));
        Assert.Equal(JobState.Running, job.State);

        Assert.True(jobs.SendText(job.Id, "Ana", enter: true));
        Assert.True(await job.WaitAsync(TimeSpan.FromSeconds(20)), "the command should finish after the answer");
        Assert.Contains("Hello Ana!", job.Tail(4000));
        Assert.Equal(0, job.ExitCode);
        Assert.Equal(JobState.Done, job.State);
    }

    [Fact]
    public async Task A_program_that_needs_a_real_terminal_works_and_arrow_keys_move_a_menu_choice()
    {
        using var jobs = new JobManager();
        string dir = TestEnvironment.NewTempDir("pty");
        // [Console]::ReadKey only works with a real console, which is the point of the pseudo-console.
        string script = "$k = [Console]::ReadKey($true); Write-Output (\"KEY=\" + $k.Key)";
        var job = jobs.StartInteractive(script, dir);
        await Task.Delay(1500);
        Assert.True(jobs.SendKey(job.Id, "down"));
        Assert.True(await job.WaitAsync(TimeSpan.FromSeconds(20)), job.Tail(2000));
        Assert.Contains("KEY=DownArrow", job.Tail(4000));
    }

    [Fact]
    public async Task Stop_ends_an_interactive_command_that_is_waiting()
    {
        using var jobs = new JobManager();
        var job = jobs.StartInteractive("Read-Host 'wait'", TestEnvironment.NewTempDir("pty"));
        Assert.True(await Until(() => job.Tail(2000).Contains("wait")));
        Assert.True(jobs.Stop(job.Id));
        Assert.True(await job.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(JobState.Stopped, job.State);
        Assert.False(jobs.SendText(job.Id, "late", true), "nothing can be typed into a finished job");
    }

    [Fact]
    public async Task A_failing_command_reports_its_exit_code()
    {
        using var jobs = new JobManager();
        var job = jobs.StartInteractive("Write-Output 'before'; exit 7", TestEnvironment.NewTempDir("pty"));
        Assert.True(await job.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(7, job.ExitCode);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("before", job.Tail(2000));
    }

    [Fact]
    public void Only_known_keys_can_be_pressed_and_text_goes_only_to_interactive_running_jobs()
    {
        Assert.Null(PtyProcess.KeyCode("format-c"));
        Assert.Equal("\x1b[A", PtyProcess.KeyCode("UP"));
        using var jobs = new JobManager();
        Assert.False(jobs.SendText(12345, "x", true));
        Assert.False(jobs.SendKey(12345, "enter"));
    }
}

/// <summary>The commands the git dialog builds, run for real against a throw-away repository.</summary>
public class GitDialogEndToEndTests
{
    private static string Git(string dir, string args)
    {
        var psi = new ProcessStartInfo("git", args) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit(20000);
        return o;
    }

    private static bool HasGit()
    {
        try { using var p = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!; p.WaitForExit(10000); return p.ExitCode == 0; }
        catch { return false; }
    }

    private static string NewRepo()
    {
        string dir = TestEnvironment.NewTempDir("gitrepo");
        Git(dir, "init -q");
        Git(dir, "config user.name Tester");
        Git(dir, "config user.email tester@example.com");
        Git(dir, "config commit.gpgsign false");
        return dir;
    }

    [Fact]
    public async Task Stage_and_commit_records_the_message_exactly_even_with_quotes_dollar_signs_and_accents()
    {
        if (!HasGit()) return;
        string dir = NewRepo();
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
        string message = "fix: it's \"broken\" $(calc) `x`; café – 100%\n\nSecond paragraph & more | pipes > redirects";

        var built = GitActions.Build("commit", message, null, stageAll: true, tempDir: Path.GetTempPath())!;
        GitActions.WriteMessage(built, message);
        using var jobs = new JobManager();
        var job = jobs.Start(built.Command, dir);
        Assert.True(await job.WaitAsync(TimeSpan.FromSeconds(60)), job.Tail(2000));
        Assert.Equal(0, job.ExitCode);

        Assert.Equal(message, Git(dir, "log -1 --format=%B").Trim().Replace("\r\n", "\n"));
        Assert.Equal("a.txt", Git(dir, "show --name-only --format= HEAD").Trim());
        Assert.False(File.Exists(built.TempFile!), "the temporary message file is removed");
    }

    [Fact]
    public async Task A_commit_with_nothing_to_commit_fails_cleanly_and_still_removes_the_message_file()
    {
        if (!HasGit()) return;
        string dir = NewRepo();
        var built = GitActions.Build("commit", "nothing here", null, stageAll: false, tempDir: Path.GetTempPath())!;
        GitActions.WriteMessage(built, "nothing here");
        using var jobs = new JobManager();
        var job = jobs.Start(built.Command, dir);
        Assert.True(await job.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.NotEqual(0, job.ExitCode);
        Assert.False(File.Exists(built.TempFile!));
    }

    [Fact]
    public async Task New_branch_creates_and_switches_to_the_branch()
    {
        if (!HasGit()) return;
        string dir = NewRepo();
        File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
        Git(dir, "add .");
        Git(dir, "commit -q -m first");
        var built = GitActions.Build("switch-new", null, "feature/login", false, Path.GetTempPath())!;
        using var jobs = new JobManager();
        var job = jobs.Start(built.Command, dir);
        Assert.True(await job.WaitAsync(TimeSpan.FromSeconds(60)), job.Tail(2000));
        Assert.Equal(0, job.ExitCode);
        Assert.Equal("feature/login", ProjectActions.Detect(dir).Branch);
    }
}

public class FolderFactsTests
{
    [Fact]
    public void An_empty_folder_has_no_files_and_is_not_a_project()
    {
        var info = ProjectActions.Detect(TestEnvironment.NewTempDir("facts"));
        Assert.False(info.HasFiles);
        Assert.False(info.IsProject);
        Assert.False(info.HasReadme);
    }

    [Fact]
    public void A_folder_with_only_a_dot_git_is_a_repository_but_still_has_no_files()
    {
        string dir = TestEnvironment.NewTempDir("facts");
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        var info = ProjectActions.Detect(dir);
        Assert.False(info.HasFiles);
        Assert.True(info.IsGitRepo);
        Assert.True(info.IsProject);
    }

    [Theory]
    [InlineData("package.json")]
    [InlineData("pyproject.toml")]
    [InlineData("go.mod")]
    [InlineData("Cargo.toml")]
    [InlineData("App.sln")]
    [InlineData("App.csproj")]
    [InlineData("MAKEFILE")]
    public void A_project_marker_file_makes_the_folder_a_project(string marker)
    {
        string dir = TestEnvironment.NewTempDir("facts");
        File.WriteAllText(Path.Combine(dir, marker), "x");
        var info = ProjectActions.Detect(dir);
        Assert.True(info.HasFiles);
        Assert.True(info.IsProject);
    }

    [Fact]
    public void Plain_files_are_not_a_project_but_a_readme_is_noticed()
    {
        string dir = TestEnvironment.NewTempDir("facts");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");
        var plain = ProjectActions.Detect(dir);
        Assert.True(plain.HasFiles);
        Assert.False(plain.IsProject);
        Assert.False(plain.HasReadme);

        File.WriteAllText(Path.Combine(dir, "README.md"), "# hi");
        Assert.True(ProjectActions.Detect(dir).HasReadme);
    }
}
