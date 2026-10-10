using System.Text.Json;
using Microsoft.Win32;
using Restyle.Core.Platform;

namespace Restyle.Core.Dock;

public enum PinKind
{
    /// <summary>A program or file path, started with the shell.</summary>
    Path,

    /// <summary>An app's AppUserModelID, started via shell:AppsFolder (works for Store apps too).</summary>
    AppId,
}

/// <summary>An app kept in the dock even when it isn't running.</summary>
public sealed record DockPin(string Name, string Target, PinKind Kind = PinKind.Path)
{
    /// <summary>Matches running windows to pins: same key = same app.</summary>
    public string Key => DockConfig.KeyFor(Kind == PinKind.AppId ? Target : null, Kind == PinKind.Path ? Target : null);
}

/// <summary>Dock settings, stored as %LOCALAPPDATA%\Restyle\dock.json and shared by the app and the dock.</summary>
public sealed class DockConfig
{
    public static readonly int[] IconSizes = [40, 48, 64];

    public List<DockPin> Pinned { get; set; } = Defaults();

    public int IconSize { get; set; } = 48;

    public bool Magnify { get; set; } = true;

    /// <summary>Keep maximized windows above the dock (registers it as an app bar).</summary>
    public bool ReserveSpace { get; set; } = true;

    /// <summary>Completely hide the Windows taskbar while the dock runs (it comes back when the dock quits).</summary>
    public bool HideTaskbar { get; set; }

    /// <summary>Show the top bar (app name, clock, Wi-Fi/Bluetooth/volume/battery, control center).</summary>
    public bool ShowTopBar { get; set; } = true;

    public static List<DockPin> Defaults() =>
    [
        new("File Explorer", "Microsoft.Windows.Explorer", PinKind.AppId),
        new("Microsoft Edge", "MSEdge", PinKind.AppId),
        new("Settings", "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel", PinKind.AppId),
    ];

    /// <summary>App identity: the AppUserModelID when known, otherwise the program path.</summary>
    public static string KeyFor(string? appId, string? exePath) =>
        !string.IsNullOrWhiteSpace(appId) ? "app:" + appId.Trim().ToLowerInvariant()
        : !string.IsNullOrWhiteSpace(exePath) ? "path:" + exePath.Trim().ToLowerInvariant()
        : "";

    public bool IsPinned(string key) => Pinned.Any(p => p.Key == key);

    public static DockConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var config = JsonSerializer.Deserialize<DockConfig>(File.ReadAllText(path), RestyleJson.Options);
                if (config is not null)
                {
                    if (!IconSizes.Contains(config.IconSize))
                        config.IconSize = 48;
                    return config;
                }
            }
        }
        catch (Exception)
        {
            // Unreadable config: fall back to defaults rather than refusing to start.
        }

        return new DockConfig();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, RestyleJson.Options));
    }
}

/// <summary>The macOS-style "magnify on hover" curve.</summary>
public static class DockMath
{
    public const double MaxScale = 1.6;

    /// <summary>Scale for an icon whose center is <paramref name="distance"/> px from the mouse.</summary>
    public static double Scale(double distance, double iconSize, double maxScale = MaxScale)
    {
        var range = iconSize * 2.5;
        var d = Math.Abs(distance);
        if (d >= range)
            return 1;
        var t = Math.Cos(d / range * Math.PI / 2);
        return 1 + (maxScale - 1) * t * t;
    }
}

/// <summary>"Start the dock when I sign in", via the per-user Run key (no admin, no scheduled tasks).</summary>
public static class DockStartup
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Restyle Dock";

    public static bool IsEnabled(IRegistry registry) => registry.GetString(RunKey, ValueName) is not null;

    public static void Enable(IRegistry registry, string exePath) =>
        registry.Set(RunKey, ValueName, $"\"{exePath}\" --dock", RegistryValueKind.String);

    public static void Disable(IRegistry registry) => registry.Delete(RunKey, ValueName);
}
