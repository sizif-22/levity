using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace AntigravitySidebar.Services
{
    public class AppSettings
    {
        public bool IsPinned { get; set; } = false;
        public string? WorkingDirectory { get; set; }
        public double SidebarWidth { get; set; } = 460;
        public bool LaunchOnStartup { get; set; } = false;

        // Mobile access through the relay (off until the user turns it on)
        public bool RemoteEnabled { get; set; } = false;
        public string? RelayUrl { get; set; }       // wss://levity-relay.<account>.workers.dev
        public string? MobileAppUrl { get; set; }   // https://<project>.vercel.app
        public List<PairedDevice> Devices { get; set; } = new();

        // Project review
        public string ProjectsRoot { get; set; } = @"D:\";
        public string? SyncPrompt { get; set; }

        // Project list ordering: keys are lower-cased full paths
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

        public void SetPinned(string path, bool pinned)
        {
            string key = ProjectKey(path);
            PinnedProjects.RemoveAll(k => k == key);
            if (pinned) PinnedProjects.Add(key);
            Save();
        }

        // LEVITY_SETTINGS_DIR lets tests run without touching the real settings file.
        private static readonly string SettingsDir = Environment.GetEnvironmentVariable("LEVITY_SETTINGS_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AntigravitySidebar");
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
                    key.DeleteValue("AntigravitySidebar", false);
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
