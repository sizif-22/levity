using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Levity.Services
{
    public sealed record SlowStep(string Tool, double Seconds, string What);
    public sealed record ToolStat(string Name, int Count, double TotalSeconds, double MaxSeconds);
    public sealed record TurnStat(double Seconds, long InputTokens, long OutputTokens, long ThinkingTokens, long CachedTokens);

    public sealed record SessionStatsResult(
        int Turns, double TotalSeconds, long InputTokens, long OutputTokens, long ThinkingTokens,
        IReadOnlyList<TurnStat> PerTurn, IReadOnlyList<ToolStat> Tools, IReadOnlyList<SlowStep> SlowSteps, IReadOnlyList<string> Findings);

    /// <summary>
    /// Where the time and tokens of a conversation went, worked out from the events Levity stores: how long each turn took, which tools were
    /// slow, how many separate steps were spent on things that could have been one. The assistant reports its time and tokens as running
    /// totals for the whole conversation, so the per-turn numbers here are the differences.
    /// </summary>
    public static class SessionStats
    {
        public static SessionStatsResult Compute(IEnumerable<string> historyLines)
        {
            var results = new List<(double Seconds, long In, long Out, long Think, long Cache)>();
            var tools = new Dictionary<string, (int Count, double Total, double Max)>(StringComparer.Ordinal);
            var slow = new List<SlowStep>();
            int shellCommands = 0;
            double shellSeconds = 0;

            foreach (string line in historyLines)
            {
                if (!line.StartsWith("{\"type\":\"agy_event\"", StringComparison.Ordinal)) continue;
                JsonNode? payload;
                try { payload = JsonNode.Parse(line)?["payload"]; } catch { continue; }
                string? evt = (string?)payload?["event"];
                if (evt == "result" && payload?["result"] is JsonObject r)
                {
                    var u = r["usage"] as JsonObject;
                    results.Add((Num(r["duration_seconds"]), (long)Num(u?["input_tokens"]), (long)Num(u?["output_tokens"]), (long)Num(u?["thinking_tokens"]), (long)Num(u?["cache_read_tokens"])));
                }
                else if (evt == "step_update" && payload?["step_update"] is JsonObject su && (string?)su["step_type"] == "tool" && (string?)su["state"] == "DONE")
                {
                    string name = (string?)su["tool_name"] ?? (string?)su["tool_info"]?["name"] ?? "tool";
                    double secs = Num(su["duration_seconds"]);
                    tools.TryGetValue(name, out var t);
                    tools[name] = (t.Count + 1, t.Total + secs, Math.Max(t.Max, secs));
                    string what = Describe(su["tool_info"]?["parameters"] as JsonObject);
                    if (name == "run_command") { shellCommands++; shellSeconds += secs; }
                    if (secs >= 8) slow.Add(new SlowStep(name, Math.Round(secs, 1), what));
                }
            }

            // running totals -> per-turn figures
            bool cumulative = results.Count > 1 && Enumerable.Range(1, results.Count - 1).All(i => results[i].Seconds >= results[i - 1].Seconds && results[i].In >= results[i - 1].In);
            var perTurn = new List<TurnStat>();
            for (int i = 0; i < results.Count; i++)
            {
                var cur = results[i];
                var prev = cumulative && i > 0 ? results[i - 1] : (0, 0, 0, 0, 0);
                perTurn.Add(new TurnStat(Math.Round(cur.Seconds - prev.Item1, 1), cur.In - prev.Item2, cur.Out - prev.Item3, cur.Think - prev.Item4, cur.Cache - prev.Item5));
            }

            var toolStats = tools.Select(kv => new ToolStat(kv.Key, kv.Value.Count, Math.Round(kv.Value.Total, 1), Math.Round(kv.Value.Max, 1))).OrderByDescending(t => t.TotalSeconds).ToList();
            var last = results.Count > 0 ? results[^1] : (0, 0L, 0L, 0L, 0L);
            double totalSeconds = cumulative ? last.Item1 : results.Sum(r => r.Seconds);
            long inTok = cumulative ? last.Item2 : results.Sum(r => r.In), outTok = cumulative ? last.Item3 : results.Sum(r => r.Out), thinkTok = cumulative ? last.Item4 : results.Sum(r => r.Think);

            return new SessionStatsResult(results.Count, Math.Round(totalSeconds, 1), inTok, outTok, thinkTok, perTurn, toolStats,
                slow.OrderByDescending(s => s.Seconds).Take(8).ToList(), Findings(perTurn, toolStats, shellCommands, shellSeconds, totalSeconds));
        }

        private static IReadOnlyList<string> Findings(List<TurnStat> perTurn, List<ToolStat> tools, int shellCommands, double shellSeconds, double totalSeconds)
        {
            var list = new List<string>();
            if (shellCommands >= 3 && shellSeconds / shellCommands >= 5)
                list.Add($"{shellCommands} shell commands took {shellSeconds / shellCommands:0.#} seconds each ({shellSeconds:0} s of {totalSeconds:0} s). Every command starts a PowerShell, and a profile that loads a prompt theme or predictions makes that slow. Add \"if ($env:LEVITY_AGENT) {{ return }}\" as the first line of your PowerShell profile (see docs/PERFORMANCE.md).");
            var reads = tools.FirstOrDefault(t => t.Name == "read_file");
            if (reads != null && reads.Count >= 12)
                list.Add($"{reads.Count} files were read one at a time, and each read waits for the model. One command that prints several files (rg -n, or Get-Content a,b,c) is much faster.");
            var searches = tools.FirstOrDefault(t => t.Name == "search_web");
            if (searches != null && searches.Count >= 8) list.Add($"{searches.Count} separate web searches; each one is a full round trip.");
            if (perTurn.Count > 0 && perTurn[0].InputTokens >= 100_000)
                list.Add($"The first message used {perTurn[0].InputTokens:N0} input tokens before any work. Most of it is the assistant's own built-in prompt and tool lists, sent with every turn; the size report below shows whether your own skills and rules add to it.");
            var share = totalSeconds > 0 ? tools.Sum(t => t.TotalSeconds) / totalSeconds : 0;
            if (totalSeconds >= 120 && share < 0.35) list.Add($"Only {share:P0} of the time was spent running tools; the rest was the model thinking between steps. Fewer, bigger steps and the Fast setting help most.");
            return list;
        }

        private static double Num(JsonNode? n)
        {
            if (n == null) return 0;
            try { return double.Parse(n.ToJsonString().Trim('"'), System.Globalization.CultureInfo.InvariantCulture); } catch { return 0; }
        }

        private static string Describe(JsonObject? p)
        {
            if (p == null) return "";
            foreach (string key in new[] { "CommandLine", "Path", "query", "Query", "Url", "TaskId" })
                if ((string?)p[key] is { Length: > 0 } v) return v.Length > 140 ? v[..140] + "..." : v;
            return "";
        }
    }

    /// <summary>A read-only look at how big the assistant's folder is, because everything in it that is loaded is sent with every turn.</summary>
    public static class GeminiFootprint
    {
        public sealed record Folder(string Name, double Megabytes, int Files);
        public sealed record Result(string Path, IReadOnlyList<Folder> Folders, double TotalMegabytes, int TotalFiles, bool Truncated, IReadOnlyList<string> Notes);

        public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");

        /// <summary>Sizes the top-level folders. Bounded in time and file count; never follows links; never changes anything.</summary>
        public static Result Scan(string? path = null, int maxSeconds = 8, int maxFiles = 300_000)
        {
            path ??= DefaultPath;
            var folders = new List<Folder>();
            long totalBytes = 0;
            int totalFiles = 0;
            bool truncated = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (!Directory.Exists(path)) return new Result(path, folders, 0, 0, false, new[] { "No .gemini folder here." });

            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                long bytes = 0;
                int files = 0;
                if (entry is FileInfo f) { bytes = f.Length; files = 1; }
                else
                {
                    var stack = new Stack<string>();
                    stack.Push(entry.FullName);
                    while (stack.Count > 0)
                    {
                        if (sw.Elapsed.TotalSeconds > maxSeconds || totalFiles + files >= maxFiles) { truncated = true; break; }
                        string d = stack.Pop();
                        try
                        {
                            foreach (var e in new DirectoryInfo(d).EnumerateFileSystemInfos())
                            {
                                if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                                if (e is FileInfo fi) { bytes += fi.Length; files++; } else stack.Push(e.FullName);
                            }
                        }
                        catch { }
                    }
                }
                totalBytes += bytes;
                totalFiles += files;
                folders.Add(new Folder(entry.Name, Math.Round(bytes / 1048576.0, 1), files));
            }

            var ordered = folders.OrderByDescending(f => f.Megabytes).ToList();
            var notes = new List<string>();
            foreach (var f in ordered.Where(f => f.Megabytes >= 50 || f.Files >= 5000).Take(4))
            {
                string n = f.Name.ToLowerInvariant();
                if (n.Contains("backup")) notes.Add($"'{f.Name}' ({f.Megabytes:0} MB, {f.Files:N0} files) looks like a backup. If you do not need it, archiving it keeps the assistant's folder smaller.");
                else if (n.Contains("browser")) notes.Add($"'{f.Name}' ({f.Megabytes:0} MB) is a browser profile used by the assistant's browser tools.");
                else if (n == "brain" || n.Contains("antigravity-cli")) notes.Add($"'{f.Name}' holds the assistant's saved conversations and notes ({f.Megabytes:0} MB, {f.Files:N0} files).");
            }
            if (totalFiles > 10_000) notes.Add($"{totalFiles:N0} files in total. Only what the assistant loads (skills, rules, tool lists) costs tokens; the rest is just disk.");
            return new Result(path, ordered, Math.Round(totalBytes / 1048576.0, 1), totalFiles, truncated, notes);
        }
    }
}
