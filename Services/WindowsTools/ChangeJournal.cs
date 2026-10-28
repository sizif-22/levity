using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Levity.Services.WindowsTools
{
    public sealed record JournalEntry(string Id, DateTime TimeUtc, string Tool, string Summary, string UndoKind, JsonObject UndoData, bool Reverted, string? RevertedNote);

    /// <summary>
    /// A record of every change the toolbox makes to Windows, written BEFORE the change, with what is needed to put it back (the old
    /// value, the old file). "Revert" runs the matching undo. Entries are plain JSON files kept for 30 days.
    /// </summary>
    public sealed class ChangeJournal
    {
        public const int KeepDays = 30;
        private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

        private readonly string _dir;
        private readonly object _gate = new();
        private readonly Dictionary<string, Func<JsonObject, string?>> _undo = new(StringComparer.Ordinal);

        public ChangeJournal(string directory)
        {
            _dir = directory;
        }

        /// <summary>Tools register how to undo their own kind of change. The handler returns null on success or a message on failure.</summary>
        public void RegisterUndo(string kind, Func<JsonObject, string?> handler) => _undo[kind] = handler;

        public bool CanUndo(string kind) => _undo.ContainsKey(kind);

        /// <summary>Writes the entry to disk and returns it. Call this before changing anything; if it throws, do not change.</summary>
        public JournalEntry Record(string tool, string summary, string undoKind, JsonObject undoData)
        {
            var entry = new JournalEntry(Guid.NewGuid().ToString("N")[..12], DateTime.UtcNow, tool, summary, undoKind, undoData, false, null);
            Directory.CreateDirectory(_dir);
            Save(entry);
            return entry;
        }

        public IReadOnlyList<JournalEntry> List(int max = 100)
        {
            var list = new List<JournalEntry>();
            if (!Directory.Exists(_dir)) return list;
            foreach (string file in Directory.EnumerateFiles(_dir, "*.json"))
            {
                try
                {
                    var e = Load(file);
                    if (e == null) continue;
                    if (e.TimeUtc < DateTime.UtcNow.AddDays(-KeepDays)) { try { File.Delete(file); } catch { } continue; }
                    list.Add(e);
                }
                catch { /* a damaged file is skipped, never fatal */ }
            }
            return list.OrderByDescending(e => e.TimeUtc).Take(max).ToList();
        }

        public JournalEntry? Find(string id) => IsPlainId(id) ? Load(PathFor(id)) : null;

        /// <summary>Puts a change back. Returns null on success, or what went wrong.</summary>
        public string? Revert(string id)
        {
            lock (_gate)
            {
                var entry = Find(id);
                if (entry == null) return "That change is not in the journal.";
                if (entry.Reverted) return "That change was already reverted.";
                if (!_undo.TryGetValue(entry.UndoKind, out var handler)) return $"Levity cannot undo this kind of change ({entry.UndoKind}).";
                string? error;
                try { error = handler(entry.UndoData); }
                catch (Exception ex) { error = ex.Message; }
                if (error != null) return error;
                Save(entry with { Reverted = true, RevertedNote = DateTime.UtcNow.ToString("u") });
                return null;
            }
        }

        // ---- storage ----

        private static bool IsPlainId(string id) => id.Length is > 0 and <= 32 && id.All(c => char.IsAsciiLetterOrDigit(c));

        private string PathFor(string id) => Path.Combine(_dir, id + ".json");

        private void Save(JournalEntry e)
        {
            var node = new JsonObject
            {
                ["id"] = e.Id,
                ["timeUtc"] = e.TimeUtc.ToString("o"),
                ["tool"] = e.Tool,
                ["summary"] = e.Summary,
                ["undoKind"] = e.UndoKind,
                ["undoData"] = JsonNode.Parse(e.UndoData.ToJsonString()),
                ["reverted"] = e.Reverted,
                ["revertedNote"] = e.RevertedNote,
            };
            string path = PathFor(e.Id);
            string temp = path + ".tmp";
            File.WriteAllText(temp, node.ToJsonString(Pretty));
            File.Move(temp, path, overwrite: true);   // never leaves a half-written entry
        }

        private static JournalEntry? Load(string path)
        {
            if (!File.Exists(path)) return null;
            var n = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (n == null) return null;
            return new JournalEntry(
                (string?)n["id"] ?? Path.GetFileNameWithoutExtension(path),
                DateTime.TryParse((string?)n["timeUtc"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.UtcNow,
                (string?)n["tool"] ?? "",
                (string?)n["summary"] ?? "",
                (string?)n["undoKind"] ?? "",
                n["undoData"] as JsonObject ?? new JsonObject(),
                (bool?)n["reverted"] ?? false,
                (string?)n["revertedNote"]);
        }

        public static JsonNode ToJson(JournalEntry e) => new JsonObject
        {
            ["id"] = e.Id,
            ["time"] = e.TimeUtc.ToString("o"),
            ["tool"] = e.Tool,
            ["summary"] = e.Summary,
            ["reverted"] = e.Reverted,
        };
    }
}
