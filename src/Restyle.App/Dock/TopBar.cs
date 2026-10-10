using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace Restyle.App.Docking;

/// <summary>Light/dark colors shared by the dock, top bar and control center.</summary>
internal static class DockTheme
{
    public static bool IsLight =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is 1;

    public static SolidColorBrush Brush(uint argb) =>
        new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI");
}

/// <summary>
/// A Mac/GNOME-style bar across the top: Start + current app on the left, the clock in the middle,
/// tray/Wi-Fi/Bluetooth/volume/battery on the right. Clicking the status icons opens the control center.
/// </summary>
public sealed class TopBar : Window
{
    private const double BarHeight = 30;

    private readonly DockWindow _dock;
    private readonly Border _background = new();
    private readonly TextBlock _appName = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _clock = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _wifi = Glyph("");
    private readonly TextBlock _bluetooth = Glyph("");
    private readonly TextBlock _volume = Glyph("");
    private readonly TextBlock _battery = Glyph("");
    private readonly TextBlock _batteryText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
    private readonly List<Button> _buttons = [];
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Audio _audio = new();
    private readonly Dictionary<string, string> _names = [];
    private readonly WinEventProc _foregroundProc;
    private IntPtr _foregroundHook;
    private AppBar? _appBar;
    private ControlCenter? _controlCenter;
    private DateTime _lastClosed;
    private int _tick;

