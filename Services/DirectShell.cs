using System.IO;
using System.Text.RegularExpressions;

namespace AntigravitySidebar.Services
{
    /// <summary>Helpers for "/cmd{$...}" commands that run directly in PowerShell (see JobManager).</summary>
    public static class DirectShell
    {
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
    }
}
