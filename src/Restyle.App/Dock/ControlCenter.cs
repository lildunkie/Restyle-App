using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Restyle.Core.Settings;
using Windows.Devices.Radios;

namespace Restyle.App.Docking;

/// <summary>
/// GNOME/macOS-style quick settings: Wi-Fi, Bluetooth, dark mode, volume, brightness, battery,
/// plus shortcuts to Windows' own panels. Closes when you click elsewhere.
/// </summary>
internal sealed class ControlCenter : Window
{
    private const double PanelWidth = 360;

    private readonly Audio _audio;
    private readonly DockWindow _dock;
    private readonly StackPanel _root = new() { Margin = new Thickness(14) };
    private readonly DispatcherTimer _brightnessDelay = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private int _pendingBrightness = -1;
    private bool _closing;

    public ControlCenter(Audio audio, DockWindow dock)
    {
        _audio = audio;
        _dock = dock;

        ThemeMode = System.Windows.ThemeMode.System; // Fluent look for this window only (the bars stay lightweight)
        Title = "Control center";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Width = PanelWidth;
        SizeToContent = SizeToContent.Height;
        Content = _root;

        Deactivated += (_, _) => SafeClose();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                SafeClose();
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        };
        _brightnessDelay.Tick += (_, _) =>
        {
            _brightnessDelay.Stop();
            var value = _pendingBrightness;
            Task.Run(() => Brightness.Set(value));
        };

