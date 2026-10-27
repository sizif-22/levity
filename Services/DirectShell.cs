using System.IO;
using System.Text.RegularExpressions;

namespace Levity.Services
{
    /// <summary>Helpers for "/cmd{$...}" commands that run directly in PowerShell (see JobManager).</summary>
    public static class DirectShell
    {
        private static readonly Regex CdPattern = new(@"^(?:cd|chdir|set-location|sl)(?:\s+/d)?(?:\s+(?<p>.+))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BackgroundFlag = new(@"(?:^-b\s+|\s+-b$)", RegexOptions.Compiled);

        /// <summary>"-b" at the start or end asks for a background job straight away. Returns the command without the flag.</summary>
        public static bool StripBackgroundFlag(string command, out string stripped)
        {
            stripped = BackgroundFlag.Replace(command.Trim(), "").Trim();
            return stripped.Length > 0 && stripped != command.Trim();
        }

        /// <summary>
        /// Reads the flags in front of or after a command: "-b" (background) and "-i" (interactive, the command can ask questions).
        /// "-i" is only recognised at the start, because it is a real option of many tools (grep -i, sed -i).
        /// </summary>
        public static void StripFlags(string command, out string stripped, out bool background, out bool interactive)
        {
            string s = (command ?? "").Trim();
            background = interactive = false;
            while (true)
            {
                if (s.StartsWith("-i ", StringComparison.Ordinal)) { interactive = true; s = s.Substring(3).TrimStart(); continue; }
                if (s.StartsWith("-b ", StringComparison.Ordinal)) { background = true; s = s.Substring(3).TrimStart(); continue; }
                if (s.EndsWith(" -b", StringComparison.Ordinal)) { background = true; s = s.Substring(0, s.Length - 3).TrimEnd(); continue; }
                break;
            }
            if (s.Length == 0) { stripped = (command ?? "").Trim(); background = interactive = false; return; }
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
