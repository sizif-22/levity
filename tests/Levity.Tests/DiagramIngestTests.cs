using System.Diagnostics;
using AntigravitySidebar.Services;
using Xunit;

namespace Levity.Tests;

internal static class Proj
{
    public static string Make(params (string path, string content)[] files)
    {
        string root = TestEnvironment.NewTempDir("proj");
        foreach (var (path, content) in files) Write(root, path, content);
        return root;
    }

    public static void Write(string root, string path, string content)
    {
        string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public static (int code, string output) Git(string root, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in new[] { "-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o);
    }
}

public class DiagramRedactionTests
{
    [Theory]
    [InlineData("-----BEGIN RSA " + "PRIVATE KEY-----\nMIIEowIBAAKCAQEA\nabc\n-----END RSA " + "PRIVATE KEY-----", "MIIEowIBAAKCAQEA")]
    [InlineData("aws = AKIA" + "IOSFODNN7EXAMPLE", "AKIA" + "IOSFODNN7EXAMPLE")]
    [InlineData("token: ghp_" + "abcdefghijklmnopqrstuvwxyz0123456789", "ghp_" + "abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("key = sk-" + "abcdefghijklmnopqrstuvwxyz012345", "sk-" + "abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9" + ".eyJzdWIiOiIxMjM0NTY3ODkwIn0." + "dBjftJeZ4CVPmB92K27uhbUJU1p1r", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("db = postgres://admin:hunter2pass@db.example.com/app", "hunter2pass")]
    [InlineData("password = \"correct-horse-battery\"", "correct-horse-battery")]
    [InlineData("API_KEY=abcd1234efgh5678", "abcd1234efgh5678")]
    [InlineData("client_secret: 'Zm9vYmFyYmF6cXV4'", "Zm9vYmFyYmF6cXV4")]
    [InlineData("Authorization: Bearer abcdefghijklmnop", "abcdefghijklmnop")]
    public void Secret_looking_values_are_replaced(string text, string secret)
    {
        string result = DiagramIngest.Redact(text, out int count);
        Assert.DoesNotContain(secret, result);
        Assert.Contains("[REDACTED", result);
        Assert.True(count >= 1);
    }

    [Theory]
    [InlineData("class JobManager { }")]
    [InlineData("// the password is checked elsewhere")]
    [InlineData("var token = reader.Next();")]
    [InlineData("password = ")]
    [InlineData("secret: short")]
    [InlineData("see https://example.com/docs for details")]
    public void Ordinary_code_and_prose_is_left_alone(string text)
    {
        Assert.Equal(text, DiagramIngest.Redact(text, out int count));
        Assert.Equal(0, count);
    }

    [Fact]
    public void The_key_name_stays_so_the_code_is_still_understandable()
    {
        Assert.Equal("password = \"[REDACTED]\"", DiagramIngest.Redact("password = \"correct-horse-battery\"", out _));
    }

    [Fact]
    public void A_pathological_input_does_not_hang()
    {
        string evil = "password" + string.Concat(Enumerable.Repeat("=\"", 20000)) + new string('a', 20000);
        var sw = Stopwatch.StartNew();
        DiagramIngest.Redact(evil, out _);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "redaction took " + sw.Elapsed);
    }
}

public class DiagramIngestTests
{
    private static string[] Listed(DiagramIngestResult r) => r.TreeText.Split('\n');

    [Theory]
    [InlineData(".env", false)]
    [InlineData(".env.production", false)]
    [InlineData("config/.env.local", false)]
    [InlineData(".env.example", true)]
    [InlineData("server.pem", false)]
    [InlineData("keys/id_rsa", false)]
    [InlineData("deploy.pfx", false)]
    [InlineData("credentials.json", false)]
    [InlineData("secrets.yml", false)]
    [InlineData("service-account-prod.json", false)]
    [InlineData("terraform.tfstate", false)]
    [InlineData("node_modules/x/index.js", false)]
    [InlineData("bin/Debug/App.dll", false)]
    [InlineData("obj/project.assets.json", false)]
    [InlineData("assets/logo.png", false)]
    [InlineData("package.lock", false)]
    [InlineData("src/App.tsx", true)]
    [InlineData("Services/Secrets.cs", true)]
    [InlineData("docs/secrets-handling.md", true)]
    public void Secret_ignored_and_binary_files_never_get_listed(string path, bool listed)
    {
        Assert.Equal(listed, DiagramIngest.IsListable(path));
    }

    [Fact]
    public async Task Builds_a_listing_without_secrets_ignored_folders_or_binaries()
    {
        string root = Proj.Make(
            ("README.md", "# Demo\nA demo project."), ("Program.cs", "class Program { }"), ("src/app.ts", "export const a = 1;"),
            (".env", "SECRET_KEY=topsecretvalue123"), ("key.pem", "-----BEGIN " + "PRIVATE KEY-----\nabc"),
            ("node_modules/pkg/index.js", "x"), ("bin/Debug/x.cs", "class X {}"), ("logo.png", "png"));

        var r = await DiagramIngest.BuildAsync(root);

        Assert.Equal(new[] { "Program.cs", "README.md", "src/app.ts" }, Listed(r));
        Assert.Equal("README.md", r.Context.ReadmePath);
        Assert.Contains("A demo project.", r.ReadmeText);
        Assert.Null(r.Context.Resolve(".env"));
        Assert.Null(r.Context.Resolve("key.pem"));
    }

    [Fact]
    public async Task A_secret_file_content_is_never_part_of_anything_that_would_be_sent()
    {
        string root = Proj.Make(("README.md", "demo"), (".env", "SECRET_KEY=topsecretvalue123"), ("cert.pem", "TOPSECRETPEM"), ("app.cs", "class App { }"));
        var r = await DiagramIngest.BuildAsync(root);
        string prompt = DiagramPrompt.Build(r);
        Assert.DoesNotContain("topsecretvalue123", prompt);
        Assert.DoesNotContain("TOPSECRETPEM", prompt);
        Assert.DoesNotContain(".env", prompt.Replace(".env.example", ""));
    }

    [Fact]
    public async Task Secret_looking_values_inside_source_excerpts_are_redacted_and_counted()
    {
        string root = Proj.Make(("README.md", "demo"), ("Program.cs", "class Program { const string Password = \"correct-horse-battery\"; string key = \"AKIA" + "IOSFODNN7EXAMPLE\"; }"));
        var r = await DiagramIngest.BuildAsync(root);
        string prompt = DiagramPrompt.Build(r);
        Assert.DoesNotContain("correct-horse-battery", prompt);
        Assert.DoesNotContain("AKIA" + "IOSFODNN7EXAMPLE", prompt);
        Assert.True(r.Stats.Redactions >= 2);
    }

    [Fact]
    public async Task Csharp_type_usage_becomes_a_reference()
    {
        string root = Proj.Make(
            ("Program.cs", "class Program { void Main() { var s = new JobService(); } }"),
            ("JobService.cs", "class JobService { Store store; }"),
            ("Store.cs", "class Store { }"));
        var r = await DiagramIngest.BuildAsync(root);
        Assert.Equal(new[] { "JobService.cs" }, r.Context.References["Program.cs"]);
        Assert.Equal(new[] { "Store.cs" }, r.Context.References["JobService.cs"]);
        Assert.Empty(r.Context.References["Store.cs"]);
        Assert.All(new[] { "Program.cs", "JobService.cs", "Store.cs" }, f => Assert.Contains(f, r.Context.ReadPaths));
    }

    [Fact]
    public async Task Names_mentioned_only_in_comments_or_declared_twice_are_not_references()
    {
        string root = Proj.Make(
            ("Program.cs", "// uses Helper somewhere\nclass Program { /* Widget */ void Main() { } }"),
            ("Helper.cs", "class Helper { }"), ("Widget.cs", "class Widget { }"), ("Widget2.cs", "class Widget { }"));
        var r = await DiagramIngest.BuildAsync(root);
        Assert.Empty(r.Context.References["Program.cs"]);
    }

    [Fact]
    public async Task A_type_does_not_reference_its_own_file()
    {
        string root = Proj.Make(("Solo.cs", "class Solo { Solo next; static Solo Make() => new Solo(); }"));
        Assert.Empty((await DiagramIngest.BuildAsync(root)).Context.References["Solo.cs"]);
    }

    [Fact]
    public async Task Typescript_relative_imports_are_resolved_including_index_files()
    {
        string root = Proj.Make(
            ("src/app.ts", "import { b } from './lib/b';\nimport c from \"./lib\";\nimport x from 'react';\nexport * from './lib/b';\nconst d = await import('./lib/d.js');\nconst e = require('../README.md');"),
            ("src/lib/b.ts", "export const b = 1;"), ("src/lib/index.ts", "export {}"), ("src/lib/d.js", "x"), ("README.md", "r"));
        var refs = (await DiagramIngest.BuildAsync(root)).Context.References["src/app.ts"];
        Assert.Equal(new[] { "README.md", "src/lib/b.ts", "src/lib/d.js", "src/lib/index.ts" }, refs.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Python_imports_are_resolved()
    {
        string root = Proj.Make(("main.py", "import util\nfrom pkg.mod import thing\nimport os"), ("util.py", "x=1"), ("pkg/__init__.py", ""), ("pkg/mod.py", "thing=1"));
        var refs = (await DiagramIngest.BuildAsync(root)).Context.References["main.py"];
        Assert.Equal(new[] { "pkg/mod.py", "util.py" }, refs.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Languages_without_an_index_are_listed_but_not_indexed()
    {
        string root = Proj.Make(("main.go", "package main\nimport \"x/y\""), ("y/y.go", "package y"));
        var r = await DiagramIngest.BuildAsync(root);
        Assert.NotNull(r.Context.Resolve("main.go"));
        Assert.Empty(r.Context.References);
        Assert.Contains("No reference index", r.SourceIndex);
    }

    [Fact]
    public async Task Excerpts_prefer_entry_points_and_much_referenced_files_and_skip_tests()
    {
        var files = new List<(string, string)>
        {
            ("README.md", "demo"), ("Program.cs", "class Program { Core c; }"), ("Core.cs", "class Core { }"),
            ("Tests/CoreTests.cs", "class CoreTests { Core c; }"), ("app.test.ts", "x"), ("Gen.g.cs", "class Gen {}"),
        };
        for (int i = 0; i < 40; i++) files.Add(($"Leaf/Leaf{i}.cs", $"class Leaf{i} {{ }}"));
        var r = await DiagramIngest.BuildAsync(Proj.Make(files.ToArray()));

        var picked = r.Sources.Select(s => s.Path).ToList();
        Assert.Contains("Program.cs", picked);
        Assert.Contains("Core.cs", picked);
        Assert.DoesNotContain("Tests/CoreTests.cs", picked);
        Assert.DoesNotContain("Gen.g.cs", picked);
        Assert.True(r.Sources.Count <= DiagramIngest.MaxExcerptFiles + 8);
        Assert.True(r.Stats.ExcerptChars <= DiagramIngest.MaxTotalExcerptChars + DiagramIngest.MaxExcerptChars);
    }

    [Fact]
    public async Task Long_files_are_cut_to_the_excerpt_limit()
    {
        string root = Proj.Make(("README.md", "d"), ("Program.cs", "class Program { }\n" + new string('x', 20000)));
        var r = await DiagramIngest.BuildAsync(root);
        Assert.True(r.Sources.Single(s => s.Path == "Program.cs").Text.Length <= DiagramIngest.MaxExcerptChars);
    }

    [Fact]
    public async Task A_huge_readme_is_cut()
    {
        string root = Proj.Make(("README.md", new string('r', 100_000)));
        Assert.True((await DiagramIngest.BuildAsync(root)).ReadmeText!.Length <= DiagramIngest.MaxReadmeChars);
    }

    [Fact]
    public async Task A_missing_folder_is_reported()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => DiagramIngest.BuildAsync(Path.Combine(TestEnvironment.Root, "nope-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public async Task Cancellation_stops_ingestion()
    {
        string root = Proj.Make(("README.md", "d"), ("a.cs", "class A {}"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiagramIngest.BuildAsync(root, cts.Token));
    }

    // ---- safe reading ----

    [Fact]
    public void ReadText_refuses_traversal_secret_names_binary_files_and_folders()
    {
        string root = Proj.Make(("ok.txt", "hello"), (".env", "X=1"), ("sub/f.txt", "in a folder"));
        File.WriteAllBytes(Path.Combine(root, "bin.dat"), new byte[] { 1, 2, 0, 3 });
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(root)!, "outside.txt"), "outside");

        Assert.Equal("hello", DiagramIngest.ReadText(root, "ok.txt", 1000));
        Assert.Null(DiagramIngest.ReadText(root, "../outside.txt", 1000));
        Assert.Null(DiagramIngest.ReadText(root, "..\\outside.txt", 1000));
        Assert.Null(DiagramIngest.ReadText(root, "sub/../../outside.txt", 1000));
        Assert.Null(DiagramIngest.ReadText(root, Path.Combine(Path.GetDirectoryName(root)!, "outside.txt"), 1000));
        Assert.Null(DiagramIngest.ReadText(root, ".env", 1000));
        Assert.Null(DiagramIngest.ReadText(root, "bin.dat", 1000));
        Assert.Null(DiagramIngest.ReadText(root, "sub", 1000));
        Assert.Null(DiagramIngest.ReadText(root, "missing.txt", 1000));
    }

    [Fact]
    public void ReadText_stops_at_the_byte_limit()
    {
        string root = Proj.Make(("big.txt", new string('a', 10_000)));
        Assert.Equal(100, DiagramIngest.ReadText(root, "big.txt", 100)!.Length);
    }

    [Fact]
    public async Task A_junction_pointing_outside_the_project_is_never_followed()
    {
        string root = Proj.Make(("README.md", "d"), ("a.cs", "class A {}"));
        string outside = TestEnvironment.NewTempDir("outside");
        File.WriteAllText(Path.Combine(outside, "stolen.cs"), "class Stolen { const string K = \"outside-secret\"; }");
        string link = Path.Combine(root, "linked");

        var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        mk.WaitForExit();
        Assert.True(Directory.Exists(link), "the test could not create a junction");

        var r = await DiagramIngest.BuildAsync(root);
        Assert.DoesNotContain("linked/stolen.cs", r.TreeText);
        Assert.DoesNotContain("outside-secret", DiagramPrompt.Build(r));
        Assert.Null(DiagramIngest.ReadText(root, "linked/stolen.cs", 1000));
    }

    // ---- tree text ----

    [Fact]
    public void A_small_project_lists_every_file()
    {
        Assert.Equal("a.cs\nb/c.cs", DiagramIngest.BuildTreeText(new[] { "a.cs", "b/c.cs" }));
    }

    [Fact]
    public void A_huge_project_collapses_deep_folders_with_a_count()
    {
        var files = Enumerable.Range(0, 2000).Select(i => $"src/deep/module/file{i}.cs").Concat(new[] { "README.md", "src/main.cs" }).ToList();
        string tree = DiagramIngest.BuildTreeText(files);
        var lines = tree.Split('\n');
        Assert.True(lines.Length <= DiagramIngest.MaxTreeLines + 1);
        Assert.Contains("src/deep/module/ (2000 files)", lines);
        Assert.Contains("README.md", lines);
    }

    // ---- git ----

    [Fact]
    public async Task In_a_git_repo_gitignored_files_are_left_out_and_head_is_reported()
    {
        string root = Proj.Make(("README.md", "d"), ("a.cs", "class A {}"), (".gitignore", "ignored.cs\nlogs/\n"), ("ignored.cs", "class Ignored {}"), ("logs/x.cs", "class L {}"), (".env", "S=1"));
        Assert.Equal(0, Proj.Git(root, "init", "-q").code);
        Assert.Equal(0, Proj.Git(root, "add", "-A").code);
        Assert.Equal(0, Proj.Git(root, "commit", "-q", "-m", "init").code);
        Proj.Write(root, "untracked.cs", "class Untracked {}");

        var r = await DiagramIngest.BuildAsync(root);
        var listed = Listed(r);
        Assert.Contains("a.cs", listed);
        Assert.Contains("untracked.cs", listed);          // not committed, not ignored: still part of the project
        Assert.DoesNotContain("ignored.cs", listed);
        Assert.DoesNotContain("logs/x.cs", listed);
        Assert.DoesNotContain(".env", listed);

        string? head = await DiagramIngest.GitHeadAsync(root);
        Assert.Matches("^[0-9a-f]{40,64}$", head!);
    }

    [Fact]
    public async Task Outside_git_there_is_no_head()
    {
        Assert.Null(await DiagramIngest.GitHeadAsync(Proj.Make(("a.cs", "class A {}"))));
    }

    [Fact]
    public async Task A_repo_that_tries_to_run_a_command_through_its_own_git_config_gets_nothing_executed()
    {
        // Regression guard. Git 2.44 does not run core.fsmonitor for "ls-files" even without our "-c core.fsmonitor=false",
        // so this passes either way today; the flag protects against git versions and index states where it would.
        string root = Proj.Make(("README.md", "d"), ("a.cs", "class A {}"));
        Assert.Equal(0, Proj.Git(root, "init", "-q").code);
        Assert.Equal(0, Proj.Git(root, "add", "-A").code);
        string marker = Path.Combine(TestEnvironment.NewTempDir("marker"), "executed.txt");
        string hook = Path.Combine(TestEnvironment.NewTempDir("hook"), "fsmonitor.cmd");
        File.WriteAllText(hook, $"@echo off\r\necho ran > \"{marker}\"\r\n");
        Proj.Git(root, "config", "core.fsmonitor", hook.Replace('\\', '/'));

        var r = await DiagramIngest.BuildAsync(root);

        Assert.Contains("a.cs", r.TreeText);
        Assert.False(File.Exists(marker), "a command from the repository's own config was executed");
    }
}
