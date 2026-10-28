using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Levity.Services.WindowsTools;

namespace Levity.Services
{
    public sealed record CheckpointChange(char Status, string Path);

    public sealed record CheckpointDiff(string Id, IReadOnlyList<CheckpointChange> Changes, int Insertions, int Deletions)
    {
        public int Added => Changes.Count(c => c.Status == 'A');
        public int Modified => Changes.Count(c => c.Status == 'M' || c.Status == 'T');
        public int Deleted => Changes.Count(c => c.Status == 'D');
    }

    /// <summary>
    /// Undo for an AI turn. Before the assistant starts, the working folder is snapshotted into git's object store (using a temporary index, so
    /// your staged changes, your branch and your files are never touched; no branch or tag is created, so nothing shows up in "git log --all" or
    /// graph tools, and the list of checkpoints is a small file inside .git). Git keeps unreferenced objects for two weeks. After the turn
    /// the snapshot is compared with the folder to show what changed, and "Undo" puts those files back. Untracked files count as part of the
    /// snapshot (ignored files do not). Works in any git repository; a folder that is not one has no checkpoints.
    /// </summary>
    public sealed class Checkpoints
    {
        public const int Keep = 20;
        private static readonly string[] Quiet = { "-c", "core.quotepath=off", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=NUL", "-c", "commit.gpgsign=false", "-c", "user.name=Levity", "-c", "user.email=levity@localhost",
        // some tools watch git through trace2 and write their own notes for every commit they see; these internal snapshots are not commits worth noting
        "-c", "trace2.eventTarget=", "-c", "trace2.normalTarget=", "-c", "trace2.perfTarget=" };

        private readonly string _root;
        private readonly string _metaFile;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public string Root => _root;

        private Checkpoints(string root, string gitDir) { _root = root; _metaFile = Path.Combine(gitDir, "levity", "checkpoints.json"); }

        private async Task<Proc.Output> Git(IEnumerable<string> args, int seconds = 30, Dictionary<string, string>? env = null, CancellationToken ct = default)
        {
            var all = new List<string>(Quiet) { "-C", _root };
            all.AddRange(args);
            return await Proc.RunAsync("git", all, seconds, ct, env);
        }

        /// <summary>The checkpoint engine for the repository that contains <paramref name="folder"/>, or null if it is not inside one (or git is missing).</summary>
        public static async Task<Checkpoints?> OpenAsync(string folder, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
            var r = await Proc.RunAsync("git", new[] { "-C", folder, "rev-parse", "--show-toplevel" }, 10, ct);
            if (r.ExitCode != 0 || r.TimedOut || string.IsNullOrWhiteSpace(r.Text)) return null;
            string top = r.Text.Split('\n')[0].Trim().Replace('/', '\\');
            var g = await Proc.RunAsync("git", new[] { "-C", folder, "rev-parse", "--absolute-git-dir" }, 10, ct);
            if (g.ExitCode != 0 || string.IsNullOrWhiteSpace(g.Text)) return null;
            string gitDir = g.Text.Split('\n')[0].Trim().Replace('/', '\\');
            return Directory.Exists(top) && Directory.Exists(gitDir) ? new Checkpoints(top, gitDir) : null;
        }

        /// <summary>A tree object of everything in the folder that git would track, built in a throw-away index.</summary>
        private async Task<string?> SnapshotTreeAsync(CancellationToken ct)
        {
            string index = Path.Combine(Path.GetTempPath(), "levity-index-" + Guid.NewGuid().ToString("N"));
            var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
            try
            {
                var head = await Git(new[] { "rev-parse", "--verify", "--quiet", "HEAD" }, 10, null, ct);
                if (head.ExitCode == 0 && (await Git(new[] { "read-tree", "HEAD" }, 20, env, ct)).ExitCode != 0) return null;
                if ((await Git(new[] { "add", "-A", "--", "." }, 60, env, ct)).ExitCode != 0) return null;
                var tree = await Git(new[] { "write-tree" }, 20, env, ct);
                return tree.ExitCode == 0 && tree.Text.Trim().Length >= 40 ? tree.Text.Trim().Split('\n')[0] : null;
            }
            finally { try { File.Delete(index); } catch { } }
        }

