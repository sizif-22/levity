using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Levity.Services.WindowsTools
{
    /// <summary>
    /// What this PC is, built once and kept: Windows edition and build, hardware, drives, installed developer tools with versions, shells
    /// and the power plan. The assistant gets a short summary with its first message, so it does not spend steps (and about 8 seconds per
    /// shell start) discovering things the PC already knows. Rebuilt after a day or on request; built in the background.
    /// </summary>
    public sealed class MachineProfile
    {
        public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

        private readonly string _cacheFile;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private JsonObject? _memory;

        public MachineProfile(string cacheFile) { _cacheFile = cacheFile; }

        /// <summary>The saved profile if it is fresh; otherwise rebuilt (and saved). Never throws.</summary>
        public async Task<JsonObject> GetAsync(bool refresh = false, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (!refresh)
                {
                    _memory ??= LoadFresh();
                    if (_memory != null) return _memory;
                }
                var built = await BuildAsync(ct);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
                    File.WriteAllText(_cacheFile, built.ToJsonString());
                }
                catch { /* a profile that cannot be saved is still useful */ }
                _memory = built;
                return built;
            }
            finally { _gate.Release(); }
        }

        /// <summary>The saved profile without building anything (null when there is none or it is old).</summary>
        public JsonObject? Peek() => _memory ??= LoadFresh();

        private JsonObject? LoadFresh()
        {
            try
            {
                if (!File.Exists(_cacheFile)) return null;
                var n = JsonNode.Parse(File.ReadAllText(_cacheFile)) as JsonObject;
                if (n == null || !DateTime.TryParse((string?)n["built_utc"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var built)) return null;
                return DateTime.UtcNow - built > MaxAge ? null : n;
            }
            catch { return null; }
        }

        // ---- building ----

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatus
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

        private static readonly (string Name, string[] Files, string[] VersionArgs)[] DevTools =
        {
            ("git", new[] { "git" }, new[] { "--version" }),
            ("node", new[] { "node" }, new[] { "--version" }),
            ("npm", new[] { "npm" }, new[] { "--version" }),
            ("bun", new[] { "bun" }, new[] { "--version" }),
            ("pnpm", new[] { "pnpm" }, new[] { "--version" }),
            ("python", new[] { "python", "py" }, new[] { "--version" }),
            ("dotnet", new[] { "dotnet" }, new[] { "--version" }),
            ("docker", new[] { "docker" }, new[] { "--version" }),
            ("code", new[] { "code" }, new[] { "--version" }),
            ("winget", new[] { "winget" }, new[] { "--version" }),
            ("wsl", new[] { "wsl" }, Array.Empty<string>()),            // its output is UTF-16, so only its presence is recorded
            ("cloudflared", new[] { "cloudflared" }, new[] { "--version" }),
            ("pwsh", new[] { "pwsh" }, new[] { "--version" }),
        };

        public static string? FindOnPath(string name)
        {
            string[] exts = { ".exe", ".cmd", ".bat" };
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    foreach (string ext in exts)
                    {
                        string candidate = Path.Combine(dir.Trim('"'), name + ext);
                        if (File.Exists(candidate)) return candidate;
                    }
                }
                catch { }
            }
            return null;
        }

        public static JsonObject BuildFast()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string product = (key?.GetValue("ProductName") as string) ?? "Windows";
            string display = (key?.GetValue("DisplayVersion") as string) ?? (key?.GetValue("ReleaseId") as string) ?? "";
            string build = (key?.GetValue("CurrentBuild") as string) ?? "";
            int ubr = key?.GetValue("UBR") is int u ? u : 0;
            if (int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");

            string cpu = "";
            using (var c = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")) cpu = ((c?.GetValue("ProcessorNameString") as string) ?? "").Trim();

            var mem = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            double ramGb = GlobalMemoryStatusEx(ref mem) ? Math.Round(mem.TotalPhys / 1073741824.0, 1) : 0;

            var drives = new JsonArray();
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
                drives.Add(new JsonObject { ["drive"] = d.Name, ["total_gb"] = Math.Round(d.TotalSize / 1073741824.0, 0), ["free_gb"] = Math.Round(d.AvailableFreeSpace / 1073741824.0, 0), ["format"] = d.DriveFormat });

            return new JsonObject
            {
                ["built_utc"] = DateTime.UtcNow.ToString("o"),
                ["windows"] = new JsonObject { ["edition"] = product, ["version"] = display, ["build"] = ubr > 0 ? $"{build}.{ubr}" : build, ["architecture"] = RuntimeInformation.OSArchitecture.ToString() },
                ["cpu"] = new JsonObject { ["name"] = cpu, ["logical_cores"] = Environment.ProcessorCount },
                ["ram_gb"] = ramGb,
                ["drives"] = drives,
                ["user_profile"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ["shells"] = new JsonArray(new[] { "powershell", "pwsh", "cmd" }.Where(s => s == "cmd" || FindOnPath(s) != null).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            };
        }

        public static async Task<JsonObject> BuildAsync(CancellationToken ct = default)
        {
            var profile = BuildFast();
            var tools = new JsonObject();
            using var limit = new SemaphoreSlim(4);
            var jobs = DevTools.Select(async t =>
            {
                string? path = t.Files.Select(FindOnPath).FirstOrDefault(p => p != null);
                if (path == null) return;
                string version = "";
                if (t.VersionArgs.Length > 0)
                {
                    await limit.WaitAsync(ct);
                    try
                    {
                        var r = await Proc.RunAsync(path, t.VersionArgs, 6, ct);
                        if (r.ExitCode == 0 && !r.TimedOut) version = r.Text.Split('\n').FirstOrDefault()?.Trim() ?? "";
                    }
                    finally { limit.Release(); }
                }
                lock (tools) tools[t.Name] = new JsonObject { ["version"] = version, ["path"] = path };
            }).ToArray();
            try { await Task.WhenAll(jobs); } catch { /* one probe failing must not lose the rest */ }
            profile["dev_tools"] = new JsonObject(tools.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => KeyValuePair.Create(k.Key, k.Value is JsonNode n ? JsonNode.Parse(n.ToJsonString()) : null)));

            try
            {
                var plans = await PowerPlans.ListAsync();
                var active = plans.FirstOrDefault(p => p.Active);
                if (active != null) profile["power_plan"] = active.Name;
            }
            catch { }
            return profile;
        }

        /// <summary>A few lines the assistant reads at the start of a conversation. Kept short on purpose: every character is paid for on every turn.</summary>
        public static string Summary(JsonObject p)
        {
            var sb = new StringBuilder();
            var w = p["windows"] as JsonObject;
            sb.Append("This PC: ").Append((string?)w?["edition"]).Append(' ').Append((string?)w?["version"]).Append(" (build ").Append((string?)w?["build"]).Append(", ").Append((string?)w?["architecture"]).Append("). ");
            string? cpu = (string?)p["cpu"]?["name"];
            if (!string.IsNullOrEmpty(cpu)) sb.Append(cpu).Append(", ").Append((int?)p["cpu"]?["logical_cores"]).Append(" threads, ");
            sb.Append((double?)p["ram_gb"]).Append(" GB RAM. ");
            if (p["drives"] is JsonArray drives && drives.Count > 0)
                sb.Append("Drives: ").Append(string.Join(", ", drives.OfType<JsonObject>().Select(d => $"{(string?)d["drive"]}".TrimEnd('\\') + $" {(double?)d["free_gb"]}/{(double?)d["total_gb"]} GB free"))).Append(". ");
            if (p["dev_tools"] is JsonObject tools && tools.Count > 0)
                sb.Append("Installed: ").Append(string.Join(", ", tools.Select(kv =>
                {
                    string v = (string?)kv.Value?["version"] ?? "";
                    v = System.Text.RegularExpressions.Regex.Match(v, @"\d+(\.\d+)+").Value;
                    return v.Length > 0 ? $"{kv.Key} {v}" : kv.Key;
                }))).Append(". ");
            if (p["power_plan"] != null) sb.Append("Power plan: ").Append((string?)p["power_plan"]).Append(". ");
            sb.Append("Home: ").Append((string?)p["user_profile"]).Append('.');
            return sb.ToString();
        }
    }
}
