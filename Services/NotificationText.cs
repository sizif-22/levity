using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Levity.Services
{
    /// <summary>What happened in one assistant turn, for the notice that says it finished.</summary>
    public sealed record TurnSummary(string Folder, string? Request, string? Answer, int Steps, TimeSpan Elapsed, int Files, int Added, int Removed, bool Failed);

    /// <summary>
    /// The words of the phone push and the Windows notice. Plain functions so they can be tested without a window. A notice says which
    /// project, what was asked, how the answer starts and what it cost (steps, time, files changed), so it can be judged from the lock screen.
    /// The push text is encrypted to the phone (RFC 8291), but a lock screen shows it, so LEVITY_PUSH_DETAILS=0 sends the plain "Levity has news".
    /// </summary>
    public static class NotificationText
    {
        public const string GenericTitle = "Levity has news";
        public const string GenericBody = "Open Levity to see it.";
        public const int RequestChars = 70, AnswerChars = 150, CommandChars = 100;

        /// <summary>On unless LEVITY_PUSH_DETAILS=0.</summary>
        public static bool PushDetailsEnabled => !string.Equals(DotEnv.Get("LEVITY_PUSH_DETAILS"), "0", StringComparison.Ordinal);

        private static readonly Regex Link = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);
        private static readonly Regex Marks = new(@"[#*`>]|(?<![A-Za-z0-9])_|_(?![A-Za-z0-9])", RegexOptions.Compiled);
        private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

        /// <summary>Markdown marks removed, white space folded, cut at a word with an ellipsis when longer than <paramref name="max"/>.</summary>
        public static string Snippet(string? text, int max, bool markdown = true)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string t = text;
            if (markdown) t = Marks.Replace(Link.Replace(t, "$1"), " ");   // a command keeps its > and *
            t = Spaces.Replace(t, " ").Trim();
            if (t.Length <= max) return t;
            int cut = t.LastIndexOf(' ', max - 1);
            if (cut < max * 6 / 10) cut = max - 1;   // no good word break near the end: cut hard
            return t[..cut].TrimEnd(' ', ',', ';', ':', '.', '-') + "…";
        }

        public static string FolderName(string? cwd)
        {
            if (string.IsNullOrWhiteSpace(cwd)) return "Home";
            string name = System.IO.Path.GetFileName(cwd.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? cwd.TrimEnd('\\', '/') : name;
        }

        public static string Duration(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds} s";
            if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min {t.Seconds:00} s";
            return $"{(int)t.TotalHours} h {t.Minutes:00} min";
        }

        /// <summary>
        /// Notices from different projects must not replace each other, so the tag is per project: lower-case letters, digits and "-" only
        /// (it comes from a folder name and ends up in the push payload).
        /// </summary>
        public static string Tag(string folder)
        {
            var sb = new StringBuilder("levity-");
            foreach (char c in folder.ToLowerInvariant())
            {
                if (sb.Length >= 40) break;
                sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
            }
            return sb.ToString();
        }

        public static (string Title, string Body) ForTurn(TurnSummary s)
        {
            string folder = string.IsNullOrWhiteSpace(s.Folder) ? "Home" : s.Folder;
            string title = $"Levity · {folder} · {(s.Failed ? "failed" : "done")}";

            var lines = new List<string>();
            string request = Snippet(s.Request, RequestChars);
            if (request.Length > 0) lines.Add("Re: " + request);
            string answer = Snippet(s.Answer, AnswerChars);
            if (answer.Length > 0) lines.Add(answer);

            var facts = new List<string>();
            if (s.Steps > 0) facts.Add(s.Steps == 1 ? "1 step" : $"{s.Steps} steps");
            if (s.Elapsed > TimeSpan.Zero) facts.Add(Duration(s.Elapsed));
            if (s.Files > 0) facts.Add($"{s.Files} file{(s.Files == 1 ? "" : "s")} changed" + (s.Added + s.Removed > 0 ? $" (+{s.Added}/-{s.Removed})" : ""));
            if (facts.Count > 0) lines.Add(string.Join(" · ", facts));

            if (lines.Count == 0) lines.Add(s.Failed ? $"The assistant stopped with an error in {folder}." : $"The assistant finished in {folder}.");
            return (title, string.Join("\n", lines));
        }

        public static (string Title, string Body) ForJob(int id, string status, string? cwd, string? command)
        {
            string folder = FolderName(cwd);
            string cmd = Snippet(command, CommandChars, markdown: false);
            return ($"Levity · {folder} · job {status}", cmd.Length > 0 ? $"Job #{id}: {cmd}" : $"Job #{id} {status}");
        }
    }
}
