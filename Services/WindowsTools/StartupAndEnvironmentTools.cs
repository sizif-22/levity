using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Levity.Services.WindowsTools
{
    // ====================================================================================
    // Startup apps
    // ====================================================================================

    /// <summary>Where start-up entries live. Overridable so tests can use a throw-away registry key and folders.</summary>
    public sealed class StartupLocations
    {
        public (string Root, string SubKey) RunUser { get; init; } = ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run");
        public (string Root, string SubKey) RunMachine { get; init; } = ("HKLM", @"Software\Microsoft\Windows\CurrentVersion\Run");
        /// <summary>Windows keeps the per-user on/off choice here for BOTH the user's and the machine's entries, so no admin rights are needed to turn one off.</summary>
        public (string Root, string SubKey) ApprovedRun { get; init; } = ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
        public (string Root, string SubKey) ApprovedFolder { get; init; } = ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder");
        public string? UserFolder { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        public string? CommonFolder { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
    }

    internal static class StartupEntries
    {
        public sealed record Entry(string Name, string Command, string Source, bool Enabled);

        public static List<Entry> Read(StartupLocations loc)
        {
            var list = new List<Entry>();
            bool Enabled((string Root, string SubKey) approved, string name)
            {
                using var k = RegistryUndo.OpenRoot(approved.Root, false).OpenSubKey(approved.SubKey, false);
                return !(k?.GetValue(name) is byte[] b && b.Length > 0 && (b[0] & 1) == 1);
            }
            void FromRun((string Root, string SubKey) run, string source)
            {
                using var k = RegistryUndo.OpenRoot(run.Root, false).OpenSubKey(run.SubKey, false);
                if (k == null) return;
                foreach (string name in k.GetValueNames())
                    list.Add(new Entry(name, Convert.ToString(k.GetValue(name)) ?? "", source, Enabled(loc.ApprovedRun, name)));
            }
            void FromFolder(string? folder, string source)
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
                foreach (string f in Directory.EnumerateFiles(folder))
                {
                    string name = Path.GetFileName(f);
                    if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new Entry(name, f, source, Enabled(loc.ApprovedFolder, name)));
                }
            }
            FromRun(loc.RunUser, "run-user");
            FromRun(loc.RunMachine, "run-machine");
            FromFolder(loc.UserFolder, "folder-user");
            FromFolder(loc.CommonFolder, "folder-common");
            return list;
        }
    }

    public sealed class StartupAppsListTool : WindowsTool
    {
        private readonly StartupLocations _loc;
        public StartupAppsListTool(StartupLocations? loc = null) { _loc = loc ?? new StartupLocations(); }
        public override string Name => "startup_apps_list";
        public override string Description => "List the programs that start with Windows (registry Run keys and the Startup folders) and whether each is enabled.";
        public override JsonObject InputSchema => Schema(new JsonObject());

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            var entries = StartupEntries.Read(_loc);
            var arr = new JsonArray(entries.Select(e => (JsonNode)new JsonObject { ["name"] = e.Name, ["source"] = e.Source, ["enabled"] = e.Enabled, ["command"] = e.Command.Length > 260 ? e.Command[..260] + "..." : e.Command }).ToArray());
            return Task.FromResult(ToolResult.Success($"{entries.Count} start-up entries, {entries.Count(e => e.Enabled)} enabled.", arr));
        }
    }

    public sealed class StartupAppsSetTool : WindowsTool
    {
        private readonly StartupLocations _loc;
        public StartupAppsSetTool(StartupLocations? loc = null) { _loc = loc ?? new StartupLocations(); }
        public override string Name => "startup_apps_set";
        public override ToolAccess Access => ToolAccess.Change;
        public override string Description => "Turn a start-up program on or off (what Task Manager's Startup tab does; the program is not uninstalled). name is from startup_apps_list; give source when two entries share a name. Journaled and revertible.";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["name"] = Prop("string", "The entry name from startup_apps_list."),
            ["enabled"] = Prop("boolean", "true to start with Windows, false to stop."),
            ["source"] = Prop("string", "Only needed if the name is ambiguous.", "run-user", "run-machine", "folder-user", "folder-common"),
            ["dry_run"] = Prop("boolean", "Describe the change without making it."),
        }, "name", "enabled");

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string name = RequiredStr(args, "name");
            bool enable = Bool(args, "enabled");
            if (!args.ContainsKey("enabled")) throw new ToolInputException("'enabled' is required.");
            string? source = Str(args, "source");
            var matches = StartupEntries.Read(_loc).Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) && (source == null || e.Source == source)).ToList();
            if (matches.Count == 0) return Task.FromResult(ToolResult.Failure("No start-up entry with that name. Use startup_apps_list."));
            if (matches.Count > 1) return Task.FromResult(ToolResult.Failure("More than one entry has that name; add source: " + string.Join(", ", matches.Select(m => m.Source))));
            var e = matches[0];
            if (e.Enabled == enable) return Task.FromResult(ToolResult.Success($"'{e.Name}' is already {(enable ? "enabled" : "disabled")}. Nothing changed."));

            var approved = e.Source.StartsWith("folder", StringComparison.Ordinal) ? _loc.ApprovedFolder : _loc.ApprovedRun;
            string summary = $"{(enable ? "Enable" : "Disable")} start-up entry '{e.Name}' ({e.Source})";
            if (ctx.DryRun) return Task.FromResult(ToolResult.Success($"Dry run: would {char.ToLowerInvariant(summary[0])}{summary[1..]}. Nothing was changed."));

            var snapshot = RegistryUndo.Snapshot(approved.Root, approved.SubKey, new[] { e.Name });
            var entry = ctx.Journal.Record("startup_apps_set", summary, "registry_values", snapshot);
            var data = new byte[12];
            data[0] = enable ? (byte)2 : (byte)3;
            if (!enable) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            using (var k = RegistryUndo.OpenRoot(approved.Root, true).CreateSubKey(approved.SubKey, true))
                k.SetValue(e.Name, data, RegistryValueKind.Binary);
            int sharing = StartupEntries.Read(_loc).Count(o => string.Equals(o.Name, e.Name, StringComparison.OrdinalIgnoreCase)) - 1;
            string note = sharing > 0 ? $" Note: {sharing} other entr{(sharing == 1 ? "y has" : "ies have")} the same name and share this switch (Windows keeps one on/off choice per name)." : "";
            return Task.FromResult(ToolResult.Success($"{summary}. Takes effect at the next sign-in.{note} Journal id {entry.Id}."));
        }
    }

    // ====================================================================================
    // Environment variables and PATH
    // ====================================================================================

    public interface IEnvironmentStore
    {
        string? Get(string scope, string name);
        IDictionary<string, string> All(string scope);
        void Set(string scope, string name, string? value);
    }

    /// <summary>The real thing. Setting a user variable tells running programs about it, so new terminals see it.</summary>
    public sealed class WindowsEnvironmentStore : IEnvironmentStore
    {
        private static EnvironmentVariableTarget Target(string scope) => scope switch
        {
            "user" => EnvironmentVariableTarget.User,
            "machine" => EnvironmentVariableTarget.Machine,
            "process" => EnvironmentVariableTarget.Process,
            _ => throw new ToolInputException("scope must be user, machine or process."),
        };

        public string? Get(string scope, string name) => Environment.GetEnvironmentVariable(name, Target(scope));

        public IDictionary<string, string> All(string scope)
        {
            var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables(Target(scope))) result[(string)e.Key] = (string?)e.Value ?? "";
            return result;
        }

        public void Set(string scope, string name, string? value) => Environment.SetEnvironmentVariable(name, value, Target(scope));
    }

    internal static class EnvRules
    {
        public static readonly Regex Name = new(@"^[A-Za-z_][A-Za-z0-9_]{0,254}$", RegexOptions.Compiled);
        public const int MaxValue = 32_000;

        public static string Scope(JsonObject args, string fallback, params string[] allowed)
        {
            string? s = args.TryGetPropertyValue("scope", out var n) && n is JsonValue v ? v.ToString() : null;
            if (string.IsNullOrEmpty(s)) return fallback;
            string? match = allowed.FirstOrDefault(a => string.Equals(a, s, StringComparison.OrdinalIgnoreCase));
            return match ?? throw new ToolInputException("scope must be one of: " + string.Join(", ", allowed) + ".");
        }

        public static string NormalizeEntry(string e) => Environment.ExpandEnvironmentVariables(e.Trim()).TrimEnd('\\', '/').ToLowerInvariant();
    }

    public sealed class EnvironmentGetTool : WindowsTool
    {
        private readonly IEnvironmentStore _store;
        public EnvironmentGetTool(IEnvironmentStore? store = null) { _store = store ?? new WindowsEnvironmentStore(); }
        public override string Name => "environment_get";
        public override string Description => "Read environment variables. With name: its value in the chosen scope (user, machine or process). Without name: the names in that scope with shortened values.";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["scope"] = Prop("string", "Where to look (default user).", "user", "machine", "process"),
            ["name"] = Prop("string", "A variable name; leave out to list."),
        });

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string scope = EnvRules.Scope(args, "user", "user", "machine", "process");
            string? name = Str(args, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (!EnvRules.Name.IsMatch(name)) return Task.FromResult(ToolResult.Failure("That is not a valid variable name."));
                string? v = _store.Get(scope, name);
                return Task.FromResult(v == null ? ToolResult.Success($"{name} is not set in the {scope} scope.") : ToolResult.Success($"{name} ({scope}) = {v}"));
            }
            var all = _store.All(scope);
            var obj = new JsonObject();
            foreach (var kv in all) obj[kv.Key] = kv.Value.Length > 160 ? kv.Value[..160] + "..." : kv.Value;
            return Task.FromResult(ToolResult.Success($"{all.Count} variables in the {scope} scope.", obj));
        }
    }

    public sealed class EnvironmentSetTool : WindowsTool
    {
        private readonly IEnvironmentStore _store;
        public EnvironmentSetTool(IEnvironmentStore? store = null) { _store = store ?? new WindowsEnvironmentStore(); }
        public override string Name => "environment_set";
        public override ToolAccess Access => ToolAccess.Change;
        public override bool NeedsAdminToChange => true;
        public override string Description => "Set or remove an environment variable for the user (new terminals see it). Machine-wide changes need administrator rights. To change PATH use path_edit instead. Journaled and revertible.";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["name"] = Prop("string", "Variable name (letters, digits, underscore)."),
            ["value"] = Prop("string", "The new value; leave out together with remove=true to delete it."),
            ["remove"] = Prop("boolean", "Delete the variable."),
            ["scope"] = Prop("string", "user (default) or machine.", "user", "machine"),
            ["dry_run"] = Prop("boolean", "Describe the change without making it."),
        }, "name");

        public static void RegisterUndo(ChangeJournal journal, IEnvironmentStore? store = null)
        {
            var s = store ?? new WindowsEnvironmentStore();
            journal.RegisterUndo("env_var", data =>
            {
                s.Set((string?)data["scope"] ?? "user", (string?)data["name"] ?? "", (bool?)data["existed"] == true ? (string?)data["old"] : null);
                return null;
            });
        }

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string name = RequiredStr(args, "name");
            if (!EnvRules.Name.IsMatch(name)) return Task.FromResult(ToolResult.Failure("That is not a valid variable name."));
            if (name.Equals("PATH", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(ToolResult.Failure("Use path_edit to change PATH: it adds and removes single entries safely."));
            string scope = EnvRules.Scope(args, "user", "user", "machine");
            bool remove = Bool(args, "remove");
            string? value = Str(args, "value");
            if (!remove && value == null) return Task.FromResult(ToolResult.Failure("Give a value, or set remove=true."));
            if (value != null && value.Length > EnvRules.MaxValue) return Task.FromResult(ToolResult.Failure("That value is too long."));
            if (value != null && value.Any(c => c == '\0')) return Task.FromResult(ToolResult.Failure("The value contains an invalid character."));

            string? old = _store.Get(scope, name);
            string summary = remove ? $"Remove the {scope} variable {name}" : $"Set the {scope} variable {name}" + (old != null ? " (changing the existing value)" : "");
            if (ctx.DryRun) return Task.FromResult(ToolResult.Success($"Dry run: would {char.ToLowerInvariant(summary[0])}{summary[1..]}. Nothing was changed."));
            if (scope == "machine") return Task.FromResult(ToolResult.Failure("Machine-wide variables need administrator rights, which Levity does not have yet. Use scope user, or make the change yourself in an administrator window."));

            var entry = ctx.Journal.Record("environment_set", summary, "env_var", new JsonObject { ["scope"] = scope, ["name"] = name, ["existed"] = old != null, ["old"] = old });
            _store.Set(scope, name, remove ? null : value);
            return Task.FromResult(ToolResult.Success($"{summary}. Open terminals keep the old value until restarted. Journal id {entry.Id}."));
        }
    }

    public sealed class PathCheckTool : WindowsTool
    {
        private readonly IEnvironmentStore _store;
        public PathCheckTool(IEnvironmentStore? store = null) { _store = store ?? new WindowsEnvironmentStore(); }
        public override string Name => "path_check";
        public override string Description => "Check the PATH variable (user and machine): entries that do not exist, duplicates, entries that point to a file, and a total length near Windows' limits.";
        public override JsonObject InputSchema => Schema(new JsonObject());

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            var findings = new JsonArray();
            var seen = new Dictionary<string, string>();
            int total = 0;
            foreach (string scope in new[] { "user", "machine" })
            {
                string path = _store.Get(scope, "PATH") ?? "";
                total += path.Length;
                if (path.Length > 2048) findings.Add(new JsonObject { ["scope"] = scope, ["problem"] = "long", ["detail"] = $"{path.Length} characters; some programs cut PATH at 2048." });
                foreach (string raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string entry = raw.Trim().Trim('"');
                    if (entry.Length == 0) continue;
                    string key = EnvRules.NormalizeEntry(entry);
                    string expanded = Environment.ExpandEnvironmentVariables(entry);
                    if (seen.TryGetValue(key, out string? firstScope))
                        findings.Add(new JsonObject { ["scope"] = scope, ["problem"] = "duplicate", ["entry"] = entry, ["detail"] = $"Already in the {firstScope} PATH." });
                    else seen[key] = scope;
                    if (File.Exists(expanded)) findings.Add(new JsonObject { ["scope"] = scope, ["problem"] = "is_a_file", ["entry"] = entry, ["detail"] = "PATH entries must be folders." });
                    else if (!Directory.Exists(expanded)) findings.Add(new JsonObject { ["scope"] = scope, ["problem"] = "missing", ["entry"] = entry, ["detail"] = "The folder does not exist." });
                }
            }
            return Task.FromResult(ToolResult.Success(findings.Count == 0 ? "PATH looks healthy." : $"{findings.Count} problem(s) found in PATH.", findings));
        }
    }

    public sealed class PathEditTool : WindowsTool
    {
        private readonly IEnvironmentStore _store;
        public PathEditTool(IEnvironmentStore? store = null) { _store = store ?? new WindowsEnvironmentStore(); }
        public override string Name => "path_edit";
        public override ToolAccess Access => ToolAccess.Change;
        public override string Description => "Edit the user PATH safely: add one folder, remove one entry, or remove duplicates and entries whose folder no longer exists (action clean). Machine PATH needs administrator rights. Journaled and revertible.";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["action"] = Prop("string", "What to do.", "add", "remove", "clean"),
            ["entry"] = Prop("string", "The folder, for add or remove."),
            ["dry_run"] = Prop("boolean", "Describe the change without making it."),
        }, "action");

        public static void RegisterUndo(ChangeJournal journal, IEnvironmentStore? store = null) => EnvironmentSetTool.RegisterUndo(journal, store);

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string action = OneOf(args, "action", "add", "remove", "clean");
            string current = _store.Get("user", "PATH") ?? "";
            var entries = current.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).Where(e => e.Length > 0).ToList();
            var updated = new List<string>(entries);
            string summary;

            if (action == "clean")
            {
                var seen = new HashSet<string>();
                updated = entries.Where(e => seen.Add(EnvRules.NormalizeEntry(e)) && (Directory.Exists(Environment.ExpandEnvironmentVariables(e.Trim('"'))) || e.Contains('%'))).ToList();
                summary = $"Clean the user PATH: remove {entries.Count - updated.Count} duplicate or missing entr{(entries.Count - updated.Count == 1 ? "y" : "ies")}";
            }
            else
            {
                string entry = RequiredStr(args, "entry").Trim();
                if (entry.Contains(';') || entry.Any(char.IsControl) || entry.Length > 1000) return Task.FromResult(ToolResult.Failure("That is not a valid folder entry."));
                string key = EnvRules.NormalizeEntry(entry);
                if (action == "add")
                {
                    if (entries.Any(e => EnvRules.NormalizeEntry(e) == key)) return Task.FromResult(ToolResult.Success("That folder is already in the user PATH. Nothing changed."));
                    updated.Add(entry);
                    summary = $"Add {entry} to the user PATH" + (Directory.Exists(Environment.ExpandEnvironmentVariables(entry)) ? "" : " (note: that folder does not exist yet)");
                }
                else
                {
                    updated = entries.Where(e => EnvRules.NormalizeEntry(e) != key).ToList();
                    if (updated.Count == entries.Count) return Task.FromResult(ToolResult.Success("That folder is not in the user PATH. Nothing changed."));
                    summary = $"Remove {entry} from the user PATH";
                }
            }

            if (updated.Count == entries.Count && action == "clean") return Task.FromResult(ToolResult.Success("The user PATH has nothing to clean."));
            string joined = string.Join(';', updated);
            if (joined.Length > EnvRules.MaxValue) return Task.FromResult(ToolResult.Failure("The new PATH would be too long."));
            if (ctx.DryRun) return Task.FromResult(ToolResult.Success($"Dry run: would {char.ToLowerInvariant(summary[0])}{summary[1..]}. Nothing was changed."));

            var journalEntry = ctx.Journal.Record("path_edit", summary, "env_var", new JsonObject { ["scope"] = "user", ["name"] = "PATH", ["existed"] = _store.Get("user", "PATH") != null, ["old"] = current });
            _store.Set("user", "PATH", joined);
            return Task.FromResult(ToolResult.Success($"{summary}. Open terminals keep the old PATH until restarted. Journal id {journalEntry.Id}."));
        }
    }
}
