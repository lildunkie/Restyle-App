using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Restyle.App.Controls;
using Restyle.App.Docking;
using Restyle.App.Services;
using Restyle.Core.Dock;
using Restyle.Core.Settings;

namespace Restyle.App.Pages;

/// <summary>Turn the macOS-style dock on/off and adjust it. The dock reloads these settings live.</summary>
public sealed class DockPage : UserControl, IPage
{
    private readonly TextBlock _status = Ui.Text("", "Hint");
    private readonly Button _toggle;
    private readonly CheckBox _startup;
    private readonly CheckBox _autoHide;
    private readonly ComboBox _size = new() { MinWidth = 140 };
    private readonly CheckBox _magnify;
    private readonly CheckBox _reserve;
    private readonly CheckBox _hideTaskbar;
    private readonly CheckBox _topBar;
    private bool _loading;

    public DockPage()
    {
        var root = new StackPanel { Margin = new Thickness(28, 20, 28, 28), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        root.Children.Add(Ui.Text("Dock", "PageTitle"));
        root.Children.Add(Ui.Text(
            "A Mac-style dock for your favorite and open apps, with hover zoom. It runs separately from this window, " +
            "so it keeps working after you close Restyle. Right-click an app in the dock to keep it there or remove it.",
            "PageSubtitle"));

        _toggle = Ui.Button("", (_, _) => ToggleDock(), accent: true);
        _toggle.Margin = new Thickness(0);
        root.Children.Add(Row("Dock", _status, _toggle));

        _startup = Switch(() => SetStartup(_startup!.IsChecked == true));
        root.Children.Add(Row("Start the dock when I sign in", Ui.Text("Adds Restyle Dock to your startup apps (you can also turn it off in Settings > Apps > Startup).", "Hint"), _startup));

        _autoHide = Switch(() => MainWindow.Instance.Run(
            () => S.Engine.Set("taskbarAutoHide", JsonValue.Create(_autoHide!.IsChecked == true)),
            _autoHide!.IsChecked == true ? "The Windows taskbar now hides until you move the mouse to the very bottom edge." : "The Windows taskbar is always shown again."));
        root.Children.Add(Row("Auto-hide the Windows taskbar", Ui.Text(
            "The taskbar slides away and pops back up when the mouse touches the bottom edge of the screen. Can be undone.",
            "Hint"), _autoHide));

        _hideTaskbar = Switch(() => Update(c => c.HideTaskbar = _hideTaskbar!.IsChecked == true));
        root.Children.Add(Row("Completely hide the Windows taskbar", Ui.Text(
            "While the dock is on, the taskbar is gone entirely and never pops up. It comes back as soon as you turn the dock off. " +
            "You won't see the clock or tray icons; the Start menu still opens from the dock or the Windows key.",
            "Hint"), _hideTaskbar));

        _topBar = Switch(() => Update(c => c.ShowTopBar = _topBar!.IsChecked == true));
        root.Children.Add(Row("Top bar and control center", Ui.Text(
            "A bar across the top with the app you're using, the clock, and Wi-Fi, Bluetooth, volume and battery. " +
            "Click those icons for quick controls (volume, brightness, Wi-Fi, Bluetooth, dark mode). The ^ button shows your tray apps.",
            "Hint"), _topBar));

        root.Children.Add(Ui.Text("Appearance", "SectionHeader"));
        foreach (var (size, label) in new[] { (40, "Small"), (48, "Medium"), (64, "Large") })
            _size.Items.Add(new ComboBoxItem { Content = label, Tag = size });
        _size.SelectionChanged += (_, _) =>
        {
            if (!_loading && _size.SelectedItem is ComboBoxItem { Tag: int size })
                Update(c => c.IconSize = size);
        };
        root.Children.Add(Row("Icon size", Ui.Text("How big the app icons are.", "Hint"), _size));

        _magnify = Switch(() => Update(c => c.Magnify = _magnify!.IsChecked == true));
        root.Children.Add(Row("Magnify on hover", Ui.Text("Icons grow as your mouse passes over them, like on a Mac.", "Hint"), _magnify));

        _reserve = Switch(() => Update(c => c.ReserveSpace = _reserve!.IsChecked == true));
        root.Children.Add(Row("Keep windows above the dock", Ui.Text("Maximized windows stop at the dock instead of going underneath it.", "Hint"), _reserve));

        Refresh();
    }

    private static AppServices S => AppServices.Current;

    public void Refresh()
    {
        _loading = true;
        try
        {
            var running = DockHost.IsRunning;
            _status.Text = running ? "The dock is on." : "The dock is off.";
            _toggle.Content = running ? "Turn dock off" : "Turn dock on";
            _ = RefreshStartupAsync();
            _autoHide.IsChecked = S.Catalog.Get<TaskbarAutoHideSetting>().Read();

            var config = DockConfig.Load(DockHost.ConfigPath);
            _size.SelectedItem = _size.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == config.IconSize);
            _magnify.IsChecked = config.Magnify;
            _reserve.IsChecked = config.ReserveSpace;
            _hideTaskbar.IsChecked = config.HideTaskbar;
            _topBar.IsChecked = config.ShowTopBar;
        }
        finally
        {
            _loading = false;
        }
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
            var enabled = await DockAutoStart.IsEnabledAsync();
            _loading = true;
            _startup.IsChecked = enabled;
        }
        catch (Exception)
        {
            _startup.IsEnabled = false;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Update(Action<DockConfig> change)
    {
        if (_loading)
            return;
        var config = DockConfig.Load(DockHost.ConfigPath);
        change(config);
        config.Save(DockHost.ConfigPath); // a running dock picks this up immediately
    }

    private CheckBox Switch(Action onClick)
    {
        var box = new CheckBox { Style = (Style)Application.Current.FindResource("Switch") };
        box.Click += (_, _) =>
        {
            if (!_loading)
                onClick();
        };
        return box;
    }

    private static Border Row(string title, UIElement detail, FrameworkElement control)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(Ui.Text(title));
        text.Children.Add(detail);
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return Ui.Card(grid);
    }
}
