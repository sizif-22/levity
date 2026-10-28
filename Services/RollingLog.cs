using System;
using System.IO;

namespace Levity.Services
{
    /// <summary>
    /// The small text logs next to Levity.exe (startup.log, debug.log). They used to grow forever, and debug.log held every word of every
    /// conversation. Now a log is cut to a fresh file when it passes 1 MB (the old one is kept once as ".1"), and the conversation
    /// traffic is only written when LEVITY_DEBUG_LOG=1.
    /// </summary>
    public static class RollingLog
    {
        public const long MaxBytes = 1_000_000;
        private static readonly object Gate = new();

        /// <summary>Whether to record the full assistant traffic (every message and every event). Off unless asked for.</summary>
        public static bool Verbose => string.Equals(DotEnv.Get("LEVITY_DEBUG_LOG"), "1", StringComparison.Ordinal);

        public static void Append(string fileName, string text) => AppendTo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName), text, MaxBytes);

        public static void AppendTo(string path, string text) => AppendTo(path, text, MaxBytes);

        public static void AppendTo(string path, string text, long maxBytes)
        {
            try
            {
                lock (Gate)
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > maxBytes)
                    {
                        string old = path + ".1";
                        try { if (File.Exists(old)) File.Delete(old); } catch { }
                        try { File.Move(path, old); } catch { try { File.Delete(path); } catch { } }
                    }
                    File.AppendAllText(path, text);
                }
            }
            catch { /* a log must never be the reason something fails */ }
        }
    }
}
