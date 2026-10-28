using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Levity.Services.WindowsTools
{
    public enum ToolAccess
    {
        /// <summary>Only looks. Safe to run any time.</summary>
        ReadOnly,
        /// <summary>Changes something on the PC. Every change is written to the change journal first, so it can be undone.</summary>
        Change,
    }

    public sealed record ToolResult(bool Ok, string Text, JsonNode? Data = null)
    {
        public static ToolResult Success(string text, JsonNode? data = null) => new(true, text, data);
        public static ToolResult Failure(string text) => new(false, text);
    }

    /// <summary>What a tool gets when it runs.</summary>
    public sealed class ToolContext
    {
        public ChangeJournal Journal { get; init; } = null!;
        /// <summary>Describe what would change, change nothing and write nothing to the journal.</summary>
        public bool DryRun { get; init; }
        public CancellationToken Cancel { get; init; }
        /// <summary>Running with administrator rights (the elevated copy of Levity that runs one approved tool).</summary>
        public bool Elevated { get; init; } = Elevation.IsElevated;
    }

    public static class Elevation
    {
        public static bool IsElevated
        {
            get
            {
                try
                {
                    using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                    return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }
        }
    }

    /// <summary>
    /// One typed action the assistant can take on Windows. Parameters are named and checked; no tool takes a command line, so nothing the
    /// assistant (or text it was shown) writes can turn into a different command.
    /// </summary>
    public abstract class WindowsTool
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        /// <summary>JSON Schema of the arguments.</summary>
        public abstract JsonObject InputSchema { get; }
        public virtual ToolAccess Access => ToolAccess.ReadOnly;
        /// <summary>True when a change needs administrator rights; until the elevated helper exists such a change reports what it would do.</summary>
        public virtual bool NeedsAdminToChange => false;
        /// <summary>How long one call may run before it is cancelled (the default fits quick tools; installs get longer).</summary>
        public virtual int TimeoutSeconds => 60;
        public abstract Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx);

        // ---- helpers for reading arguments ----

        protected static string? Str(JsonObject args, string name)
        {
            if (!args.TryGetPropertyValue(name, out var node) || node is not JsonValue v) return null;
            return v.TryGetValue(out string? s) ? s : v.ToString();
        }

        protected static string RequiredStr(JsonObject args, string name)
        {
            string? s = Str(args, name);
            if (string.IsNullOrWhiteSpace(s)) throw new ToolInputException($"'{name}' is required.");
            return s;
        }

        protected static int Int(JsonObject args, string name, int fallback, int min, int max)
        {
            if (!args.TryGetPropertyValue(name, out var node) || node is not JsonValue v) return fallback;
            if (!v.TryGetValue(out int n)) { if (v.TryGetValue(out double d)) n = (int)d; else if (int.TryParse(v.ToString(), out int p)) n = p; else throw new ToolInputException($"'{name}' must be a number."); }
            if (n < min || n > max) throw new ToolInputException($"'{name}' must be between {min} and {max}.");
            return n;
        }

        /// <summary>A number that must be given (no silent default), within the range.</summary>
        protected static int RequiredInt(JsonObject args, string name, int min, int max)
        {
            if (!args.TryGetPropertyValue(name, out var node) || node == null) throw new ToolInputException($"'{name}' is required.");
            return Int(args, name, min, min, max);
        }

        protected static bool Bool(JsonObject args, string name, bool fallback = false)
        {
            if (!args.TryGetPropertyValue(name, out var node) || node is not JsonValue v) return fallback;
            return v.TryGetValue(out bool b) ? b : bool.TryParse(v.ToString(), out bool p) ? p : fallback;
        }

        protected static string OneOf(JsonObject args, string name, params string[] allowed)
        {
            string value = RequiredStr(args, name);
            string? match = allowed.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));
            if (match == null) throw new ToolInputException($"'{name}' must be one of: {string.Join(", ", allowed)}.");
            return match;
        }

        protected static JsonObject Schema(JsonObject properties, params string[] required)
        {
            var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
            if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
            return schema;
        }

        protected static JsonObject Prop(string type, string description, params string[] enumValues)
        {
            var p = new JsonObject { ["type"] = type, ["description"] = description };
            if (enumValues.Length > 0) p["enum"] = new JsonArray(enumValues.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
            return p;
        }
    }

    /// <summary>The caller asked for something invalid. The message is shown to the assistant so it can correct itself.</summary>
    public sealed class ToolInputException : Exception
    {
        public ToolInputException(string message) : base(message) { }
    }

    public sealed class ToolRegistry
    {
        private readonly Dictionary<string, WindowsTool> _tools = new(StringComparer.Ordinal);

        public void Add(WindowsTool tool)
        {
            if (_tools.ContainsKey(tool.Name)) throw new InvalidOperationException($"Duplicate tool name {tool.Name}");
            _tools[tool.Name] = tool;
        }

        public IReadOnlyCollection<WindowsTool> All => _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

        public WindowsTool? Find(string name) => _tools.TryGetValue(name, out var t) ? t : null;

        /// <summary>The MCP "tools/list" entries.</summary>
        public JsonArray Definitions()
        {
            var arr = new JsonArray();
            foreach (var t in All)
            {
                bool readOnly = t.Access == ToolAccess.ReadOnly;
                arr.Add(new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description + (t.NeedsAdminToChange ? " (Changing needs administrator rights; without them this only reports what it would do.)" : ""),
                    ["inputSchema"] = JsonNode.Parse(t.InputSchema.ToJsonString()),
                    ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly, ["destructiveHint"] = false },
                });
            }
            return arr;
        }

        public async Task<ToolResult> CallAsync(string name, JsonObject args, ToolContext ctx)
        {
            var tool = Find(name);
            if (tool == null) return ToolResult.Failure($"Unknown tool '{name}'.");
            // "dry_run" in the arguments always means a preview, however the tool was reached: a change is never made when it is set.
            if (!ctx.DryRun && args.TryGetPropertyValue("dry_run", out var dry) && dry is JsonValue dv && dv.TryGetValue(out bool wantsDry) && wantsDry)
                ctx = new ToolContext { Journal = ctx.Journal, DryRun = true, Cancel = ctx.Cancel, Elevated = ctx.Elevated };
            try { return await tool.RunAsync(args, ctx); }
            catch (ToolInputException ex) { return ToolResult.Failure(ex.Message); }
            catch (OperationCanceledException) { return ToolResult.Failure("Cancelled."); }
            catch (Exception ex) { return ToolResult.Failure($"{tool.Name} failed: {ex.Message}"); }
        }
    }
}
