using System;
using System.IO;

namespace AntigravitySidebar.Services
{
    /// <summary>
    /// Minimal .env loader. Looks for LEVITY_ENV_FILE, then ".env" next to Levity.exe, then in the settings folder.
    /// Values already present in the real environment win, so a shell variable always overrides the file.
    /// </summary>
    public static class DotEnv
    {
        public static void Load()
        {
            string? explicitFile = Environment.GetEnvironmentVariable("LEVITY_ENV_FILE");
            string settingsDir = Environment.GetEnvironmentVariable("LEVITY_SETTINGS_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AntigravitySidebar");

            foreach (string? path in new[]
            {
                explicitFile,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env"),
                Path.Combine(settingsDir, ".env")
            })
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                try { Apply(File.ReadAllLines(path)); } catch { }
            }
        }

        public static void Apply(string[] lines)
        {
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith("export ")) line = line.Substring(7).TrimStart();

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();

                if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                    value = value.Substring(1, value.Length - 2);
                else
                {
                    int hash = value.IndexOf(" #", StringComparison.Ordinal); // trailing comment on an unquoted value
                    if (hash >= 0) value = value.Substring(0, hash).TrimEnd();
                }

                if (value.Length == 0) continue; // "KEY=" means unset
                if (Environment.GetEnvironmentVariable(key) == null) Environment.SetEnvironmentVariable(key, value);
            }
        }

        public static string? Get(string key)
        {
            string? v = Environment.GetEnvironmentVariable(key);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        public static double GetDouble(string key, double fallback) =>
            double.TryParse(Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) && d > 0 ? d : fallback;
    }
}
