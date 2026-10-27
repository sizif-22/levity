using Levity.Services;
using Xunit;

namespace Levity.Tests;

public class SessionStoreTests
{
    private static ChatSession Make(string id, string title, DateTime updated, int lines = 2) => new()
    {
        Id = id,
        Title = title,
        Cwd = @"C:\proj",
        ConversationId = "conv-" + id,
        Created = updated.AddMinutes(-5),
        Updated = updated,
        Messages = 1,
        History = Enumerable.Range(0, lines).Select(i => $"{{\"type\":\"user_message\",\"text\":\"line {i}\"}}").ToList(),
    };

    [Fact]
    public void Save_then_load_returns_the_full_history()
    {
        var store = new SessionStore();
        string id = Guid.NewGuid().ToString("N");
        store.Save(Make(id, "hello", DateTime.UtcNow, lines: 5));

        var loaded = store.Load(id);
        Assert.NotNull(loaded);
        Assert.Equal("hello", loaded!.Title);
        Assert.Equal("conv-" + id, loaded.ConversationId);
        Assert.Equal(5, loaded.History.Count);
    }

    [Fact]
    public void List_is_newest_first_and_carries_metadata_without_the_history()
    {
        var store = new SessionStore();
        string older = Guid.NewGuid().ToString("N"), newer = Guid.NewGuid().ToString("N");
        store.Save(Make(older, "older", DateTime.UtcNow.AddHours(-2)));
        store.Save(Make(newer, "newer", DateTime.UtcNow.AddHours(-1)));

        var ids = store.List().Select(s => s.Id).ToList();
        Assert.True(ids.IndexOf(newer) < ids.IndexOf(older));
        Assert.All(store.List(), s => Assert.Empty(s.History));
    }

    [Fact]
    public void Saving_the_same_id_again_replaces_it()
    {
        var store = new SessionStore();
        string id = Guid.NewGuid().ToString("N");
        store.Save(Make(id, "first", DateTime.UtcNow, lines: 1));
        store.Save(Make(id, "second", DateTime.UtcNow, lines: 3));

        Assert.Single(store.List(), s => s.Id == id);
        Assert.Equal("second", store.Load(id)!.Title);
        Assert.Equal(3, store.Load(id)!.History.Count);
    }

    [Fact]
    public void Delete_removes_the_file_and_the_listing()
    {
        var store = new SessionStore();
        string id = Guid.NewGuid().ToString("N");
        store.Save(Make(id, "doomed", DateTime.UtcNow));
        Assert.True(store.Delete(id));
        Assert.Null(store.Load(id));
        Assert.DoesNotContain(store.List(), s => s.Id == id);
        Assert.False(store.Delete(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..\\..\\evil")]
    [InlineData("a/b")]
    [InlineData("con:")]
    public void Ids_that_are_not_plain_file_names_are_refused(string id)
    {
        var store = new SessionStore();
        Assert.Null(store.Load(id));
        Assert.False(store.Delete(id));
    }

    [Fact]
    public void A_new_store_reloads_what_an_earlier_one_saved()
    {
        string id = Guid.NewGuid().ToString("N");
        new SessionStore().Save(Make(id, "persisted", DateTime.UtcNow));
        Assert.Contains(new SessionStore().List(), s => s.Id == id && s.Title == "persisted");
    }

    [Fact]
    public void A_corrupt_file_is_skipped_instead_of_breaking_the_list()
    {
        string dir = Path.Combine(AppSettings.DataDir, "sessions");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "broken-" + Guid.NewGuid().ToString("N") + ".json"), "{ not json");
        var store = new SessionStore();
        Assert.NotNull(store.List());
    }
}

public class ProjectScannerTests
{
    [Fact]
    public void Finds_projects_by_marker_files_and_git_folders_and_names_their_kind()
    {
        string root = TestEnvironment.NewTempDir("scan");
        Directory.CreateDirectory(Path.Combine(root, "web-app"));
        File.WriteAllText(Path.Combine(root, "web-app", "package.json"), "{}");
        Directory.CreateDirectory(Path.Combine(root, "plain-git", ".git"));
        Directory.CreateDirectory(Path.Combine(root, "notes"));            // not a project
        Directory.CreateDirectory(Path.Combine(root, "node_modules"));     // skipped by name
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(root, "node_modules", "pkg", "package.json"), "{}");

        var found = ProjectScanner.Scan(root);

        Assert.Equal(new[] { "plain-git", "web-app" }, found.Select(p => p.Name).ToArray());
        Assert.Equal("Git", found.Single(p => p.Name == "plain-git").Kind);
        Assert.NotEqual("Git", found.Single(p => p.Name == "web-app").Kind);
    }

    [Fact]
    public void Looks_one_level_down_inside_collection_folders()
    {
        string root = TestEnvironment.NewTempDir("scan");
        string child = Path.Combine(root, "group", "inner-project");
        Directory.CreateDirectory(Path.Combine(child, ".git"));

        Assert.Contains(ProjectScanner.Scan(root), p => p.Name == "inner-project");
    }

    [Fact]
    public void Reports_which_docs_and_skills_a_project_already_has()
    {
        string root = TestEnvironment.NewTempDir("scan");
        string p = Path.Combine(root, "documented");
        Directory.CreateDirectory(Path.Combine(p, ".git"));
        Directory.CreateDirectory(Path.Combine(p, ".agent", "skills"));
        File.WriteAllText(Path.Combine(p, "README.md"), "# hi");
        File.WriteAllText(Path.Combine(p, "AGENTS.md"), "# agents");

        var info = ProjectScanner.Scan(root).Single();
        Assert.True(info.HasReadme);
        Assert.True(info.HasAgentsDoc);
        Assert.True(info.HasSkills);
    }

    [Fact]
    public void Missing_or_blank_roots_give_an_empty_list()
    {
        Assert.Empty(ProjectScanner.Scan(""));
        Assert.Empty(ProjectScanner.Scan(Path.Combine(TestEnvironment.Root, "nope-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void Describe_handles_any_folder_even_without_project_markers()
    {
        string dir = TestEnvironment.NewTempDir("plain");
        var info = ProjectScanner.Describe(dir);
        Assert.Equal("Folder", info.Kind);
        Assert.Equal(dir, info.Path);
        Assert.False(info.HasReadme);
    }
}

public class ProjectSyncPromptTests
{
    [Fact]
    public void Report_mode_forbids_writing_and_update_mode_limits_it_to_docs()
    {
        string report = ProjectSyncService.BuildPrompt(ProjectSyncService.ReportMode, null);
        string update = ProjectSyncService.BuildPrompt(ProjectSyncService.UpdateMode, null);
        Assert.Contains("REPORT ONLY", report);
        Assert.Contains("Do not create, edit or delete", report);
        Assert.Contains("documentation and skill files only", update);
        Assert.Contains("Never modify source code", update);
    }

    [Fact]
    public void A_custom_prompt_replaces_the_default_body_but_keeps_the_guard()
    {
        string text = ProjectSyncService.BuildPrompt(ProjectSyncService.ReportMode, "Only list TODOs.");
        Assert.Contains("Only list TODOs.", text);
        Assert.Contains("REPORT ONLY", text);
        Assert.DoesNotContain("maintenance pass", text);
    }
}
