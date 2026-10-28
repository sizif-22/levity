using System.Diagnostics;
using Levity.Services;
using Xunit;

namespace Levity.Tests;

public class CheckpointsTests
{
    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30000);
        return o;
    }

    private static string NewRepo(bool commit = true)
    {
        string dir = TestEnvironment.NewTempDir("ckpt");
        Git(dir, "init", "-q");
        Git(dir, "config", "user.name", "Tester");
        Git(dir, "config", "user.email", "t@example.com");
        Git(dir, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(dir, ".gitignore"), "ignored/\n*.log\n");
        File.WriteAllText(Path.Combine(dir, "keep.txt"), "keep v1\n");
        File.WriteAllText(Path.Combine(dir, "edit.txt"), "line one\nline two\n");
        File.WriteAllText(Path.Combine(dir, "remove.txt"), "to be deleted\n");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "nested.txt"), "nested\n");
        if (commit) { Git(dir, "add", "-A"); Git(dir, "commit", "-q", "-m", "initial"); }
        return dir;
    }

    private static string Read(string dir, string rel) => File.ReadAllText(Path.Combine(dir, rel));

    [Fact]
    public async Task A_folder_that_is_not_a_repository_has_no_checkpoints()
    {
        Assert.Null(await Checkpoints.OpenAsync(TestEnvironment.NewTempDir("plain")));
        Assert.Null(await Checkpoints.OpenAsync(Path.Combine(TestEnvironment.Root, "nope-" + Guid.NewGuid().ToString("N"))));
        Assert.Null(await Checkpoints.OpenAsync(""));
    }

    [Fact]
    public async Task A_subfolder_finds_the_repository_it_lives_in()
    {
        string dir = NewRepo();
        var c = await Checkpoints.OpenAsync(Path.Combine(dir, "sub"));
        Assert.NotNull(c);
        Assert.Equal(Path.GetFullPath(dir).TrimEnd('\\').ToLowerInvariant(), c!.Root.ToLowerInvariant());
    }

    [Fact]
    public async Task Nothing_changed_means_an_empty_diff()
    {
        string dir = NewRepo();
        var c = (await Checkpoints.OpenAsync(dir))!;
        string id = (await c.CreateAsync())!;
        Assert.NotNull(id);
        var diff = await c.DiffAsync(id);
        Assert.NotNull(diff);
        Assert.Empty(diff!.Changes);
    }

    [Fact]
    public async Task The_diff_lists_added_modified_and_deleted_files_and_counts_lines_and_ignores_ignored_files()
    {
        string dir = NewRepo();
        var c = (await Checkpoints.OpenAsync(dir))!;
        string id = (await c.CreateAsync())!;

        File.WriteAllText(Path.Combine(dir, "edit.txt"), "line one\nline two changed\nline three\n");
        File.WriteAllText(Path.Combine(dir, "brand-new.txt"), "hello\n");
        File.Delete(Path.Combine(dir, "remove.txt"));
        Directory.CreateDirectory(Path.Combine(dir, "ignored"));
        File.WriteAllText(Path.Combine(dir, "ignored", "cache.bin"), "x");
        File.WriteAllText(Path.Combine(dir, "debug.log"), "noise");

        var diff = (await c.DiffAsync(id))!;
        var byPath = diff.Changes.ToDictionary(x => x.Path, x => x.Status);
        Assert.Equal('M', byPath["edit.txt"]);
        Assert.Equal('A', byPath["brand-new.txt"]);
        Assert.Equal('D', byPath["remove.txt"]);
        Assert.Equal(3, diff.Changes.Count);
        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Modified);
        Assert.Equal(1, diff.Deleted);
        Assert.True(diff.Insertions >= 3);
        Assert.True(diff.Deletions >= 2);
    }

    [Fact]
    public async Task Undo_puts_everything_back_and_leaves_ignored_files_and_unchanged_files_alone()
    {
        string dir = NewRepo();
        var c = (await Checkpoints.OpenAsync(dir))!;
        string id = (await c.CreateAsync())!;

        File.WriteAllText(Path.Combine(dir, "edit.txt"), "ruined\n");
        File.WriteAllText(Path.Combine(dir, "new-file.txt"), "created by the turn\n");
        File.Delete(Path.Combine(dir, "remove.txt"));
        Directory.CreateDirectory(Path.Combine(dir, "ignored"));
        File.WriteAllText(Path.Combine(dir, "ignored", "cache.bin"), "keep me");
        File.WriteAllText(Path.Combine(dir, "sub", "nested.txt"), "changed nested\n");

        var (restored, removed, error) = await c.RestoreAsync(id);

        Assert.Null(error);
        Assert.Equal(3, restored);
        Assert.Equal(1, removed);
        Assert.Equal("line one\nline two\n", Read(dir, "edit.txt").Replace("\r\n", "\n"));
        Assert.Equal("to be deleted\n", Read(dir, "remove.txt").Replace("\r\n", "\n"));
        Assert.Equal("nested\n", Read(dir, Path.Combine("sub", "nested.txt")).Replace("\r\n", "\n"));
        Assert.False(File.Exists(Path.Combine(dir, "new-file.txt")));
        Assert.Equal("keep me", Read(dir, Path.Combine("ignored", "cache.bin")));
        Assert.Equal("keep v1\n", Read(dir, "keep.txt").Replace("\r\n", "\n"));
        Assert.Empty((await c.DiffAsync(id))!.Changes);
    }

    [Fact]
    public async Task Work_that_was_already_there_before_the_turn_survives_an_undo_even_if_it_was_never_committed()
    {
        string dir = NewRepo();
        File.WriteAllText(Path.Combine(dir, "my-uncommitted-edit.txt"), "I was working on this\n");
        File.WriteAllText(Path.Combine(dir, "edit.txt"), "my own edit before the turn\n");
        var c = (await Checkpoints.OpenAsync(dir))!;
        string id = (await c.CreateAsync())!;

        File.WriteAllText(Path.Combine(dir, "edit.txt"), "the assistant overwrote it\n");
        File.WriteAllText(Path.Combine(dir, "my-uncommitted-edit.txt"), "the assistant deleted my notes\n");
        await c.RestoreAsync(id);

        Assert.Equal("my own edit before the turn\n", Read(dir, "edit.txt").Replace("\r\n", "\n"));
        Assert.Equal("I was working on this\n", Read(dir, "my-uncommitted-edit.txt").Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Taking_a_checkpoint_never_touches_the_branch_the_index_or_the_stash()
    {
        string dir = NewRepo();
        File.WriteAllText(Path.Combine(dir, "staged.txt"), "staged\n");
        Git(dir, "add", "staged.txt");
        File.WriteAllText(Path.Combine(dir, "unstaged.txt"), "not staged\n");
        string headBefore = Git(dir, "rev-parse", "HEAD").Trim();
        string statusBefore = Git(dir, "status", "--porcelain");
        string branchBefore = Git(dir, "branch", "--show-current").Trim();
        string logBefore = Git(dir, "log", "--oneline", "--branches", "--tags");   // (not --all: tools that watch git may add their own notes refs, unrelated to checkpoints)
        string refsBefore = Git(dir, "for-each-ref", "refs/heads", "refs/tags", "refs/remotes");

        var c = (await Checkpoints.OpenAsync(dir))!;
        await c.CreateAsync();
        await c.DiffAsync((await c.ListAsync())[0]);

        Assert.Equal(headBefore, Git(dir, "rev-parse", "HEAD").Trim());
        Assert.Equal(statusBefore, Git(dir, "status", "--porcelain"));
        Assert.Equal(branchBefore, Git(dir, "branch", "--show-current").Trim());
        Assert.Equal(logBefore, Git(dir, "log", "--oneline", "--branches", "--tags"));   // the checkpoint is invisible to log, graph tools and branch lists
        Assert.Equal(refsBefore, Git(dir, "for-each-ref", "refs/heads", "refs/tags", "refs/remotes"));
        Assert.Empty(Git(dir, "for-each-ref", "refs/levity"));
        Assert.Empty(Git(dir, "stash", "list"));
    }

    [Fact]
    public async Task A_repository_with_no_commits_yet_works_too()
    {
        string dir = NewRepo(commit: false);
        var c = (await Checkpoints.OpenAsync(dir))!;
        string? id = await c.CreateAsync();
        Assert.NotNull(id);
        File.WriteAllText(Path.Combine(dir, "keep.txt"), "changed\n");
        File.WriteAllText(Path.Combine(dir, "late.txt"), "late\n");
        var diff = (await c.DiffAsync(id!))!;
        Assert.Contains(diff.Changes, x => x.Path == "keep.txt" && x.Status == 'M');
        Assert.Contains(diff.Changes, x => x.Path == "late.txt" && x.Status == 'A');
        var (_, removed, error) = await c.RestoreAsync(id!);
        Assert.Null(error);
        Assert.Equal(1, removed);
        Assert.Equal("keep v1\n", Read(dir, "keep.txt").Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Only_the_newest_twenty_checkpoints_are_kept()
    {
        string dir = NewRepo();
        var c = (await Checkpoints.OpenAsync(dir))!;
        var ids = new List<string>();
        for (int i = 0; i < Checkpoints.Keep + 4; i++) ids.Add((await c.CreateAsync())!);
        var kept = await c.ListAsync();
        Assert.Equal(Checkpoints.Keep, kept.Count);
        Assert.Contains(ids[^1], kept);
        Assert.DoesNotContain(ids[0], kept);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..\\..\\evil")]
    [InlineData("a/b")]
    [InlineData("refs/heads/main")]
    [InlineData("nope")]
    [InlineData("--help")]
    public async Task Ids_that_are_not_real_checkpoints_are_refused(string id)
    {
        string dir = NewRepo();
        var c = (await Checkpoints.OpenAsync(dir))!;
        Assert.Null(await c.DiffAsync(id));
        var (restored, removed, error) = await c.RestoreAsync(id);
        Assert.NotNull(error);
        Assert.Equal(0, restored + removed);
    }

    [Fact]
    public async Task Filenames_with_spaces_accents_and_odd_characters_come_back_exactly()
    {
        string dir = NewRepo();
        string odd = "café notes (draft) #1 & more.txt";
        File.WriteAllText(Path.Combine(dir, odd), "original\n");
        Git(dir, "add", "-A");
        Git(dir, "commit", "-q", "-m", "odd name");
        var c = (await Checkpoints.OpenAsync(dir))!;
        string id = (await c.CreateAsync())!;
        File.WriteAllText(Path.Combine(dir, odd), "changed\n");
        Assert.Contains((await c.DiffAsync(id))!.Changes, x => x.Path == odd);
        var (_, _, error) = await c.RestoreAsync(id);
        Assert.Null(error);
        Assert.Equal("original\n", Read(dir, odd).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task A_second_undo_of_the_same_checkpoint_after_more_work_only_reverses_that_work()
    {
        string dir = NewRepo();
        var c = (await Checkpoints.OpenAsync(dir))!;
        string id = (await c.CreateAsync())!;
        File.WriteAllText(Path.Combine(dir, "edit.txt"), "first change\n");
        await c.RestoreAsync(id);
        File.WriteAllText(Path.Combine(dir, "edit.txt"), "second change\n");
        var (restored, _, error) = await c.RestoreAsync(id);
        Assert.Null(error);
        Assert.Equal(1, restored);
        Assert.Equal("line one\nline two\n", Read(dir, "edit.txt").Replace("\r\n", "\n"));
    }
}
