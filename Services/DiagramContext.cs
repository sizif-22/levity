using System;
using System.Collections.Generic;
using System.Linq;

namespace AntigravitySidebar.Services
{
    /// <summary>
    /// What Levity knows about a project: its files, which ones were read, and which files reference which.
    /// Built from disk by <see cref="DiagramIngest"/>; built from plain lists in tests.
    /// </summary>
    public sealed class DiagramContext
    {
        private readonly Dictionary<string, string> _canonical = new(StringComparer.OrdinalIgnoreCase);

        public DiagramContext(string root, IEnumerable<string> files)
        {
            Root = root;
            foreach (string f in files)
            {
                string? p = DiagramText.NormalizeRepoPath(f);
                if (p == null) continue;
                _canonical[p] = p;
                // Every parent folder is a valid target too (a node may stand for a whole folder).
                int slash = p.LastIndexOf('/');
                while (slash > 0)
                {
                    string dir = p.Substring(0, slash);
                    _canonical.TryAdd(dir, dir);
                    slash = dir.LastIndexOf('/');
                }
            }
            Files = files.Select(DiagramText.NormalizeRepoPath).Where(p => p != null).Select(p => p!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public string Root { get; }

        /// <summary>All project files (repo-relative, forward slashes).</summary>
        public HashSet<string> Files { get; }

        /// <summary>The README that was given to the model, if any.</summary>
        public string? ReadmePath { get; set; }

        /// <summary>Files whose content was read (excerpted or indexed).</summary>
        public HashSet<string> ReadPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>For each file read, the other project files it references.</summary>
        public Dictionary<string, string[]> References { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The canonical spelling of an existing file or folder, or null if the path is unsafe or not in the project.</summary>
        public string? Resolve(string? path)
        {
            string? p = DiagramText.NormalizeRepoPath(path);
            return p != null && _canonical.TryGetValue(p, out string? c) ? c : null;
        }

        /// <summary>True if <paramref name="file"/> is the node's file or lies inside the node's folder.</summary>
        public static bool Covers(string nodePath, string file) =>
            string.Equals(nodePath, file, StringComparison.OrdinalIgnoreCase) || file.StartsWith(nodePath + "/", StringComparison.OrdinalIgnoreCase);
    }
}
