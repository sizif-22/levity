param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Path,

    [Parameter(Mandatory = $false)]
    [ValidateSet("Fill", "Fit", "Stretch", "Tile", "Center", "Span")]
    [string]$Style = "Fill"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Path)) {
    throw "Wallpaper file not found: $Path"
}

$fullPath = (Resolve-Path $Path).Path

# Map styles to Windows registry values & IDesktopWallpaper position enum
$styleMap = @{
    "Center"  = 0
    "Tile"    = 1
    "Stretch" = 2
    "Fit"     = 3
    "Fill"    = 4
    "Span"    = 5
}
$positionVal = $styleMap[$Style]

$csharpCode = @'
using System;
using System.Runtime.InteropServices;

public class WinDesktopWallpaper
{
    [ComImport]
    [Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
    public class DesktopWallpaperCoClass { }

    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetMonitorDevicePathAt(uint monitorIndex);
        uint GetMonitorDevicePathCount();
        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out IntPtr displayRect);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(int position);
        int GetPosition();
    }

    public static void SetDesktopWallpaper(string imagePath, int position)
    {
        IDesktopWallpaper wallpaper = (IDesktopWallpaper)new DesktopWallpaperCoClass();
        wallpaper.SetPosition(position);
        uint count = wallpaper.GetMonitorDevicePathCount();
        for (uint i = 0; i < count; i++)
        {
            string monitorId = wallpaper.GetMonitorDevicePathAt(i);
            wallpaper.SetWallpaper(monitorId, imagePath);
        }
        wallpaper.SetWallpaper(null, imagePath);
        Marshal.ReleaseComObject(wallpaper);
    }

    public static string GetCurrentWallpaper()
    {
        IDesktopWallpaper wallpaper = (IDesktopWallpaper)new DesktopWallpaperCoClass();
        string current = wallpaper.GetWallpaper(null);
        Marshal.ReleaseComObject(wallpaper);
        return current;
    }
}
'@

if (-not ([System.Management.Automation.PSTypeName]'WinDesktopWallpaper').Type) {
    Add-Type -TypeDefinition $csharpCode
}

[WinDesktopWallpaper]::SetDesktopWallpaper($fullPath, $positionVal)
$active = [WinDesktopWallpaper]::GetCurrentWallpaper()

Write-Output "Successfully set active wallpaper to: $active (Style: $Style)"
