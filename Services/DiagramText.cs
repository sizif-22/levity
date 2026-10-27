using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AntigravitySidebar.Services
{
    /// <summary>Cleaning of model-supplied text and paths. Nothing from the model reaches the UI, Mermaid or the file system without passing through here.</summary>
    public static class DiagramText
    {
        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
        private static readonly string[] ReservedNames =
        {
            "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
            "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
        };

        /// <summary>
        /// Plain visible text: NFC, no control characters, no zero-width or bidirectional-override characters
        /// (used to disguise text), single spaces. Returns "" when nothing visible is left.
        /// </summary>
        public static string Normalize(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length);
            foreach (char c in value.Normalize(NormalizationForm.FormC))
            {
                if (c < 0x20 || (c >= 0x7F && c <= 0x9F)) { sb.Append(' '); continue; }
                if (c == '\u2028' || c == '\u2029') { sb.Append(' '); continue; }
                if ((c >= '\u200B' && c <= '\u200F') || (c >= '\u202A' && c <= '\u202E') || (c >= '\u2060' && c <= '\u2064') || (c >= '\u2066' && c <= '\u2069') || c == '\uFEFF') continue;
                sb.Append(c);
            }
            return Whitespace.Replace(sb.ToString(), " ").Trim();
        }

        /// <summary>Normalize, then cut to <paramref name="max"/> characters (ending with an ellipsis if cut).</summary>
        public static string Limit(string? value, int max)
        {
            string s = Normalize(value);
            if (s.Length <= max) return s;
            return s.Substring(0, Math.Max(0, max - 1)).TrimEnd() + "\u2026";
        }

        /// <summary>
        /// A repo-relative path with forward slashes, or null if it is anything else: absolute, drive-qualified,
        /// parent-escaping, containing wildcards, control or reserved device names, or too long.
        /// </summary>
        public static string? NormalizeRepoPath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string p = value.Trim().Replace('\\', '/');
            while (p.StartsWith("./", StringComparison.Ordinal)) p = p.Substring(2);
            p = p.TrimEnd('/');
            if (p.Length == 0 || p.Length > DiagramLimits.MaxPath) return null;
            if (p[0] == '/' || p.Contains(':') || p.Contains("//", StringComparison.Ordinal)) return null;
            if (p.Any(c => c < 0x20 || c == 0x7F || "\"<>|?*".IndexOf(c) >= 0)) return null;
            if (Normalize(p) != p) return null; // hidden or odd characters

            foreach (string seg in p.Split('/'))
            {
                if (seg.Length == 0 || seg == "." || seg == "..") return null;
                if (seg.EndsWith(' ') || seg.EndsWith('.')) return null; // Windows silently strips these
                string stem = seg.Split('.')[0].ToLowerInvariant();
                if (Array.IndexOf(ReservedNames, stem) >= 0) return null;
            }
            return p;
        }

        /// <summary>
        /// Text that is safe inside a double-quoted Mermaid label. Every character Mermaid or HTML could treat
        /// as syntax becomes a numeric entity, so model text can only ever be displayed, never interpreted.
        /// </summary>
        public static string EscapeMermaidLabel(string? value)
        {
            string s = Normalize(value);
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if ("\"#&<>\\|[](){}`;%:*~_^".IndexOf(c) >= 0) sb.Append('#').Append((int)c).Append(';');
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
