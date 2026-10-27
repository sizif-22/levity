using System.Runtime.CompilerServices;

namespace Levity.Tests;

/// <summary>
/// AppSettings and SessionStore read LEVITY_SETTINGS_DIR once, so it must be set before any of them is touched.
/// Every test run gets its own throw-away folder and never sees the developer's real settings.
/// </summary>
internal static class TestEnvironment
{
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "levity-tests-" + Guid.NewGuid().ToString("N"));
    public static string SettingsDir => Path.Combine(Root, "settings");

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(SettingsDir);
        Environment.SetEnvironmentVariable("LEVITY_SETTINGS_DIR", SettingsDir);
        // Keep the developer's own .env out of the tests.
        foreach (string key in new[] { "LEVITY_RELAY_URL", "LEVITY_APP_URL", "LEVITY_PROJECTS_ROOT", "LEVITY_SIDEBAR_WIDTH", "AGY_PATH", "ANTIGRAVITY_IDE_PATH", "LEVITY_ENV_FILE" })
            Environment.SetEnvironmentVariable(key, null);
    }

    public static string NewTempDir(string name = "t")
    {
        string dir = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
