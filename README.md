# Restyle

Make Windows look the way you want: macOS, GNOME, KDE Plasma, Ubuntu, Linux Mint, Pop!_OS, elementary OS,
or your own style. One-click presets, an optional Mac-style dock and top bar, and every change can be undone.
Restyle never modifies Windows system files, never needs admin rights, and never connects to the internet.

## Download

Get the latest `Restyle-win-x64.zip` from the [Releases](../../releases) page, unzip it anywhere and run
`Restyle.exe`. Nothing to install; works on 64-bit Windows 10 (1903+) and Windows 11.

> Windows SmartScreen may warn that the app is from an unknown publisher, because the download isn't
> code-signed. Click **More info → Run anyway**, or build it yourself from source (below).

## Features

- **Presets**: GNOME, KDE, Mac (light/dark), Ubuntu, Linux Mint, Pop!_OS, elementary OS, Light & Clean and
  Windows Default, plus your own. Each one shows exactly what will change (before → after) before it's applied.
  Save, export and import presets as small JSON files.
- **Customize**: light/dark mode (apps and Windows separately), accent color, accent on title bars/taskbar,
  transparency, taskbar alignment/auto-hide/Search/Widgets/Task View, nine original built-in wallpapers,
  mouse pointer schemes (including importing cursor packs), desktop icons.
