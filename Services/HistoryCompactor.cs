using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Levity.Services
{
    /// <summary>
    /// Keeps the stored conversation small. The assistant streams its answer as hundreds of tiny fragments, each wrapped in about 230
    /// characters of envelope; in stored chats the fragments were about 80% of everything kept. Neighbouring fragments of the same step
    /// are merged into one, which shows exactly the same text when the conversation is replayed. A size budget drops the oldest turns.
    /// </summary>
    public static class HistoryCompactor
    {
        /// <summary>A merged fragment stops growing here (merging re-writes it each time).</summary>
        public const int MaxMergedChars = 8000;

        /// <summary>About 3 MB of stored events (several long conversations) before the oldest turns are dropped.</summary>
        public const int DefaultBudgetChars = 3_000_000;

        private const string AgyEventPrefix = "{\"type\":\"agy_event\"";
        private const string UserMessagePrefix = "{\"type\":\"user_message\"";

        /// <summary>True when the two lines are neighbouring fragments of the same streaming answer; <paramref name="merged"/> is the single replacement line.</summary>
        public static bool TryMerge(string last, string next, out string merged)
        {
            merged = "";
            if (!last.StartsWith(AgyEventPrefix, StringComparison.Ordinal) || !next.StartsWith(AgyEventPrefix, StringComparison.Ordinal)) return false;
            if (!last.Contains("\"agent_response\"", StringComparison.Ordinal) || !next.Contains("\"agent_response\"", StringComparison.Ordinal)) return false;
            if (!last.Contains("\"ACTIVE\"", StringComparison.Ordinal) || !next.Contains("\"ACTIVE\"", StringComparison.Ordinal)) return false;
            try
            {
                var a = JsonNode.Parse(last);
                var b = JsonNode.Parse(next);
                var pa = a?["payload"]; var pb = b?["payload"];
                if ((string?)pa?["event"] != "step_update" || (string?)pb?["event"] != "step_update") return false;
                var sa = pa?["step_update"]; var sb = pb?["step_update"];
                if (sa == null || sb == null) return false;
                if ((string?)sa["step_type"] != "agent_response" || (string?)sb["step_type"] != "agent_response") return false;
                if ((string?)sa["state"] != "ACTIVE" || (string?)sb["state"] != "ACTIVE") return false;
                var ia = sa["step_index"]; var ib = sb["step_index"];
                if (ia == null || ib == null || ia.ToJsonString() != ib.ToJsonString()) return false;
                if (sa["text_delta"] is not JsonValue ta || sb["text_delta"] is not JsonValue tb) return false;
                if (!ta.TryGetValue(out string? textA) || !tb.TryGetValue(out string? textB) || textA == null || textB == null) return false;
                if (textA.Length + textB.Length > MaxMergedChars) return false;
                // Anything else the fragment carries (usage, ids) must be identical, or merging would lose information.
                if (OtherFields(sa) != OtherFields(sb)) return false;
                sa["text_delta"] = textA + textB;
                merged = a!.ToJsonString();
                return true;
            }
            catch { return false; }
        }

        private static string OtherFields(JsonNode step)
        {
            var copy = JsonNode.Parse(step.ToJsonString())!.AsObject();
            copy.Remove("text_delta");
            return copy.ToJsonString();
        }

        /// <summary>Adds a line, merging it into the previous one when they are neighbouring fragments.</summary>
        public static void Add(List<string> history, string json)
        {
            if (history.Count > 0 && TryMerge(history[^1], json, out string merged)) { history[^1] = merged; return; }
            history.Add(json);
        }

        /// <summary>
        /// Drops the oldest turns (up to the next user message) until the stored text fits the budget. The latest turn is never dropped,
        /// so a replay always starts at a user message and never in the middle of an answer.
        /// </summary>
        public static int TrimToBudget(List<string> history, int maxChars = DefaultBudgetChars)
        {
            long total = 0;
            foreach (string line in history) total += line.Length;
            int dropped = 0;
            while (total > maxChars)
            {
                int next = -1;
                for (int i = 1; i < history.Count; i++)
                    if (history[i].StartsWith(UserMessagePrefix, StringComparison.Ordinal)) { next = i; break; }
                if (next < 0) break;   // only one turn left: keep it
                for (int i = 0; i < next; i++) total -= history[i].Length;
                history.RemoveRange(0, next);
                dropped += next;
            }
            return dropped;
        }
    }
}
