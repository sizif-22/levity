using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Levity.Services.WindowsTools
{
    /// <summary>Where the settings tools read and write. Overridable so tests can point them at a throw-away registry key.</summary>
    internal static class SettingsLocations
    {
        public const string DesktopKey = @"Control Panel\Desktop";
        public const string ThemeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        public static readonly string[] WallpaperValues = { "WallpaperStyle", "TileWallpaper" };
        public static readonly string[] ThemeValues = { "AppsUseLightTheme", "SystemUsesLightTheme" };
    }

    internal static class PowerPlans
    {
        private static readonly Regex Line = new(@"Power Scheme GUID:\s*(?<guid>[0-9a-fA-F-]{36})\s*\((?<name>[^)]*)\)\s*(?<active>\*)?", RegexOptions.Compiled);

        public sealed record Plan(string Guid, string Name, bool Active);

        public static List<Plan> Parse(string output)
        {
            var list = new List<Plan>();
            foreach (Match m in Line.Matches(output ?? ""))
                list.Add(new Plan(m.Groups["guid"].Value.ToLowerInvariant(), m.Groups["name"].Value.Trim(), m.Groups["active"].Success));
            return list;
        }

        public static async Task<List<Plan>> ListAsync() => Parse((await Proc.RunAsync("powercfg", new[] { "/list" }, 10)).Text);

        public static async Task<string?> SetActiveAsync(string guid)
        {
            if (!System.Guid.TryParse(guid, out _)) return "Not a valid power plan id.";
            var r = await Proc.RunAsync("powercfg", new[] { "/setactive", guid }, 15);
            return r.ExitCode == 0 ? null : "powercfg refused: " + r.Text;
        }
    }

    public sealed class SettingsGetTool : WindowsTool
    {
        public override string Name => "settings_get";
        public override string Description => "Read Windows settings: the desktop wallpaper and its style, light or dark theme, the power plans (and which is active) and the display (resolution, scale, monitors). area: wallpaper, theme, power_plan, display or all.";
        public override JsonObject InputSchema => Schema(new JsonObject { ["area"] = Prop("string", "Which settings to read.", "wallpaper", "theme", "power_plan", "display", "all") });

        public override async Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string area = Str(args, "area") is { Length: > 0 } a ? OneOf(args, "area", "wallpaper", "theme", "power_plan", "display", "all") : "all";
            var data = new JsonObject();
            if (area is "wallpaper" or "all") data["wallpaper"] = Wallpaper();
            if (area is "theme" or "all") data["theme"] = Theme();
            if (area is "power_plan" or "all")
            {
                var plans = await PowerPlans.ListAsync();
                data["power_plans"] = new JsonArray(plans.Select(p => (JsonNode)new JsonObject { ["id"] = p.Guid, ["name"] = p.Name, ["active"] = p.Active }).ToArray());
            }
            if (area is "display" or "all") data["display"] = Display();
            return ToolResult.Success($"Settings ({area}).", data);
        }

        internal static JsonObject Wallpaper()
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsLocations.DesktopKey);
            string style = (key?.GetValue("WallpaperStyle") as string) ?? "";
            string tile = (key?.GetValue("TileWallpaper") as string) ?? "";
            string name = (style, tile) switch { ("10", "0") => "fill", ("6", "0") => "fit", ("2", "0") => "stretch", ("0", "1") => "tile", ("0", "0") => "center", ("22", "0") => "span", _ => "unknown" };
            return new JsonObject { ["path"] = Native.GetWallpaper(), ["style"] = name };
        }

        internal static JsonObject Theme()
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsLocations.ThemeKey);
            bool Light(string n) => key?.GetValue(n) is int v ? v != 0 : true;
            return new JsonObject { ["apps"] = Light("AppsUseLightTheme") ? "light" : "dark", ["system"] = Light("SystemUsesLightTheme") ? "light" : "dark" };
        }

        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

        internal static JsonObject Display()
        {
            uint dpi = 96;
            try { dpi = GetDpiForSystem(); } catch { }
            return new JsonObject
            {
                ["primary_resolution"] = $"{GetSystemMetrics(0)}x{GetSystemMetrics(1)}",
                ["monitors"] = GetSystemMetrics(80),
                ["scale_percent"] = (int)Math.Round(dpi / 96.0 * 100),
            };
        }
    }

    public sealed class SettingsSetTool : WindowsTool
    {
        private static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp" };
        private static readonly Dictionary<string, (string Style, string Tile)> Styles = new(StringComparer.OrdinalIgnoreCase)
        {
            ["fill"] = ("10", "0"), ["fit"] = ("6", "0"), ["stretch"] = ("2", "0"), ["tile"] = ("0", "1"), ["center"] = ("0", "0"), ["span"] = ("22", "0"),
        };

        public override string Name => "settings_set";
        public override ToolAccess Access => ToolAccess.Change;
        public override string Description => "Change a Windows setting. area=wallpaper: value is the full path of a local .jpg/.png/.bmp file, optional style (fill, fit, stretch, tile, center, span). area=theme: value light or dark. area=power_plan: value is a plan name or id from settings_get. Every change is journaled and can be reverted. Use dry_run to preview.";
        public override JsonObject InputSchema => Schema(new JsonObject
        {
            ["area"] = Prop("string", "What to change.", "wallpaper", "theme", "power_plan"),
            ["value"] = Prop("string", "The new value (see description)."),
            ["style"] = Prop("string", "Wallpaper only: how the picture is placed.", "fill", "fit", "stretch", "tile", "center", "span"),
            ["dry_run"] = Prop("boolean", "Describe the change without making it."),
        }, "area", "value");

        public static void RegisterUndo(ChangeJournal journal)
        {
            journal.RegisterUndo("wallpaper", data =>
            {
                string? err = RegistryUndo.Restore((JsonObject)data["registry"]!);
                if (err != null) return err;
                Native.SetWallpaper((string?)data["path"] ?? "");
                return null;
            });
            journal.RegisterUndo("registry_values", data => RegistryUndo.Restore(data, (string?)data["broadcast"]));
            journal.RegisterUndo("power_plan", data =>
            {
                string guid = (string?)data["guid"] ?? "";
                return PowerPlans.SetActiveAsync(guid).GetAwaiter().GetResult();
            });
        }

        public override async Task<ToolResult> RunAsync(JsonObject args, ToolContext ctx)
        {
            string area = OneOf(args, "area", "wallpaper", "theme", "power_plan");
            string value = RequiredStr(args, "value");
            return area switch
            {
                "wallpaper" => SetWallpaper(value, Str(args, "style"), ctx),
                "theme" => SetTheme(value, ctx),
                _ => await SetPowerPlan(value, ctx),
            };
        }

        private static ToolResult SetWallpaper(string value, string? style, ToolContext ctx)
        {
            if (value.StartsWith(@"\\", StringComparison.Ordinal)) return ToolResult.Failure("Network paths are not accepted; use a file on this PC.");
            if (!Path.IsPathFullyQualified(value)) return ToolResult.Failure("Give the full path of the picture.");
            string full = Path.GetFullPath(value);
            if (!Extensions.Contains(Path.GetExtension(full).ToLowerInvariant())) return ToolResult.Failure("The picture must be a .jpg, .jpeg, .png or .bmp file.");
            var info = new FileInfo(full);
            if (!info.Exists) return ToolResult.Failure("That file does not exist.");
            if (info.Length > 100L * 1024 * 1024) return ToolResult.Failure("That picture is larger than 100 MB.");
            (string Style, string Tile)? placement = null;
            if (!string.IsNullOrWhiteSpace(style))
            {
                if (!Styles.TryGetValue(style, out var st)) return ToolResult.Failure("style must be one of: " + string.Join(", ", Styles.Keys));
                placement = st;
            }
            string summary = $"Set the wallpaper to {full}" + (style != null ? $" ({style})" : "");
            if (ctx.DryRun) return ToolResult.Success("Dry run: would " + summary[0..1].ToLowerInvariant() + summary[1..] + ". Nothing was changed.");

            var snapshot = RegistryUndo.Snapshot("HKCU", SettingsLocations.DesktopKey, SettingsLocations.WallpaperValues);
            var entry = ctx.Journal.Record("settings_set", summary, "wallpaper", new JsonObject { ["path"] = Native.GetWallpaper(), ["registry"] = snapshot });
            if (placement is { } p)
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsLocations.DesktopKey, true);
                key.SetValue("WallpaperStyle", p.Style, RegistryValueKind.String);
                key.SetValue("TileWallpaper", p.Tile, RegistryValueKind.String);
            }
            if (!Native.SetWallpaper(full)) return ToolResult.Failure("Windows refused to set the wallpaper. The change was recorded as " + entry.Id + "; revert it if anything looks off.");
            return ToolResult.Success(summary + $". Journal id {entry.Id} (revert with the Windows changes list).");
        }

        private static ToolResult SetTheme(string value, ToolContext ctx)
        {
            if (!string.Equals(value, "light", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "dark", StringComparison.OrdinalIgnoreCase))
                return ToolResult.Failure("value must be light or dark.");
            bool light = string.Equals(value, "light", StringComparison.OrdinalIgnoreCase);
            string summary = $"Set Windows and apps to the {(light ? "light" : "dark")} theme";
            if (ctx.DryRun) return ToolResult.Success($"Dry run: would set Windows and apps to the {(light ? "light" : "dark")} theme. Nothing was changed.");

            var snapshot = RegistryUndo.Snapshot("HKCU", SettingsLocations.ThemeKey, SettingsLocations.ThemeValues);
            snapshot["broadcast"] = "ImmersiveColorSet";
            var entry = ctx.Journal.Record("settings_set", summary, "registry_values", snapshot);
            using (var key = Registry.CurrentUser.CreateSubKey(SettingsLocations.ThemeKey, true))
                foreach (string n in SettingsLocations.ThemeValues) key.SetValue(n, light ? 1 : 0, RegistryValueKind.DWord);
            Native.BroadcastSettingChange("ImmersiveColorSet");
            return ToolResult.Success($"{summary}. Journal id {entry.Id}.");
        }

        private static async Task<ToolResult> SetPowerPlan(string value, ToolContext ctx)
        {
            var plans = await PowerPlans.ListAsync();
            var match = plans.FirstOrDefault(p => string.Equals(p.Guid, value, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase));
            if (match == null) return ToolResult.Failure("No such power plan. Available: " + string.Join(", ", plans.Select(p => p.Name)));
            var current = plans.FirstOrDefault(p => p.Active);
            string summary = $"Switch the power plan to {match.Name}" + (current != null ? $" (was {current.Name})" : "");
            if (ctx.DryRun) return ToolResult.Success("Dry run: would " + char.ToLowerInvariant(summary[0]) + summary[1..] + ". Nothing was changed.");
            var entry = ctx.Journal.Record("settings_set", summary, "power_plan", new JsonObject { ["guid"] = current?.Guid ?? match.Guid });
            string? err = await PowerPlans.SetActiveAsync(match.Guid);
            return err == null ? ToolResult.Success($"{summary}. Journal id {entry.Id}.") : ToolResult.Failure(err);
        }
    }
}