- **Dock, top bar and control center** (optional): see [below](#dock-optional).
- **Backups**: your original settings are saved automatically on first launch. *Undo last change* (survives
  restarts), *Restore my original settings*, and named backups.
- **Safe and private**: only per-user (HKCU) settings and documented Windows APIs; no uxtheme patching or theme
  DLLs; no network access, accounts, ads or telemetry. All data stays in `%LOCALAPPDATA%\Restyle`.
- **Light**: about 40–50 MB of memory for the settings window and 35–45 MB for the dock + top bar.

## Dock (optional)

A Mac-style dock: Start button, pinned apps, open apps (with a dot), the Recycle Bin, hover magnification
and a launch "bounce". Turn it on from the **Dock** page or run `Restyle.exe --dock`.

- Click an app to open it, bring it to the front, or minimize it (several windows: click to cycle).
- Right-click an app for *Open new window*, *Close*, *Keep in Dock* / *Remove from Dock*. Right-click the
  dock itself to toggle magnification, open settings, or quit.
- It runs as its own small process (≈35–45 MB), separate from the main window, and only while switched on.
  *Start the dock when I sign in* adds a per-user startup entry (`HKCU\…\Run\Restyle Dock`).
- It registers as a Windows app bar (`SHAppBarMessage`), so maximized windows stop above it, and it hides
  itself for fullscreen games and videos. It never takes keyboard focus.
- *Hide the Windows taskbar* uses the normal (undoable) taskbar auto-hide setting; the real taskbar, tray
  and clock are still one mouse-move away at the bottom edge.
- *Completely hide the Windows taskbar* hides Explorer's taskbar windows while the dock runs and shows them
  again the moment the dock quits (or crashes, or the PC restarts).
- **Top bar** (on by default): Start + the app you're using, date and clock (click: notifications/calendar),
  and Wi-Fi, Bluetooth, volume and battery icons. The `^` button briefly brings up the real Windows tray
  for background apps (Windows only lets Explorer host tray icons), then hides it again.
- **Control center** (click the status icons): Wi-Fi and Bluetooth on/off with network and paired devices,
  dark mode, volume (Core Audio) and screen brightness (WMI, built-in screens only) sliders, battery, and
  shortcuts to Windows Quick Settings, Notifications, Settings and Lock.
- Settings live in `%LOCALAPPDATA%\Restyle\dock.json`; a running dock picks up changes instantly.

## Build and run

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows 10 (1809+) or Windows 11.

```powershell
dotnet build                          # build everything
dotnet run --project src/Restyle.App  # start the app
```

### Publish a single .exe

```powershell
dotnet publish src/Restyle.App -p:PublishProfile=win-x64
```

This produces `publish\Restyle.exe` (self-contained, so it runs on any x64 Windows 10/11 PC with nothing to
install) and a `publish\Presets\` folder. Keep the folder next to the exe if you want to edit the built-in presets;
if it's missing, Restyle uses the copies embedded in the exe.

| Build | Exe size | Notes |
|---|---|---|
| Self-contained (default profile) | ~150 MB | Nothing to install |
| Framework-dependent: `dotnet publish src/Restyle.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true` | ~1 MB | Needs the .NET 10 Desktop Runtime |

Memory is kept low by software rendering (no GPU driver stack), a small GC nursery
(`System.GC.Gen0Size` in the csproj) and idle heap compaction in the dock. Single-file compression is
turned off on purpose: it halves the file but costs ~85 MB more RAM at runtime.

## What Restyle changes

All values are under `HKEY_CURRENT_USER` unless noted. Changes are broadcast with `WM_SETTINGCHANGE`
(`ImmersiveColorSet` / `TraySettings`) so they show up immediately.

| Setting (preset key) | Where it lives | Windows version notes |
|---|---|---|
| `appsTheme` | `…\Themes\Personalize\AppsUseLightTheme` | |
| `systemTheme` | `…\Themes\Personalize\SystemUsesLightTheme` | Windows 10 1903+ |
| `accentColor` | `…\Explorer\Accent\AccentColorMenu`, `AccentPalette`, `StartColorMenu`; `…\DWM\AccentColor`, `ColorizationColor`, `ColorizationAfterglow`; `Control Panel\Desktop\AutoColorization` | Title bars of already-open windows may only update when they're next focused |
| `accentOnTitleBars` | `…\DWM\ColorPrevalence` | |
| `accentOnTaskbar` | `…\Themes\Personalize\ColorPrevalence` | Windows only honors it when `systemTheme` is dark |
| `transparency` | `…\Themes\Personalize\EnableTransparency` | |
| `taskbarAlignment` | `…\Explorer\Advanced\TaskbarAl` | **Windows 11 only** |
| `taskbarAutoHide` | `SHAppBarMessage(ABM_SETSTATE)` | |
| `taskbarSearch` | `…\Search\SearchboxTaskbarMode` | `iconAndLabel` is Windows 11 only |
| `taskbarWidgets` | `…\Explorer\Advanced\TaskbarDa` | **Windows 11 only.** Some builds block apps from changing it; Restyle then offers to open Settings ▸ Taskbar |
| `taskbarTaskView` | `…\Explorer\Advanced\ShowTaskViewButton` | |
| `wallpaper` | `SystemParametersInfo(SPI_SETDESKWALLPAPER)` + `Control Panel\Desktop\WallpaperStyle`/`TileWallpaper` | Restoring a slideshow/Spotlight setup restores only the image that was showing |
| `cursorScheme` | `Control Panel\Cursors` (+ `…\Cursors\Schemes` in HKCU/HKLM, read-only for HKLM), `SystemParametersInfo(SPI_SETCURSORS)` | |
| `desktopIcons` | `…\Explorer\Advanced\HideIcons` | **Needs Explorer to restart.** Restyle asks first |

Settings that don't exist on your Windows build are disabled in the UI and skipped by presets.
Explorer is only ever restarted when a changed setting requires it, and only after you say yes.

### Where Restyle keeps its files

`%LOCALAPPDATA%\Restyle\`
- `original.json`: snapshot taken on first launch (*Restore my original settings*)
- `backups\`: your named backups
- `history.json`: the undo history (last 50 changes)
- `Presets\`: presets you saved or imported
- `Wallpapers\`: built-in wallpapers, rendered at your screen resolution the first time they're used
- `Cursors\`: cursor packs you imported (copied here; nothing in `C:\Windows` is touched)

To remove Restyle completely, first restore your original settings if you want them back, then delete the exe
and this folder.

## Adding a preset

Presets are small JSON files. Only the settings you list are changed; everything else is left alone.

```jsonc
{
  "name": "Ubuntu-ish",
  "description": "Dark, orange accent, dock-style left taskbar.",
  "order": 5,                       // position on the Presets page (lower = first)
  "settings": {
    "appsTheme": "dark",            // "light" | "dark"
    "systemTheme": "dark",
    "accentColor": "#E95420",       // "#RRGGBB" or "auto"
    "accentOnTitleBars": false,
    "accentOnTaskbar": false,
    "transparency": true,
    "taskbarAlignment": "left",     // "left" | "center"
    "taskbarAutoHide": false,
    "taskbarSearch": "icon",        // "hidden" | "icon" | "box" | "iconAndLabel"
    "taskbarWidgets": false,
    "taskbarTaskView": true,
    "desktopIcons": true,
    "wallpaper": "builtin:gnome-blobs",   // or a file path; %VARS% are expanded
    // "wallpaper": { "path": "C:\\Pictures\\x.jpg", "fit": "fill" },  // fill|fit|stretch|tile|center|span
    "cursorScheme": "Windows Black"       // any installed scheme name
  }
}
```

Built-in wallpaper ids: `gnome-blobs`, `plasma-shapes`, `soft-light`, `nord-waves`, `mint-hills`,
`aqua-waves`, `midnight-waves`, `aubergine`, `pop-peaks`.

Where to put it:
- **Just for you:** use *Presets ▸ Import a preset…*, or drop the file into `%LOCALAPPDATA%\Restyle\Presets\`
  (*Open presets folder* button).
- **Ship it with the app:** add it to `src/Restyle.App/Presets/` (it's copied next to the exe and embedded).

No recompiling is needed to edit presets: change the JSON and switch back to the Presets page.

## Adding a new setting (for developers)

1. Create a class in `src/Restyle.Core/Settings/`. For a simple on/off registry value, derive from
   `DwordToggleSetting` and fill in the key, value name and default; otherwise derive from `Setting<T>` and
   implement `Read()` and `Apply(value)`. Give it an `Id`, a `Name` and a plain-language `Description`
   (shown as the tooltip). Override `MinimumBuild`, `RequiresExplorerRestart` or `BroadcastAreas` as needed.
2. Register it in `SettingsCatalog`.
3. Add a row for it in `CustomizePage` (`Switch(...)` or `Combo(...)` is usually one line).

Backups, undo, presets and the "what will change" list pick up the new setting automatically.

## Project layout

```
src/Restyle.Core/        settings layer, apply/undo engine, backups, presets (no UI)
  Platform/              registry + Win32 wrappers (IRegistry, ISystemApi) and OS version detection
  Settings/              one class per setting, plus the cursor pack importer
  Engine/                SettingsEngine (plan → apply → broadcast → record undo), ChangeHistory
  Backup/, Presets/      snapshot and preset storage
src/Restyle.App/         WPF UI (.NET 10 Fluent theme; follows light/dark mode automatically)
  Presets/               the built-in preset JSON files
  Dock/                  dock, top bar and control center
```

## Contributing

Ideas, bug reports and pull requests are welcome.

- New settings: see [Adding a new setting](#adding-a-new-setting-for-developers).
- New presets: just add a JSON file to `src/Restyle.App/Presets/` (see [Adding a preset](#adding-a-preset)).
- Keep the project's promises: per-user settings only, documented Windows APIs, everything undoable,
  no network access.

## License

[MIT](LICENSE): free to use, modify and share, including in your own projects.
