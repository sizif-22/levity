using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Levity.Services
{
    public sealed record ProjectScript(string Name, string Command);

    /// <summary>What can be offered as one-tap buttons for a folder: the scripts in its package.json and whether it is a git repository.</summary>
    public sealed record ProjectActionsInfo(string Path, string? PackageManager, IReadOnlyList<ProjectScript> Scripts, bool IsGitRepo, string? Branch);

    public static class ProjectActions
    {
        private const int MaxScripts = 60;

        public static ProjectActionsInfo Detect(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return new ProjectActionsInfo(path ?? "", null, Array.Empty<ProjectScript>(), false, null);

            var scripts = ReadScripts(path);
            string? pm = scripts.Count > 0 ? DetectPackageManager(path) : null;
            string? gitDir = FindGitDir(path);
            return new ProjectActionsInfo(path, pm, scripts, gitDir != null, gitDir == null ? null : ReadBranch(gitDir));
        }

        /// <summary>The "scripts" of the folder's own package.json (not parent folders'), in file order. Anything that is not a plain string is skipped.</summary>
        public static IReadOnlyList<ProjectScript> ReadScripts(string folder)
        {
            string file = System.IO.Path.Combine(folder, "package.json");
            if (!File.Exists(file)) return Array.Empty<ProjectScript>();
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 2_000_000) return Array.Empty<ProjectScript>();
                using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("scripts", out var s) || s.ValueKind != JsonValueKind.Object)
                    return Array.Empty<ProjectScript>();
                var list = new List<ProjectScript>();
                foreach (var p in s.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.String) continue;
                    string name = p.Name;
                    if (name.Length == 0 || name.Length > 100 || name.Any(char.IsControl)) continue;
                    string cmd = p.Value.GetString() ?? "";
                    list.Add(new ProjectScript(name, cmd.Length > 300 ? cmd.Substring(0, 300) + "…" : cmd));
                    if (list.Count >= MaxScripts) break;
                }
                return list;
            }
            catch { return Array.Empty<ProjectScript>(); }
        }

        /// <summary>Picks the tool the project uses from its lock file; npm when there is none.</summary>
        public static string DetectPackageManager(string folder)
        {
            bool has(string f) => File.Exists(System.IO.Path.Combine(folder, f));
            if (has("bun.lockb") || has("bun.lock")) return "bun";
            if (has("pnpm-lock.yaml")) return "pnpm";
            if (has("yarn.lock")) return "yarn";
            return "npm";
        }

        /// <summary>The command that runs a script. The name must be one of the project's own scripts; it is quoted so it can never be read as anything else.</summary>
        public static string? RunCommand(string folder, string scriptName)
        {
            var scripts = ReadScripts(folder);
            var match = scripts.FirstOrDefault(s => string.Equals(s.Name, scriptName, StringComparison.Ordinal));
            if (match == null) return null;
            return DetectPackageManager(folder) + " run " + PsQuote(match.Name);
        }

        /// <summary>A PowerShell single-quoted string. Every kind of single quote (straight and curly) is doubled, which is how PowerShell reads them.</summary>
        public static string PsQuote(string value)
        {
            var sb = new StringBuilder("'");
            foreach (char c in value)
            {
                sb.Append(c);
                if (c == '\'' || c == '‘' || c == '’' || c == '‚' || c == '‛') sb.Append(c);
            }
            return sb.Append('\'').ToString();
        }

        // ---- git ----

        /// <summary>The ".git" of this folder or the nearest parent folder (a file for worktrees and submodules), or null.</summary>
        public static string? FindGitDir(string folder)
        {
            try
            {
                var dir = new DirectoryInfo(folder);
                for (int i = 0; dir != null && i < 12; i++, dir = dir.Parent)
                {
                    string g = System.IO.Path.Combine(dir.FullName, ".git");
                    if (Directory.Exists(g)) return g;
                    if (File.Exists(g)) return g;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Current branch name read straight from HEAD (no process started), or null if detached or unreadable.</summary>
        public static string? ReadBranch(string gitDirOrFile)
        {
            try
            {
                string dir = gitDirOrFile;
                if (File.Exists(gitDirOrFile))
                {
                    string first = File.ReadLines(gitDirOrFile).FirstOrDefault() ?? "";
                    if (!first.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) return null;
                    dir = first.Substring(7).Trim();
                    if (!System.IO.Path.IsPathRooted(dir)) dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(gitDirOrFile)!, dir));
                }
                string head = File.ReadAllText(System.IO.Path.Combine(dir, "HEAD")).Trim();
                const string prefix = "ref: refs/heads/";
                return head.StartsWith(prefix, StringComparison.Ordinal) ? head.Substring(prefix.Length) : null;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// The git buttons. Each one is built here from a fixed list, so what runs is never text from the UI. The only free text is a commit
    /// message (written to a file, never put on the command line) and a branch name (checked against a strict pattern).
    /// </summary>
    public static class GitActions
    {
        private static readonly Regex BranchName = new(@"^(?!-)[A-Za-z0-9._/-]{1,100}$", RegexOptions.Compiled);
        public const int MaxMessage = 2000;

        public sealed record Built(string Typed, string Command, string? TempFile);

        public static readonly string[] Simple = { "status", "add", "push", "pull", "fetch", "log", "diff", "branches", "stash", "stash-pop" };

        public static Built? Build(string action, string? message, string? name, bool stageAll, string tempDir)
        {
            switch ((action ?? "").ToLowerInvariant())
            {
                case "status": return new("/git status", "git status -sb", null);
                case "add": return new("/git add .", "git add .", null);
                case "push": return new("/git push", "git push", null);
                case "pull": return new("/git pull", "git pull", null);
                case "fetch": return new("/git fetch", "git fetch --all --prune", null);
                case "log": return new("/git log", "git log --oneline --graph --decorate -n 15", null);
                case "diff": return new("/git diff", "git diff --stat", null);
                case "branches": return new("/git branches", "git branch -vv", null);
                case "stash": return new("/git stash", "git stash", null);
                case "stash-pop": return new("/git stash pop", "git stash pop", null);
                case "switch-new":
                    if (name == null || !BranchName.IsMatch(name.Trim())) return null;
                    return new($"/git switch -c {name.Trim()}", "git switch -c " + ProjectActions.PsQuote(name.Trim()), null);
                case "commit":
                {
                    string msg = (message ?? "").Replace("\r\n", "\n").Trim();
                    if (msg.Length == 0 || msg.Length > MaxMessage) return null;
                    // The message travels in a file: no quoting, no length or character limits on the command line.
                    string file = Path.Combine(tempDir, "levity-commit-" + Guid.NewGuid().ToString("N") + ".txt");
                    string q = ProjectActions.PsQuote(file);
                    string commit = $"git commit -F {q}; $c = $LASTEXITCODE; Remove-Item -LiteralPath {q} -Force -ErrorAction SilentlyContinue; exit $c";
                    string cmd = stageAll ? $"git add .; if ($LASTEXITCODE -ne 0) {{ Remove-Item -LiteralPath {q} -Force -ErrorAction SilentlyContinue; exit $LASTEXITCODE }}; {commit}" : commit;
                    string firstLine = msg.Split('\n')[0];
                    return new($"/git {(stageAll ? "add . && " : "")}commit \"{(firstLine.Length > 80 ? firstLine.Substring(0, 77) + "..." : firstLine)}\"", cmd, file);
                }
                default: return null;
            }
        }

        /// <summary>The commit message file Build asked for. Written without a byte-order mark so git reads it as plain UTF-8.</summary>
        public static void WriteMessage(Built built, string message)
        {
            if (built.TempFile == null) return;
            File.WriteAllText(built.TempFile, message.Replace("\r\n", "\n").Trim() + "\n", new UTF8Encoding(false));
        }
    }

    public sealed record GitStatus(string? Branch, string? Upstream, int Ahead, int Behind, int Changes);

    public static class GitStatusParser
    {
        private static readonly Regex Counts = new(@"\[(?:ahead (?<a>\d+))?(?:, )?(?:behind (?<b>\d+))?\]\s*$", RegexOptions.Compiled);

        /// <summary>Reads the output of "git status --porcelain=v1 -b".</summary>
        public static GitStatus Parse(string output)
        {
            string? branch = null, upstream = null;
            int ahead = 0, behind = 0, changes = 0;
            foreach (string raw in (output ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    string head = line.Substring(3);
                    var m = Counts.Match(head);
                    if (m.Success)
                    {
                        if (m.Groups["a"].Success) ahead = int.Parse(m.Groups["a"].Value);
                        if (m.Groups["b"].Success) behind = int.Parse(m.Groups["b"].Value);
                        head = head.Substring(0, m.Index).TrimEnd();
                    }
                    int dots = head.IndexOf("...", StringComparison.Ordinal);
                    if (dots >= 0) { branch = head.Substring(0, dots); upstream = head.Substring(dots + 3); }
                    else branch = head.StartsWith("No commits yet on ", StringComparison.Ordinal) ? head.Substring(18) : head;
                    continue;
                }
                changes++;
            }
            return new GitStatus(branch, upstream, ahead, behind, changes);
        }
    }

    /// <summary>Commands that nearly always stop to ask something, so they are run in a pseudo-console without the user having to say so.</summary>
    public static class InteractiveCommands
    {
        private static readonly Regex Known = new(
            @"(?ix)
              (^|[;&|]\s*)(?:
                  (?:bun|npm|pnpm|yarn)\s+(?:x\s+)?(?:create|init)\b
                | (?:npx|bunx|pnpx)\s+(?:-y\s+|--yes\s+)?(?:create-|degit|shadcn|prisma\s+(?:init|migrate\s+dev)|convex\s+(?:dev|deploy|init|login|logout)|vercel|wrangler\s+login|firebase\s+(?:login|init)|sv\s+create)
                | (?:bun|npm|pnpm|yarn)\s+(?:x\s+|exec\s+|dlx\s+|run\s+)?convex\s+(?:dev|deploy|init|login|logout)\b
                | convex\s+(?:dev|deploy|init|login|logout)\b
                | (?:gh\s+auth\s+login|gh\s+repo\s+create|vercel(?:\s+(?:login|link|deploy|--prod))?|wrangler\s+login|firebase\s+(?:login|init)|npm\s+(?:login|adduser|publish)|az\s+login|aws\s+configure|ssh\s|ftp\s|python\s*$|node\s*$)
              )", RegexOptions.Compiled);

        public static bool LooksInteractive(string command) => Known.IsMatch(command ?? "");
    }
}
