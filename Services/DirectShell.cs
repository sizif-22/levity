using System.IO;
using System.Text.RegularExpressions;

namespace Levity.Services
{
    /// <summary>Helpers for "/cmd{$...}" commands that run directly in PowerShell (see JobManager).</summary>
    public static class DirectShell
    {
        private static readonly Regex CdPattern = new(@"^(?:cd|chdir|set-location|sl)(?:\s+/d)?(?:\s+(?<p>.+))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Reads the "-i" flag in front of a command (interactive: it can ask questions, so it runs in a terminal of its own).
        /// Only at the start, because "-i" is a real option of many tools (grep -i, sed -i). A slow command needs no flag: anything
        /// still running after a few seconds moves to the Jobs panel by itself.
        /// </summary>
        public static void StripFlags(string command, out string stripped, out bool interactive)
        {
            string s = (command ?? "").Trim();
            interactive = false;
            while (s.StartsWith("-i ", StringComparison.Ordinal)) { interactive = true; s = s.Substring(3).TrimStart(); }
            if (s.Length == 0) { stripped = (command ?? "").Trim(); interactive = false; return; }
            stripped = s;
        }

        /// <summary>If the command is a plain "cd", returns the absolute target (may not exist); otherwise null.</summary>
        public static string? ResolveCd(string command, string cwd)
        {
            var m = CdPattern.Match(command.Trim());
            if (!m.Success) return null;
            string raw = m.Groups["p"].Success ? m.Groups["p"].Value.Trim() : "";
            // "cd .. && dir" is a real shell line, not a folder change: leave it to PowerShell.
            if (raw.IndexOfAny(new[] { '&', '|', ';', '<', '>' }) >= 0) return null;
            string p = raw.Trim('"', '\'');
            if (p.Length == 0 || p == "~") return AppSettings.HomeFolder;
            if (p.StartsWith("~/") || p.StartsWith("~\\")) p = Path.Combine(AppSettings.HomeFolder, p.Substring(2));
            try { return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(cwd, p)); }
            catch { return p; }
        }
    }
}