        Build();
    }

    public void ShowBelow(double top)
    {
        Top = top + 6;
        Left = SystemParameters.PrimaryScreenWidth - PanelWidth - 8;
        Show();
        Activate();
    }

    private void SafeClose()
    {
        if (_closing)
            return;
        _closing = true;
        Close();
    }

    private void Build()
    {
        // Tiles
        var tiles = new UniformGrid { Columns = 2 };
        var wifi = Tile("", "Wi-Fi", "…");
        var bluetooth = Tile("", "Bluetooth", "…");
        var dark = Tile("", "Dark mode", DockTheme.IsLight ? "Off" : "On");
        dark.IsChecked = !DockTheme.IsLight;
        var tray = new Button { Content = TileContent("", "Tray apps", "Background apps") };
        StyleTile(tray);
        tiles.Children.Add(wifi);
        tiles.Children.Add(bluetooth);
        tiles.Children.Add(dark);
        tiles.Children.Add(tray);
        _root.Children.Add(tiles);

        var links = new DockPanel { Margin = new Thickness(4, 2, 4, 6) };
        links.Children.Add(Link("Wi-Fi networks ›", "ms-availablenetworks:"));
        var btLink = Link("Bluetooth devices ›", "ms-settings:bluetooth");
        btLink.HorizontalAlignment = HorizontalAlignment.Right;
        links.Children.Add(btLink);
        _root.Children.Add(links);

        // Sound
        _root.Children.Add(Header("Sound"));
        var mute = IconButton(_audio.Muted ? "" : "", "Mute / unmute");
        var volume = new Slider { Minimum = 0, Maximum = 100, Value = (_audio.Volume ?? 0) * 100, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
        var volumeText = new TextBlock { Width = 36, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = $"{volume.Value:0}" };
        volume.ValueChanged += (_, _) =>
        {
            _audio.Volume = (float)(volume.Value / 100);
            if (_audio.Muted && volume.Value > 0)
                _audio.Muted = false;
            volumeText.Text = $"{volume.Value:0}";
            ((TextBlock)mute.Content).Text = _audio.Muted ? "" : "";
        };
        mute.Click += (_, _) =>
        {
            _audio.Muted = !_audio.Muted;
            ((TextBlock)mute.Content).Text = _audio.Muted ? "" : "";
        };
        _root.Children.Add(SliderRow(mute, volume, volumeText));
        _root.Children.Add(Hint(_audio.DeviceName is { Length: > 0 } device ? device : "No speakers found", new Thickness(44, 0, 0, 0)));

        // Brightness (only on screens that support it; WMI is slow, so look it up in the background)
        var brightnessHeader = Header("Brightness");
        var brightness = new Slider { Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
        var brightnessText = new TextBlock { Width = 36, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var brightnessRow = SliderRow(IconButton("", "Brightness"), brightness, brightnessText);
        brightnessHeader.Visibility = brightnessRow.Visibility = Visibility.Collapsed;
        _root.Children.Add(brightnessHeader);
        _root.Children.Add(brightnessRow);
        _ = Task.Run(Brightness.Read).ContinueWith(t => Dispatcher.Invoke(() =>
        {
            if (t.Result is not int level)
                return;
            brightness.Value = level;
            brightnessText.Text = $"{level}";
            brightnessHeader.Visibility = brightnessRow.Visibility = Visibility.Visible;
            brightness.ValueChanged += (_, _) =>
            {
                brightnessText.Text = $"{brightness.Value:0}";
                _pendingBrightness = (int)brightness.Value;
                _brightnessDelay.Stop();
                _brightnessDelay.Start();
            };
        }));

        // Bluetooth devices
        var devices = new StackPanel();
        _root.Children.Add(devices);

        // Battery
        var battery = Battery.Read();
        if (battery.HasBattery)
        {
            _root.Children.Add(Header("Battery"));
            var detail = battery.Charging
                ? battery.Percent >= 100 ? "Fully charged" : "Charging"
                : battery.Remaining is { } left ? $"About {(int)left.TotalHours} h {left.Minutes} min left" : "On battery";
            var row = new DockPanel { Margin = new Thickness(4, 0, 4, 0) };
            var link = Link("Power settings ›", "ms-settings:batterysaver");
            DockPanel.SetDock(link, Dock.Right);
            row.Children.Add(link);
            row.Children.Add(new TextBlock { Text = $"{battery.Percent}%  ·  {detail}", VerticalAlignment = VerticalAlignment.Center });
            _root.Children.Add(row);
        }

        // Footer
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        footer.Children.Add(TextButton("Quick Settings", () => { SafeClose(); ShellNative.PressWinKey('A'); }, "Windows' own quick settings panel"));
        footer.Children.Add(TextButton("Notifications", () => { SafeClose(); ShellNative.PressWinKey('N'); }, "Notifications and calendar"));
        var settings = IconButton("", "All settings");
        settings.Click += (_, _) => { SafeClose(); ShellNative.Start("ms-settings:", null); };
        var lockButton = IconButton("", "Lock this PC");
        lockButton.Click += (_, _) => { SafeClose(); ShellNative.Lock(); };
        DockPanel.SetDock(settings, Dock.Right);
        DockPanel.SetDock(lockButton, Dock.Right);
        footer.Children.Add(settings);
        footer.Children.Add(lockButton);
        _root.Children.Add(footer);

        // Tile behavior
        tray.Click += (_, _) =>
        {
            SafeClose();
            _dock.RevealTray();
        };
        dark.Click += (_, _) => SetDarkMode(dark.IsChecked == true, dark);
        _ = SetUpRadio(wifi, RadioKind.WiFi, "ms-availablenetworks:", null);
        _ = SetUpRadio(bluetooth, RadioKind.Bluetooth, "ms-settings:bluetooth", devices);
    }

    private async Task SetUpRadio(ToggleButton tile, RadioKind kind, string fallbackUri, StackPanel? devices)
    {
        var radio = await Connectivity.FindRadio(kind);
        async Task Update()
        {
            var on = radio?.State == RadioState.On;
            tile.IsChecked = on;
            string subtitle;
            if (radio is null)
                subtitle = "Open settings";
            else if (!on)
                subtitle = "Off";
            else if (kind == RadioKind.WiFi)
                subtitle = Connectivity.CurrentNetwork() is { IsWifi: true } n ? n.Name : "Not connected";
            else
            {
                var paired = await Connectivity.PairedBluetoothDevices();
                subtitle = paired.FirstOrDefault(d => d.Connected)?.Name ?? "On";
                if (devices is not null)
                    ShowDevices(devices, paired);
            }

            SetSubtitle(tile, subtitle);
            if (devices is not null && !on)
                devices.Children.Clear();
        }

        await Update();
        tile.Click += async (_, _) =>
        {
            if (radio is null)
            {
                SafeClose();
                ShellNative.Start(fallbackUri, null);
                return;
            }

            try
            {
                await radio.SetStateAsync(tile.IsChecked == true ? RadioState.On : RadioState.Off);
                await Task.Delay(kind == RadioKind.WiFi ? 1500 : 500); // give it a moment to (dis)connect
            }
            catch (Exception)
            {
                // Windows refused (e.g. airplane mode policy); just show the real state.
            }

            await Update();
        };
    }

    private void ShowDevices(StackPanel panel, List<Connectivity.BluetoothDeviceInfo> devices)
    {
        panel.Children.Clear();
        if (devices.Count == 0)
            return;
        panel.Children.Add(Header("Bluetooth devices"));
        foreach (var device in devices.Take(5))
        {
            var row = new DockPanel { Margin = new Thickness(4, 2, 4, 2) };
            var state = new TextBlock { Text = device.Connected ? "Connected" : "Not connected", Opacity = 0.7, FontSize = 12 };
            DockPanel.SetDock(state, Dock.Right);
            row.Children.Add(state);
            row.Children.Add(new TextBlock { Text = device.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(row);
        }
    }

    /// <summary>Switches apps and Windows to dark/light together (like GNOME's "Dark Style").</summary>
    private static void SetDarkMode(bool dark, ToggleButton tile)
    {
        try
        {
            var catalog = new SettingsCatalog(SettingContext.ForCurrentMachine());
            var value = System.Text.Json.Nodes.JsonValue.Create(dark ? "dark" : "light");
            catalog.Get<AppsThemeSetting>().ApplyJson(value);
            if (catalog.Get<SystemThemeSetting>().IsSupported)
                catalog.Get<SystemThemeSetting>().ApplyJson(value);
            catalog.Context.System.BroadcastSettingChange("ImmersiveColorSet");
            SetSubtitle(tile, dark ? "On" : "Off");
        }
        catch (Exception)
        {
            tile.IsChecked = !dark;
        }
    }

    // ---- Small UI helpers --------------------------------------------------------------

    private static ToggleButton Tile(string glyph, string title, string subtitle)
    {
        var tile = new ToggleButton { Content = TileContent(glyph, title, subtitle) };
        StyleTile(tile);
        return tile;
    }

    private static void StyleTile(ButtonBase tile)
    {
        tile.Height = 60;
        tile.Margin = new Thickness(4);
        tile.Padding = new Thickness(12, 6, 12, 6);
        tile.HorizontalAlignment = HorizontalAlignment.Stretch;
        tile.HorizontalContentAlignment = HorizontalAlignment.Left;
        tile.Focusable = false;
    }

    private static StackPanel TileContent(string glyph, string title, string subtitle)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 110, Tag = "subtitle" });
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = glyph, FontFamily = DockTheme.Icons, FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        panel.Children.Add(text);
        return panel;
    }

    private static void SetSubtitle(ContentControl tile, string subtitle)
    {
        if (tile.Content is StackPanel { Children: [_, StackPanel text] } && text.Children[1] is TextBlock sub)
            sub.Text = subtitle;
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(4, 12, 0, 4),
    };

    private static TextBlock Hint(string text, Thickness margin) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = 0.7,
        Margin = margin,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static Button IconButton(string glyph, string tip) => new()
    {
        Content = new TextBlock { Text = glyph, FontFamily = DockTheme.Icons, FontSize = 16 },
        Width = 36,
        Height = 32,
        Padding = new Thickness(0),
        ToolTip = tip,
        Focusable = false,
    };

    private Button TextButton(string text, Action click, string tip)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), ToolTip = tip, Focusable = false };
        button.Click += (_, _) => click();
        return button;
    }

    private Button Link(string text, string uri)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 12 },
            Padding = new Thickness(6, 2, 6, 2),
            Focusable = false,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        button.Click += (_, _) =>
        {
            SafeClose();
            ShellNative.Start(uri, null);
        };
        return button;
    }

    private static DockPanel SliderRow(Button icon, Slider slider, TextBlock value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 4, 0) };
        icon.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(value);
        row.Children.Add(slider);
        return row;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
