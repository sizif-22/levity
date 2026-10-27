using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravitySidebar.Services
{
    /// <param name="Tokens">Total tokens the agent reported using (0 if unknown).</param>
    public sealed record ModelRunResult(string? Json, string? Error, long Tokens, double Seconds);

    /// <summary>Whatever answers the diagram prompt. The real one is agy; tests use a fake.</summary>
    public interface IDiagramModelRunner
    {
        Task<ModelRunResult> RunAsync(string prompt, CancellationToken ct);
    }

    /// <summary>
    /// Asks agy for the graph in a one-shot session: an empty temporary working folder, no permission-skipping flag, a timeout,
    /// and the whole process tree killed when it ends or is cancelled. The prompt goes in through stdin (the command line is too short for it).
    /// </summary>
    public sealed class AgyDiagramRunner : IDiagramModelRunner
    {
        public async Task<ModelRunResult> RunAsync(string prompt, CancellationToken ct)
        {
            var started = DateTime.UtcNow;
            string work = Path.Combine(Path.GetTempPath(), "levity-diagram-" + Guid.NewGuid().ToString("N"));
            string cwd = Path.Combine(work, "run");
            Directory.CreateDirectory(cwd);                       // empty: nothing for the agent to wander into
            string schemaPath = Path.Combine(work, "schema.json");
            File.WriteAllText(schemaPath, DiagramSchema.Json);

            string effort = (DotEnv.Get("LEVITY_DIAGRAM_EFFORT") ?? "low").ToLowerInvariant();
            if (effort is not ("low" or "medium" or "high")) effort = "low";
            int timeoutSeconds = int.TryParse(DotEnv.Get("LEVITY_DIAGRAM_TIMEOUT"), out int t) ? Math.Clamp(t, 30, 1800) : 300;

            var psi = new System.Diagnostics.ProcessStartInfo(AgyProcessManager.LocateAgyExecutable() ?? "agy")
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string a in new[] { "--input-format=stream-json", "--output-format=stream-json", "--print=", "--json-schema", schemaPath, "--effort", effort })
                psi.ArgumentList.Add(a);

            using var proc = new System.Diagnostics.Process { StartInfo = psi };
            WinJob? job = null;
            try
            {
                try { proc.Start(); }
                catch (Exception ex) { return new ModelRunResult(null, "Could not start agy: " + ex.Message, 0, 0); }
                job = WinJob.TryCreate(proc);

                var stderr = new StringBuilder();
                _ = Task.Run(async () =>
                {
                    try { string? l; while ((l = await proc.StandardError.ReadLineAsync()) != null) { if (stderr.Length < 2000) stderr.AppendLine(l); } } catch { }
                });

                string message = JsonSerializer.Serialize(new { @event = "user", message = new { content = prompt } });
                await proc.StandardInput.WriteLineAsync(message);
                await proc.StandardInput.FlushAsync();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                try
                {
                    string? line;
                    while ((line = await proc.StandardOutput.ReadLineAsync(cts.Token)) != null)
                    {
                        if (line.Length == 0 || line[0] != '{') continue;
                        var parsed = TryParseResult(line, started);
                        if (parsed != null) return parsed;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) throw;
                    return new ModelRunResult(null, $"The assistant did not answer within {timeoutSeconds} seconds.", 0, (DateTime.UtcNow - started).TotalSeconds);
                }

                string tail = stderr.ToString().Trim();
                return new ModelRunResult(null, "agy ended without an answer." + (tail.Length > 0 ? " " + DiagramText.Limit(tail, 300) : ""), 0, (DateTime.UtcNow - started).TotalSeconds);
            }
            finally
            {
                job?.Terminate();
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
                job?.Dispose();
                try { Directory.Delete(work, true); } catch { }
            }
        }

        /// <summary>The "result" event of agy's stream, as a ModelRunResult; null for every other event.</summary>
        internal static ModelRunResult? TryParseResult(string line, DateTime started)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("event", out var ev) || ev.GetString() != "result") return null;
                if (!root.TryGetProperty("result", out var r) || r.ValueKind != JsonValueKind.Object) return new ModelRunResult(null, "agy returned an empty result.", 0, 0);

                double seconds = (DateTime.UtcNow - started).TotalSeconds;
                long tokens = r.TryGetProperty("usage", out var u) && u.TryGetProperty("total_tokens", out var tt) && tt.TryGetInt64(out long n) ? n : 0;
                string status = r.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "" : "";
                if (status == "ERROR")
                {
                    string err = r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
                    return new ModelRunResult(null, "The assistant reported an error." + (err.Length > 0 ? " " + DiagramText.Limit(err, 300) : ""), tokens, seconds);
                }

                if (r.TryGetProperty("structured_output", out var so))
                {
                    if (so.ValueKind == JsonValueKind.Object) return new ModelRunResult(so.GetRawText(), null, tokens, seconds);
                    if (so.ValueKind == JsonValueKind.String && so.GetString() is { Length: > 0 } text) return new ModelRunResult(text, null, tokens, seconds);
                }
                string? response = r.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.String ? resp.GetString() : null;
                return new ModelRunResult(response, response == null ? "The assistant returned no answer." : null, tokens, seconds);
            }
            catch (JsonException) { return null; }
        }
    }

    public sealed record DiagramResult(string Root, DiagramGraph Graph, CompiledDiagram Compiled, DateTime GeneratedUtc, string? Head, bool Stale, DiagramStats? Stats, IReadOnlyList<string> Notes, long Tokens);

    public sealed record DiagramProgress(string Root, string Phase, string Message);

    /// <summary>Diagrams cached under the settings folder, one JSON file per project. A cached file is re-validated on every read.</summary>
    public sealed class DiagramStore
    {
        private const int Version = 1;
        private readonly string _dir;

        public DiagramStore(string? dir = null) => _dir = dir ?? Path.Combine(AppSettings.DataDir, "diagrams");

        public static string NormalizeRoot(string root) => Path.GetFullPath(root).TrimEnd('\\', '/');

        private string PathFor(string root)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeRoot(root).ToLowerInvariant()));
            return Path.Combine(_dir, Convert.ToHexString(hash).Substring(0, 32).ToLowerInvariant() + ".json");
        }

        public void Save(string root, DiagramGraph graph, string? head, DiagramStats? stats, IReadOnlyList<string> notes, long tokens)
        {
            Directory.CreateDirectory(_dir);
            var record = new
            {
                version = Version,
                root = NormalizeRoot(root),
                head,
                generatedUtc = DateTime.UtcNow,
                promptVersion = DiagramPrompt.Version,
                graphJson = DiagramValidator.ToJson(graph),
                stats,
                notes = notes.Take(30).Select(n => DiagramText.Limit(n, 300)).ToArray(),
                tokens,
            };
            string path = PathFor(root), tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(record));
            File.Move(tmp, path, overwrite: true);
        }

        /// <summary>The cached diagram for a project, or null if there is none or the file cannot be trusted.</summary>
        public (DiagramGraph graph, string? head, DateTime generatedUtc, int promptVersion, DiagramStats? stats, IReadOnlyList<string> notes, long tokens)? Load(string root)
        {
            try
            {
                string path = PathFor(root);
                if (!File.Exists(path) || new FileInfo(path).Length > DiagramLimits.MaxStoredBytes * 3) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var r = doc.RootElement;
                if (r.GetProperty("version").GetInt32() != Version) return null;
                if (!string.Equals(r.GetProperty("root").GetString(), NormalizeRoot(root), StringComparison.OrdinalIgnoreCase)) return null; // a file for another folder

                string? graphJson = r.GetProperty("graphJson").GetString();
                if (graphJson == null || graphJson.Length > DiagramLimits.MaxStoredBytes) return null;
                var validated = DiagramValidator.ValidateStored(graphJson);
                if (!validated.Ok) return null;

                string? head = r.TryGetProperty("head", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
                DateTime generated = r.GetProperty("generatedUtc").GetDateTime();
                int promptVersion = r.TryGetProperty("promptVersion", out var pv) ? pv.GetInt32() : 0;
                DiagramStats? stats = null;
                if (r.TryGetProperty("stats", out var s) && s.ValueKind == JsonValueKind.Object)
                    stats = JsonSerializer.Deserialize<DiagramStats>(s.GetRawText());
                var notes = r.TryGetProperty("notes", out var nt) && nt.ValueKind == JsonValueKind.Array
                    ? nt.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => DiagramText.Limit(x.GetString(), 300)).Take(30).ToList()
                    : new List<string>();
                long tokens = r.TryGetProperty("tokens", out var tk) && tk.TryGetInt64(out long tv) ? tv : 0;
                return (validated.Graph!, head, generated, promptVersion, stats, notes, tokens);
            }
            catch { return null; }
        }

        public bool Delete(string root)
        {
            try { string p = PathFor(root); if (!File.Exists(p)) return false; File.Delete(p); return true; }
            catch { return false; }
        }
    }

    /// <summary>Runs the whole pipeline: read the project, ask the assistant, validate, compile, cache. One run at a time.</summary>
    public sealed class DiagramService
    {
        public const int MaxAttempts = 2;
        public const string BusyMessage = "A diagram is already being generated.";
        private readonly IDiagramModelRunner _runner;
        private readonly DiagramStore _store;
        private readonly object _gate = new();
        private CancellationTokenSource? _cts;

        public DiagramService(IDiagramModelRunner runner, DiagramStore store)
        {
            _runner = runner;
            _store = store;
        }

        public event Action<DiagramProgress>? Progress;

        public bool IsRunning { get { lock (_gate) return _cts != null; } }

        /// <summary>Which project is being generated right now, or null.</summary>
        public string? RunningRoot { get; private set; }

        public void Cancel()
        {
            CancellationTokenSource? cts;
            lock (_gate) cts = _cts;
            try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        /// <summary>Reads the project and reports what would be sent, without calling the assistant.</summary>
        public async Task<(DiagramStats stats, int promptChars)> PrepareAsync(string root, CancellationToken ct = default)
        {
            var ing = await DiagramIngest.BuildAsync(root, ct);
            return (ing.Stats, DiagramPrompt.Build(ing).Length);
        }

        /// <summary>The cached diagram, marked stale when the project has moved on since it was made.</summary>
        public async Task<DiagramResult?> GetAsync(string root, CancellationToken ct = default)
        {
            var cached = _store.Load(root);
            if (cached == null) return null;
            var (graph, head, generated, promptVersion, stats, notes, tokens) = cached.Value;
            string? now = await DiagramIngest.GitHeadAsync(root, ct);
            bool stale = promptVersion != DiagramPrompt.Version || (head != null && now != null && !string.Equals(head, now, StringComparison.OrdinalIgnoreCase));
            return new DiagramResult(DiagramStore.NormalizeRoot(root), graph, DiagramCompiler.Compile(graph), generated, head, stale, stats, notes, tokens);
        }

        public bool Delete(string root) => _store.Delete(root);

        /// <returns>The diagram, or a plain-language error. Cancellation throws <see cref="OperationCanceledException"/>.</returns>
        public async Task<(DiagramResult? result, string? error)> GenerateAsync(string root, CancellationToken outer = default)
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_cts != null) return (null, BusyMessage);
                cts = _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
                RunningRoot = DiagramStore.NormalizeRoot(root);
            }

            string normalized = DiagramStore.NormalizeRoot(root);
            void Report(string phase, string message) => Progress?.Invoke(new DiagramProgress(normalized, phase, message));
            try
            {
                var ct = cts.Token;
                Report("ingest", "Reading the project...");
                DiagramIngestResult ing;
                try { ing = await DiagramIngest.BuildAsync(root, ct); }
                catch (DirectoryNotFoundException) { return (null, "That folder does not exist."); }

                string prompt = DiagramPrompt.Build(ing);
                string request = prompt;
                long tokens = 0;
                DiagramValidation? validation = null;

                for (int attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    Report("generating", attempt == 1
                        ? $"Asking the assistant ({ing.Stats.Files} files listed, {ing.Stats.ExcerptFiles} excerpts)..."
                        : "Asking the assistant to fix problems in its first answer...");
                    var run = await _runner.RunAsync(request, ct);
                    tokens += run.Tokens;
                    if (run.Error != null) return (null, run.Error);

                    Report("validating", "Checking the result against the project...");
                    validation = DiagramValidator.Validate(run.Json, ing.Context);
                    if (validation.Ok) break;
                    if (attempt < MaxAttempts) request = DiagramPrompt.BuildRetry(prompt, run.Json ?? "", DiagramValidator.FormatFeedback(validation.Issues));
                }

                if (validation is not { Ok: true })
                {
                    string problems = string.Join(" ", (validation?.Issues ?? new List<DiagramIssue>()).Take(3).Select(i => $"{i.Where}: {i.Message}"));
                    return (null, "The assistant's answer could not be used. " + problems);
                }

                var graph = validation.Graph!;
                string? head = await DiagramIngest.GitHeadAsync(root, ct);
                try { _store.Save(root, graph, head, ing.Stats, validation.Notes, tokens); }
                catch (Exception ex) { validation.Notes.Add("The diagram could not be saved for later: " + ex.Message); }

                Report("done", "Done.");
                return (new DiagramResult(normalized, graph, DiagramCompiler.Compile(graph), DateTime.UtcNow, head, false, ing.Stats, validation.Notes, tokens), null);
            }
            finally
            {
                lock (_gate) { _cts = null; RunningRoot = null; }
                cts.Dispose();
            }
        }
    }
}
