using Restyle.Core.Settings;

namespace Restyle.App.Pages;

/// <summary>Everything about the taskbar and the Start menu: Windows' own taskbar options, plus Restyle's replacements.</summary>
public sealed class TaskbarPage : SettingsPage
{
    public TaskbarPage()
        : base("Taskbar & Start",
            "Windows' taskbar options, and Restyle's own top bar and Start menu. Changes apply right away.")
    {
        Section("Windows taskbar");
        Combo(Get<TaskbarAlignmentSetting>(), (TaskbarAlignment.Left, "Left"), (TaskbarAlignment.Center, "Centered"));
        Switch(Get<TaskbarAutoHideSetting>());
        var search = new List<(SearchButtonMode, string)>
        {
            (SearchButtonMode.Hidden, "Hidden"), (SearchButtonMode.Icon, "Icon only"), (SearchButtonMode.Box, "Search box"),
        };
        if (S.Os.IsWindows11)
            search.Add((SearchButtonMode.IconAndLabel, "Icon and label"));
        Combo(Get<TaskbarSearchSetting>(), [.. search]);
        Switch(Get<TaskbarWidgetsSetting>());
        Switch(Get<TaskbarTaskViewSetting>());
        Root.Children.Add(Row("Completely hide the Windows taskbar",
            "While the Restyle dock runs, the taskbar is gone entirely (it comes back when the dock stops). " +
            "Open Start from the dock or the Windows key; the top bar's ^ button shows your tray apps.",
            DockSwitch(c => c.HideTaskbar, (c, v) => c.HideTaskbar = v)));

        Section("Start menu");
        Root.Children.Add(Row("Restyle Start menu",
            "The dock's and top bar's Start button opens a Start menu in the middle of the screen that matches your theme, " +
            "with search, pinned apps and all apps. Right-click an app to pin it. The Windows key still opens the regular Start menu.",
            DockSwitch(c => c.RestyleStart, (c, v) => c.RestyleStart = v)));

        Section("Top bar");
        Root.Children.Add(Row("Top bar and control center",
            "A bar across the top with the app you're using, the clock, and Wi-Fi, Bluetooth, volume and battery. " +
            "Click those icons for quick controls (volume, brightness, Wi-Fi, Bluetooth, dark mode).",
            DockSwitch(c => c.ShowTopBar, (c, v) => c.ShowTopBar = v)));

        Root.Children.Add(Pointer("Hiding the taskbar, the Start menu and the top bar run with the Restyle dock", "Dock", "dock"));

        Refresh();
    }
}
