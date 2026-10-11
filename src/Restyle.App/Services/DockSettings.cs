using Restyle.App.Docking;
using Restyle.Core.Dock;
using Restyle.Core.Settings;

namespace Restyle.App.Services;

/// <summary>
/// Dock and widget options as regular settings, so presets can turn them on and Undo / Restore
/// can turn them off again. Registered with the catalog by <see cref="AppServices"/>.
/// </summary>
public static class DockSettings
{
    public static IEnumerable<ISetting> All(SettingContext context) =>
    [
        new DockHideTaskbarSetting(context),
        new DockTopBarSetting(context),
        new DesktopWidgetsSetting(context),
        new DockStartMenuSetting(context),
        new DockBarSetting(context),
        new WidgetLayoutSetting(context),
        new WidgetGlassSetting(context),
        new DockStyleSetting(context),
        new DockPositionSetting(context),
        new DockRunningSetting(context), // last, so the dock starts with the options above already saved
    ];
}

/// <summary>Turns the Restyle dock (and with it the top bar and widgets) on or off.</summary>
public sealed class DockRunningSetting(SettingContext context) : Setting<bool>(context)
{
    public override string Id => "dock";
    public override string Name => "Restyle dock";
    public override string Description =>
        "The Mac-style dock at the bottom of the screen. The top bar and desktop widgets run with it.";

    public override bool Read() => DockHost.IsRunning;

    public override void Apply(bool value)
    {
        if (value == DockHost.IsRunning)
            return;
        if (value)
            DockHost.Start();
        else
            DockHost.Stop();

        // Wait briefly so Undo and the next read see the new state.
        for (var i = 0; i < 30 && DockHost.IsRunning != value; i++)
            Thread.Sleep(100);
    }

    public override string Describe(bool value) => value ? "On" : "Off";
}

/// <summary>Base for an on/off option stored in the dock's settings file (the dock reloads it live).</summary>
public abstract class DockOptionSetting(SettingContext context) : Setting<bool>(context)
{
    protected abstract bool Get(DockConfig config);

    protected abstract void Set(DockConfig config, bool value);

    public override bool Read() => Get(DockConfig.Load(DockHost.ConfigPath));

    public override void Apply(bool value)
    {
        var config = DockConfig.Load(DockHost.ConfigPath);
        Set(config, value);
        config.Save(DockHost.ConfigPath);
    }

    public override string Describe(bool value) => value ? "On" : "Off";
}

public sealed class DockHideTaskbarSetting(SettingContext context) : DockOptionSetting(context)
{
    public override string Id => "dockHideTaskbar";
    public override string Name => "Hide Windows taskbar (with dock)";
    public override string Description =>
        "While the Restyle dock runs, the Windows taskbar is hidden completely. It comes back when the dock stops.";
    protected override bool Get(DockConfig config) => config.HideTaskbar;
    protected override void Set(DockConfig config, bool value) => config.HideTaskbar = value;
}

public sealed class DockTopBarSetting(SettingContext context) : DockOptionSetting(context)
{
    public override string Id => "dockTopBar";
    public override string Name => "Top bar (with dock)";
    public override string Description =>
        "A bar across the top with the current app, the clock, and Wi-Fi, Bluetooth, volume and battery controls.";
    protected override bool Get(DockConfig config) => config.ShowTopBar;
    protected override void Set(DockConfig config, bool value) => config.ShowTopBar = value;
}

public sealed class DockStartMenuSetting(SettingContext context) : DockOptionSetting(context)
{
    public override string Id => "dockStartMenu";
    public override string Name => "Restyle Start menu (with dock)";
    public override string Description =>
        "The dock's Start button opens a Start menu in the middle of the screen that matches your theme, with search, " +
        "pinned apps and all apps. The Windows key still opens the regular Start menu.";
    protected override bool Get(DockConfig config) => config.RestyleStart;
    protected override void Set(DockConfig config, bool value) => config.RestyleStart = value;
}

public sealed class DockBarSetting(SettingContext context) : DockOptionSetting(context)
{
    public override string Id => "dockBar";
    public override string Name => "Dock bar";
    public override string Description =>
        "Show the dock itself. Turn off for desktops that launch apps from a widget; the top bar, widgets and Start menu keep working.";
    protected override bool Get(DockConfig config) => config.ShowDock;
    protected override void Set(DockConfig config, bool value) => config.ShowDock = value;
}

