using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AntigravitySidebar.Services
{
    public record ShellResult(string Output, int ExitCode, double Seconds, bool TimedOut);

    /// <summary>Runs a /cmd command straight in PowerShell, no AI round-trip.</summary>
    public static class DirectShell
    {
        private const int MaxOutputChars = 20000;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
        private static readonly Regex CdPattern = new(@"^(?:cd|chdir|set-location|sl)(?:\s+/d)?(?:\s+(?<p>.+))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>If the command is a plain "cd", returns the absolute target (may not exist); otherwise null.</summary>
        public static string? ResolveCd(string command, string cwd)
        {
            var m = CdPattern.Match(command.Trim());
            if (!m.Success) return null;
            string p = m.Groups["p"].Success ? m.Groups["p"].Value.Trim().Trim('"', '\'') : "";
            if (p.Length == 0 || p == "~") return AppSettings.HomeFolder;
            if (p.StartsWith("~/") || p.StartsWith("~\\")) p = Path.Combine(AppSettings.HomeFolder, p.Substring(2));
            try { return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(cwd, p)); }
            catch { return p; }
        }

        public static async Task<ShellResult> RunAsync(string command, string cwd)
        {
            var sw = Stopwatch.StartNew();
            var psi = new ProcessStartInfo("powershell.exe")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + command })
                psi.ArgumentList.Add(a);

            var output = new StringBuilder();
            var gate = new object();
            void Append(string? line)
            {
                if (line == null) return;
                lock (gate) { if (output.Length < MaxOutputChars) output.AppendLine(line); }
            }

            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => Append(e.Data);
            proc.ErrorDataReceived += (_, e) => Append(e.Data);
            try
            {
                proc.Start();
                proc.StandardInput.Close(); // commands that prompt must not hang
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                return new ShellResult($"Could not start PowerShell: {ex.Message}", -1, 0, false);
            }

            bool timedOut = false;
            using var cts = new System.Threading.CancellationTokenSource(Timeout);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
            if (!timedOut) proc.WaitForExit(); // flush async readers

            string text;
            lock (gate) text = output.ToString().TrimEnd();
            if (text.Length >= MaxOutputChars) text += "\n… output truncated";
            return new ShellResult(text, timedOut ? -1 : proc.ExitCode, sw.Elapsed.TotalSeconds, timedOut);
        }
    }
}
