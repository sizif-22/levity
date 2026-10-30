using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Levity.Services
{
    /// <summary>One piece of a bigger job. A task owns its files: no other task in the same plan may touch them.</summary>
    public sealed record TurboTask(string Id, string Title, IReadOnlyList<string> Files, string Instructions);

    public sealed record TurboPlan(string Contracts, IReadOnlyList<TurboTask> Tasks);

    /// <summary>The outcome of one assistant run: did it finish, and what did it say.</summary>
    public sealed record AgentRun(bool Ok, string Reply, double Seconds);

    /// <summary>
    /// Turbo mode: a planner splits the request into tasks that touch different files, the tasks run on separate assistants at the same
    /// time, and the results are put back together. Everything here is UI-free so it can be tested with a fake assistant.
    /// If the job cannot be split safely (one task, a bad plan, overlapping files that merge into one) Turbo says so and the caller
    /// sends the message to the normal assistant, so Turbo never makes a job worse than not using it.
    /// </summary>
    public static class Turbo
    {
        public const int MaxWorkers = 4;
        public const int MaxFilesPerTask = 40;

        public static string PlannerPrompt(string request, int maxWorkers) =>
            "[TURBO PLANNER. Do NOT change any file and do NOT run anything slow. You may read a few files or use rg to see what exists, " +
            "then answer with ONE json object and nothing else.]\n\n" +
            "Split the request below into at most " + maxWorkers + " tasks that can be done AT THE SAME TIME by different people.\n" +
            "Rules:\n" +
            "- Every file may be listed in exactly one task. Tasks never edit the same file.\n" +
            "- Put anything that several tasks depend on (names, function signatures, message formats, field names) in \"contracts\", written out exactly, so the tasks agree without talking.\n" +
            "- If the request is small, depends step by step on earlier results, or cannot be split by file, return a single task.\n" +
            "- Files are paths relative to the project folder; list the files the task will create or change.\n" +
            "- Do not add a task just to run tests: tests are run once at the end.\n" +
            "Format: {\"contracts\":\"...\",\"tasks\":[{\"id\":\"t1\",\"title\":\"short\",\"files\":[\"a/b.cs\"],\"instructions\":\"what to do, in full\"}]}\n\n" +
            "REQUEST:\n" + request;

        public static string WorkerPrompt(string request, TurboPlan plan, TurboTask task)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[TURBO WORKER. Other workers are editing other files in this same folder right now.]");
            sb.AppendLine("Do ONLY your task. Edit ONLY the files listed for you (plus new files inside the same folders if you must). Never edit, move or delete anything else.");
            sb.AppendLine("Do not run git add, commit, stash or checkout. Do not run the whole test suite; run only a quick check of your own files if there is one.");
            sb.AppendLine("When done, reply with 3 lines at most: what you changed, and anything the others must know.");
            sb.AppendLine();
            sb.AppendLine("WHOLE REQUEST (for context):");
            sb.AppendLine(request);
            if (!string.IsNullOrWhiteSpace(plan.Contracts)) { sb.AppendLine(); sb.AppendLine("AGREED CONTRACTS (use exactly these):"); sb.AppendLine(plan.Contracts); }
            sb.AppendLine();
            sb.AppendLine("ALL TASKS (so you know who does what):");
            foreach (var t in plan.Tasks) sb.AppendLine($"- {t.Id}: {t.Title} [{string.Join(", ", t.Files)}]");
            sb.AppendLine();
            sb.AppendLine($"YOUR TASK: {task.Id}: {task.Title}");
            sb.AppendLine("Your files: " + string.Join(", ", task.Files));
            sb.AppendLine(task.Instructions);
            return sb.ToString();
        }

        /// <summary>Reads the planner's answer. The assistant may wrap the json in text or a code fence, so only the outermost {...} is parsed. Null when it is not a usable plan.</summary>
        public static TurboPlan? ParsePlan(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return null;
            int a = reply.IndexOf('{'), b = reply.LastIndexOf('}');
            if (a < 0 || b <= a) return null;
            try
            {
                using var doc = JsonDocument.Parse(reply.Substring(a, b - a + 1));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tasks", out var tasksEl) || tasksEl.ValueKind != JsonValueKind.Array) return null;
                string contracts = root.TryGetProperty("contracts", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                var tasks = new List<TurboTask>();
                int n = 0;
                foreach (var t in tasksEl.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.Object) continue;
                    n++;
                    string id = Str(t, "id") is { Length: > 0 } i ? i : "t" + n;
                    string title = Str(t, "title") is { Length: > 0 } ti ? ti : id;
                    string instr = Str(t, "instructions");
                    if (instr.Length == 0) continue;
                    var files = new List<string>();
                    if (t.TryGetProperty("files", out var fl) && fl.ValueKind == JsonValueKind.Array)
                        foreach (var f in fl.EnumerateArray())
                            if (f.ValueKind == JsonValueKind.String && NormalizePath(f.GetString()) is { } p && !files.Contains(p, StringComparer.OrdinalIgnoreCase)) files.Add(p);
                    tasks.Add(new TurboTask(id, title, files.Take(MaxFilesPerTask).ToList(), instr));
                }
                return tasks.Count == 0 ? null : new TurboPlan(contracts, tasks);
            }
            catch (JsonException) { return null; }
        }

        private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

        /// <summary>Relative, forward-slash, no "..": a task may not claim a path outside the project.</summary>
        public static string? NormalizePath(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return null;
            p = p.Trim().Replace('\\', '/').TrimStart('/');
            if (p.StartsWith("./")) p = p[2..];
            if (p.Length == 0 || p.Contains("..") || p.Contains(':')) return null;
            return p;
        }

        private static bool Overlaps(TurboTask a, TurboTask b) =>
            a.Files.Any(fa => b.Files.Any(fb => string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase)
                || fa.StartsWith(fb.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
                || fb.StartsWith(fa.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)));

        /// <summary>
        /// Makes the plan safe to run at once: tasks that share a file (or a folder and a file inside it) are merged into one, tasks that
        /// name no files cannot be proven separate so they are merged into one too, and the count is cut to maxWorkers by merging the smallest.
        /// </summary>
        public static TurboPlan Normalize(TurboPlan plan, int maxWorkers)
        {
            maxWorkers = Math.Clamp(maxWorkers, 1, MaxWorkers);
            var groups = new List<List<TurboTask>>();
            var unknown = new List<TurboTask>();
            foreach (var t in plan.Tasks)
            {
                if (t.Files.Count == 0) { unknown.Add(t); continue; }
                var hits = groups.Where(g => g.Any(x => Overlaps(x, t))).ToList();
                var merged = new List<TurboTask> { t };
                foreach (var h in hits) { merged.AddRange(h); groups.Remove(h); }
                groups.Add(merged);
            }
            if (unknown.Count > 0) groups.Add(unknown);

            while (groups.Count > maxWorkers)
            {
                var two = groups.OrderBy(g => g.Sum(t => t.Files.Count + 1)).Take(2).ToList();
                foreach (var g in two) groups.Remove(g);
                groups.Add(two.SelectMany(g => g).ToList());
            }
            return new TurboPlan(plan.Contracts, groups.Select(Merge).ToList());
        }

        private static TurboTask Merge(List<TurboTask> g)
        {
            if (g.Count == 1) return g[0];
            return new TurboTask(string.Join("+", g.Select(t => t.Id)), string.Join(" + ", g.Select(t => t.Title)),
                g.SelectMany(t => t.Files).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                string.Join("\n\n", g.Select(t => $"[{t.Title}]\n{t.Instructions}")));
        }

        public sealed record TurboResult(bool Split, string Markdown, int Workers, int Failed, double Seconds);

        public delegate Task<AgentRun> AgentRunner(string folder, string prompt, string effort, CancellationToken ct);

        /// <summary>
        /// Plans and runs the job. Split=false means "not worth splitting": nothing was changed and the caller should send the message normally.
        /// progress gets short markdown lines as things happen.
        /// </summary>
        public static async Task<TurboResult> RunAsync(string request, string folder, AgentRunner run, Action<string> progress, CancellationToken ct, int maxWorkers = MaxWorkers)
        {
            var started = DateTime.UtcNow;
            progress("**Turbo** · planning…\n");
            var planRun = await run(folder, PlannerPrompt(request, maxWorkers), AgentLaunch.Fast, ct);
            var plan = planRun.Ok ? ParsePlan(planRun.Reply) : null;
            if (plan != null) plan = Normalize(plan, maxWorkers);
            if (plan == null || plan.Tasks.Count < 2)
            {
                progress("Not worth splitting; the normal assistant takes this one.\n");
                return new TurboResult(false, "", 0, 0, (DateTime.UtcNow - started).TotalSeconds);
            }

            progress($"Split into {plan.Tasks.Count} tasks, running at the same time:\n" +
                string.Join("", plan.Tasks.Select(t => $"- **{t.Title}** ({t.Files.Count} file{(t.Files.Count == 1 ? "" : "s")})\n")));

            var runs = await Task.WhenAll(plan.Tasks.Select(async t =>
            {
                try
                {
                    var r = await run(folder, WorkerPrompt(request, plan, t), AgentLaunch.Fast, ct);
                    progress($"- {(r.Ok ? "done" : "FAILED")}: {t.Title} ({r.Seconds:0} s)\n");
                    return (task: t, run: r);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    progress($"- FAILED: {t.Title}: {ex.Message}\n");
                    return (task: t, run: new AgentRun(false, ex.Message, 0));
                }
            }));

            int failed = runs.Count(r => !r.run.Ok);
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine(failed == 0 ? $"**Turbo finished** · {plan.Tasks.Count} workers in parallel, {(DateTime.UtcNow - started).TotalSeconds:0} s." :
                $"**Turbo finished with {failed} failed task(s)** · {(DateTime.UtcNow - started).TotalSeconds:0} s. Use Undo to roll back, or ask me to redo the failed ones.");
            foreach (var r in runs)
                sb.AppendLine($"\n**{r.task.Title}**{(r.run.Ok ? "" : " (failed)")}\n{Tail(r.run.Reply, 600)}");
            return new TurboResult(true, sb.ToString(), plan.Tasks.Count, failed, (DateTime.UtcNow - started).TotalSeconds);
        }

        private static string Tail(string text, int max)
        {
            text = (text ?? "").Trim();
            return text.Length <= max ? text : "…" + text[^max..];
        }
    }
}
