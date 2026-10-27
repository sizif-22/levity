---
name: windows-wallpaper-manager
description: >-
  Use this skill whenever asked to download high-resolution wallpapers, find black and white / aesthetic wallpapers,
  or change/apply the desktop background on Windows 10/11. Includes verified IDesktopWallpaper COM scripts to avoid
  the Windows 11 legacy API wallpaper failure.
---

# Windows Wallpaper Manager

This skill provides verified, immediate procedures and scripts for fetching high-resolution wallpapers and applying them reliably on modern Windows (Windows 10 & Windows 11).

---

## 1. Why Standard Win32 Fails on Windows 11

On Windows 10 and Windows 11:
- The legacy Win32 API (`SystemParametersInfo` with `SPI_SETDESKWALLPAPER`) often updates registry keys but **does not** notify the desktop shell or refresh `TranscodedWallpaper`, especially when slideshows, virtual desktops, or multi-monitors are configured.
- Modern Windows desktop wallpapers are managed via the native **`IDesktopWallpaper`** COM interface:
  - CLSID: `C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD`
  - IID: `B92B56A9-8B55-4E14-9A89-0199BBB6F93B`

---

## 2. Setting Wallpaper Instantly

Run the bundled PowerShell script:

```powershell
& "$env:USERPROFILE\.gemini\config\skills\windows-wallpaper-manager\scripts\Set-Wallpaper.ps1" -Path "<path-to-image>" -Style "Fill"
```

Supported styles: `Fill` (default), `Fit`, `Stretch`, `Tile`, `Center`, `Span`.

---

## 3. Searching and Downloading Wallpapers

To search, download, and optionally apply a wallpaper in a single command:

```powershell
# Search and download:
& "$env:USERPROFILE\.gemini\config\skills\windows-wallpaper-manager\scripts\Fetch-Wallpaper.ps1" -Query "black and white minimalist" -AtLeast "1920x1080"

# Search, download, AND apply immediately:
& "$env:USERPROFILE\.gemini\config\skills\windows-wallpaper-manager\scripts\Fetch-Wallpaper.ps1" -Query "black and white space" -Apply
```

Wallpapers are stored in `%USERPROFILE%\Pictures\Wallpapers` by default (override with `-OutputDir`).
