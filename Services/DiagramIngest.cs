using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravitySidebar.Services
{
    /// <param name="Files">Files listed to the model (secrets and binaries excluded).</param>
    /// <param name="ExcerptFiles">Source files whose beginning is included.</param>
    /// <param name="IndexedFiles">Files read in full by Levity to find references between files.</param>
    /// <param name="Redactions">Secret-looking values replaced before anything is sent.</param>
    /// <param name="Truncated">The project was larger than the limits, so the listing is partial.</param>
    public sealed record DiagramStats(int Files, int ExcerptFiles, int ExcerptChars, int IndexedFiles, int Redactions, bool Truncated);

    public sealed record DiagramSource(string Path, string Text);

    /// <summary>Everything gathered from a project folder, ready to be turned into a prompt.</summary>
    public sealed class DiagramIngestResult
    {
        public required DiagramContext Context { get; init; }
        public required string TreeText { get; init; }
        public string? ReadmeText { get; init; }
        public required IReadOnlyList<DiagramSource> Sources { get; init; }
        public required DiagramStats Stats { get; init; }
        public required string SourceIndex { get; init; }
    }

    /// <summary>
    /// Reads a project folder for diagram generation. Everything here is defensive: it never follows links out of the
    /// folder, never reads secret-like files, and redacts secret-looking values before text can leave the PC.
    /// </summary>
    public static class DiagramIngest
    {
        public const int MaxFiles = 20_000;
        public const int MaxTreeLines = 1500;
        public const int MaxReadmeChars = 12_000;
        public const int MaxExcerptFiles = 24;
        public const int MaxExcerptChars = 3_500;
        public const int MaxTotalExcerptChars = 80_000;
        public const int MaxIndexedFiles = 600;
        public const int MaxIndexBytesPerFile = 200_000;
        public const long MaxIndexBytesTotal = 40_000_000;
        public const int MaxRefsPerFile = 40;
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

        private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".hg", ".svn", "node_modules", "bin", "obj", "dist", "build", "out", "target", "publish", ".next", ".nuxt", ".vs", ".idea",
            ".venv", "venv", "__pycache__", ".pytest_cache", ".mypy_cache", ".gradle", ".wrangler", "coverage", ".cache", ".turbo",
            "testresults",
        };

        private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py", ".go", ".rs", ".java", ".kt", ".rb", ".php", ".swift", ".c", ".h", ".cpp", ".hpp", ".ps1", ".sh", ".vue", ".svelte",
        };

        private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp", ".webp", ".svg", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".mp3", ".mp4", ".wav", ".mov", ".zip", ".gz", ".tgz", ".7z", ".rar",
            ".exe", ".dll", ".pdb", ".so", ".dylib", ".class", ".jar", ".pdf", ".lock", ".map", ".snap", ".webmanifest", ".bin", ".dat",
        };

        private static readonly string[] ManifestNames =
        {
            "package.json", "go.mod", "Cargo.toml", "pyproject.toml", "requirements.txt", "pom.xml", "build.gradle", "Dockerfile", "docker-compose.yml", "wrangler.toml", "wrangler.jsonc",
        };

        private static readonly Regex SecretName = new(
            @"(^|/)(\.env(?!\.(example|sample|template|dist)$)(\..+)?|.*\.(pem|key|pfx|p12|jks|keystore|kdbx|ppk|crt|cer)|id_(rsa|dsa|ecdsa|ed25519)(\.pub)?|\.npmrc|\.pypirc|\.netrc|\.htpasswd|(credentials?|secrets?)(\.(json|ya?ml|toml|ini|xml|cfg|conf|properties|txt|env|plist))?|.*[-_.](credentials?|secrets?)\.(json|ya?ml|toml|ini|xml|cfg|conf|properties|txt|env|plist)|service-account.*\.json|appsettings\.(production|prod|secrets)\.json|.*\.tfstate(\..+)?|.*\.tfvars)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NonProduct = new(@"(^|/)(tests?|__tests__|spec|specs|e2e|fixtures|__fixtures__|mocks?|benchmarks?|examples?|samples?|vendor|third_party|migrations)(/|$)|\.(test|spec)\.[a-z]+$|\.min\.(js|css)$|\.(g|designer|generated)\.cs$|package-lock\.json$|yarn\.lock$|pnpm-lock\.yaml$|bun\.lockb?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ------------------------------------------------------------------ public entry point

        public static async Task<DiagramIngestResult> BuildAsync(string root, CancellationToken ct = default)
        {
            root = Path.GetFullPath(root).TrimEnd('\\', '/');
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("That folder does not exist.");

            var (rawFiles, truncated) = await ListFilesAsync(root, ct);
            var files = rawFiles.Select(DiagramText.NormalizeRepoPath).Where(p => p != null).Select(p => p!)
                .Where(IsListable).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal).ToList();
            ct.ThrowIfCancellationRequested();

            var ctx = new DiagramContext(root, files);

            string? readmeText = null;
            string? readme = files.FirstOrDefault(f => !f.Contains('/') && Regex.IsMatch(f, @"^readme(\.[a-z0-9]+)?$", RegexOptions.IgnoreCase));
            if (readme != null)
            {
                string? raw = ReadText(root, readme, MaxReadmeChars * 4);
                if (raw != null)
                {
                    readmeText = Redact(Truncate(raw, MaxReadmeChars), out _);
                    ctx.ReadmePath = readme;
                    ctx.ReadPaths.Add(readme);
                }
            }

            ct.ThrowIfCancellationRequested();
            var inbound = BuildReferences(ctx, files, ct);

            // Choose what the model reads: manifests first, then the most-referenced, entry-point-like source files.
            var chosen = new List<string>();
            foreach (string name in ManifestNames)
                foreach (string f in files.Where(f => Depth(f) <= 1 && string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)).Take(1))
                    chosen.Add(f);
            foreach (string f in files.Where(f => Depth(f) <= 2 && f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).Take(3)) chosen.Add(f);
            chosen = chosen.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();

            var candidates = files
                .Where(f => SourceExtensions.Contains(Path.GetExtension(f)) && !NonProduct.IsMatch(f))
                .Select(f => (File: f, Score: Score(f, inbound)))
                .OrderByDescending(x => x.Score).ThenBy(x => x.File, StringComparer.Ordinal)
                .Select(x => x.File);
            foreach (string f in candidates)
            {
                if (chosen.Count >= MaxExcerptFiles + 8) break;
                if (!chosen.Contains(f, StringComparer.OrdinalIgnoreCase)) chosen.Add(f);
            }

            int redactions = 0, total = 0;
            var sources = new List<DiagramSource>();
            foreach (string f in chosen)
            {
                ct.ThrowIfCancellationRequested();
                if (total >= MaxTotalExcerptChars) break;
                string? raw = ReadText(root, f, MaxExcerptChars * 4);
                if (raw == null) continue;
                string text = Redact(Truncate(raw, MaxExcerptChars), out int n);
                redactions += n;
                sources.Add(new DiagramSource(f, text));
                ctx.ReadPaths.Add(f);
                total += text.Length;
            }

            string index = BuildSourceIndex(ctx, sources, inbound);
            var stats = new DiagramStats(files.Count, sources.Count, total, ctx.References.Count, redactions, truncated);
            return new DiagramIngestResult
            {
                Context = ctx,
                TreeText = BuildTreeText(files),
                ReadmeText = readmeText,
                Sources = sources,
                Stats = stats,
                SourceIndex = index,
            };
        }

        // ------------------------------------------------------------------ listing

        internal static bool IsListable(string path)
        {
            if (SecretName.IsMatch(path)) return false;
            foreach (string seg in path.Split('/').SkipLast(1))
                if (IgnoredDirs.Contains(seg)) return false;
            return !SkippedExtensions.Contains(Path.GetExtension(path));
        }

        internal static bool IsSecretName(string path) => SecretName.IsMatch(path);

        private static async Task<(List<string> files, bool truncated)> ListFilesAsync(string root, CancellationToken ct)
        {
            if (Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")))
            {
                var git = await TryGitListAsync(root, ct);
                if (git != null) return (git.Take(MaxFiles).ToList(), git.Count > MaxFiles);
            }
            var files = new List<string>();
            bool truncated = false;
            Walk(root, root, 0, files, ref truncated);
            return (files, truncated);
        }

        private static void Walk(string root, string dir, int depth, List<string> files, ref bool truncated)
        {
            if (depth > 12 || truncated) return;
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
            catch { return; }
            foreach (string entry in entries)
            {
                if (files.Count >= MaxFiles) { truncated = true; return; }
                string name = Path.GetFileName(entry);
                FileAttributes attr;
                try { attr = File.GetAttributes(entry); } catch { continue; }
                if (attr.HasFlag(FileAttributes.ReparsePoint)) continue; // never follow links or junctions
                if (attr.HasFlag(FileAttributes.Directory))
                {
                    if (!IgnoredDirs.Contains(name)) Walk(root, entry, depth + 1, files, ref truncated);
                }
                else files.Add(Path.GetRelativePath(root, entry).Replace('\\', '/'));
            }
        }

        private static async Task<List<string>?> TryGitListAsync(string root, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo("git")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                // The folder is untrusted: stop its own git config from running a command (core.fsmonitor) while we list files.
                foreach (string a in new[] { "-C", root, "-c", "core.fsmonitor=false", "-c", "core.quotepath=false", "ls-files", "-z", "--cached", "--others", "--exclude-standard" }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p == null) return null;
                var stdout = p.StandardOutput.ReadToEndAsync();
                _ = p.StandardError.ReadToEndAsync();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                try { await p.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { try { p.Kill(true); } catch { } ct.ThrowIfCancellationRequested(); return null; }
                if (p.ExitCode != 0) return null;
                return (await stdout).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; } // git not installed: fall back to walking the folder
        }

        /// <summary>The commit the project is at, or null when it is not a git repository (or git is unavailable).</summary>
        public static async Task<string?> GitHeadAsync(string root, CancellationToken ct = default)
        {
            try
            {
                var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (string a in new[] { "-C", root, "rev-parse", "HEAD" }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p == null) return null;
                var stdout = p.StandardOutput.ReadToEndAsync();
                _ = p.StandardError.ReadToEndAsync();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                try { await p.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { try { p.Kill(true); } catch { } return null; }
                string head = (await stdout).Trim();
                return p.ExitCode == 0 && Regex.IsMatch(head, "^[0-9a-f]{40,64}$") ? head : null;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ safe reading

        /// <summary>
        /// The absolute path for a repo-relative path, or null unless it lies strictly inside <paramref name="root"/> with no link or junction
        /// anywhere along the way. The target itself need not exist yet (the first missing part ends the check).
        /// </summary>
        public static string? SafeFullPath(string root, string relative)
        {
            try
            {
                string? rel = DiagramText.NormalizeRepoPath(relative);
                if (rel == null) return null;
                string rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
                string full = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;

                string current = rootFull;
                foreach (string seg in rel.Split('/'))
                {
                    current = Path.Combine(current, seg);
                    if (!File.Exists(current) && !Directory.Exists(current)) break;
                    if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return null;
                }
                return full;
            }
            catch { return null; }
        }

        /// <summary>
        /// Reads the start of a project file as text. Returns null for anything that is not an ordinary text file strictly inside the
        /// project: links, junctions, binary files, secret-like names, missing files.
        /// </summary>
        internal static string? ReadText(string root, string relative, int maxBytes)
        {
            try
            {
                string? rel = DiagramText.NormalizeRepoPath(relative);
                if (rel == null || SecretName.IsMatch(rel)) return null;
                string? full = SafeFullPath(root, rel);
                if (full == null || !File.Exists(full)) return null;

                using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[Math.Min(maxBytes, (int)Math.Min(fs.Length, int.MaxValue))];
                int read = 0, n;
                while (read < buffer.Length && (n = fs.Read(buffer, read, buffer.Length - read)) > 0) read += n;
                if (Array.IndexOf(buffer, (byte)0, 0, Math.Min(read, 4096)) >= 0) return null; // binary
                return Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch { return null; }
        }

        private static string Truncate(string text, int max) => text.Length <= max ? text : text.Substring(0, max);

        private static int Depth(string path) => path.Count(c => c == '/');

        // ------------------------------------------------------------------ redaction

        private static readonly (Regex pattern, string replacement)[] Redactions =
        {
            (new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)", RegexOptions.Compiled, RegexTimeout), "[REDACTED PRIVATE KEY]"),
            (new Regex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled, RegexTimeout), "[REDACTED]"),
            (new Regex(@"\bgh[pousr]_[A-Za-z0-9]{30,}\b|\bgithub_pat_[A-Za-z0-9_]{30,}\b", RegexOptions.Compiled, RegexTimeout), "[REDACTED]"),
            (new Regex(@"\bsk-[A-Za-z0-9_\-]{20,}\b|\bxox[baprs]-[A-Za-z0-9\-]{10,}\b|\bAIza[0-9A-Za-z_\-]{35}\b", RegexOptions.Compiled, RegexTimeout), "[REDACTED]"),
            (new Regex(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\b", RegexOptions.Compiled, RegexTimeout), "[REDACTED]"),
            (new Regex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=\-]{12,}", RegexOptions.Compiled, RegexTimeout), "Bearer [REDACTED]"),
            (new Regex(@"://[^/\s:@""']+:[^/\s@""']+@", RegexOptions.Compiled, RegexTimeout), "://[REDACTED]@"),
            (new Regex(@"(?i)\b(password|passwd|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key|client[_-]?secret|authorization|bearer)([""']?\s*[:=]\s*[""']?|\s+is\s+)([^\s""'<>;,)\]}]{8,})", RegexOptions.Compiled, RegexTimeout), "$1$2[REDACTED]"),
        };

        /// <summary>Replaces secret-looking values with [REDACTED]. A safety net, not a guarantee: secret-like files are also never read.</summary>
        public static string Redact(string text, out int count)
        {
            int n = 0;
            try
            {
                foreach (var (pattern, replacement) in Redactions)
                    text = pattern.Replace(text, m =>
                    {
                        // "password = reader.Next();" is code, not a secret: skip values that are calls.
                        if (replacement == "$1$2[REDACTED]" && m.Groups[3].Value.Contains('(')) return m.Value;
                        n++;
                        return m.Result(replacement);
                    });
            }
            catch (RegexMatchTimeoutException)
            {
                count = n + 1;
                return "[content omitted: it could not be checked for secrets in time]"; // fail closed
            }
            count = n;
            return text;
        }

        // ------------------------------------------------------------------ tree text

        internal static string BuildTreeText(IReadOnlyList<string> files)
        {
            if (files.Count <= MaxTreeLines) return string.Join("\n", files);

            // Too big to list: keep the first three levels and say how many files hide below each folder.
            var lines = new SortedSet<string>(StringComparer.Ordinal);
            var hidden = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string f in files)
            {
                string[] segs = f.Split('/');
                if (segs.Length <= 3) { lines.Add(f); continue; }
                string dir = string.Join("/", segs.Take(3)) + "/";
                hidden[dir] = hidden.GetValueOrDefault(dir) + 1;
            }
            foreach (var (dir, count) in hidden) lines.Add($"{dir} ({count} files)");
            return string.Join("\n", lines.Take(MaxTreeLines)) + (lines.Count > MaxTreeLines ? "\n... (list truncated)" : "");
        }

        // ------------------------------------------------------------------ references between files

        private static readonly Regex TsImport = new(
            @"(?:\bimport\s*(?:[^'""`;]*?\sfrom\s*)?|\bexport\s*[^'""`;]*?\sfrom\s*|\brequire\s*\(\s*|\bimport\s*\(\s*)['""]([^'""]+)['""]", RegexOptions.Compiled, RegexTimeout);
        private static readonly Regex CsDecl = new(@"\b(?:class|struct|interface|record|enum)\s+([A-Z][A-Za-z0-9_]{2,})", RegexOptions.Compiled, RegexTimeout);
        private static readonly Regex Ident = new(@"\b[A-Z][A-Za-z0-9_]{2,}\b", RegexOptions.Compiled, RegexTimeout);
        private static readonly Regex CsComments = new(@"//[^\n]*|/\*[\s\S]*?\*/", RegexOptions.Compiled, RegexTimeout);
        private static readonly Regex PyImport = new(@"^\s*(?:from\s+([A-Za-z_][\w\.]*)\s+import|import\s+([A-Za-z_][\w\.]*))", RegexOptions.Multiline | RegexOptions.Compiled, RegexTimeout);
        private static readonly string[] TsExtensions = { "", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".json" };

        /// <summary>
        /// Finds which project files reference which others (TypeScript/JavaScript imports, C# type usage, Python imports).
        /// Fills <see cref="DiagramContext.References"/> and returns how many files reference each file.
        /// </summary>
        internal static Dictionary<string, int> BuildReferences(DiagramContext ctx, IReadOnlyList<string> files, CancellationToken ct)
        {
            var inbound = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var fileSet = ctx.Files;
            var code = files.Where(f => SourceExtensions.Contains(Path.GetExtension(f)) && !NonProduct.IsMatch(f))
                .OrderBy(f => Depth(f)).ThenBy(f => f, StringComparer.Ordinal).Take(MaxIndexedFiles).ToList();

            var text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            long bytes = 0;
            foreach (string f in code)
            {
                ct.ThrowIfCancellationRequested();
                if (bytes > MaxIndexBytesTotal) break;
                string? t = ReadText(ctx.Root, f, MaxIndexBytesPerFile);
                if (t == null) continue;
                text[f] = t;
                bytes += t.Length;
            }

            // C#: which file declares which type (a name declared in several files is ambiguous and ignored).
            var declaredIn = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var ownTypes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (f, t) in text.Where(kv => kv.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                var own = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match m in CsDecl.Matches(CsComments.Replace(t, " ")))
                {
                    string name = m.Groups[1].Value;
                    own.Add(name);
                    if (!declaredIn.TryGetValue(name, out var list)) declaredIn[name] = list = new List<string>();
                    if (!list.Contains(f, StringComparer.OrdinalIgnoreCase)) list.Add(f);
                }
                ownTypes[f] = own;
            }

            foreach (var (f, t) in text)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                var refs = new SortedSet<string>(StringComparer.Ordinal);
                string ext = Path.GetExtension(f).ToLowerInvariant();

                if (ext is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" or ".vue" or ".svelte")
                {
                    foreach (Match m in TsImport.Matches(t))
                    {
                        string spec = m.Groups[1].Value;
                        if (!spec.StartsWith("./") && !spec.StartsWith("../")) continue;
                        string? target = ResolveRelative(f, spec, fileSet);
                        if (target != null && !string.Equals(target, f, StringComparison.OrdinalIgnoreCase)) refs.Add(target);
                    }
                }
                else if (ext == ".cs")
                {
                    foreach (Match m in Ident.Matches(CsComments.Replace(t, " ")))
                    {
                        if (!declaredIn.TryGetValue(m.Value, out var where) || where.Count != 1) continue;
                        if (ownTypes.TryGetValue(f, out var own) && own.Contains(m.Value)) continue;
                        if (!string.Equals(where[0], f, StringComparison.OrdinalIgnoreCase)) refs.Add(where[0]);
                    }
                }
                else if (ext == ".py")
                {
                    foreach (Match m in PyImport.Matches(t))
                    {
                        string module = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Replace('.', '/');
                        foreach (string cand in new[] { module + ".py", module + "/__init__.py", Dir(f) + module + ".py", Dir(f) + module + "/__init__.py" })
                        {
                            string? hit = ctx.Resolve(cand);
                            if (hit != null && fileSet.Contains(hit) && !string.Equals(hit, f, StringComparison.OrdinalIgnoreCase)) { refs.Add(hit); break; }
                        }
                    }
                }
                else continue; // no reference index for this language yet: its edges stay "inferred"

                ctx.References[f] = refs.Take(MaxRefsPerFile).ToArray();
                ctx.ReadPaths.Add(f);
                foreach (string r in refs) inbound[r] = inbound.GetValueOrDefault(r) + 1;
                }
                catch (RegexMatchTimeoutException) { /* a pathological file: skip it, its edges stay inferred */ }
            }
            return inbound;
        }

        private static string Dir(string path)
        {
            int i = path.LastIndexOf('/');
            return i < 0 ? "" : path.Substring(0, i + 1);
        }

        private static string? ResolveRelative(string fromFile, string spec, HashSet<string> files)
        {
            var parts = new List<string>(Dir(fromFile).TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries));
            foreach (string seg in spec.Split('/'))
            {
                if (seg == "." || seg.Length == 0) continue;
                if (seg == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
                else parts.Add(seg);
            }
            string basePath = string.Join("/", parts);
            foreach (string ext in TsExtensions)
                if (files.TryGetValue(basePath + ext, out string? actual)) return actual;
            foreach (string ext in TsExtensions.Skip(1).Take(4))
                if (files.TryGetValue(basePath + "/index" + ext, out string? actual)) return actual;
            return null;
        }

        // ------------------------------------------------------------------ scoring and the source index

        private static int Score(string file, Dictionary<string, int> inbound)
        {
            int score = inbound.GetValueOrDefault(file) * 4;
            string name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (name is "program" or "main" or "index" or "app" or "startup" or "server" or "mainwindow" or "__main__" or "manage" or "cli") score += 6;
            score -= Depth(file);
            if (file.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)) score -= 20;
            return score;
        }

        private static string BuildSourceIndex(DiagramContext ctx, IReadOnlyList<DiagramSource> sources, Dictionary<string, int> inbound)
        {
            var sb = new StringBuilder();
            sb.AppendLine("SOURCE INDEX (computed by Levity from the code, not by a model).");
            sb.AppendLine("For each file Levity read, the project files it references. A relationship is supported only by an excerpt, a reference listed here, or the README.");
            sb.AppendLine("Files marked [excerpt] are included below; the others were read by Levity only to find references.");
            var excerpted = sources.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (file, refs) in ctx.References.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                sb.Append(file).Append(excerpted.Contains(file) ? " [excerpt]" : "").Append(" -> ");
                sb.AppendLine(refs.Length == 0 ? "(references no other project file)" : string.Join(", ", refs.Take(12)) + (refs.Length > 12 ? ", ..." : ""));
            }
            var core = inbound.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(15).ToList();
            if (core.Count > 0) sb.AppendLine("CORE MODULES (most referenced): " + string.Join(", ", core.Select(kv => $"{kv.Key} ({kv.Value})")));
            if (ctx.References.Count == 0) sb.AppendLine("No reference index was possible for this project's languages; treat every relationship as inferred unless an excerpt or the README shows it.");
            return sb.ToString();
        }
    }
}
