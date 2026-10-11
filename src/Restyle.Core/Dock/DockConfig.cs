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

    public const string MacStyle = "mac", GlassStyle = "glass", ClearStyle = "transparent", BarStyle = "bar", PillStyle = "pill";
    public static readonly string[] Styles = [MacStyle, GlassStyle, ClearStyle, BarStyle, PillStyle];

    public const string BottomEdge = "bottom", LeftEdge = "left", RightEdge = "right";
    public static readonly string[] Edges = [BottomEdge, LeftEdge, RightEdge];

    /// <summary>
    /// How the dock looks: "mac" (rounded translucent panel), "glass" (frosted panel with a bright edge),
    /// "transparent" (just the icons), "bar" (full-width flat bar) or "pill" (small floating pill).
    /// </summary>
    public string Style { get; set; } = MacStyle;

    /// <summary>Which screen edge the dock sits on: "bottom", "left" or "right".</summary>
    public string Edge { get; set; } = BottomEdge;

    public bool Magnify { get; set; } = true;

    /// <summary>Keep maximized windows above the dock (registers it as an app bar).</summary>
    public bool ReserveSpace { get; set; } = true;

    /// <summary>Completely hide the Windows taskbar while the dock runs (it comes back when the dock quits).</summary>
    public bool HideTaskbar { get; set; }

    /// <summary>Show the top bar (app name, clock, Wi-Fi/Bluetooth/volume/battery, control center).</summary>
    public bool ShowTopBar { get; set; } = true;

    /// <summary>
    /// Show the dock bar itself. Off = only the extras run (top bar, widgets, Start menu, hidden taskbar),
    /// e.g. for a desktop that launches apps from a widget instead.
    /// </summary>
    public bool ShowDock { get; set; } = true;

    /// <summary>Show the desktop widgets (world clock, big clock, weather, media, calendar...).</summary>
    public bool ShowWidgets { get; set; }

    public WidgetConfig Widgets { get; set; } = new();

    public LockWidgetConfig LockWidgets { get; set; } = new();

    /// <summary>The dock's Start button opens Restyle's own centered Start menu instead of the Windows one.</summary>
    public bool RestyleStart { get; set; }

    /// <summary>Apps pinned in Restyle's Start menu.</summary>
    public List<DockPin> StartPins { get; set; } = DefaultStartPins();

    public static List<DockPin> DefaultStartPins() =>
    [
        new("Microsoft Edge", "MSEdge", PinKind.AppId),
        new("File Explorer", "Microsoft.Windows.Explorer", PinKind.AppId),
        new("Settings", "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel", PinKind.AppId),
        new("Microsoft Store", "Microsoft.WindowsStore_8wekyb3d8bbwe!App", PinKind.AppId),
        new("Photos", "Microsoft.Windows.Photos_8wekyb3d8bbwe!App", PinKind.AppId),
        new("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", PinKind.AppId),
        new("Notepad", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", PinKind.AppId),
        new("Clock", "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App", PinKind.AppId),
        new("Paint", "Microsoft.Paint_8wekyb3d8bbwe!App", PinKind.AppId),
        new("Snipping Tool", "Microsoft.ScreenSketch_8wekyb3d8bbwe!App", PinKind.AppId),
        new("Terminal", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", PinKind.AppId),
        new("Media Player", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", PinKind.AppId),
    ];

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
                var config = JsonSerializer.Deserialize<DockConfig>(ReadShared(path), RestyleJson.Options);
                if (config is not null)
                {
                    if (!IconSizes.Contains(config.IconSize))
                        config.IconSize = 48;
                    config.Widgets ??= new WidgetConfig();
                    config.Widgets.Placements ??= [];
                    config.LockWidgets ??= new LockWidgetConfig();
                    config.LockWidgets.Widgets ??= [];
                    config.StartPins ??= DefaultStartPins();
                    if (!Styles.Contains(config.Style))
                        config.Style = MacStyle;
                    if (!Edges.Contains(config.Edge))
                        config.Edge = BottomEdge;
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

    /// <summary>
    /// Saves atomically (write a temp file, then swap it in), so the dock never reads a half-written file
    /// while the app is applying several dock options in a row. Retries briefly if the file is busy.
    /// </summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, RestyleJson.Options));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(25); // the dock is reading it right now
            }
            catch (UnauthorizedAccessException) when (attempt < 20)
            {
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>Reads the file without blocking a writer, retrying briefly if it's being replaced.</summary>
    private static string ReadShared(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(25);
            }
        }
    }
}

/// <summary>
/// Widgets drawn into the lock screen picture. (Windows doesn't let apps put live widgets on the lock screen,
/// so Restyle draws them onto the picture and refreshes it.)
/// </summary>
public sealed class LockWidgetConfig
{
    public static readonly (string Id, string Name)[] All =
    [
        ("calendar", "Calendar"),
        ("weather", "Weather"),
        ("note", "Today note"),
        ("cities", "World times"),
    ];

    public bool Enabled { get; set; }

    public List<string> Widgets { get; set; } = ["calendar", "weather", "note", "cities"];

    /// <summary>"right" or "left" side of the screen.</summary>
    public string Side { get; set; } = "right";

    /// <summary>The plain lock screen picture the widgets are drawn on ("builtin:...", a file, or "spotlight").</summary>
    public string? Base { get; set; }
}

/// <summary>A widget you've moved, resized, shown or hidden.</summary>
public sealed class WidgetPlacement
{
    /// <summary>Center of the widget as a fraction of the screen's usable width (0 = left edge, 1 = right); null = default spot.</summary>
    public double? X { get; set; }

    /// <summary>Center as a fraction of the usable height (0 = top, 1 = bottom); null = default spot.</summary>
    public double? Y { get; set; }

    /// <summary>Size multiplier (0.6 to 1.8).</summary>
    public double Scale { get; set; } = 1;

    /// <summary>Shown/hidden by you; null = whatever the style shows by default.</summary>
    public bool? Visible { get; set; }
}

/// <summary>A city on the world clock map.</summary>
public sealed record WorldCity(string Name, string TimeZone, double Latitude, double Longitude);

/// <summary>
/// Desktop widget settings. Everything works offline except weather and news, which stay off
/// until the user enters a city or a news feed.
/// </summary>
public sealed class WidgetConfig
{
    public const string PanelLayout = "panel", GlassLayout = "glass", CornersLayout = "corners";

    /// <summary>
    /// How the widgets are arranged: "panel" (map, clock and a column of tiles), "glass" (a big centered
    /// clock with media and volume pills) or "corners" (big type in the corners and an app column).
    /// </summary>
    public string Layout { get; set; } = PanelLayout;

    /// <summary>Draw the widget panels as frosted ("liquid") glass instead of dark tiles.</summary>
    public bool Glass { get; set; }

    /// <summary>
    /// Where you moved each widget, how big you made it, and whether you showed or hid it, by widget id.
    /// Anything not listed uses the style's default.
    /// </summary>
    public Dictionary<string, WidgetPlacement> Placements { get; set; } = [];

    /// <summary>Every widget there is; each style shows some of them by default.</summary>
    public static readonly (string Id, string Name)[] AllWidgets =
    [
        ("map", "World clock map"),
        ("clock", "Clock with headlines"),
        ("tiles", "Tile panel (weather, clock, apps, sliders, media, calendar)"),
        ("center-clock", "Big centered clock"),
        ("pills", "Media and volume pills"),
        ("weather", "Weather chip"),
        ("apps", "App column"),
        ("bars", "Music bars"),
        ("date", "Big date"),
        ("corner-clock", "Two-tone clock"),
    ];

    /// <summary>Which widgets a style shows when you haven't changed anything.</summary>
    public static string[] DefaultWidgets(string layout) => layout switch
    {
        GlassLayout => ["center-clock", "pills"],
        CornersLayout => ["weather", "apps", "bars", "date", "corner-clock"],
        _ => ["map", "clock", "tiles"],
    };

    // Each style keeps its own arrangement ("glass:pills", "corners:date", ...), so switching styles or
    // applying a preset never scrambles the layout you made for another style.
    private string Key(string id) => $"{Layout}:{id}";

    public WidgetPlacement? FindPlacement(string id) => Placements.GetValueOrDefault(Key(id));

    public bool IsVisible(string id) =>
        FindPlacement(id)?.Visible is bool visible ? visible : DefaultWidgets(Layout).Contains(id);

    public WidgetPlacement Placement(string id)
    {
        if (!Placements.TryGetValue(Key(id), out var placement))
            Placements[Key(id)] = placement = new WidgetPlacement();
        return placement;
    }

    /// <summary>Back to the current style's default spots, sizes and widgets.</summary>
    public void ResetPlacements()
    {
        foreach (var key in Placements.Keys.Where(k => k.StartsWith(Layout + ":", StringComparison.Ordinal)).ToList())
            Placements.Remove(key);
    }

    /// <summary>The world clock cities you can choose from.</summary>
    public static List<WorldCity> CityChoices() =>
    [
        new("Honolulu", "Hawaiian Standard Time", 21.31, -157.86),
        new("Anchorage", "Alaskan Standard Time", 61.22, -149.90),
        new("Los Angeles", "Pacific Standard Time", 34.05, -118.24),
        new("Vancouver", "Pacific Standard Time", 49.28, -123.12),
        new("Denver", "Mountain Standard Time", 39.74, -104.99),
        new("Chicago", "Central Standard Time", 41.88, -87.63),
        new("Mexico City", "Central Standard Time (Mexico)", 19.43, -99.13),
        new("New York", "Eastern Standard Time", 40.71, -74.01),
        new("Toronto", "Eastern Standard Time", 43.65, -79.38),
        new("Bogotá", "SA Pacific Standard Time", 4.71, -74.07),
        new("São Paulo", "E. South America Standard Time", -23.55, -46.63),
        new("Buenos Aires", "Argentina Standard Time", -34.60, -58.38),
        new("Reykjavik", "Greenwich Standard Time", 64.15, -21.94),
        new("London", "GMT Standard Time", 51.51, -0.13),
        new("Lisbon", "GMT Standard Time", 38.72, -9.14),
        new("Madrid", "Romance Standard Time", 40.42, -3.70),
        new("Paris", "Romance Standard Time", 48.86, 2.35),
        new("Berlin", "W. Europe Standard Time", 52.52, 13.40),
        new("Rome", "W. Europe Standard Time", 41.90, 12.50),
        new("Lagos", "W. Central Africa Standard Time", 6.52, 3.38),
        new("Cairo", "Egypt Standard Time", 30.04, 31.24),
        new("Johannesburg", "South Africa Standard Time", -26.20, 28.05),
        new("Nairobi", "E. Africa Standard Time", -1.29, 36.82),
        new("Istanbul", "Turkey Standard Time", 41.01, 28.98),
        new("Moscow", "Russian Standard Time", 55.76, 37.62),
        new("Dubai", "Arabian Standard Time", 25.20, 55.27),
        new("Delhi", "India Standard Time", 28.61, 77.21),
        new("Bangkok", "SE Asia Standard Time", 13.76, 100.50),
        new("Singapore", "Singapore Standard Time", 1.35, 103.82),
        new("Hong Kong", "China Standard Time", 22.32, 114.17),
        new("Beijing", "China Standard Time", 39.90, 116.40),
        new("Manila", "Singapore Standard Time", 14.60, 120.98),
        new("Seoul", "Korea Standard Time", 37.57, 126.98),
        new("Tokyo", "Tokyo Standard Time", 35.68, 139.69),
        new("Perth", "W. Australia Standard Time", -31.95, 115.86),
        new("Sydney", "AUS Eastern Standard Time", -33.87, 151.21),
        new("Auckland", "New Zealand Standard Time", -36.85, 174.76),
    ];

    /// <summary>The four app shortcuts in the widget panel.</summary>
    public List<DockPin> Apps { get; set; } = DefaultApps();

    public List<WorldCity> Cities { get; set; } = DefaultCities();

    /// <summary>Display name of the weather location; null = weather off.</summary>
    public string? WeatherCity { get; set; }

    public double? WeatherLatitude { get; set; }

    public double? WeatherLongitude { get; set; }

    /// <summary>Null = follow the region setting (°F in the US, °C elsewhere).</summary>
    public bool? Fahrenheit { get; set; }

    /// <summary>An RSS/Atom feed for the headlines under the clock; null = news off.</summary>
    public string? NewsFeed { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool WeatherEnabled => WeatherLatitude is not null && WeatherLongitude is not null;

    public static List<DockPin> DefaultApps() =>
    [
        new("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", PinKind.AppId),
        new("File Explorer", "Microsoft.Windows.Explorer", PinKind.AppId),
        new("Notepad", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", PinKind.AppId),
        new("Settings", "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel", PinKind.AppId),
    ];

    public static List<WorldCity> DefaultCities() =>
    [
        new("Los Angeles", "Pacific Standard Time", 34.05, -118.24),
        new("New York", "Eastern Standard Time", 40.71, -74.01),
        new("São Paulo", "E. South America Standard Time", -23.55, -46.63),
        new("London", "GMT Standard Time", 51.51, -0.13),
        new("Dubai", "Arabian Standard Time", 25.20, 55.27),
        new("Singapore", "Singapore Standard Time", 1.35, 103.82),
        new("Tokyo", "Tokyo Standard Time", 35.68, 139.69),
        new("Sydney", "AUS Eastern Standard Time", -33.87, 151.21),
    ];
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
