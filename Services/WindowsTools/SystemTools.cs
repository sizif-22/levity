using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Levity.Services.WindowsTools
{
    // ====================================================================================
    // Disk usage
    // ====================================================================================

    public sealed class DiskUsageTool : WindowsTool
    {
        public const int MaxFiles = 2_000_000;

        public override string Name => "disk_usage";
        public override string Description => "Find what is using disk space. Without path: free and used space on every drive. With path: the largest items directly inside that folder (sizes include everything below them). Reads only; never deletes.";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["path"] = Prop("string", "A folder to look inside; leave out for the drive summary."),
            ["top"] = Prop("integer", "How many of the largest items to return (1 to 50, default 15)."),
        });

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string? path = Str(args, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                var drives = new JsonArray();
                foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
                    drives.Add(new JsonObject { ["drive"] = d.Name, ["label"] = d.VolumeLabel, ["total_gb"] = Math.Round(d.TotalSize / 1073741824.0, 1), ["free_gb"] = Math.Round(d.AvailableFreeSpace / 1073741824.0, 1), ["used_percent"] = (int)Math.Round(100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize) });
                return Task.FromResult(ToolResult.Success("Drives.", drives));
            }
            if (!Path.IsPathFullyQualified(path)) return Task.FromResult(ToolResult.Failure("Give a full path such as C:\\Users."));
            string full = Path.GetFullPath(path);
            if (!Directory.Exists(full)) return Task.FromResult(ToolResult.Failure("That folder does not exist."));
            int top = Int(args, "top", 15, 1, 50);

            long files = 0;
            bool truncated = false;
            var sw = Stopwatch.StartNew();
            long Size(string dir)
            {
                long total = 0;
                var stack = new Stack<string>();
                stack.Push(dir);
                while (stack.Count > 0)
                {
                    if (ctx.Cancel.IsCancellationRequested || sw.Elapsed > TimeSpan.FromSeconds(40) || files > MaxFiles) { truncated = true; break; }
                    string d = stack.Pop();
                    try
                    {
                        foreach (var entry in new DirectoryInfo(d).EnumerateFileSystemInfos())
                        {
                            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;   // links and junctions: never follow, they can loop or leave the folder
                            if (entry is FileInfo f) { total += f.Length; files++; }
                            else stack.Push(entry.FullName);
                        }
                    }
                    catch { /* no access: skip it */ }
                }
                return total;
            }

            var items = new List<(string Name, long Bytes, bool IsDir)>();
            try
            {
                foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos())
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (entry is FileInfo f) { items.Add((f.Name, f.Length, false)); files++; }
                    else items.Add((entry.Name, Size(entry.FullName), true));
                }
            }
            catch (Exception ex) { return Task.FromResult(ToolResult.Failure("Could not read that folder: " + ex.Message)); }

            var arr = new JsonArray(items.OrderByDescending(i => i.Bytes).Take(top).Select(i => (JsonNode)new JsonObject { ["name"] = i.Name, ["type"] = i.IsDir ? "folder" : "file", ["size_mb"] = Math.Round(i.Bytes / 1048576.0, 1) }).ToArray());
            double totalMb = Math.Round(items.Sum(i => i.Bytes) / 1048576.0, 1);
            return Task.FromResult(ToolResult.Success($"{full}: {totalMb} MB in {files} files" + (truncated ? " (stopped early: the folder is very large, so sizes are lower bounds)" : "") + ".", arr));
        }
    }

    // ====================================================================================
    // Network
    // ====================================================================================

    public sealed class NetworkInfoTool : WindowsTool
    {
        private static readonly Regex HostName = new(@"^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$", RegexOptions.Compiled);

        public override string Name => "network_info";
        public override string Description => "Look at the network. action=adapters (connected adapters with addresses, gateways and DNS servers), dns (resolve host), ping (host, 4 tries), port_owner (which program is using a TCP port, or lists listening ports).";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["action"] = Prop("string", "What to check.", "adapters", "dns", "ping", "port_owner"),
            ["host"] = Prop("string", "Host name or address for dns and ping."),
            ["port"] = Prop("integer", "TCP port for port_owner; leave out to list all listening ports."),
        }, "action");

        public override async Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            switch (OneOf(args, "action", "adapters", "dns", "ping", "port_owner"))
            {
                case "adapters":
                {
                    var arr = new JsonArray();
                    foreach (var n in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                    {
                        var p = n.GetIPProperties();
                        arr.Add(new JsonObject
                        {
                            ["name"] = n.Name, ["type"] = n.NetworkInterfaceType.ToString(), ["status"] = n.OperationalStatus.ToString(),
                            ["speed_mbps"] = n.Speed > 0 ? n.Speed / 1_000_000 : 0,
                            ["ipv4"] = new JsonArray(p.UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(a => (JsonNode)JsonValue.Create(a.Address.ToString())!).ToArray()),
                            ["gateway"] = new JsonArray(p.GatewayAddresses.Select(g => (JsonNode)JsonValue.Create(g.Address.ToString())!).ToArray()),
                            ["dns"] = new JsonArray(p.DnsAddresses.Select(d => (JsonNode)JsonValue.Create(d.ToString())!).ToArray()),
                        });
                    }
                    return ToolResult.Success($"{arr.Count} adapters.", arr);
                }
                case "dns":
                {
                    string host = Host(args);
                    try
                    {
                        var ips = await Dns.GetHostAddressesAsync(host, ctx.Cancel);
                        return ToolResult.Success($"{host} resolves to {ips.Length} address(es).", new JsonArray(ips.Select(i => (JsonNode)JsonValue.Create(i.ToString())!).ToArray()));
                    }
                    catch (Exception ex) { return ToolResult.Failure($"Could not resolve {host}: {ex.Message}"); }
                }
                case "ping":
                {
                    string host = Host(args);
                    using var ping = new Ping();
                    var replies = new JsonArray();
                    int ok = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        try
                        {
                            var r = await ping.SendPingAsync(host, 2000);
                            if (r.Status == IPStatus.Success) ok++;
                            replies.Add(new JsonObject { ["status"] = r.Status.ToString(), ["ms"] = r.Status == IPStatus.Success ? r.RoundtripTime : null });
                        }
                        catch (Exception ex) { return ToolResult.Failure($"Could not ping {host}: {ex.InnerException?.Message ?? ex.Message}"); }
                    }
                    return ToolResult.Success($"{ok} of 4 replies from {host}.", replies);
                }
                default:
                {
                    int? port = args.ContainsKey("port") ? Int(args, "port", 0, 1, 65535) : null;
                    var rows = Native.TcpTable().Where(t => port != null ? t.LocalPort == port : t.State == "LISTEN").ToList();
                    var arr = new JsonArray();
                    foreach (var r in rows.Take(200))
                    {
                        string proc = "unknown";
                        try { proc = Process.GetProcessById(r.Pid).ProcessName; } catch { }
                        arr.Add(new JsonObject { ["port"] = r.LocalPort, ["state"] = r.State, ["pid"] = r.Pid, ["program"] = proc });
                    }
                    return ToolResult.Success(port != null ? (rows.Count == 0 ? $"Nothing is using TCP port {port}." : $"TCP port {port} is in use.") : $"{rows.Count} listening ports.", arr);
                }
            }
        }

        private static string Host(JsonObject args)
        {
            string host = RequiredStr(args, "host").Trim();
            if (!HostName.IsMatch(host) && !IPAddress.TryParse(host, out _)) throw new ToolInputException("That is not a valid host name or address.");
            return host;
        }
    }

    // ====================================================================================
    // Processes
    // ====================================================================================

    public sealed class ProcessesTool : WindowsTool
    {
        public override string Name => "processes";
        public override string Description => "Look at running programs. action=top_cpu or top_memory (the heaviest, default top_memory), find (by name part). Shows id, name, memory and CPU share over half a second.";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["action"] = Prop("string", "What to list (default top_memory).", "top_cpu", "top_memory", "find"),
            ["name"] = Prop("string", "A part of the program name, for find."),
            ["top"] = Prop("integer", "How many to return (1 to 50, default 15)."),
        });

        public override async Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string action = Str(args, "action") is { Length: > 0 } ? OneOf(args, "action", "top_cpu", "top_memory", "find") : "top_memory";
            int top = Int(args, "top", 15, 1, 50);
            string? needle = action == "find" ? RequiredStr(args, "name").Trim() : null;

            var before = Snapshot();
            await Task.Delay(500, ctx.Cancel);
            var after = Snapshot();
            double seconds = 0.5;
            var rows = new List<(int Pid, string Name, double Mb, double Cpu)>();
            foreach (var (pid, (name, cpu, mem)) in after)
            {
                if (needle != null && name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                double usedCpu = before.TryGetValue(pid, out var b) ? (cpu - b.Cpu).TotalSeconds / seconds / Environment.ProcessorCount * 100 : 0;
                rows.Add((pid, name, Math.Round(mem / 1048576.0, 1), Math.Round(Math.Max(0, usedCpu), 1)));
            }
            var ordered = action == "top_cpu" ? rows.OrderByDescending(r => r.Cpu) : rows.OrderByDescending(r => r.Mb);
            var arr = new JsonArray(ordered.Take(top).Select(r => (JsonNode)new JsonObject { ["pid"] = r.Pid, ["name"] = r.Name, ["memory_mb"] = r.Mb, ["cpu_percent"] = r.Cpu }).ToArray());
            return ToolResult.Success($"{rows.Count} program(s) match; showing {arr.Count}.", arr);
        }

        private static Dictionary<int, (string Name, TimeSpan Cpu, long Mem)> Snapshot()
        {
            var d = new Dictionary<int, (string, TimeSpan, long)>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try { d[p.Id] = (p.ProcessName, p.TotalProcessorTime, p.WorkingSet64); }
                    catch { d[p.Id] = (SafeName(p), TimeSpan.Zero, 0); }
                }
            }
            return d;
        }

        private static string SafeName(Process p) { try { return p.ProcessName; } catch { return "?"; } }
    }

    public sealed class ProcessEndTool : WindowsTool
    {
        private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
        {
            "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "fontdrvhost", "explorer", "memory compression", "levity",
        };

        public override string Name => "process_end";
        public override ToolAccess Access => ToolAccess.Change;
        public override string Description => "End one running program by its id (from processes). You must repeat its exact name as a check. Essential Windows processes and Levity itself are refused. Unsaved work in that program is lost and this cannot be undone; it is recorded in the journal.";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["pid"] = Prop("integer", "The process id."),
            ["name"] = Prop("string", "The program's name exactly as shown by processes (without .exe)."),
            ["dry_run"] = Prop("boolean", "Describe the change without making it."),
        }, "pid", "name");

        public static void RegisterUndo(ChangeJournal journal) =>
            journal.RegisterUndo("none", _ => "Ending a program cannot be undone; start it again yourself.");

        public override Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            int pid = RequiredInt(args, "pid", 1, int.MaxValue);
            string name = RequiredStr(args, "name").Trim();
            Process p;
            try { p = Process.GetProcessById(pid); } catch { return Task.FromResult(ToolResult.Failure("No program with that id is running.")); }
            using (p)
            {
                if (!string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)) return Task.FromResult(ToolResult.Failure($"Process {pid} is '{p.ProcessName}', not '{name}'. Nothing was ended."));
                if (Protected.Contains(p.ProcessName) || pid == Environment.ProcessId) return Task.FromResult(ToolResult.Failure($"'{p.ProcessName}' is an essential process and will not be ended."));
                string summary = $"End {p.ProcessName} (id {pid})";
                if (ctx.DryRun) return Task.FromResult(ToolResult.Success($"Dry run: would end {p.ProcessName} (id {pid}). Nothing was changed."));
                var entry = ctx.Journal.Record("process_end", summary + " (cannot be undone)", "none", new JsonObject { ["pid"] = pid, ["name"] = p.ProcessName });
                try { p.Kill(true); }
                catch (Exception ex) { return Task.FromResult(ToolResult.Failure($"Could not end it: {ex.Message}")); }
                return Task.FromResult(ToolResult.Success($"{summary}. Journal id {entry.Id}."));
            }
        }
    }

    // ====================================================================================
    // Machine profile
    // ====================================================================================

    public sealed class MachineProfileTool : WindowsTool
    {
        private readonly MachineProfile _profile;
        public MachineProfileTool(MachineProfile profile) { _profile = profile; }
        public override string Name => "machine_profile";
        public override string Description => "A summary of this PC: Windows edition and build, hardware, drives, installed developer tools and their versions, default shell and power plan. Cached; refresh=true rebuilds it.";
        public override JsonObject InputSchema => Schema(new JsonObject { ["refresh"] = Prop("boolean", "Rebuild instead of using the saved summary.") });

        public override async Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            var p = await _profile.GetAsync(Bool(args, "refresh"), ctx.Cancel);
            return ToolResult.Success(MachineProfile.Summary(p), p);
        }
    }
}