        /// <summary>Snapshots the folder now. Returns the checkpoint id, or null if it could not be made (the turn should then simply run without one).</summary>
        public async Task<string?> CreateAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                string? tree = await SnapshotTreeAsync(ct);
                if (tree == null) return null;
                var head = await Git(new[] { "rev-parse", "--verify", "--quiet", "HEAD" }, 10, null, ct);
                var args = new List<string> { "commit-tree", tree, "-m", "Levity checkpoint" };
                if (head.ExitCode == 0) { args.Add("-p"); args.Add(head.Text.Trim().Split('\n')[0]); }
                var commit = await Git(args, 20, null, ct);
                if (commit.ExitCode != 0) return null;
                string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
                var list = LoadMeta();
                list.Insert(0, (id, commit.Text.Trim().Split('\n')[0], DateTime.UtcNow));
                SaveMeta(list.Take(Keep).ToList());   // older ones are forgotten; git cleans their objects up in its own time
                return id;
            }
            finally { _gate.Release(); }
        }

        private static bool PlainId(string id) => id.Length is > 0 and <= 40 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

        private async Task<string?> CommitOfAsync(string id, CancellationToken ct)
        {
            if (!PlainId(id)) return null;
            var entry = LoadMeta().FirstOrDefault(e => e.Id == id);
            if (entry.Commit == null || entry.Commit.Length < 40 || !entry.Commit.All(Uri.IsHexDigit)) return null;
            var r = await Git(new[] { "cat-file", "-e", entry.Commit + "^{commit}" }, 10, null, ct);   // gone if git cleaned it up
            return r.ExitCode == 0 ? entry.Commit : null;
        }

        /// <summary>What changed in the folder since the checkpoint (null if the checkpoint is gone).</summary>
        public async Task<CheckpointDiff?> DiffAsync(string id, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                string? commit = await CommitOfAsync(id, ct);
                if (commit == null) return null;
                string? now = await SnapshotTreeAsync(ct);
                if (now == null) return null;
                var status = await Git(new[] { "diff-tree", "-r", "-z", "--no-renames", "--name-status", commit + "^{tree}", now }, 30, null, ct);
                if (status.ExitCode != 0) return null;
                var changes = new List<CheckpointChange>();
                string[] tokens = status.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i + 1 < tokens.Length; i += 2)
                {
                    string s = tokens[i].Trim();
                    string path = tokens[i + 1];
                    if (s.Length == 0 || !SafeRelativePath(path)) continue;
                    changes.Add(new CheckpointChange(s[0], path.Replace('/', '\\')));
                }
                int ins = 0, del = 0;
                var num = await Git(new[] { "diff-tree", "-r", "--no-renames", "--numstat", commit + "^{tree}", now }, 30, null, ct);
                if (num.ExitCode == 0)
                    foreach (string line in num.Text.Split('\n'))
                    {
                        var p = line.Split('\t');
                        if (p.Length >= 3 && int.TryParse(p[0], out int a) && int.TryParse(p[1], out int d)) { ins += a; del += d; }
                    }
                return new CheckpointDiff(id, changes, ins, del);
            }
            finally { _gate.Release(); }
        }

        /// <summary>Puts the folder back the way the checkpoint had it: changed and deleted files are restored, files created since are removed.</summary>
        public async Task<(int Restored, int Removed, string? Error)> RestoreAsync(string id, CancellationToken ct = default)
        {
            var diff = await DiffAsync(id, ct);
            if (diff == null) return (0, 0, "That checkpoint is no longer available.");
            await _gate.WaitAsync(ct);
            try
            {
                string? commit = await CommitOfAsync(id, ct);
                if (commit == null) return (0, 0, "That checkpoint is no longer available.");
                int removed = 0;
                foreach (var c in diff.Changes.Where(c => c.Status == 'A'))
                {
                    string full = Path.GetFullPath(Path.Combine(_root, c.Path));
                    if (!full.StartsWith(_root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) continue;   // never outside the repository
                    try { if (File.Exists(full)) { File.SetAttributes(full, FileAttributes.Normal); File.Delete(full); removed++; } } catch (Exception ex) { return (0, removed, "Could not remove " + c.Path + ": " + ex.Message); }
                }
                var toRestore = diff.Changes.Where(c => c.Status != 'A').Select(c => c.Path.Replace('\\', '/')).ToList();
                int restored = 0;
                for (int i = 0; i < toRestore.Count; i += 50)
                {
                    var batch = toRestore.Skip(i).Take(50).ToList();
                    var args = new List<string> { "restore", "--source=" + commit, "--worktree", "--" };
                    args.AddRange(batch);
                    var r = await Git(args, 60, null, ct);
                    if (r.ExitCode != 0) return (restored, removed, "git could not restore some files: " + Short(r.Text));
                    restored += batch.Count;
                }
                return (restored, removed, null);
            }
            finally { _gate.Release(); }
        }

        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(LoadMeta().Select(e => e.Id).ToList());

        // ---- the list of checkpoints: a small file inside .git ----

        private List<(string Id, string Commit, DateTime Created)> LoadMeta()
        {
            var list = new List<(string, string, DateTime)>();
            try
            {
                if (!File.Exists(_metaFile)) return list;
                var arr = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_metaFile)) as System.Text.Json.Nodes.JsonArray;
                if (arr == null) return list;
                foreach (var n in arr.OfType<System.Text.Json.Nodes.JsonObject>())
                    list.Add(((string?)n["id"] ?? "", (string?)n["commit"] ?? "", DateTime.TryParse((string?)n["created"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.MinValue));
            }
            catch { }
            return list.Where(e => e.Item1.Length > 0).ToList();
        }

        private void SaveMeta(List<(string Id, string Commit, DateTime Created)> list)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_metaFile)!);
            var arr = new System.Text.Json.Nodes.JsonArray(list.Select(e => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["id"] = e.Id, ["commit"] = e.Commit, ["created"] = e.Created.ToString("o") }).ToArray());
            string temp = _metaFile + ".tmp";
            File.WriteAllText(temp, arr.ToJsonString());
            File.Move(temp, _metaFile, true);
        }

        private static bool SafeRelativePath(string p) =>
            p.Length > 0 && !Path.IsPathRooted(p) && !p.Contains("..") && !p.StartsWith('/') && !p.Any(char.IsControl);

        private static string Short(string s) => s.Length > 300 ? s[..300] + "..." : s;
    }
}
