using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Levity.Services.WindowsTools
{
    /// <summary>Runs a program with fixed arguments and returns its output. Arguments are a list, never a command line, so nothing is re-parsed.</summary>
    public static class Proc
    {
        public sealed record Output(int ExitCode, string Text, bool TimedOut);

        public static async Task<Output> RunAsync(string file, IEnumerable<string> args, int timeoutSeconds = 20, CancellationToken ct = default, IDictionary<string, string>? environment = null)
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            if (environment != null) foreach (var kv in environment) psi.Environment[kv.Key] = kv.Value;   // how text reaches a script: as data, never spliced into it
            using var p = new Process { StartInfo = psi };
            var sb = new StringBuilder();
            var gate = new object();
            void OnData(object _, DataReceivedEventArgs e) { if (e.Data != null) lock (gate) { if (sb.Length < 400_000) sb.AppendLine(e.Data); } }
            p.OutputDataReceived += OnData;
            p.ErrorDataReceived += OnData;
            try { p.Start(); }
            catch (Exception ex) { return new Output(-1, $"Could not start {Path.GetFileName(file)}: {ex.Message}", false); }
            try { p.StandardInput.Close(); } catch { }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try { await p.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return new Output(-1, sb.ToString().TrimEnd(), true);
            }
            p.WaitForExit(1000);
            lock (gate) return new Output(p.ExitCode, sb.ToString().TrimEnd(), false);
        }
    }

    /// <summary>The few Windows calls the tools need.</summary>
    internal static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder buffer, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, string value, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

        private const uint SPI_GETDESKWALLPAPER = 0x0073;
        private const uint SPI_SETDESKWALLPAPER = 0x0014;
        private const uint SPIF_UPDATEINIFILE_SENDCHANGE = 0x03;
        private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
        private const uint WM_SETTINGCHANGE = 0x001A;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        public static string GetWallpaper()
        {
            var sb = new StringBuilder(520);
            return SystemParametersInfo(SPI_GETDESKWALLPAPER, 520, sb, 0) ? sb.ToString() : "";
        }

        public static bool SetWallpaper(string path) => SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE_SENDCHANGE);

        /// <summary>Tells running programs that a setting changed ("ImmersiveColorSet" for theme, "Environment" for variables).</summary>
        public static void BroadcastSettingChange(string area) =>
            SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, area, SMTO_ABORTIFHUNG, 2000, out _);

        // ---- listening ports and who owns them ----

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

        public sealed record TcpEntry(int LocalPort, int Pid, string State);

        private static readonly string[] TcpStates = { "", "CLOSED", "LISTEN", "SYN_SENT", "SYN_RCVD", "ESTABLISHED", "FIN_WAIT1", "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK", "TIME_WAIT", "DELETE_TCB" };

        public static IReadOnlyList<TcpEntry> TcpTable()
        {
            const int AF_INET = 2, TCP_TABLE_OWNER_PID_ALL = 5;
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return Array.Empty<TcpEntry>();
                int count = Marshal.ReadInt32(buf);
                var list = new List<TcpEntry>(count);
                IntPtr row = buf + 4;
                for (int i = 0; i < count; i++, row += 24)
                {
                    int state = Marshal.ReadInt32(row);
                    int localPort = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, 8));
                    int pid = Marshal.ReadInt32(row, 20);
                    list.Add(new TcpEntry(localPort, pid, state is >= 0 and < 13 ? TcpStates[state] : state.ToString()));
                }
                return list;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    /// <summary>Reading and restoring registry values, used by every tool that changes one so the undo is always the same code.</summary>
    internal static class RegistryUndo
    {
        public static RegistryKey OpenRoot(string root, bool writable) => root switch
        {
            "HKCU" => Registry.CurrentUser,
            "HKLM" => Registry.LocalMachine,
            _ => throw new ToolInputException("Unknown registry root."),
        };

        /// <summary>Records the current state of the named values so they can be put back exactly (including "was not there").</summary>
        public static JsonObject Snapshot(string root, string subKey, IEnumerable<string> names)
        {
            var values = new JsonArray();
            using var key = OpenRoot(root, false).OpenSubKey(subKey, false);
            foreach (string name in names)
            {
                var item = new JsonObject { ["name"] = name };
                object? v = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (v == null) item["exists"] = false;
                else
                {
                    var kind = key!.GetValueKind(name);
                    item["exists"] = true;
                    item["kind"] = kind.ToString();
                    item["value"] = v switch
                    {
                        byte[] b => Convert.ToBase64String(b),
                        int i => i,
                        long l => l,
                        string[] arr => new JsonArray(arr.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                        _ => v.ToString(),
                    };
                }
                values.Add(item);
            }
            return new JsonObject { ["root"] = root, ["subKey"] = subKey, ["values"] = values };
        }

        private static long Number(JsonNode? n) => n == null ? 0 : long.Parse(n.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Puts a snapshot back. Returns null on success or the problem.</summary>
        public static string? Restore(JsonObject snap, string? broadcastArea = null)
        {
            string root = (string?)snap["root"] ?? "", subKey = (string?)snap["subKey"] ?? "";
            var values = snap["values"] as JsonArray;
            if (root.Length == 0 || subKey.Length == 0 || values == null) return "The saved state is damaged.";
            using var key = OpenRoot(root, true).CreateSubKey(subKey, true);
            foreach (var item in values.OfType<JsonObject>())
            {
                string name = (string?)item["name"] ?? "";
                if (!((bool?)item["exists"] ?? false)) { key.DeleteValue(name, false); continue; }
                var kind = Enum.Parse<RegistryValueKind>((string?)item["kind"] ?? "String");
                object value = kind switch
                {
                    RegistryValueKind.DWord => unchecked((int)Number(item["value"])),
                    RegistryValueKind.QWord => Number(item["value"]),
                    RegistryValueKind.Binary => Convert.FromBase64String((string?)item["value"] ?? ""),
                    RegistryValueKind.MultiString => ((JsonArray)item["value"]!).Select(x => (string?)x ?? "").ToArray(),
                    _ => (string?)item["value"] ?? "",
                };
                key.SetValue(name, value, kind);
            }
            if (broadcastArea != null) Native.BroadcastSettingChange(broadcastArea);
            return null;
        }
    }
}