    public TopBar(DockWindow dock)
    {
        _dock = dock;
        Title = "Restyle Top Bar";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = DockTheme.Text;
        FontSize = 13;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        left.Children.Add(BarButton(new Image { Source = DockWindow.StartIcon, Width = 18, Height = 18 }, ShellNative.OpenStartMenu, "Start"));
        _appName.Margin = new Thickness(8, 0, 0, 0);
        left.Children.Add(_appName);
        grid.Children.Add(left);

        var clock = BarButton(_clock, () => ShellNative.PressWinKey('N'), "Notifications and calendar");
        Grid.SetColumn(clock, 1);
        grid.Children.Add(clock);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 6, 0) };
        right.Children.Add(BarButton(Glyph(""), _dock.RevealTray, "Background apps (system tray)"));
        var status = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var icon in new[] { _wifi, _bluetooth, _volume, _battery })
        {
            icon.Margin = new Thickness(6, 0, 6, 0);
            status.Children.Add(icon);
        }

        status.Children.Add(_batteryText);
        right.Children.Add(BarButton(status, ToggleControlCenter, "Wi-Fi, Bluetooth, sound, brightness and battery"));
        right.Children.Add(BarButton(Glyph(""), () => ShellNative.PressWinKey('N'), "Notifications"));
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        _background.Child = grid;
        Content = _background;

        _clockTimer.Tick += (_, _) => Tick();
        _foregroundProc = (_, _, hwnd, _, _, _, _) => UpdateAppName(hwnd);
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => Cleanup();
    }

    private static TextBlock Glyph(string glyph) => new()
    {
        Text = glyph,
        FontFamily = DockTheme.Icons,
        FontSize = 14,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Button BarButton(UIElement content, Action click, string tip)
    {
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(1, 3, 1, 3),
            ToolTip = tip,
            Focusable = false,
            Cursor = Cursors.Hand,
            Template = ButtonTemplate(),
        };
        button.Click += (_, _) => click();
        _buttons.Add(button);
        return button;
    }

    /// <summary>A flat, rounded hover highlight instead of the classic 3D button.</summary>
    private static ControlTemplate ButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("TopBarHover"), "Bd"));
        template.Triggers.Add(hover);
        return template;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLongPtr(hwnd, -20);
        SetWindowLongPtr(hwnd, -20, (IntPtr)((long)ex | 0x80 /* tool window */ | 0x8000000 /* no activate */));
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);

        _appBar = new AppBar(hwnd, top: true);
        ApplyTheme();
        Position();
        Tick();
        _ = UpdateBluetooth();
        _clockTimer.Start();
        _foregroundHook = SetWinEventHook(3 /* EVENT_SYSTEM_FOREGROUND */, 3, IntPtr.Zero, _foregroundProc, 0, 0, 2 /* skip own process */);
        UpdateAppName(ShellNative.Foreground);
    }

    public void Position()
    {
        if (_appBar is null)
            return;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        _appBar.Register();
        var top = _appBar.Reserve((int)Math.Ceiling(BarHeight * scale)) / scale;
        Left = 0;
        Top = top;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = BarHeight;
    }

    public void ApplyTheme()
    {
        var light = DockTheme.IsLight;
        _background.Background = light ? DockTheme.Brush(0xE6F3F3F3) : DockTheme.Brush(0xE61A1A1A);
        Foreground = light ? DockTheme.Brush(0xFF1A1A1A) : Brushes.White;
        Resources["TopBarHover"] = light ? DockTheme.Brush(0x1A000000) : DockTheme.Brush(0x26FFFFFF);
        foreach (var b in _buttons)
            b.Foreground = Foreground;
    }

    /// <summary>Clock every second; status icons every few seconds (cheap calls only).</summary>
    private void Tick()
    {
        var now = DateTime.Now;
        _clock.Text = $"{now:ddd MMM d}   {now:t}";
        if (_tick++ % 3 != 0)
            return;

        var network = Connectivity.CurrentNetwork();
        _wifi.Text = network switch
        {
            null => "",              // no connection (shown dimmed)
            { IsWifi: false } => "", // ethernet
            { Bars: <= 1 } => "",
            { Bars: 2 } => "",
            { Bars: 3 } => "",
            _ => "",
        };
        _wifi.Opacity = network is null ? 0.4 : 1;

        var volume = _audio.Volume;
        _volume.Text = volume is null || _audio.Muted ? ""
            : volume < 0.01 ? "" : volume < 0.34 ? "" : volume < 0.67 ? "" : "";

        var battery = Battery.Read();
        _battery.Visibility = _batteryText.Visibility = battery.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        if (battery.HasBattery)
        {
            var level = Math.Clamp(battery.Percent / 10, 0, 10);
            _battery.Text = battery.Charging ? char.ConvertFromUtf32(0xEBAB + level) : level == 10 ? "" : char.ConvertFromUtf32(0xE850 + level);
            _batteryText.Text = $"{battery.Percent}%";
        }

        if (_tick % 30 == 1)
            _ = UpdateBluetooth();
    }

    public async Task UpdateBluetooth()
    {
        var radio = await Connectivity.FindRadio(RadioKind.Bluetooth);
        _bluetooth.Visibility = radio is null ? Visibility.Collapsed : Visibility.Visible;
        _bluetooth.Opacity = radio?.State == RadioState.On ? 1 : 0.45;
    }

    private void UpdateAppName(IntPtr hwnd)
    {
        if (ShellNative.IsDesktopOrShell(hwnd))
        {
            _appName.Text = "Desktop";
            return;
        }

        if (ShellNative.Describe(hwnd) is not { } window)
            return; // our own windows: keep showing the previous app

        var key = window.AppId ?? window.ExePath ?? window.Title;
        if (!_names.TryGetValue(key, out var name))
            _names[key] = name = ShellNative.AppName(window.AppId, window.ExePath, window.Title);
        _appName.Text = name;
    }

    private void ToggleControlCenter()
    {
        if (_controlCenter is not null)
        {
            _controlCenter.Close();
            return;
        }

        // The click that closed it (by deactivating) shouldn't immediately reopen it.
        if ((DateTime.Now - _lastClosed).TotalMilliseconds < 300)
            return;

        _controlCenter = new ControlCenter(_audio, _dock);
        _controlCenter.Closed += (_, _) =>
        {
            _controlCenter = null;
            _lastClosed = DateTime.Now;
            Tick();
            _ = UpdateBluetooth();

            // The panel pulls in the Fluent theme and device APIs; hand that memory back once it's gone.
            var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            later.Tick += (_, _) =>
            {
                later.Stop();
                if (_controlCenter is null)
                    MemoryTrimmer.TrimNow();
            };
            later.Start();
        };
        _controlCenter.ShowBelow(Top + BarHeight);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_appBar is not null && msg == _appBar.CallbackMessage)
        {
            if ((int)wParam == AppBar.ABN_POSCHANGED)
            {
                Position();
            }
            else if ((int)wParam == AppBar.ABN_FULLSCREENAPP)
            {
                var fullscreen = lParam != IntPtr.Zero;
                Topmost = !fullscreen;
                Visibility = fullscreen ? Visibility.Hidden : Visibility.Visible;
            }
        }
        else if (msg == 0x21) // WM_MOUSEACTIVATE: never steal focus
        {
            handled = true;
            return 3;
        }
        else if (msg == 0x1A && lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            ApplyTheme();
        }
        else if (msg is 0x7E or 0x2E0) // display or DPI changed
        {
            Dispatcher.BeginInvoke(Position);
        }

        return IntPtr.Zero;
    }

    /// <summary>Explorer restarted and forgot our reserved space.</summary>
    public void ExplorerRestarted()
    {
        _appBar?.Forget();
        Position();
    }

    private void Cleanup()
    {
        _clockTimer.Stop();
        _controlCenter?.Close();
        if (_foregroundHook != IntPtr.Zero)
            UnhookWinEvent(_foregroundHook);
        _appBar?.Dispose();
    }

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}
