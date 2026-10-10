using Restyle.Core.Platform;

namespace Restyle.Core.Settings;

public enum TaskbarAlignment
{
    Left,
    Center,
}

public enum SearchButtonMode
{
    Hidden = 0,
    Icon = 1,
    Box = 2,
    IconAndLabel = 3,
}

public sealed class TaskbarAlignmentSetting(SettingContext context) : Setting<TaskbarAlignment>(context)
{
    public override string Id => "taskbarAlignment";
    public override string Name => "Taskbar alignment";
    public override string Description =>
        "Put the Start button and app icons in the middle of the taskbar, or on the left like older Windows and KDE.";
    protected override int MinimumBuild => OsInfo.Win11;
    public override IReadOnlyList<string> BroadcastAreas => [Keys.TrayArea];

    public override TaskbarAlignment Read() =>
        Registry.GetDword(Keys.ExplorerAdvanced, "TaskbarAl") is 0 ? TaskbarAlignment.Left : TaskbarAlignment.Center;

    public override void Apply(TaskbarAlignment value) =>
        Registry.SetDword(Keys.ExplorerAdvanced, "TaskbarAl", value == TaskbarAlignment.Center ? 1 : 0);

    public override string Describe(TaskbarAlignment value) => value == TaskbarAlignment.Center ? "Centered" : "Left";
}

/// <summary>Uses SHAppBarMessage(ABM_SETSTATE) so it applies instantly, without touching binary StuckRects data.</summary>
public sealed class TaskbarAutoHideSetting(SettingContext context) : Setting<bool>(context)
{
    public override string Id => "taskbarAutoHide";
    public override string Name => "Automatically hide the taskbar";
    public override string Description =>
        "Slides the taskbar out of the way until you move the mouse to the bottom of the screen.";

    public override bool Read() => System.GetTaskbarAutoHide();

    public override void Apply(bool value) => System.SetTaskbarAutoHide(value);

    public override string Describe(bool value) => value ? "On" : "Off";
}

public sealed class TaskbarSearchSetting(SettingContext context) : Setting<SearchButtonMode>(context)
{
    public override string Id => "taskbarSearch";
    public override string Name => "Search on taskbar";
    public override string Description =>
        "Show a search box, just a search icon, or nothing. You can always search by opening Start and typing.";
    public override IReadOnlyList<string> BroadcastAreas => [Keys.TrayArea];

    public override SearchButtonMode Read()
    {
        var raw = Registry.GetDword(Keys.Search, "SearchboxTaskbarMode");
        return raw is >= 0 and <= 3 ? (SearchButtonMode)raw.Value : SearchButtonMode.Box;
    }

    public override void Apply(SearchButtonMode value) =>
        Registry.SetDword(Keys.Search, "SearchboxTaskbarMode", (int)value);

    public override string Describe(SearchButtonMode value) => value switch
    {
        SearchButtonMode.Hidden => "Hidden",
        SearchButtonMode.Icon => "Icon only",
        SearchButtonMode.Box => "Search box",
        SearchButtonMode.IconAndLabel => "Icon and label",
        _ => value.ToString(),
    };
}

public sealed class TaskbarWidgetsSetting(SettingContext context) : DwordToggleSetting(context)
{
    public override string Id => "taskbarWidgets";
    public override string Name => "Widgets button";
    public override string Description =>
        "The weather/news button on the taskbar. Some Windows 11 updates don't let apps change this; if so, Restyle will open the right Settings page for you.";
    protected override int MinimumBuild => OsInfo.Win11;
    public override IReadOnlyList<string> BroadcastAreas => [Keys.TrayArea];
    protected override string KeyPath => Keys.ExplorerAdvanced;
    protected override string ValueName => "TaskbarDa";
    protected override bool DefaultValue => true;

    protected override Exception Blocked(UnauthorizedAccessException ex) => new SettingBlockedException(
        "Windows doesn't allow apps to change the Widgets button on this version. You can switch it in Settings > Personalization > Taskbar.",
        "ms-settings:taskbar", ex);
}

public sealed class TaskbarTaskViewSetting(SettingContext context) : DwordToggleSetting(context)
{
    public override string Id => "taskbarTaskView";
    public override string Name => "Task View button";
    public override string Description =>
        "The button that shows all open windows and virtual desktops (also available with Win+Tab).";
    public override IReadOnlyList<string> BroadcastAreas => [Keys.TrayArea];
    protected override string KeyPath => Keys.ExplorerAdvanced;
    protected override string ValueName => "ShowTaskViewButton";
    protected override bool DefaultValue => true;
}

public sealed class DesktopIconsSetting(SettingContext context) : DwordToggleSetting(context)
{
    public override string Id => "desktopIcons";
    public override string Name => "Show desktop icons";
    public override string Description =>
        "Show or hide all icons on the desktop (your files stay where they are). Needs Explorer to restart to take effect.";
    public override bool RequiresExplorerRestart => true;
    protected override string KeyPath => Keys.ExplorerAdvanced;
    protected override string ValueName => "HideIcons";
    protected override bool DefaultValue => true;
    protected override bool Inverted => true;
}
