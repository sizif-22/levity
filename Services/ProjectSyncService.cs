using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Levity.Services
{
    public class SyncItem
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Status { get; set; } = "pending"; // pending | running | done | error | cancelled
        public string Summary { get; set; } = "";
        public double Seconds { get; set; }
    }

    /// <summary>
    /// Runs one agy session per project, one after another, each with the same review prompt.
    /// </summary>
    public class ProjectSyncService
    {
        public const string UpdateMode = "update";
        public const string ReportMode = "report";

        private static readonly TimeSpan PerProjectTimeout = TimeSpan.FromMinutes(20);

        private CancellationTokenSource? _cts;
        private AgyProcessManager? _current;

        public bool IsRunning { get; private set; }
        public string Mode { get; private set; } = UpdateMode;
        public List<SyncItem> Items { get; private set; } = new();

        public event Action? OnProgress;

        public static string BuildPrompt(string mode, string? customPrompt)
        {
            string body = string.IsNullOrWhiteSpace(customPrompt) ? DefaultPrompt : customPrompt!;
            string guard = mode == ReportMode
                ? "MODE: REPORT ONLY. Do not create, edit or delete any file. Reply with the exact changes you would make to the docs and skills, per file."
                : "MODE: UPDATE. You may create or edit documentation and skill files only.";
            return $"{guard}\n\n{body}";
        }

        public const string DefaultPrompt =
@"You are doing a maintenance pass on this project so future AI sessions start with accurate context.

1. Inspect the project read-only: structure, stack, entry points, how to build / run / test, deploy or env setup, and what changed recently (git log if available).
2. README.md: create it if missing; otherwise fix anything stale and keep what is still correct.
3. AGENTS.md: create or update with purpose, stack, folder layout, exact commands, conventions, and known gotchas.
   Keep every part that is still correct, including a section the file already has called ""Where things live"".
   Add or refresh a final section `## Where things live`: the features of this project, one line each, in the form
   `- <feature>: path/a (what it does), path/b (what it does), ...`. List the files a change to that feature touches end to end
   (screen or entry point, server or backend code, data model or schema, limits or config, tests). Find them with one repo-wide search per feature
   (`rg -n -i`), cover the 8 to 15 features people change most (publishing, auth, payments, each integration, and so on), names and paths only:
   no code, no secrets, no line numbers. Fix a line that is wrong instead of adding a second one. Future sessions read this section first and skip searching.
4. Skills: create or update 2-5 project skills at .agent/skills/<skill-name>/SKILL.md (frontmatter: name, description). Pick recurring tasks specific to this project, such as build and run, test, release, or data setup. If the project already uses another skills location or format, follow that instead.

Hard rules:
- Touch documentation and skill files only. Never modify source code, configs, lockfiles, or .env files.
- Never read out or write secret values (keys, tokens, passwords) into any file.
- Do not install dependencies, run migrations, or run commands that modify the project. Do not git commit or push.
- Finish with a 3-line summary: what you changed, what you created, what is still unclear.";

        public async Task RunAsync(IEnumerable<ProjectInfo> projects, string mode, string? customPrompt)
        {
            if (IsRunning) return;

            Items = projects.Select(p => new SyncItem { Name = p.Name, Path = p.Path }).ToList();
            Mode = mode == ReportMode ? ReportMode : UpdateMode;
            IsRunning = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            string prompt = BuildPrompt(Mode, customPrompt);
            OnProgress?.Invoke();

            try
            {
                foreach (var item in Items)
                {
                    if (ct.IsCancellationRequested) { item.Status = "cancelled"; continue; }
                    await RunOneAsync(item, prompt, ct);
                    OnProgress?.Invoke();
                }
            }
            finally
            {
                IsRunning = false;
                _current = null;
                _cts?.Dispose();
                _cts = null;
                OnProgress?.Invoke();
            }
        }

        public void Cancel()
        {
            try { _cts?.Cancel(); } catch { }
            try { _current?.Stop(); } catch { }
        }

        private async Task RunOneAsync(SyncItem item, string prompt, CancellationToken ct)
        {
            item.Status = "running";
            OnProgress?.Invoke();

            var started = DateTime.UtcNow;
            var done = new TaskCompletionSource<(bool ok, string note)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reply = new StringBuilder();
            var manager = new AgyProcessManager(item.Path);
            _current = manager;

            manager.OnEventReceived += line =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("event", out var evt)) return;

                    switch (evt.GetString())
                    {
                        case "step_update":
                            if (root.TryGetProperty("step_update", out var su)
                                && su.TryGetProperty("step_type", out var st) && st.GetString() == "agent_response"
                                && su.TryGetProperty("text_delta", out var delta) && delta.ValueKind == JsonValueKind.String)
                            {
                                reply.Append(delta.GetString());
                            }
                            break;

                        case "result":
                            bool failed = root.TryGetProperty("result", out var res)
                                && res.TryGetProperty("status", out var status) && status.GetString() == "ERROR";
                            done.TrySetResult((!failed, failed ? "agy reported an error" : ""));
                            break;
                    }
                }
                catch { }
            };
            manager.OnStatusChanged += text =>
            {
                if (text.Contains("Stopped") || text.Contains("Error") || text.Contains("Failed"))
                    done.TrySetResult((false, text));
            };

            try
            {
                if (!manager.Start() || !manager.SendPrompt(prompt))
                {
                    item.Status = "error";
                    item.Summary = "Could not start agy in this folder.";
                    return;
                }

                var timeout = Task.Delay(PerProjectTimeout, ct);
                var finished = await Task.WhenAny(done.Task, timeout);

                if (ct.IsCancellationRequested) { item.Status = "cancelled"; return; }
                if (finished != done.Task) { item.Status = "error"; item.Summary = "Timed out after 20 minutes."; return; }

                var (ok, note) = done.Task.Result;
                item.Status = ok ? "done" : "error";
                item.Summary = ok ? Tail(reply.ToString(), 600) : (note.Length > 0 ? note : "Failed.");
            }
            catch (OperationCanceledException)
            {
                item.Status = "cancelled";
            }
            catch (Exception ex)
            {
                item.Status = "error";
                item.Summary = ex.Message;
            }
            finally
            {
                item.Seconds = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1);
                try { manager.Stop(); } catch { }
            }
        }

        private static string Tail(string text, int max)
        {
            text = text.Trim();
            return text.Length <= max ? text : "…" + text[^max..];
        }
    }
}
