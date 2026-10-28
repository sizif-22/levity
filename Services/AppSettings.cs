using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Levity.Services
{
    public class AppSettings
    {
        public bool IsPinned { get; set; } = false;
        public string? WorkingDirectory { get; set; }
        public double SidebarWidth { get; set; } = DotEnv.GetDouble("LEVITY_SIDEBAR_WIDTH", 460);
        public bool LaunchOnStartup { get; set; } = false;

        // Windows tools for the assistant (an MCP server inside Levity, loopback only). Off until the user turns it on.
        public bool WindowsToolsEnabled { get; set; } = false;
        public int WindowsToolsPort { get; set; }
        public string? WindowsToolsToken { get; set; }

        /// <summary>How hard the assistant thinks: low (fast, the default), medium or high (deep). Fast is plenty for most tasks and much quicker.</summary>
        public string Effort { get; set; } = AgentLaunch.Fast;

        /// <summary>"auto" (the assistant works normally) or "plan" (it only plans and proposes, changing nothing).</summary>
        public string Mode { get; set; } = AgentLaunch.Auto;

        // Mobile access through the relay (off until the user turns it on)
        public bool RemoteEnabled { get; set; } = false;
        public string? RelayUrl { get; set; } = DotEnv.Get("LEVITY_RELAY_URL"); // wss://levity-relay.<account>.workers.dev
        public string? MobileAppUrl { get; set; } = DotEnv.Get("LEVITY_APP_URL");
        public List<PairedDevice> Devices { get; set; } = new();

        // Project review
        public string ProjectsRoot { get; set; } = DotEnv.Get("LEVITY_PROJECTS_ROOT") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public string? SyncPrompt { get; set; }

        // Project list ordering: keys are lower-cased full paths
        // Folders the user picked by hand (any path, not only under ProjectsRoot), newest first
        public List<string> RecentFolders { get; set; } = new();
        public List<string> PinnedProjects { get; set; } = new();
        public Dictionary<string, int> ProjectUses { get; set; } = new();
        public Dictionary<string, DateTime> ProjectLastUsed { get; set; } = new();

        public static string ProjectKey(string path) => path.TrimEnd('\\', '/').ToLowerInvariant();

        public void RecordProjectUse(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string key = ProjectKey(path);
            ProjectUses[key] = ProjectUses.GetValueOrDefault(key) + 1;
            ProjectLastUsed[key] = DateTime.UtcNow;
            Save();
        }

        public static string HomeFolder => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public void AddRecentFolder(string path)
        {
            string full = Path.GetFullPath(path).TrimEnd('\\', '/');
            if (full.Length == 0 || string.Equals(full, HomeFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            RecentFolders.RemoveAll(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase));
            RecentFolders.Insert(0, full);
            if (RecentFolders.Count > 12) RecentFolders.RemoveRange(12, RecentFolders.Count - 12);
        }

        public void SetPinned(string path, bool pinned)
        {
            string key = ProjectKey(path);
            PinnedProjects.RemoveAll(k => k == key);
            if (pinned) PinnedProjects.Add(key);
            Save();
        }

        /// <summary>The folder name used before the app was called Levity. Its contents are moved to the new folder on first run.</summary>
        public const string LegacyDataFolderName = "AntigravitySidebar";

        /// <summary>
        /// Where settings, saved chats and diagrams live: LEVITY_SETTINGS_DIR if set, else %APPDATA%\Levity. If only the old
        /// %APPDATA%\AntigravitySidebar exists it is moved over first. If the move fails (a file is in use) the old folder keeps being
        /// used for this run, so nothing is ever lost or split between two places.
        /// </summary>
        public static string ResolveDataDir(string appData, string? envOverride)
        {
            if (!string.IsNullOrWhiteSpace(envOverride)) return envOverride;
            string current = Path.Combine(appData, "Levity");
            string legacy = Path.Combine(appData, LegacyDataFolderName);
            try
            {
                if (!Directory.Exists(legacy)) return current;
                if (Directory.Exists(current))
                {
                    if (Directory.EnumerateFileSystemEntries(current).Any()) return current;   // already in use: the new folder wins
                    Directory.Delete(current);                                                   // an empty leftover must not block the move
                }
                Directory.Move(legacy, current);
                return current;
            }
            catch { return Directory.Exists(legacy) ? legacy : current; }
        }

        // LEVITY_SETTINGS_DIR lets tests run without touching the real settings file.
        private static readonly string SettingsDir = ResolveDataDir(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetEnvironmentVariable("LEVITY_SETTINGS_DIR"));
        public static string DataDir => SettingsDir;
        private static readonly string SettingsFilePath = Path.Combine(SettingsDir, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        if (string.IsNullOrWhiteSpace(settings.WorkingDirectory) || !Directory.Exists(settings.WorkingDirectory))
                        {
                            settings.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        }
                        if (string.IsNullOrWhiteSpace(settings.ProjectsRoot) || !Directory.Exists(settings.ProjectsRoot))
                        {
                            settings.ProjectsRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        }
                        settings.RecentFolders ??= new();
                        settings.RecentFolders.RemoveAll(r => !Directory.Exists(r));
                        return settings;
                    }
                }
            }
            catch { }

            return new AppSettings
            {
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch { }
        }

        public static void SetStartupRegistration(bool enable)
        {
            try
            {
                const string runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                using var key = Registry.CurrentUser.OpenSubKey(runKey, true);
                if (key != null)
                {
                    string appPath = Environment.ProcessPath ?? "";
                    if (enable && !string.IsNullOrEmpty(appPath))
                    {
                        key.SetValue("Levity", $"\"{appPath}\"");
                    }
                    else
                    {
                        key.DeleteValue("Levity", false);
                    }
                    key.DeleteValue(LegacyDataFolderName, false);   // the startup entry older versions registered under the old name
                }
            }
            catch { }
        }
    }

    /// <summary>A phone that was approved on this PC. Its key never touches disk unencrypted (Windows DPAPI, current user).</summary>
    public class PairedDevice
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string KeyProtected { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public DateTime? LastSeenUtc { get; set; }

        public static PairedDevice Create(string name, byte[] key) => new()
        {
            Id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(),
            Name = name,
            KeyProtected = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
                key, null, System.Security.Cryptography.DataProtectionScope.CurrentUser)),
            CreatedUtc = DateTime.UtcNow
        };

        public byte[]? TryGetKey()
        {
            try
            {
                return System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(KeyProtected), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            }
            catch { return null; }
        }
    }
}
