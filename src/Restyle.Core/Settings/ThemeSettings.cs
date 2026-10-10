using Restyle.Core.Platform;

namespace Restyle.Core.Settings;

public enum ThemeMode
{
    Light,
    Dark,
}

/// <summary>Shared logic for the two light/dark switches (AppsUseLightTheme / SystemUsesLightTheme).</summary>
public abstract class ThemeModeSetting(SettingContext context) : Setting<ThemeMode>(context)
{
    protected abstract string ValueName { get; }

    public override IReadOnlyList<string> BroadcastAreas => [Keys.ColorArea];

    public override ThemeMode Read() =>
        Registry.GetDword(Keys.Personalize, ValueName) is 0 ? ThemeMode.Dark : ThemeMode.Light;

    public override void Apply(ThemeMode value) =>
        Registry.SetDword(Keys.Personalize, ValueName, value == ThemeMode.Light ? 1 : 0);
}

public sealed class AppsThemeSetting(SettingContext context) : ThemeModeSetting(context)
{
    public override string Id => "appsTheme";
    public override string Name => "App mode";
    public override string Description =>
        "Light or dark colors inside apps such as Settings, File Explorer and Notepad.";
    protected override string ValueName => "AppsUseLightTheme";
}

public sealed class SystemThemeSetting(SettingContext context) : ThemeModeSetting(context)
{
    public override string Id => "systemTheme";
    public override string Name => "Windows mode";
    public override string Description =>
        "Light or dark colors for the taskbar, Start menu and notifications.";
    protected override string ValueName => "SystemUsesLightTheme";
    protected override int MinimumBuild => OsInfo.Win10_1903;
}

public sealed class TransparencySetting(SettingContext context) : DwordToggleSetting(context)
{
    public override string Id => "transparency";
    public override string Name => "Transparency effects";
    public override string Description =>
        "Lets the taskbar, Start menu and some windows show a frosted-glass blur of what's behind them.";
    public override IReadOnlyList<string> BroadcastAreas => [Keys.ColorArea];
    protected override string KeyPath => Keys.Personalize;
    protected override string ValueName => "EnableTransparency";
    protected override bool DefaultValue => true;
}