public sealed class WidgetGlassSetting(SettingContext context) : DockOptionSetting(context)
{
    public override string Id => "widgetGlass";
    public override string Name => "Liquid glass widgets";
    public override string Description => "Draw the desktop widgets as frosted glass that shows a blur of your wallpaper.";
    protected override bool Get(DockConfig config) => config.Widgets.Glass;
    protected override void Set(DockConfig config, bool value) => config.Widgets.Glass = value;
}

/// <summary>Which widget arrangement is shown ("panel", "glass" or "corners").</summary>
public sealed class WidgetLayoutSetting(SettingContext context) : Setting<string>(context)
{
    public override string Id => "widgetLayout";
    public override string Name => "Widget style";
    public override string Description =>
        "Tiles: map, clock and a column of tiles. Glass clock: a big centered clock with media and volume. Corners: big type in the corners with an app column.";

    public override string Read() => DockConfig.Load(DockHost.ConfigPath).Widgets.Layout;

    public override void Apply(string value)
    {
        if (value is not (WidgetConfig.PanelLayout or WidgetConfig.GlassLayout or WidgetConfig.CornersLayout))
            throw new ArgumentException($"Unknown widget style '{value}'. Use \"panel\", \"glass\" or \"corners\".");
        var config = DockConfig.Load(DockHost.ConfigPath);
        config.Widgets.Layout = value;
        config.Save(DockHost.ConfigPath);
    }

    public override string Describe(string value) => value switch
    {
        WidgetConfig.GlassLayout => "Glass clock",
        WidgetConfig.CornersLayout => "Corners",
        _ => "Tiles",
    };
}

/// <summary>Base for a choice stored in the dock's settings file, from a fixed list of values.</summary>
public abstract class DockChoiceSetting(SettingContext context) : Setting<string>(context)
{
    protected abstract string[] Values { get; }

    protected abstract string Get(DockConfig config);

    protected abstract void Set(DockConfig config, string value);

    public override string Read() => Get(DockConfig.Load(DockHost.ConfigPath));

    public override void Apply(string value)
    {
        if (!Values.Contains(value))
            throw new ArgumentException($"Unknown value '{value}'. Use one of: {string.Join(", ", Values)}.");
        var config = DockConfig.Load(DockHost.ConfigPath);
        Set(config, value);
        config.Save(DockHost.ConfigPath);
    }
}

public sealed class DockStyleSetting(SettingContext context) : DockChoiceSetting(context)
{
    public override string Id => "dockStyle";
    public override string Name => "Dock style";
    public override string Description =>
        "Mac (rounded translucent panel), Liquid glass (frosted panel), Transparent (just the icons), Bar (full-width flat bar) or Minimal pill.";
    protected override string[] Values => DockConfig.Styles;
    protected override string Get(DockConfig config) => config.Style;
    protected override void Set(DockConfig config, string value) => config.Style = value;
    public override string Describe(string value) => DockWindow.StyleName(value);
}

public sealed class DockPositionSetting(SettingContext context) : DockChoiceSetting(context)
{
    public override string Id => "dockPosition";
    public override string Name => "Dock position";
    public override string Description => "Which edge of the screen the dock sits on: bottom, left or right.";
    protected override string[] Values => DockConfig.Edges;
    protected override string Get(DockConfig config) => config.Edge;
    protected override void Set(DockConfig config, string value) => config.Edge = value;
    public override string Describe(string value) => char.ToUpperInvariant(value[0]) + value[1..];
}

public sealed class DesktopWidgetsSetting(SettingContext context) : DockOptionSetting(context)
{
    public override string Id => "desktopWidgets";
    public override string Name => "Desktop widgets (with dock)";
    public override string Description =>
        "World clock, big clock, weather, analog clock, app shortcuts, volume and brightness, now playing and a calendar, on your desktop.";
    protected override bool Get(DockConfig config) => config.ShowWidgets;
    protected override void Set(DockConfig config, bool value) => config.ShowWidgets = value;
}
