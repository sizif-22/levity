using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Levity.Services.WindowsTools
{
    // ====================================================================================
    // Printers (straight from the Windows print spooler, no extra libraries)
    // ====================================================================================

    internal static class Spooler
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PrinterInfo2
        {
            public string pServerName, pPrinterName, pShareName, pPortName, pDriverName, pComment, pLocation;
            public IntPtr pDevMode;
            public string pSepFile, pPrintProcessor, pDatatype, pParameters;
            public IntPtr pSecurityDescriptor;
            public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs, AveragePPM;
        }

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr buffer, uint size, out uint needed, out uint returned);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetDefaultPrinter(StringBuilder buffer, ref int size);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDefaultPrinter(string name);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool SetPrinter(IntPtr handle, uint level, IntPtr info, uint command);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr handle);

        public sealed record Printer(string Name, string Port, string Driver, int Jobs, bool Offline, bool IsDefault);

        public static string DefaultName()
        {
            int size = 260;
            var sb = new StringBuilder(size);
            return GetDefaultPrinter(sb, ref size) ? sb.ToString() : "";
        }

        public static List<Printer> List()
        {
            const uint LOCAL_AND_CONNECTIONS = 2 | 4;
            EnumPrinters(LOCAL_AND_CONNECTIONS, null, 2, IntPtr.Zero, 0, out uint needed, out _);
            var result = new List<Printer>();
            if (needed == 0) return result;
            IntPtr buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!EnumPrinters(LOCAL_AND_CONNECTIONS, null, 2, buf, needed, out _, out uint count)) return result;
                string def = DefaultName();
                int size = Marshal.SizeOf<PrinterInfo2>();
                for (int i = 0; i < count; i++)
                {
                    var p = Marshal.PtrToStructure<PrinterInfo2>(buf + i * size);
                    bool offline = (p.Status & 0x80) != 0 || (p.Attributes & 0x400) != 0;   // PRINTER_STATUS_OFFLINE / work-offline attribute
                    result.Add(new Printer(p.pPrinterName ?? "", p.pPortName ?? "", p.pDriverName ?? "", (int)p.cJobs, offline, string.Equals(p.pPrinterName, def, StringComparison.OrdinalIgnoreCase)));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return result;
        }

        public static bool SetDefault(string name) => SetDefaultPrinter(name);

        public static bool Purge(string name)
        {
            if (!OpenPrinter(name, out var h, IntPtr.Zero)) return false;
            try { return SetPrinter(h, 0, IntPtr.Zero, 3); }   // PRINTER_CONTROL_PURGE
            finally { ClosePrinter(h); }
        }
    }

    public sealed class PrintersListTool : WindowsTool
    {
        public override string Name => "printers_list";
        public override string Description => "List printers with their port, driver, number of waiting jobs, whether they are offline and which is the default.";
        public override JsonObject InputSchema => Schema(new JsonObject());
        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            var list = Spooler.List();
            return Task.FromResult(ToolResult.Success($"{list.Count} printers.", new JsonArray(list.Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["port"] = p.Port, ["driver"] = p.Driver, ["waiting_jobs"] = p.Jobs, ["offline"] = p.Offline, ["default"] = p.IsDefault }).ToArray())));
        }
    }

    public sealed class PrinterControlTool : WindowsTool
    {
        public override string Name => "printer_control";
        public override ToolAccess Access => ToolAccess.Change;
        public override string Description => "Make a printer the default, or clear its waiting print jobs (for a stuck queue). name is from printers_list. Setting the default is journaled and revertible; clearing jobs cannot be undone.";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["name"] = Prop("string", "The printer name exactly as in printers_list."),
            ["action"] = Prop("string", "What to do.", "set_default", "clear_jobs"),
            ["dry_run"] = Prop("boolean", "Describe the change without making it."),
        }, "name", "action");

        public static void RegisterUndo(ChangeJournal journal) =>
            journal.RegisterUndo("printer_default", data =>
            {
                string old = (string?)data["old"] ?? "";
                return old.Length == 0 || Spooler.SetDefault(old) ? null : "Windows refused to restore the default printer.";
            });

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string name = RequiredStr(args, "name");
            string action = OneOf(args, "action", "set_default", "clear_jobs");
            var printers = Spooler.List();
            var p = printers.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (p == null) return Task.FromResult(ToolResult.Failure("No printer with that exact name. Use printers_list."));
            string summary = action == "set_default" ? $"Make '{p.Name}' the default printer" : $"Clear {p.Jobs} waiting print job(s) of '{p.Name}'";
            if (action == "set_default" && p.IsDefault) return Task.FromResult(ToolResult.Success("That printer is already the default. Nothing changed."));
            if (ctx.DryRun) return Task.FromResult(ToolResult.Success($"Dry run: would {char.ToLowerInvariant(summary[0])}{summary[1..]}. Nothing was changed."));

            var entry = ctx.Journal.Record("printer_control", summary + (action == "clear_jobs" ? " (cannot be undone)" : ""), action == "set_default" ? "printer_default" : "none", new JsonObject { ["old"] = Spooler.DefaultName() });
            bool ok = action == "set_default" ? Spooler.SetDefault(p.Name) : Spooler.Purge(p.Name);
            if (!ok) { ctx.Journal.Cancel(entry.Id); return Task.FromResult(ToolResult.Failure("Windows refused. Clearing a queue may need administrator rights.")); }
            return Task.FromResult(ToolResult.Success($"{summary}. Journal id {entry.Id}."));
        }
    }

    // ====================================================================================
    // Event log
    // ====================================================================================

    public sealed class EventLogTool : WindowsTool
    {
        public override string Name => "event_log";
        public override string Description => "Recent errors and warnings from the Windows event log, grouped by source and event id with counts and one sample message each. log: System or Application; hours: how far back (1 to 168, default 24); level: errors (default) or warnings_and_errors; event_id: only this id.";
        public override int TimeoutSeconds => 90;
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["log"] = Prop("string", "Which log (default System).", "System", "Application"),
            ["hours"] = Prop("integer", "How far back (1 to 168, default 24)."),
            ["level"] = Prop("string", "What to include.", "errors", "warnings_and_errors"),
            ["event_id"] = Prop("integer", "Only this event id."),
        });

        public static string Query(int hours, bool includeWarnings, int? eventId)
        {
            long ms = hours * 3600L * 1000;
            string level = includeWarnings ? "(Level=1 or Level=2 or Level=3)" : "(Level=1 or Level=2)";
            string id = eventId != null ? $" and (EventID={eventId})" : "";
            return $"*[System[{level}{id} and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
        }

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string log = Str(args, "log") is { Length: > 0 } ? OneOf(args, "log", "System", "Application") : "System";
            int hours = Int(args, "hours", 24, 1, 168);
            bool warn = Str(args, "level") is { Length: > 0 } && OneOf(args, "level", "errors", "warnings_and_errors") == "warnings_and_errors";
            int? id = args.ContainsKey("event_id") ? Int(args, "event_id", 0, 0, 65535) : null;

            var groups = new Dictionary<(string Source, int Id), (int Count, string Level, string Sample, DateTime Last)>();
            int read = 0;
            try
            {
                using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, Query(hours, warn, id)) { ReverseDirection = true });
                for (EventRecord? e = reader.ReadEvent(); e != null && read < 3000; e = reader.ReadEvent())
                {
                    using (e)
                    {
                        read++;
                        var key = (e.ProviderName ?? "?", e.Id);
                        if (groups.TryGetValue(key, out var g)) groups[key] = (g.Count + 1, g.Level, g.Sample, g.Last);
                        else
                        {
                            string msg = "";
                            try { msg = e.FormatDescription() ?? ""; } catch { }
                            msg = Regex.Replace(msg.Split('\n').FirstOrDefault() ?? "", @"\s+", " ").Trim();
                            groups[key] = (1, e.LevelDisplayName ?? e.Level?.ToString() ?? "", msg.Length > 240 ? msg[..240] + "..." : msg, e.TimeCreated ?? DateTime.MinValue);
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException) { return Task.FromResult(ToolResult.Failure($"Reading the {log} log needs more rights than Levity has.")); }
            catch (EventLogNotFoundException) { return Task.FromResult(ToolResult.Failure("That log does not exist.")); }

            var arr = new JsonArray(groups.OrderByDescending(g => g.Value.Count).Take(20).Select(g => (JsonNode)new JsonObject
            {
                ["source"] = g.Key.Source, ["event_id"] = g.Key.Id, ["count"] = g.Value.Count, ["level"] = g.Value.Level, ["latest"] = g.Value.Last.ToString("s"), ["sample"] = g.Value.Sample,
            }).ToArray());
            return Task.FromResult(ToolResult.Success($"{read} event(s) in the last {hours} h of the {log} log, {groups.Count} distinct kind(s); the most frequent:", arr));
        }
    }

    // ====================================================================================
    // Firewall (Windows Firewall's own COM interface)
    // ====================================================================================

    internal static class FirewallPolicy
    {
        public sealed record Rule(string Name, bool Enabled, string Direction, string Action, string Protocol, string LocalPorts, string Program);

        private static dynamic Policy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;

        public static List<Rule> List(string? filter, int max = 300)
        {
            var list = new List<Rule>();
            dynamic policy = Policy();
            foreach (dynamic r in policy.Rules)
            {
                try
                {
                    string name = (string)r.Name ?? "";
                    string program = (string?)r.ApplicationName ?? "";
                    string ports = (string?)r.LocalPorts ?? "";
                    if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 && program.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 && ports != filter) continue;
                    int proto = (int)r.Protocol;
                    list.Add(new Rule(name, (bool)r.Enabled, (int)r.Direction == 1 ? "inbound" : "outbound", (int)r.Action == 1 ? "allow" : "block", proto == 6 ? "TCP" : proto == 17 ? "UDP" : proto == 256 ? "any" : proto.ToString(), ports, program));
                    if (list.Count >= max) break;
                }
                catch { }
            }
            return list;
        }

        public static void Add(string name, string? program, int? tcpPort)
        {
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
            rule.Name = name;
            rule.Direction = 1;     // inbound
            rule.Action = 1;        // allow
            rule.Enabled = true;
            rule.Profiles = 0x7FFFFFFF;
            if (program != null) rule.ApplicationName = program;
            if (tcpPort != null) { rule.Protocol = 6; rule.LocalPorts = tcpPort.Value.ToString(); }
            Policy().Rules.Add(rule);
        }

        public static void Remove(string name) => Policy().Rules.Remove(name);
    }

    public sealed class FirewallRulesTool : WindowsTool
    {
        public override string Name => "firewall_rules";
        public override string Description => "List Windows Firewall rules (name, enabled, inbound/outbound, allow/block, protocol, ports, program). filter: part of a rule name or program path, or an exact port number, to find what allows or blocks something.";
        public override JsonObject InputSchema => Schema(new JsonObject { ["filter"] = Prop("string", "Part of a name or program path, or a port number.") });
        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            var rules = FirewallPolicy.List(Str(args, "filter"));
            return Task.FromResult(ToolResult.Success($"{rules.Count} rules.", new JsonArray(rules.Select(r => (JsonNode)new JsonObject { ["name"] = r.Name, ["enabled"] = r.Enabled, ["direction"] = r.Direction, ["action"] = r.Action, ["protocol"] = r.Protocol, ["ports"] = r.LocalPorts, ["program"] = r.Program }).ToArray())));
        }
    }

    public sealed class FirewallChangeTool : WindowsTool
    {
        public const string Prefix = "Levity: ";

        public override string Name => "firewall_change";
        public override ToolAccess Access => ToolAccess.Change;
        public override bool NeedsAdminToChange => true;
        public override string Description => "Add an inbound ALLOW rule for one program (full path) or one TCP port, or remove a rule that Levity added earlier (names start with 'Levity: '). Rules you or Windows made are never removed by this tool. Needs administrator rights. Journaled and revertible.";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["action"] = Prop("string", "What to do.", "allow_program", "allow_port", "remove"),
            ["program"] = Prop("string", "Full path of the program, for allow_program."),
            ["port"] = Prop("integer", "TCP port 1 to 65535, for allow_port."),
            ["name"] = Prop("string", "The rule name, for remove (must start with 'Levity: ')."),
            ["dry_run"] = Prop("boolean", "Describe the change without making it."),
        }, "action");

        public static void RegisterUndo(ChangeJournal journal)
        {
            journal.RegisterUndo("firewall_rule_added", data => { FirewallPolicy.Remove((string?)data["name"] ?? ""); return null; });
            journal.RegisterUndo("firewall_rule_removed", data =>
            {
                FirewallPolicy.Add((string?)data["name"] ?? "", (string?)data["program"] is { Length: > 0 } p ? p : null, data["port"] is JsonValue v && v.TryGetValue(out int port) ? port : null);
                return null;
            });
        }

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string action = OneOf(args, "action", "allow_program", "allow_port", "remove");
            string summary, name;
            string? program = null;
            int? port = null;
            if (action == "allow_program")
            {
                program = RequiredStr(args, "program").Trim();
                if (!Path.IsPathFullyQualified(program) || program.StartsWith(@"\\") || !File.Exists(program) || !program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(ToolResult.Failure("program must be the full path of an existing .exe file on this PC."));
                name = Prefix + "allow " + Path.GetFileName(program);
                summary = $"Allow inbound connections to {program} through the firewall";
            }
            else if (action == "allow_port")
            {
                port = RequiredInt(args, "port", 1, 65535);
                name = Prefix + $"allow TCP {port}";
                summary = $"Allow inbound TCP port {port} through the firewall";
            }
            else
            {
                name = RequiredStr(args, "name").Trim();
                if (!name.StartsWith(Prefix, StringComparison.Ordinal)) return Task.FromResult(ToolResult.Failure($"Only rules that Levity added (names starting with '{Prefix}') can be removed here."));
                summary = $"Remove the firewall rule '{name}'";
            }
            if (ctx.DryRun) return Task.FromResult(ToolResult.Success($"Dry run: would {char.ToLowerInvariant(summary[0])}{summary[1..]}. Nothing was changed."));
            if (!ctx.Elevated) return Task.FromResult(ToolResult.Failure($"Changing the firewall needs administrator rights. Ask Levity to run this elevated (you approve a Windows prompt on the PC). Plan: {summary}."));

            if (action == "remove")
            {
                var existing = FirewallPolicy.List(name).FirstOrDefault(r => r.Name == name);
                if (existing == null) return Task.FromResult(ToolResult.Failure("No rule with that name."));
                int? p = int.TryParse(existing.LocalPorts, out int parsed) ? parsed : null;
                var entry = ctx.Journal.Record("firewall_change", summary, "firewall_rule_removed", new JsonObject { ["name"] = name, ["program"] = existing.Program, ["port"] = p });
                try { FirewallPolicy.Remove(name); }
                catch (Exception ex) { ctx.Journal.Cancel(entry.Id); return Task.FromResult(ToolResult.Failure("Could not remove it: " + ex.Message)); }
                return Task.FromResult(ToolResult.Success($"{summary}. Journal id {entry.Id}."));
            }
            if (FirewallPolicy.List(name).Any(r => r.Name == name)) return Task.FromResult(ToolResult.Success("That rule already exists. Nothing changed."));
            var e2 = ctx.Journal.Record("firewall_change", summary, "firewall_rule_added", new JsonObject { ["name"] = name });
            try { FirewallPolicy.Add(name, program, port); }
            catch (Exception ex) { ctx.Journal.Cancel(e2.Id); return Task.FromResult(ToolResult.Failure("Could not add it: " + ex.Message)); }
            return Task.FromResult(ToolResult.Success($"{summary}. Journal id {e2.Id}."));
        }
    }

    // ====================================================================================
    // Microsoft Defender
    // ====================================================================================

    internal static class Defender
    {
        public const string StatusScript = "Get-MpComputerStatus | Select-Object AMServiceEnabled,AntivirusEnabled,RealTimeProtectionEnabled,IoavProtectionEnabled,AntivirusSignatureLastUpdated,QuickScanEndTime,FullScanEndTime | ConvertTo-Json -Compress";
        public const string ExclusionsScript = "Get-MpPreference | Select-Object ExclusionPath,ExclusionExtension,ExclusionProcess | ConvertTo-Json -Compress";
        public const string QuickScanScript = "Start-MpScan -ScanType QuickScan";
        public const string AddExclusionScript = "Add-MpPreference -ExclusionPath $env:LEVITY_ARG";
        public const string RemoveExclusionScript = "Remove-MpPreference -ExclusionPath $env:LEVITY_ARG";

        public static Task<Proc.Output> Run(string script, string? arg, int timeout, System.Threading.CancellationToken ct) =>
            Proc.RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script }, timeout, ct,
                arg == null ? null : new Dictionary<string, string> { ["LEVITY_ARG"] = arg });
    }

    public sealed class DefenderStatusTool : WindowsTool
    {
        public override string Name => "defender_status";
        public override string Description => "Microsoft Defender status: protection on or off, signature age, last scans, and the exclusion list (the list can only be read with administrator rights).";
        public override int TimeoutSeconds => 90;
        public override JsonObject InputSchema => Schema(new JsonObject());
        public override async Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            var status = await Defender.Run(Defender.StatusScript, null, 60, ctx.Cancel);
            if (status.ExitCode != 0) return ToolResult.Failure("Could not read Defender's status (it may be turned off or replaced by another antivirus).");
            var data = new JsonObject { ["status"] = TryJson(status.Text) };
            var ex = await Defender.Run(Defender.ExclusionsScript, null, 60, ctx.Cancel);
            data["exclusions"] = ex.ExitCode == 0 && !ex.Text.Contains("administrator", StringComparison.OrdinalIgnoreCase) ? TryJson(ex.Text) : "not readable without administrator rights";
            return ToolResult.Success("Defender status.", data);
        }

        private static JsonNode? TryJson(string text) { try { return JsonNode.Parse(text); } catch { return JsonValue.Create(text.Trim()); } }
    }

    public sealed class DefenderChangeTool : WindowsTool
    {
        public override string Name => "defender_change";
        public override ToolAccess Access => ToolAccess.Change;
        public override bool NeedsAdminToChange => true;
        public override int TimeoutSeconds => 120;
        public override string Description => "Start a Defender quick scan, or add or remove a scan exclusion for one full folder or file path. Exclusions weaken protection, so each one needs a clear reason from the user; a whole drive is refused. Needs administrator rights. Exclusion changes are journaled and revertible.";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["action"] = Prop("string", "What to do.", "quick_scan", "add_exclusion", "remove_exclusion"),
            ["path"] = Prop("string", "Full path for the exclusion."),
            ["dry_run"] = Prop("boolean", "Describe the change without making it."),
        }, "action");

        public static void RegisterUndo(ChangeJournal journal)
        {
            journal.RegisterUndo("defender_exclusion_added", data => Undo(Defender.RemoveExclusionScript, (string?)data["path"]));
            journal.RegisterUndo("defender_exclusion_removed", data => Undo(Defender.AddExclusionScript, (string?)data["path"]));
        }

        private static string? Undo(string script, string? path)
        {
            if (string.IsNullOrEmpty(path)) return "No path was saved.";
            var r = Defender.Run(script, path, 60, default).GetAwaiter().GetResult();
            return r.ExitCode == 0 ? null : "Defender refused: " + r.Text;
        }

        public override async Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string action = OneOf(args, "action", "quick_scan", "add_exclusion", "remove_exclusion");
            if (action == "quick_scan")
            {
                if (ctx.DryRun) return ToolResult.Success("Dry run: would start a Defender quick scan.");
                var r = await Defender.Run(Defender.QuickScanScript, null, 100, ctx.Cancel);
                return r.ExitCode == 0 ? ToolResult.Success("Quick scan finished.") : ToolResult.Failure("The scan did not run: " + Trim(r.Text));
            }
            string path = RequiredStr(args, "path").Trim();
            if (!Path.IsPathFullyQualified(path) || path.Contains('*') || path.Contains('?') || path.StartsWith(@"\\")) return ToolResult.Failure("Give one full local folder or file path, without wildcards.");
            string full = Path.GetFullPath(path).TrimEnd('\\');
            if (full.Length <= 3 || string.Equals(full, Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) return ToolResult.Failure("A whole drive or the Windows folder will not be excluded from scanning.");
            string summary = $"{(action == "add_exclusion" ? "Exclude" : "Stop excluding")} {full} {(action == "add_exclusion" ? "from" : "in")} Defender scans";
            if (ctx.DryRun) return ToolResult.Success($"Dry run: would {char.ToLowerInvariant(summary[0])}{summary[1..]}. Nothing was changed.");
            if (!ctx.Elevated) return ToolResult.Failure($"Changing Defender's exclusions needs administrator rights. Ask Levity to run this elevated (you approve a Windows prompt on the PC). Plan: {summary}.");

            var entry = ctx.Journal.Record("defender_change", summary, action == "add_exclusion" ? "defender_exclusion_added" : "defender_exclusion_removed", new JsonObject { ["path"] = full });
            var run = await Defender.Run(action == "add_exclusion" ? Defender.AddExclusionScript : Defender.RemoveExclusionScript, full, 60, ctx.Cancel);
            if (run.ExitCode != 0) { ctx.Journal.Cancel(entry.Id); return ToolResult.Failure("Defender refused: " + Trim(run.Text)); }
            return ToolResult.Success($"{summary}. Journal id {entry.Id}.");
        }

        private static string Trim(string s) => s.Length > 300 ? s[..300] + "..." : s;
    }
}
