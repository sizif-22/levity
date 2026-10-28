using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Levity.Services.WindowsTools
{
    public enum Severity { Critical = 0, Warning = 1, Info = 2, Ok = 3 }

    /// <summary>What the person can do about a finding. "tool" runs a toolbox tool, "prompt" hands the assistant a precise task, "settings" opens a Settings page.</summary>
    public sealed record DoctorFix(string Kind, string Label, string? Tool = null, JsonObject? Args = null, string? Prompt = null, string? Page = null, bool Changes = false);

    public sealed record Finding(string Id, Severity Severity, string Title, string Detail, IReadOnlyList<DoctorFix> Fixes);

    public sealed record DriveFact(string Name, double TotalGb, double FreeGb);
    public sealed record EventFact(string Source, int Id, int Count, string Sample);

    /// <summary>Everything the checks look at, gathered once. Plain data, so the rules can be tested with made-up PCs.</summary>
    public sealed class DoctorFacts
    {
        public List<DriveFact> Drives { get; set; } = new();
        public double UptimeDays { get; set; }
        public double RamTotalGb { get; set; }
        public double RamFreeGb { get; set; }
        public bool PendingReboot { get; set; }
        public int StartupEnabled { get; set; }
        public List<EventFact> Events { get; set; } = new();
        public bool? DnsWorks { get; set; }
        public int AdaptersUp { get; set; }
        public int AdaptersWithGateway { get; set; }
        public int PathMissing { get; set; }
        public int PathDuplicates { get; set; }
        public double TempGb { get; set; }
        public string TempPath { get; set; } = "";
        public bool? DefenderRealtimeOff { get; set; }
        public string? WindowsUpdateStartType { get; set; }
        public List<string> Skipped { get; set; } = new();
    }

    /// <summary>
    /// The PC Doctor: a quick scan with no assistant and no tokens, then plain findings ranked by how much they matter, each with buttons
    /// that run a tool or hand the assistant a precise task. Reads only; nothing is changed until a fix is pressed and confirmed.
    /// </summary>
    public static class PcDoctor
    {
        private static readonly HashSet<int> ServiceFailureIds = new() { 7000, 7001, 7009, 7011, 7022, 7023, 7024, 7031, 7034 };

        // ---- the rules ----

        public static IReadOnlyList<Finding> Evaluate(DoctorFacts f)
        {
            var list = new List<Finding>();

            foreach (var d in f.Drives.Where(d => d.TotalGb > 0))
            {
                double pct = 100 * d.FreeGb / d.TotalGb;
                if (pct < 5 || d.FreeGb < 5)
                    list.Add(new Finding("disk-" + d.Name, Severity.Critical, $"Drive {d.Name} is almost full", $"{d.FreeGb:0.#} GB free of {d.TotalGb:0} GB ({pct:0}%). Windows needs free space for updates, the page file and temporary files; below this things start to fail.", SpaceFixes(d.Name)));
                else if (pct < 10 || d.FreeGb < 15)
                    list.Add(new Finding("disk-" + d.Name, Severity.Warning, $"Drive {d.Name} is getting full", $"{d.FreeGb:0.#} GB free of {d.TotalGb:0} GB ({pct:0}%).", SpaceFixes(d.Name)));
            }

            if (f.PendingReboot)
                list.Add(new Finding("reboot", Severity.Warning, "A restart is waiting", "Windows or an installer is waiting for a restart to finish. Until then updates and some fixes cannot complete.", new[] { new DoctorFix("settings", "Open Windows Update", Page: "windowsupdate") }));

            if (f.UptimeDays >= 30)
                list.Add(new Finding("uptime", Severity.Info, $"This PC has been on for {f.UptimeDays:0} days", "A restart clears memory leaks and finishes pending updates. Save your work first.", Array.Empty<DoctorFix>()));

            if (f.RamTotalGb > 0 && f.RamFreeGb / f.RamTotalGb < 0.10)
                list.Add(new Finding("ram", Severity.Warning, "Memory is nearly all in use", $"{f.RamFreeGb:0.#} GB free of {f.RamTotalGb:0.#} GB. Windows will slow down and swap to disk.",
                    new[] { new DoctorFix("tool", "Show the biggest programs", "processes", new JsonObject { ["action"] = "top_memory", ["top"] = 10 }) }));

            foreach (var e in f.Events)
            {
                string src = e.Source;
                if (src.Equals("Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase) && e.Id == 41)
                    list.Add(Prompted($"power-{e.Id}", Severity.Critical, $"The PC shut down unexpectedly ({e.Count}x in the last day)", "Kernel-Power event 41: Windows restarted without a clean shutdown (power loss, a crash, or a hard reset). " + e.Sample, "Look into why this PC shut down unexpectedly (Kernel-Power 41 in the last 24 hours). Read the System event log around those times, check for blue-screen or driver or overheating clues, and tell me the most likely cause and what to do. Do not change anything yet."));
                else if (src.Equals("EventLog", StringComparison.OrdinalIgnoreCase) && e.Id == 6008)
                    list.Add(Prompted("unexpected-shutdown", Severity.Warning, "Windows reports an unexpected shutdown", e.Sample, "Windows logged an unexpected shutdown (event 6008). Check the System event log around it and tell me the likely cause. Do not change anything yet."));
                else if (src.Equals("Microsoft-Windows-WER-SystemErrorReporting", StringComparison.OrdinalIgnoreCase) || e.Id == 1001 && src.Contains("BugCheck", StringComparison.OrdinalIgnoreCase))
                    list.Add(Prompted("bugcheck", Severity.Critical, "The PC showed a blue screen", e.Sample, "This PC had a blue screen (BugCheck). Find the stop code and the faulty driver or module from the event log and the minidump if there is one, and tell me the likely cause and fix. Do not change anything yet."));
                else if ((src.Equals("disk", StringComparison.OrdinalIgnoreCase) || src.Equals("Ntfs", StringComparison.OrdinalIgnoreCase) || src.Equals("volmgr", StringComparison.OrdinalIgnoreCase)) && e.Id is 7 or 11 or 15 or 51 or 55 or 129 or 153)
                    list.Add(Prompted($"disk-errors-{src}", Severity.Critical, "The disk is reporting errors", $"{src} event {e.Id} ({e.Count}x): {e.Sample} Back up important files soon.", "The System event log shows disk or file system errors. Check the drive's health (SMART if available) and what the errors point to, and tell me how urgent it is. Do not change anything yet."));
                else if (src.Equals("Microsoft-Windows-WHEA-Logger", StringComparison.OrdinalIgnoreCase))
                    list.Add(Prompted("whea", Severity.Critical, "Hardware errors were reported", $"WHEA event {e.Id} ({e.Count}x): {e.Sample}", "Windows logged hardware errors (WHEA). Work out which component they point to and how serious it is. Do not change anything yet."));
                else if (src.Equals("Service Control Manager", StringComparison.OrdinalIgnoreCase) && ServiceFailureIds.Contains(e.Id) && e.Count >= 2)
                    list.Add(Prompted($"service-{e.Id}", Severity.Warning, $"A service keeps failing ({e.Count}x)", e.Sample, "A Windows service keeps failing: " + Trim(e.Sample, 200) + " Find out why and propose a fix. Do not change anything yet."));
                else if (src.Equals("DCOM", StringComparison.OrdinalIgnoreCase) || src.Equals("Microsoft-Windows-DistributedCOM", StringComparison.OrdinalIgnoreCase)) continue;   // 10016 is harmless noise
                else if (e.Count >= 20)
                    list.Add(Prompted($"noisy-{src}-{e.Id}", Severity.Warning, $"{src} logged event {e.Id} {e.Count} times in a day", e.Sample, $"The event log shows {src} event {e.Id} {e.Count} times in the last day: {Trim(e.Sample, 200)} Tell me what it means and whether it matters. Do not change anything yet."));
            }

            if (f.StartupEnabled > 12)
                list.Add(new Finding("startup", Severity.Info, $"{f.StartupEnabled} programs start with Windows", "Many start-up programs slow down sign-in and use memory all day.", new[] { new DoctorFix("tool", "Show them", "startup_apps_list", new JsonObject()) }));

            if (f.AdaptersUp > 0 && f.DnsWorks == false)
                list.Add(new Finding("dns", Severity.Critical, "Names do not resolve (DNS is not working)", "A network adapter is connected but looking up a website name failed. Pages and updates will not load.", new[] { new DoctorFix("tool", "Show the network", "network_info", new JsonObject { ["action"] = "adapters" }), Prompt("Fix DNS", "My DNS is not working: the network adapter is connected but name lookups fail. Diagnose it (adapters, DNS servers, gateway, firewall, VPN) and propose the fix. Use the Windows tools.") }));
            else if (f.AdaptersUp > 0 && f.AdaptersWithGateway == 0)
                list.Add(new Finding("gateway", Severity.Warning, "No network gateway", "An adapter is up but has no default gateway, so it cannot reach beyond the local network.", new[] { new DoctorFix("tool", "Show the network", "network_info", new JsonObject { ["action"] = "adapters" }) }));

            if (f.PathMissing + f.PathDuplicates > 0)
                list.Add(new Finding("path", Severity.Info, "PATH has problems", $"{f.PathMissing} entr{(f.PathMissing == 1 ? "y points" : "ies point")} to folders that do not exist and {f.PathDuplicates} duplicate{(f.PathDuplicates == 1 ? "" : "s")}. This slows command lookups and can hide the wrong program.",
                    new[] { new DoctorFix("tool", "Show the problems", "path_check", new JsonObject()), new DoctorFix("tool", "Clean the user PATH", "path_edit", new JsonObject { ["action"] = "clean" }, Changes: true) }));

            if (f.TempGb >= 2)
                list.Add(new Finding("temp", Severity.Info, $"The temporary folder holds {f.TempGb:0.#} GB", "Leftover installer and cache files. Safe to clear once nothing is installing.",
                    new[] { new DoctorFix("tool", "What is in it", "disk_usage", new JsonObject { ["path"] = f.TempPath, ["top"] = 10 }) }));

            if (f.DefenderRealtimeOff == true)
                list.Add(new Finding("defender", Severity.Warning, "Real-time virus protection is off", "Microsoft Defender's real-time protection is turned off. That is fine only if another antivirus is active.", new[] { new DoctorFix("tool", "Check Defender", "defender_status", new JsonObject()) }));

            if (string.Equals(f.WindowsUpdateStartType, "disabled", StringComparison.OrdinalIgnoreCase))
                list.Add(new Finding("wuauserv", Severity.Warning, "The Windows Update service is disabled", "Updates (including security fixes) cannot be installed while it is disabled.", new[] { new DoctorFix("tool", "Show the service", "services_list", new JsonObject { ["filter"] = "wuauserv" }), new DoctorFix("settings", "Open Windows Update", Page: "windowsupdate") }));

            if (list.Count == 0)
                list.Add(new Finding("all-good", Severity.Ok, "No problems found", "Disk space, memory, restarts, start-up load, the network, the event log and PATH all look healthy.", Array.Empty<DoctorFix>()));
            return list.OrderBy(x => x.Severity).ThenBy(x => x.Title, StringComparer.Ordinal).ToList();
        }

        private static IReadOnlyList<DoctorFix> SpaceFixes(string drive) => new[]
        {
            new DoctorFix("tool", "Find what is using it", "disk_usage", new JsonObject { ["path"] = drive.EndsWith('\\') ? drive : drive + "\\", ["top"] = 12 }),
            Prompt("Help me free space", $"Drive {drive} is almost full. Use the Windows tools to find the biggest folders, tell me what is safe to remove and what is not, and ask me before deleting anything."),
        };

        private static DoctorFix Prompt(string label, string prompt) => new("prompt", label, Prompt: prompt);

        private static Finding Prompted(string id, Severity sev, string title, string detail, string prompt) =>
            new(id, sev, title, detail, new[] { Prompt("Ask the assistant to look into it", prompt) });

        private static string Trim(string s, int n) => s.Length > n ? s[..n] + "..." : s;

        // ---- gathering the facts ----

        /// <summary>Reads the PC. Each part is separate and bounded, so one slow or unavailable part never blocks the rest; what could not be read is listed in Skipped.</summary>
        public static async Task<DoctorFacts> CollectAsync(ToolRegistry registry, ChangeJournal journal, CancellationToken ct = default)
        {
            var f = new DoctorFacts();
            var ctx = new ToolContext { Journal = journal, Cancel = ct };

            void Safe(string name, Action a) { try { a(); } catch { f.Skipped.Add(name); } }

            Safe("drives", () =>
            {
                foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
                    f.Drives.Add(new DriveFact(d.Name.TrimEnd('\\'), Math.Round(d.TotalSize / 1073741824.0, 1), Math.Round(d.AvailableFreeSpace / 1073741824.0, 1)));
            });
            Safe("uptime", () => f.UptimeDays = Math.Round(TimeSpan.FromMilliseconds(Environment.TickCount64).TotalDays, 1));
            Safe("memory", () =>
            {
                if (MachineProfile.Memory() is { } m) { f.RamTotalGb = m.TotalGb; f.RamFreeGb = m.FreeGb; }
            });
            Safe("restart", () => f.PendingReboot = IsRebootPending());
            Safe("startup", () => f.StartupEnabled = StartupEntries.Read(new StartupLocations()).Count(e => e.Enabled));
            Safe("temp", () =>
            {
                f.TempPath = Path.GetTempPath().TrimEnd('\\');
                long bytes = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var stack = new Stack<string>();
                stack.Push(f.TempPath);
                while (stack.Count > 0 && sw.Elapsed < TimeSpan.FromSeconds(6))
                {
                    try
                    {
                        foreach (var e in new DirectoryInfo(stack.Pop()).EnumerateFileSystemInfos())
                        {
                            if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                            if (e is FileInfo fi) bytes += fi.Length; else stack.Push(e.FullName);
                        }
                    }
                    catch { }
                }
                f.TempGb = Math.Round(bytes / 1073741824.0, 2);
            });
            Safe("defender", () =>
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender\Real-Time Protection");
                if (k?.GetValue("DisableRealtimeMonitoring") is int v) f.DefenderRealtimeOff = v == 1;
            });
            Safe("updates", () => f.WindowsUpdateStartType = ServicesListTool.StartType("wuauserv"));
            Safe("network", () =>
            {
                var up = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel).ToList();
                f.AdaptersUp = up.Count;
                f.AdaptersWithGateway = up.Count(n => n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)));
            });
            if (f.AdaptersUp > 0)
            {
                try
                {
                    using var dns = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    dns.CancelAfter(TimeSpan.FromSeconds(4));
                    f.DnsWorks = (await Dns.GetHostAddressesAsync("www.microsoft.com", dns.Token)).Length > 0;
                }
                catch (OperationCanceledException) { f.DnsWorks = false; }
                catch (System.Net.Sockets.SocketException) { f.DnsWorks = false; }
                catch { f.Skipped.Add("dns"); }
            }
            try
            {
                var path = await registry.CallAsync("path_check", new JsonObject(), ctx);
                if (path.Data is JsonArray arr)
                {
                    f.PathMissing = arr.OfType<JsonObject>().Count(o => (string?)o["problem"] == "missing");
                    f.PathDuplicates = arr.OfType<JsonObject>().Count(o => (string?)o["problem"] == "duplicate");
                }
            }
            catch { f.Skipped.Add("path"); }
            try
            {
                var ev = await registry.CallAsync("event_log", new JsonObject { ["log"] = "System", ["hours"] = 24 }, ctx);
                if (ev.Ok && ev.Data is JsonArray groups)
                    f.Events.AddRange(groups.OfType<JsonObject>().Select(g => new EventFact((string?)g["source"] ?? "?", (int?)g["event_id"] ?? 0, (int?)g["count"] ?? 1, (string?)g["sample"] ?? "")));
                else f.Skipped.Add("event log");
            }
            catch { f.Skipped.Add("event log"); }
            return f;
        }

        private static bool IsRebootPending()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")) if (k != null) return true;
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")) if (k != null) return true;
            using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
                if (k?.GetValue("PendingFileRenameOperations") is string[] ops && ops.Length > 0) return true;
            return false;
        }

        public static JsonObject ToJson(IReadOnlyList<Finding> findings, DoctorFacts facts) => new()
        {
            ["findings"] = new JsonArray(findings.Select(x => (JsonNode)new JsonObject
            {
                ["id"] = x.Id, ["severity"] = x.Severity.ToString().ToLowerInvariant(), ["title"] = x.Title, ["detail"] = x.Detail,
                ["fixes"] = new JsonArray(x.Fixes.Select((fx, i) => (JsonNode)new JsonObject { ["index"] = i, ["kind"] = fx.Kind, ["label"] = fx.Label, ["changes"] = fx.Changes }).ToArray()),
            }).ToArray()),
            ["skipped"] = new JsonArray(facts.Skipped.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        };
    }
}
