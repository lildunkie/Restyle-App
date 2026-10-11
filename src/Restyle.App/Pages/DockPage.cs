using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Restyle.App.Controls;
using Restyle.App.Docking;
using Restyle.Core.Dock;

namespace Restyle.App.Pages;

/// <summary>Just the dock: on/off, starting at sign-in, and how it looks. The dock reloads these settings live.</summary>
public sealed class DockPage : SettingsPage
{
    private readonly TextBlock _status = Ui.Text("", "Hint");
    private readonly Button _toggle;
    private readonly CheckBox _startup;

    public DockPage()
        : base("Dock",
            "A Mac-style dock for your favorite and open apps. It runs on its own, so it keeps working after you close this window, " +
            "and it also runs the top bar, desktop widgets and Restyle Start menu. Right-click an app in the dock to keep it there or remove it.")
    {
        _toggle = Ui.Button("", (_, _) => ToggleDock(), accent: true);
        _toggle.Margin = new Thickness(0);
        Root.Children.Add(Row("Dock", _status, _toggle));

        _startup = SwitchBox(() => SetStartup(_startup!.IsChecked == true));
        Root.Children.Add(Row("Start the dock when I sign in",
            "Adds Restyle Dock to your startup apps (you can also turn it off in Settings > Apps > Startup).", _startup));

        Section("Look");
        Root.Children.Add(Row("Style",
            "Mac: rounded see-through panel. Liquid glass: frosted panel with a bright edge. Transparent: just the icons. " +
            "Bar: a full-width flat bar like a taskbar. Minimal pill: small and compact. You can also switch by right-clicking the dock.",
            DockChoice(DockConfig.Styles.Select(s => (s, DockWindow.StyleName(s))), c => c.Style, (c, v) => c.Style = v)));
        Root.Children.Add(Row("Position", "Which edge of the screen the dock sits on.",
            DockChoice([(DockConfig.BottomEdge, "Bottom"), (DockConfig.LeftEdge, "Left side"), (DockConfig.RightEdge, "Right side")], c => c.Edge, (c, v) => c.Edge = v)));
        Root.Children.Add(Row("Icon size", "How big the app icons are.",
            DockChoice([(40, "Small"), (48, "Medium"), (64, "Large")], c => c.IconSize, (c, v) => c.IconSize = v)));
        Root.Children.Add(Row("Magnify on hover", "Icons grow as your mouse passes over them, like on a Mac.",
            DockSwitch(c => c.Magnify, (c, v) => c.Magnify = v)));

        Section("Behavior");
        Root.Children.Add(Row("Keep windows above the dock", "Maximized windows stop at the dock instead of going underneath it.",
            DockSwitch(c => c.ReserveSpace, (c, v) => c.ReserveSpace = v)));
        Root.Children.Add(Row("Show the dock bar",
            "Turn off to keep only what the dock runs (top bar, widgets, Start menu, hidden taskbar), e.g. with the Corners widgets' app column.",
            DockSwitch(c => c.ShowDock, (c, v) => c.ShowDock = v)));

        Section("Related");
        Root.Children.Add(Pointer("Hide the Windows taskbar, Restyle Start menu, top bar", "Taskbar & Start", "taskbar"));
        Root.Children.Add(Pointer("Desktop widgets and lock screen widgets", "Widgets", "widgets"));

        Refresh();
    }

    public override void Refresh()
    {
        base.Refresh();
        var running = DockHost.IsRunning;
        _status.Text = running ? "The dock is on." : "The dock is off.";
        _toggle.Content = running ? "Turn dock off" : "Turn dock on";
        _ = RefreshStartupAsync();
    }

    private void ToggleDock()
    {
        try
        {
            if (DockHost.IsRunning)
                DockHost.Stop();
            else
                DockHost.Start();
        }
        catch (Exception ex)
        {
            MainWindow.Instance.ShowMessage($"Couldn't start the dock: {ex.Message}", MessageKind.Error);
        }

        // Give the dock a moment to start or stop before showing its state.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Refresh();
        };
        timer.Start();
    }

    private async void SetStartup(bool enabled)
    {
        try
        {
            var problem = await DockAutoStart.SetEnabledAsync(enabled);
            if (problem is not null)
                MainWindow.Instance.ShowMessage(problem, MessageKind.Warning, "Open Startup settings", () => MainWindow.OpenUri("ms-settings:startupapps"));
            else
                MainWindow.Instance.ShowMessage(enabled
                    ? "The dock will start when you sign in."
                    : "The dock won't start automatically anymore.", MessageKind.Success);
        }
        catch (Exception ex)
        {
            MainWindow.Instance.ShowMessage($"Couldn't change startup: {ex.Message}", MessageKind.Error);
        }

        await RefreshStartupAsync();
    }

    private async Task RefreshStartupAsync()
    {
        try
        {
            _startup.IsChecked = await DockAutoStart.IsEnabledAsync(); // setting it in code doesn't count as a click
        }
        catch (Exception)
        {
            _startup.IsEnabled = false;
        }
    }
}
