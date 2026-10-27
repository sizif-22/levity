using System;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AntigravitySidebar.Services
{
    /// <summary>What Fast Learner is doing right now, worked out from the agent's tool calls.</summary>
    /// <param name="Phase">running | saving | done</param>
    /// <param name="Kind">run | skill | docs | notes</param>
    /// <param name="Detail">Short description to show next to the label.</param>
    /// <param name="Path">File involved, when known.</param>
    /// <param name="Source">announce (the agent said so) | path (a skill folder was written to)</param>
    public sealed record FastLearnerNotice(string Phase, string Kind, string Detail, string? Path, string Source)
    {
        /// <summary>One sentence for toasts and Windows notifications.</summary>
        public string Text => (Phase, Kind) switch
        {
            ("running", _) => $"Fast Learner is running: {Detail}",
            ("done", _) => $"Fast Learner finished: {Detail}",
            (_, "skill") => $"Fast Learner is saving a skill: {Detail}",
            (_, "docs") => $"Fast Learner is updating docs: {Detail}",
            (_, "notes") => $"Fast Learner is taking a note for later: {Detail}",
            _ => $"Fast Learner: {Detail}",
        };
    }

    /// <summary>
    /// Spots Fast Learner in agy's event stream without asking the agent to format anything special:
    /// (1) it runs fast-learner's announce.ps1, or (2) it writes into a skills folder.
    /// Only the moment a tool call starts (state ACTIVE) is considered, so each call is reported once.
    /// </summary>
    public static class FastLearnerDetector
    {
        private static readonly Regex AnnounceScript = new(@"announce\.ps1", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SkillPath = new(@"[\\/]\.(?:gemini[\\/]config|agents?)[\\/]skills[\\/]([^\\/]+)[\\/]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly string[] WriteTools = { "write_file", "replace_file_content", "multi_replace_file_content", "sed_file" };

        public static FastLearnerNotice? Inspect(string eventJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(eventJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("event", out var ev) || ev.GetString() != "step_update") return null;
                if (!root.TryGetProperty("step_update", out var su)) return null;
                if (Str(su, "step_type") != "tool" || Str(su, "state") != "ACTIVE") return null;

                string tool = Str(su, "tool_name");
                if (!su.TryGetProperty("tool_info", out var info) || !info.TryGetProperty("parameters", out var p) || p.ValueKind != JsonValueKind.Object) return null;

                if (tool == "run_command")
                {
                    string cmd = Str(p, "CommandLine");
                    return AnnounceScript.IsMatch(cmd) ? FromAnnounce(cmd) : null;
                }

                if (Array.IndexOf(WriteTools, tool) >= 0)
                {
                    string path = FirstString(p, "Path", "TargetFile", "AbsolutePath", "File");
                    var m = SkillPath.Match(path);
                    if (m.Success) return new FastLearnerNotice("saving", "skill", m.Groups[1].Value, path, "path");
                }
            }
            catch (JsonException) { }
            return null;
        }

        /// <summary>Reads -Kind, -Message, -Name and -Path out of the announce.ps1 command line.</summary>
        public static FastLearnerNotice? FromAnnounce(string commandLine)
        {
            string kind = Param(commandLine, "Kind").ToLowerInvariant();
            string message = Param(commandLine, "Message");
            string name = Param(commandLine, "Name");
            string path = Param(commandLine, "Path");

            return kind switch
            {
                "start" => new FastLearnerNotice("running", "run", Or(message, "learning from this task"), null, "announce"),
                "skill" => new FastLearnerNotice("saving", "skill", Or(name, Or(message, "a new skill")), Or(path, null), "announce"),
                "docs" => new FastLearnerNotice("saving", "docs", Or(message, Or(path, "project docs")), Or(path, null), "announce"),
                "notes" => new FastLearnerNotice("saving", "notes", Or(message, "a note"), null, "announce"),
                "done" => new FastLearnerNotice("done", "run", Or(message, "finished"), null, "announce"),
                _ => null,
            };
        }

        /// <summary>Value of "-Name value", "-Name 'value'" or -Name "value" (case-insensitive, quotes removed).</summary>
        public static string Param(string commandLine, string name)
        {
            var m = Regex.Match(commandLine, @"-" + Regex.Escape(name) + @"\s+(?:""((?:[^""\\]|\\.)*)""|'((?:[^']|'')*)'|(\S+))", RegexOptions.IgnoreCase);
            if (!m.Success) return "";
            if (m.Groups[1].Success) return m.Groups[1].Value.Replace("\\\"", "\"");
            if (m.Groups[2].Success) return m.Groups[2].Value.Replace("''", "'");
            return m.Groups[3].Value;
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static string FirstString(JsonElement e, params string[] names)
        {
            foreach (string n in names)
            {
                string s = Str(e, n);
                if (s.Length > 0) return s;
            }
            return "";
        }

        private static string Or(string? value, string? fallback) => string.IsNullOrWhiteSpace(value) ? fallback! : value.Trim();
    }
}
