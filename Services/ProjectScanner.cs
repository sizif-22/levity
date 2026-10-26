using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AntigravitySidebar.Services
{
    public record ProjectInfo(
        string Name,
        string Path,
        string Kind,
        bool HasReadme,
        bool HasAgentsDoc,
        bool HasSkills,
        DateTime LastModifiedUtc);

    /// <summary>Finds project folders under a root by looking for build/VCS markers (max two levels deep).</summary>
    public static class ProjectScanner
    {
        private static readonly string[] MarkerDirs = { ".git" };

        private static readonly (string File, string Kind)[] MarkerFiles =
        {
            ("package.json", "Node"), ("pubspec.yaml", "Flutter"), ("Cargo.toml", "Rust"), ("go.mod", "Go"),
            ("pyproject.toml", "Python"), ("requirements.txt", "Python"), ("pom.xml", "Java"),
            ("build.gradle", "Java"), ("build.gradle.kts", "Java"), ("CMakeLists.txt", "C++"),
            ("composer.json", "PHP"), ("deno.json", "Deno"), ("Makefile", "Make")
        };

        private static readonly string[] MarkerPatterns = { "*.sln", "*.csproj", "*.fsproj" };

        private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".git", "bin", "obj", "dist", "build", "target", "venv", ".venv", "__pycache__",
            "$RECYCLE.BIN", "System Volume Information", ".pnpm-store", "Downloads", "Trash", "Photos"
        };

        public static List<ProjectInfo> Scan(string root)
        {
            var found = new List<ProjectInfo>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return found;

            foreach (string dir in SafeDirs(root))
            {
                if (TryDescribe(dir, out var info)) { found.Add(info); continue; }

                // Not a project itself: treat as a collection folder and look one level down.
                foreach (string child in SafeDirs(dir))
                {
                    if (TryDescribe(child, out var childInfo)) found.Add(childInfo);
                }
            }

            return found.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<string> SafeDirs(string dir)
        {
            try
            {
                return Directory.EnumerateDirectories(dir)
                    .Where(d =>
                    {
                        string name = System.IO.Path.GetFileName(d);
                        if (SkipNames.Contains(name)) return false;
                        try { return !File.GetAttributes(d).HasFlag(FileAttributes.ReparsePoint); }
                        catch { return false; }
                    })
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>Latest sign of work: git index/HEAD/reflog (change on commit, checkout, staging) or the folder itself.</summary>
        private static DateTime LastActivity(string dir)
        {
            DateTime best = Directory.GetLastWriteTimeUtc(dir);
            foreach (string rel in new[] { @".git\index", @".git\HEAD", @".git\logs\HEAD" })
            {
                try
                {
                    var t = File.GetLastWriteTimeUtc(System.IO.Path.Combine(dir, rel));
                    if (t > best) best = t;
                }
                catch { }
            }
            return best;
        }

        private static bool TryDescribe(string dir, out ProjectInfo info)
        {
            info = null!;
            try
            {
                string? kind = null;
                bool hasGit = MarkerDirs.Any(m => Directory.Exists(System.IO.Path.Combine(dir, m)));

                foreach (var (file, k) in MarkerFiles)
                {
                    if (File.Exists(System.IO.Path.Combine(dir, file))) { kind = k; break; }
                }
                if (kind == null)
                {
                    foreach (string pattern in MarkerPatterns)
                    {
                        if (Directory.EnumerateFiles(dir, pattern).Any()) { kind = ".NET"; break; }
                    }
                }
                if (kind == null && !hasGit) return false;
                kind ??= "Git";

                bool Has(string name) => File.Exists(System.IO.Path.Combine(dir, name));
                bool hasReadme = Has("README.md") || Has("readme.md") || Has("README.txt");
                bool hasAgents = Has("AGENTS.md") || Has("GEMINI.md") || Has("CLAUDE.md");
                bool hasSkills = Directory.Exists(System.IO.Path.Combine(dir, ".agent", "skills"))
                              || Directory.Exists(System.IO.Path.Combine(dir, ".agents", "skills"));

                info = new ProjectInfo(
                    System.IO.Path.GetFileName(dir), dir, kind, hasReadme, hasAgents, hasSkills,
                    LastActivity(dir));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
