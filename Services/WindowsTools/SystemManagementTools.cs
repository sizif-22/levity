using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.ServiceProcess;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Levity.Services.WindowsTools
{
    // ====================================================================================
    // Services
    // ====================================================================================

    public sealed class ServicesListTool : WindowsTool
    {
        public override string Name => "services_list";
        public override string Description => "List Windows services with their state (running, stopped...) and start type (automatic, manual, disabled). Optional filter by part of the name or display name, or by state.";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["filter"] = Prop("string", "Part of the service name or display name."),
            ["state"] = Prop("string", "Only services in this state.", "running", "stopped", "all"),
        });

        internal static string StartType(string name)
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
            int start = k?.GetValue("Start") is int s ? s : -1;
            bool delayed = k?.GetValue("DelayedAutostart") is int d && d == 1;
            return start switch { 0 => "boot", 1 => "system", 2 => delayed ? "automatic (delayed)" : "automatic", 3 => "manual", 4 => "disabled", _ => "unknown" };
        }

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string? filter = Str(args, "filter");
            string state = Str(args, "state") is { Length: > 0 } ? OneOf(args, "state", "running", "stopped", "all") : "all";
            var arr = new JsonArray();
            foreach (var s in ServiceController.GetServices().OrderBy(s => s.ServiceName, StringComparer.OrdinalIgnoreCase))
            {
                using (s)
                {
                    if (filter != null && s.ServiceName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 && s.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string st = s.Status.ToString().ToLowerInvariant();
                    if (state != "all" && st != state) continue;
                    arr.Add(new JsonObject { ["name"] = s.ServiceName, ["display_name"] = s.DisplayName, ["state"] = st, ["start_type"] = StartType(s.ServiceName) });
                    if (arr.Count >= 300) break;
                }
            }
            return Task.FromResult(ToolResult.Success($"{arr.Count} services.", arr));
        }
    }

    public sealed class ServiceControlTool : WindowsTool
    {
        private static readonly Regex NameRule = new(@"^[A-Za-z0-9_.$\- ]{1,256}$", RegexOptions.Compiled);
        private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
        {
            "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "lsass", "WinDefend", "EventLog", "Winmgmt", "BFE", "mpssvc", "CryptSvc", "TrustedInstaller", "Schedule", "gpsvc", "ProfSvc", "UserManager", "Dhcp", "Dnscache", "nsi",
        };

        public override string Name => "service_control";
        public override ToolAccess Access => ToolAccess.Change;
        public override bool NeedsAdminToChange => true;
        public override string Description => "Start, stop or restart a Windows service by name, or set how it starts (automatic, manual, disabled). Core services Windows depends on are refused. Needs administrator rights (Levity asks you to approve on the PC). Journaled and revertible.";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["name"] = Prop("string", "The service name from services_list (not the display name)."),
            ["action"] = Prop("string", "What to do.", "start", "stop", "restart", "set_start_type"),
            ["start_type"] = Prop("string", "For set_start_type.", "automatic", "manual", "disabled"),
            ["dry_run"] = Prop("boolean", "Describe the change without making it."),
        }, "name", "action");

        public static void RegisterUndo(ChangeJournal journal)
        {
            journal.RegisterUndo("service_state", data =>
            {
                string name = (string?)data["name"] ?? "";
                using var sc = new ServiceController(name);
                bool wasRunning = (bool?)data["wasRunning"] ?? false;
                if (wasRunning && sc.Status == ServiceControllerStatus.Stopped) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); }
                else if (!wasRunning && sc.Status == ServiceControllerStatus.Running) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
                return null;
            });
            journal.RegisterUndo("service_start_type", data =>
            {
                string name = (string?)data["name"] ?? "";
                int old = (int?)data["old"] ?? 3;
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name, true);
                if (k == null) return "The service is gone.";
                k.SetValue("Start", old, RegistryValueKind.DWord);
                return null;
            });
        }

        public override async Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string name = RequiredStr(args, "name").Trim();
            if (!NameRule.IsMatch(name)) return ToolResult.Failure("That is not a valid service name.");
            string action = OneOf(args, "action", "start", "stop", "restart", "set_start_type");
            if (Protected.Contains(name)) return ToolResult.Failure($"'{name}' is a core Windows service and will not be changed.");

            ServiceController sc;
            try { sc = new ServiceController(name); _ = sc.Status; }
            catch { return ToolResult.Failure("No service with that name. Use services_list."); }
            using (sc)
            {
                bool running = sc.Status == ServiceControllerStatus.Running;
                string summary;
                int newStart = -1;
                if (action == "set_start_type")
                {
                    string st = OneOf(args, "start_type", "automatic", "manual", "disabled");
                    newStart = st switch { "automatic" => 2, "manual" => 3, _ => 4 };
                    summary = $"Set service '{name}' to start {st} (was {ServicesListTool.StartType(name)})";
                }
                else summary = $"{char.ToUpperInvariant(action[0])}{action[1..]} service '{name}' (currently {sc.Status.ToString().ToLowerInvariant()})";

                if (ctx.DryRun) return ToolResult.Success($"Dry run: would {char.ToLowerInvariant(summary[0])}{summary[1..]}. Nothing was changed.");
                if (!ctx.Elevated) return ToolResult.Failure($"Changing a service needs administrator rights. Ask Levity to run this elevated (you approve a Windows prompt on the PC). Plan: {summary}.");

                if (newStart >= 0)
                {
                    int old = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name)?.GetValue("Start") is int o ? o : 3;
                    var entry = ctx.Journal.Record("service_control", summary, "service_start_type", new JsonObject { ["name"] = name, ["old"] = old });
                    try
                    {
                        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name, true) ?? throw new InvalidOperationException("The service key is missing.");
                        k.SetValue("Start", newStart, RegistryValueKind.DWord);
                    }
                    catch (Exception ex) { ctx.Journal.Cancel(entry.Id); return ToolResult.Failure("Could not change it: " + ex.Message); }
                    return ToolResult.Success($"{summary}. Journal id {entry.Id}.");
                }

                var e2 = ctx.Journal.Record("service_control", summary, "service_state", new JsonObject { ["name"] = name, ["wasRunning"] = running });
                try
                {
                    if (action is "stop" or "restart" && sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30))); }
                    if (action is "start" or "restart") { sc.Refresh(); if (sc.Status != ServiceControllerStatus.Running) { sc.Start(); await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30))); } }
                }
                catch (Exception ex) { ctx.Journal.Cancel(e2.Id); return ToolResult.Failure("Could not change it: " + ex.Message); }
                return ToolResult.Success($"{summary}. Journal id {e2.Id}.");
            }
        }
    }

    // ====================================================================================
    // Scheduled tasks (through the Task Scheduler's own COM interface: language-neutral, no process)
    // ====================================================================================

    internal static class TaskScheduler
    {
        public sealed record TaskInfo(string Path, string Name, bool Enabled, string State, DateTime? LastRun, int LastResult, DateTime? NextRun);

        private static readonly string[] States = { "unknown", "disabled", "queued", "ready", "running" };

        public static List<TaskInfo> List(string? filter, int max = 300)
        {
            var result = new List<TaskInfo>();
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) return result;
            dynamic svc = Activator.CreateInstance(type)!;
            try
            {
                svc.Connect();
                Walk(svc.GetFolder("\\"), filter, result, max);
            }
            finally { try { System.Runtime.InteropServices.Marshal.ReleaseComObject(svc); } catch { } }
            return result;
        }

        private static void Walk(dynamic folder, string? filter, List<TaskInfo> into, int max)
        {
            if (into.Count >= max) return;
            try
            {
                foreach (dynamic t in folder.GetTasks(1))   // 1 = include hidden
                {
                    string path = (string)t.Path;
                    if (filter != null && path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    int state = (int)t.State;
                    DateTime? Date(object d) => d is DateTime dt && dt.Year > 1999 ? dt : null;
                    into.Add(new TaskInfo(path, (string)t.Name, (bool)t.Enabled, state is >= 0 and < 5 ? States[state] : "unknown", Date(t.LastRunTime), (int)t.LastTaskResult, Date(t.NextRunTime)));
                    if (into.Count >= max) return;
                }
                foreach (dynamic sub in folder.GetFolders(0)) Walk(sub, filter, into, max);
            }
            catch { /* a folder we may not read is skipped */ }
        }

        public static dynamic? Open(string path)
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) return null;
            dynamic svc = Activator.CreateInstance(type)!;
            svc.Connect();
            int slash = path.LastIndexOf('\\');
            dynamic folder = svc.GetFolder(slash <= 0 ? "\\" : path[..slash]);
            return folder.GetTask(path[(slash + 1)..]);
        }
    }

    public sealed class ScheduledTasksListTool : WindowsTool
    {
        public override string Name => "scheduled_tasks_list";
        public override string Description => "List scheduled tasks with their state, last run and result, and next run. Optional filter on the task path (for example 'Microsoft' or a vendor name).";
        public override JsonObject InputSchema => Schema(new JsonObject { ["filter"] = Prop("string", "Part of the task path.") });

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            var tasks = TaskScheduler.List(Str(args, "filter"));
            var arr = new JsonArray(tasks.Select(t => (JsonNode)new JsonObject
            {
                ["path"] = t.Path, ["enabled"] = t.Enabled, ["state"] = t.State,
                ["last_run"] = t.LastRun?.ToString("s"), ["last_result"] = t.LastResult == 0 ? "ok" : "0x" + t.LastResult.ToString("X"), ["next_run"] = t.NextRun?.ToString("s"),
            }).ToArray());
            return Task.FromResult(ToolResult.Success($"{tasks.Count} scheduled tasks.", arr));
        }
    }

    public sealed class ScheduledTaskControlTool : WindowsTool
    {
        private static readonly Regex PathRule = new(@"^\\[A-Za-z0-9_.()\- \\{}]{1,300}$", RegexOptions.Compiled);

        public override string Name => "scheduled_task_control";
        public override ToolAccess Access => ToolAccess.Change;
        public override bool NeedsAdminToChange => true;
        public override string Description => "Enable, disable or run a scheduled task now. path is the full task path from scheduled_tasks_list. Most system tasks need administrator rights. Enable and disable are journaled and revertible; running a task cannot be undone.";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["path"] = Prop("string", "The full task path, such as \\Vendor\\Updater."),
            ["action"] = Prop("string", "What to do.", "enable", "disable", "run"),
            ["dry_run"] = Prop("boolean", "Describe the change without making it."),
        }, "path", "action");

        public static void RegisterUndo(ChangeJournal journal) =>
            journal.RegisterUndo("task_state", data =>
            {
                dynamic? t = TaskScheduler.Open((string?)data["path"] ?? "");
                if (t == null) return "The task is gone.";
                t.Enabled = (bool?)data["wasEnabled"] ?? true;
                return null;
            });

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string path = RequiredStr(args, "path").Trim();
            if (!PathRule.IsMatch(path) || path.Contains("..")) return Task.FromResult(ToolResult.Failure("That is not a valid task path."));
            string action = OneOf(args, "action", "enable", "disable", "run");
            dynamic? task;
            try { task = TaskScheduler.Open(path); }
            catch { return Task.FromResult(ToolResult.Failure("No scheduled task with that path. Use scheduled_tasks_list.")); }
            if (task == null) return Task.FromResult(ToolResult.Failure("The Task Scheduler is not available."));
            bool wasEnabled = (bool)task.Enabled;
            string summary = action switch { "enable" => $"Enable scheduled task {path}", "disable" => $"Disable scheduled task {path}", _ => $"Run scheduled task {path} now" };
            if ((action == "enable" && wasEnabled) || (action == "disable" && !wasEnabled)) return Task.FromResult(ToolResult.Success($"The task is already {(wasEnabled ? "enabled" : "disabled")}. Nothing changed."));
            if (ctx.DryRun) return Task.FromResult(ToolResult.Success($"Dry run: would {char.ToLowerInvariant(summary[0])}{summary[1..]}. Nothing was changed."));

            var entry = ctx.Journal.Record("scheduled_task_control", summary + (action == "run" ? " (cannot be undone)" : ""), action == "run" ? "none" : "task_state", new JsonObject { ["path"] = path, ["wasEnabled"] = wasEnabled });
            try
            {
                if (action == "run") task.Run(null); else task.Enabled = action == "enable";
            }
            catch (Exception ex)
            {
                ctx.Journal.Cancel(entry.Id);
                return Task.FromResult(ToolResult.Failure(ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase) || ex.HResult == unchecked((int)0x80070005)
                    ? "That task needs administrator rights. Ask Levity to run this elevated (you approve a Windows prompt on the PC)."
                    : "Could not change it: " + ex.Message));
            }
            return Task.FromResult(ToolResult.Success($"{summary}. Journal id {entry.Id}."));
        }
    }

    // ====================================================================================
    // winget
    // ====================================================================================

    internal static class Winget
    {
        public static readonly Regex Id = new(@"^[A-Za-z0-9][A-Za-z0-9.\-+_]{1,100}$", RegexOptions.Compiled);
        public static readonly string[] Common = { "--accept-source-agreements", "--disable-interactivity" };

        public static string? Path => MachineProfile.FindOnPath("winget");

        public static Task<Proc.Output> RunAsync(IEnumerable<string> args, int timeoutSeconds, System.Threading.CancellationToken ct)
        {
            string? exe = Path;
            if (exe == null) return Task.FromResult(new Proc.Output(-1, "winget is not installed on this PC.", false));
            return Proc.RunAsync(exe, args, timeoutSeconds, ct);
        }
    }

    public sealed class WingetQueryTool : WindowsTool
    {
        public override string Name => "winget_query";
        public override string Description => "Ask winget (the Windows package manager): action=installed (apps winget knows), outdated (apps with updates available) or search (needs query). Text output from winget, shortened.";
        public override int TimeoutSeconds => 120;
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["action"] = Prop("string", "What to ask.", "installed", "outdated", "search"),
            ["query"] = Prop("string", "What to search for."),
        }, "action");

        public override async Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string action = OneOf(args, "action", "installed", "outdated", "search");
            var argv = new List<string>();
            switch (action)
            {
                case "installed": argv.AddRange(new[] { "list" }); break;
                case "outdated": argv.AddRange(new[] { "upgrade" }); break;
                default:
                    string q = RequiredStr(args, "query").Trim();
                    if (q.Length > 80 || q.StartsWith('-') || q.Any(char.IsControl)) throw new ToolInputException("That is not a valid search text.");
                    argv.AddRange(new[] { "search", "--query", q });
                    break;
            }
            argv.AddRange(Winget.Common);
            var r = await Winget.RunAsync(argv, 100, ctx.Cancel);
            if (r.TimedOut) return ToolResult.Failure("winget took too long.");
            string text = Regex.Replace(r.Text, @"[─-▟\\|/\-]{1}\s*(?=\r?\n)", "").Trim();
            if (text.Length > 12000) text = text[..12000] + "\n... (cut)";
            return r.ExitCode == 0 || text.Length > 0 ? ToolResult.Success($"winget {argv[0]} finished.", JsonValue.Create(text)) : ToolResult.Failure("winget failed.");
        }
    }

    public sealed class WingetChangeTool : WindowsTool
    {
        public override string Name => "winget_change";
        public override ToolAccess Access => ToolAccess.Change;
        public override int TimeoutSeconds => 600;
        public override string Description => "Install, upgrade or uninstall one app with winget, by its exact package id from winget_query search (for example Git.Git). Some installers show their own Windows prompt on the PC. Install and uninstall are journaled and revertible (the opposite action).";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["action"] = Prop("string", "What to do.", "install", "upgrade", "uninstall"),
            ["id"] = Prop("string", "The exact package id."),
            ["dry_run"] = Prop("boolean", "Describe the change without making it."),
        }, "action", "id");

        public static void RegisterUndo(ChangeJournal journal) =>
            journal.RegisterUndo("winget", data =>
            {
                string id = (string?)data["id"] ?? "";
                if (!Winget.Id.IsMatch(id)) return "Not a valid package id.";
                string undo = (string?)data["undo"] ?? "";
                var argv = new List<string> { undo, "--id", id, "--exact", "--silent" };
                argv.AddRange(Winget.Common);
                if (undo == "install") argv.Add("--accept-package-agreements");
                var r = Winget.RunAsync(argv, 580, default).GetAwaiter().GetResult();
                return r.ExitCode == 0 ? null : "winget said: " + r.Text[..Math.Min(300, r.Text.Length)];
            });

        public override async Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string action = OneOf(args, "action", "install", "upgrade", "uninstall");
            string id = RequiredStr(args, "id").Trim();
            if (!Winget.Id.IsMatch(id)) return ToolResult.Failure("That is not a valid package id (letters, digits, dot, dash).");
            if (Winget.Path == null) return ToolResult.Failure("winget is not installed on this PC.");
            string summary = $"{char.ToUpperInvariant(action[0])}{action[1..]} {id} with winget";
            if (ctx.DryRun) return ToolResult.Success($"Dry run: would {char.ToLowerInvariant(summary[0])}{summary[1..]}. Nothing was changed.");

            string kind = action == "upgrade" ? "none" : "winget";
            var entry = ctx.Journal.Record("winget_change", summary + (action == "upgrade" ? " (an upgrade cannot be undone)" : ""), kind, new JsonObject { ["id"] = id, ["undo"] = action == "install" ? "uninstall" : "install" });
            var argv = new List<string> { action, "--id", id, "--exact", "--silent" };
            argv.AddRange(Winget.Common);
            if (action != "uninstall") argv.Add("--accept-package-agreements");
            var r = await Winget.RunAsync(argv, 570, ctx.Cancel);
            if (r.ExitCode != 0)
            {
                ctx.Journal.Cancel(entry.Id);
                return ToolResult.Failure($"winget could not {action} {id}: " + (r.TimedOut ? "it took too long." : r.Text[..Math.Min(400, r.Text.Length)]));
            }
            return ToolResult.Success($"{summary}. Journal id {entry.Id}.");
        }
    }

    // ====================================================================================
    // Hosts file
    // ====================================================================================

    public sealed class HostsFileTool : WindowsTool
    {
        private static readonly Regex HostName = new(@"^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$", RegexOptions.Compiled);
        private readonly string _path;

        public HostsFileTool(string? path = null) { _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts"); }

        public override string Name => "hosts_file";
        public override ToolAccess Access => ToolAccess.Change;
        public override bool NeedsAdminToChange => true;
        public override string Description => "Read the hosts file (action=list), or add or remove one name-to-address entry (action=add or remove). Changing it needs administrator rights (Levity asks you to approve on the PC). The old file is kept in the journal and revert restores it exactly.";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["action"] = Prop("string", "What to do.", "list", "add", "remove"),
            ["address"] = Prop("string", "IP address, for add."),
            ["host"] = Prop("string", "Host name, for add or remove."),
            ["dry_run"] = Prop("boolean", "Describe the change without making it."),
        }, "action");

        public static void RegisterUndo(ChangeJournal journal) =>
            journal.RegisterUndo("file_restore", data =>
            {
                string path = (string?)data["path"] ?? "", backup = (string?)data["backup"] ?? "";
                if (!File.Exists(backup)) return "The saved copy of the file is gone.";
                File.Copy(backup, path, true);
                return null;
            });

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string action = OneOf(args, "action", "list", "add", "remove");
            string text = File.Exists(_path) ? File.ReadAllText(_path) : "";
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            if (action == "list")
            {
                var entries = new JsonArray();
                foreach (string line in lines)
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t.StartsWith('#')) continue;
                    var parts = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) entries.Add(new JsonObject { ["address"] = parts[0], ["hosts"] = string.Join(' ', parts.Skip(1).TakeWhile(p => !p.StartsWith('#'))) });
                }
                return Task.FromResult(ToolResult.Success($"{entries.Count} entries in the hosts file.", entries));
            }

            string host = RequiredStr(args, "host").Trim();
            if (!HostName.IsMatch(host)) return Task.FromResult(ToolResult.Failure("That is not a valid host name."));
            string summary;
            if (action == "add")
            {
                string address = RequiredStr(args, "address").Trim();
                if (!IPAddress.TryParse(address, out var ip)) return Task.FromResult(ToolResult.Failure("That is not a valid IP address."));
                if (lines.Any(l => IsEntryFor(l, host))) return Task.FromResult(ToolResult.Failure($"{host} already has an entry. Remove it first."));
                while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
                lines.Add($"{ip}\t{host}");
                lines.Add("");
                summary = $"Add hosts entry {ip} {host}";
            }
            else
            {
                int before = lines.Count;
                lines.RemoveAll(l => IsEntryFor(l, host));
                if (lines.Count == before) return Task.FromResult(ToolResult.Success($"{host} has no entry in the hosts file. Nothing changed."));
                summary = $"Remove the hosts entry for {host}";
            }

            if (ctx.DryRun) return Task.FromResult(ToolResult.Success($"Dry run: would {char.ToLowerInvariant(summary[0])}{summary[1..]}. Nothing was changed."));
            if (!ctx.Elevated && !CanWrite(_path)) return Task.FromResult(ToolResult.Failure($"Changing the hosts file needs administrator rights. Ask Levity to run this elevated (you approve a Windows prompt on the PC). Plan: {summary}."));

            string backupDir = Path.Combine(Path.GetTempPath(), "levity-hosts-backups");
            Directory.CreateDirectory(backupDir);
            string backup = Path.Combine(backupDir, Guid.NewGuid().ToString("N") + ".hosts");
            File.WriteAllText(backup, text);
            var entry = ctx.Journal.Record("hosts_file", summary, "file_restore", new JsonObject { ["path"] = _path, ["backup"] = backup });
            try
            {
                string temp = _path + ".levity-new";
                File.WriteAllText(temp, string.Join("\r\n", lines), new UTF8Encoding(false));
                File.Move(temp, _path, true);
            }
            catch (Exception ex) { ctx.Journal.Cancel(entry.Id); return Task.FromResult(ToolResult.Failure("Could not write the hosts file: " + ex.Message)); }
            return Task.FromResult(ToolResult.Success($"{summary}. Journal id {entry.Id}."));
        }

        private static bool IsEntryFor(string line, string host)
        {
            string t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) return false;
            var parts = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && parts.Skip(1).TakeWhile(p => !p.StartsWith('#')).Any(h => h.Equals(host, StringComparison.OrdinalIgnoreCase));
        }

        private static bool CanWrite(string path)
        {
            try { using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); return true; }
            catch { return false; }
        }
    }

    // ====================================================================================
    // Registry (read only)
    // ====================================================================================

    public sealed class RegistryReadTool : WindowsTool
    {
        private static readonly Regex KeyRule = new(@"^(HKCU|HKLM)\\[A-Za-z0-9_.()\- \\{}@#,;:+]{1,500}$", RegexOptions.Compiled);

        public override string Name => "registry_read";
        public override string Description => "Read a registry key: its values (name, type, data) and the names of its sub-keys. key is like HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run (HKCU or HKLM). Reads only; there is no registry write tool, changes are made by the specific tools.";
        public override JsonObject InputSchema => Schema(new JsonObject { ["key"] = Prop("string", "HKCU\\... or HKLM\\... path.") }, "key");

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string path = RequiredStr(args, "key").Trim().TrimEnd('\\');
            if (!KeyRule.IsMatch(path) || path.Contains("..")) return Task.FromResult(ToolResult.Failure("Give a key starting with HKCU\\ or HKLM\\."));
            int slash = path.IndexOf('\\');
            using var key = RegistryUndo.OpenRoot(path[..slash], false).OpenSubKey(path[(slash + 1)..], false);
            if (key == null) return Task.FromResult(ToolResult.Failure("That key does not exist (or cannot be read)."));
            var values = new JsonArray();
            foreach (string n in key.GetValueNames().Take(150))
            {
                object? v = key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                string shown = v switch { byte[] b => $"({b.Length} bytes) " + Convert.ToHexString(b.Take(32).ToArray()), string[] a => string.Join(" | ", a), null => "", _ => v.ToString() ?? "" };
                values.Add(new JsonObject { ["name"] = n.Length == 0 ? "(Default)" : n, ["type"] = key.GetValueKind(n).ToString(), ["data"] = shown.Length > 300 ? shown[..300] + "..." : shown });
            }
            return Task.FromResult(ToolResult.Success($"{path}: {values.Count} values, {key.SubKeyCount} sub-keys.", new JsonObject { ["values"] = values, ["subkeys"] = new JsonArray(key.GetSubKeyNames().Take(150).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()) }));
        }
    }

    // ====================================================================================
    // Certificates (read only)
    // ====================================================================================

    public sealed class CertificatesTool : WindowsTool
    {
        public override string Name => "certificates";
        public override string Description => "List certificates in a store (user or machine; My = personal, Root = trusted roots, CA = intermediates) with subject, issuer, expiry and thumbprint. expiring_within_days shows only those about to expire or already expired.";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["location"] = Prop("string", "Where (default user).", "user", "machine"),
            ["store"] = Prop("string", "Which store (default My).", "My", "Root", "CA"),
            ["expiring_within_days"] = Prop("integer", "Only certificates that expire within this many days (0 to 3650)."),
        });

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string location = Str(args, "location") is { Length: > 0 } ? OneOf(args, "location", "user", "machine") : "user";
            string store = Str(args, "store") is { Length: > 0 } ? OneOf(args, "store", "My", "Root", "CA") : "My";
            int? within = args.ContainsKey("expiring_within_days") ? Int(args, "expiring_within_days", 0, 0, 3650) : null;
            using var s = new System.Security.Cryptography.X509Certificates.X509Store(store, location == "user" ? System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser : System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
            s.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly | System.Security.Cryptography.X509Certificates.OpenFlags.OpenExistingOnly);
            var arr = new JsonArray();
            foreach (var c in s.Certificates.Cast<System.Security.Cryptography.X509Certificates.X509Certificate2>().OrderBy(c => c.NotAfter))
            {
                using (c)
                {
                    double days = (c.NotAfter - DateTime.Now).TotalDays;
                    if (within != null && days > within) continue;
                    arr.Add(new JsonObject { ["subject"] = c.Subject, ["issuer"] = c.Issuer, ["expires"] = c.NotAfter.ToString("yyyy-MM-dd"), ["days_left"] = (int)Math.Floor(days), ["thumbprint"] = c.Thumbprint });
                    if (arr.Count >= 200) break;
                }
            }
            return Task.FromResult(ToolResult.Success($"{arr.Count} certificates in {location}/{store}.", arr));
        }
    }

    // ====================================================================================
    // Windows Settings pages and default apps (guided: Windows protects those choices)
    // ====================================================================================

    public sealed class DefaultAppsTool : WindowsTool
    {
        public override string Name => "default_apps_get";
        public override string Description => "Which program opens web links, e-mail links, PDF, text and image files for this user. Reading only: Windows protects default-app choices, so to change one use open_settings with page defaultapps and tell the user what to click.";
        public override JsonObject InputSchema => Schema(new JsonObject());

        private static string? Choice(string root, string key)
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\Shell\Associations\{root}\{key}\UserChoice");
            return k?.GetValue("ProgId") as string;
        }

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            var o = new JsonObject
            {
                ["web_links_http"] = Choice("UrlAssociations", "http"),
                ["web_links_https"] = Choice("UrlAssociations", "https"),
                ["email_mailto"] = Choice("UrlAssociations", "mailto"),
                ["pdf"] = Choice("FileExts", ".pdf"),
                ["text"] = Choice("FileExts", ".txt"),
                ["jpg"] = Choice("FileExts", ".jpg"),
                ["png"] = Choice("FileExts", ".png"),
            };
            return Task.FromResult(ToolResult.Success("Default apps (program ids; none means Windows' own default).", o));
        }
    }

    public sealed class OpenSettingsTool : WindowsTool
    {
        private static readonly Dictionary<string, string> Pages = new(StringComparer.OrdinalIgnoreCase)
        {
            ["defaultapps"] = "ms-settings:defaultapps", ["apps"] = "ms-settings:appsfeatures", ["startup"] = "ms-settings:startupapps", ["windowsupdate"] = "ms-settings:windowsupdate",
            ["network"] = "ms-settings:network", ["wifi"] = "ms-settings:network-wifi", ["display"] = "ms-settings:display", ["sound"] = "ms-settings:sound", ["bluetooth"] = "ms-settings:bluetooth",
            ["privacy"] = "ms-settings:privacy", ["storage"] = "ms-settings:storagesense", ["power"] = "ms-settings:powersleep", ["about"] = "ms-settings:about", ["printers"] = "ms-settings:printers",
        };

        public override string Name => "open_settings";
        public override string Description => "Open a page of the Windows Settings app on the PC so the user can make a choice only they can make (default apps, for example). Pages: " + string.Join(", ", Pages.Keys) + ". Changes nothing by itself.";
        public override JsonObject InputSchema => Schema(new JsonObject { ["page"] = Prop("string", "Which Settings page.", Pages.Keys.ToArray()) }, "page");

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string page = OneOf(args, "page", Pages.Keys.ToArray());
            if (ctx.DryRun) return Task.FromResult(ToolResult.Success($"Dry run: would open the Settings page '{page}'."));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Pages[page]) { UseShellExecute = true });
            return Task.FromResult(ToolResult.Success($"Opened Settings: {page}. The user must make the choice there."));
        }
    }

    // ====================================================================================
    // The journal itself, for the assistant
    // ====================================================================================

    public sealed class ChangesListTool : WindowsTool
    {
        public override string Name => "changes_list";
        public override string Description => "List the Windows changes Levity made (from the change journal), newest first, with their ids and whether each was reverted.";
        public override JsonObject InputSchema => Schema(new JsonObject());
        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            var list = ctx.Journal.List(50);
            return Task.FromResult(ToolResult.Success($"{list.Count} recorded changes.", new JsonArray(list.Select(ChangeJournal.ToJson).ToArray())));
        }
    }

    public sealed class ChangesRevertTool : WindowsTool
    {
        public override string Name => "changes_revert";
        public override ToolAccess Access => ToolAccess.Change;
        public override string Description => "Put back one change from the journal by its id (from changes_list). Some changes (ending a program, running a task) cannot be undone and say so.";
        public override JsonObject InputSchema => Schema(new JsonObject { ["id"] = Prop("string", "The change id."), ["dry_run"] = Prop("boolean", "Describe the revert without doing it.") }, "id");
        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string id = RequiredStr(args, "id").Trim();
            var e = ctx.Journal.Find(id);
            if (e == null) return Task.FromResult(ToolResult.Failure("That change is not in the journal."));
            if (ctx.DryRun) return Task.FromResult(ToolResult.Success($"Dry run: would revert \"{e.Summary}\". Nothing was changed."));
            string? err = ctx.Journal.Revert(id);
            return Task.FromResult(err == null ? ToolResult.Success($"Reverted: {e.Summary}.") : ToolResult.Failure(err));
        }
    }
}
