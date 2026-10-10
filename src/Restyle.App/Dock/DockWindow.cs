using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Restyle.Core.Dock;

namespace Restyle.App.Docking;

/// <summary>
/// A macOS-style dock: pinned and running apps, hover magnification, running dots, launch bounce.
/// A transparent, non-activating, always-on-top window that registers as an app bar.
/// </summary>
public sealed class DockWindow : Window
{
    private const double Pad = 8;
    private const double Gap = 6;
    private const double BottomMargin = 6;
    private const double LabelSpace = 44;
    private const int IconPixels = 128;
    private const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    private readonly string _configPath;
    private readonly Grid _root = new();
    private readonly Border _panel = new() { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1) };
    private readonly StackPanel _items = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _label = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 3, 9, 4), Visibility = Visibility.Collapsed };
    private readonly TextBlock _labelText = new() { FontSize = 13, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };
    private readonly ResourceDictionary _menuResources = (ResourceDictionary)Application.LoadComponent(new Uri("/Restyle;component/Dock/DockMenu.xaml", UriKind.Relative));
    private readonly DispatcherTimer _refreshDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly List<string> _runningOrder = [];
    private readonly Dictionary<string, string> _names = [];
    private readonly List<IntPtr> _hooks = [];
    private readonly WinEventProc _hookProc;
    private readonly DispatcherTimer _taskbarGuard = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly uint _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    private DockConfig _config;
    private bool _taskbarHidden;
    private List<DockEntry> _entries = [];
    private DockEntry? _hovered;
    private string? _signature;
    private AppBar? _appBar;
    private FileSystemWatcher? _watcher;
    private Brush _dotBrush = Brushes.White;
    private static ImageSource? _startIcon;
    private TopBar? _topBar;
    private DateTime? _revealStarted;

    public DockWindow(string configPath)
    {
        _configPath = configPath;
        _config = DockConfig.Load(configPath);

        Title = "Restyle Dock";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;

        _label.Child = _labelText;
        _overlay.Children.Add(_label);
        _panel.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.35 };
        _root.Children.Add(_panel);
        _root.Children.Add(_items);
        _root.Children.Add(_overlay);
        Content = _root;

        _items.SizeChanged += (_, _) => _panel.Width = _items.ActualWidth + 2 * Pad;
        _root.MouseMove += (_, e) => Magnify(e.GetPosition(_root).X);
        _root.MouseLeave += (_, _) => ResetMagnify();
        _panel.MouseRightButtonUp += (_, e) =>
        {
            ShowMenu(null);
            e.Handled = true;
        };

        _refreshDebounce.Tick += (_, _) =>
        {
            _refreshDebounce.Stop();
            Refresh();
        };
        _poll.Tick += (_, _) => Refresh();
        _hookProc = OnWinEvent;

        _taskbarGuard.Tick += (_, _) => ApplyTaskbar();

        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => Cleanup();

        // Whatever happens, never leave the user without a taskbar or with reserved screen space.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreDesktop();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => RestoreDesktop();
    }

    private double IconSize => _config.IconSize;

    private double PanelHeight => IconSize + 19;

    // ---- Setup -------------------------------------------------------------------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)((long)ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);

        _appBar = new AppBar(hwnd);
        ApplyTheme();
        Position();
        ApplyTaskbar();
        EnsureTopBar();

        // Event-driven updates when windows appear/disappear, plus a slow safety poll.
        AddHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE);
        AddHook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);
        _poll.Start();

        var dir = System.IO.Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(dir);
        _watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(_configPath)) { EnableRaisingEvents = true };
        _watcher.Changed += (_, _) => Dispatcher.BeginInvoke(ReloadConfig, DispatcherPriority.Background);
        _watcher.Created += (_, _) => Dispatcher.BeginInvoke(ReloadConfig, DispatcherPriority.Background);

        Refresh();
    }

    private void ReloadConfig()
    {
        _config = DockConfig.Load(_configPath);
        _signature = null;
        Position();
        ApplyTaskbar();
        EnsureTopBar();
        Refresh();
    }

    private void EnsureTopBar()
    {
        if (_config.ShowTopBar && _topBar is null)
        {
            _topBar = new TopBar(this);
            _topBar.Closed += (_, _) => _topBar = null;
            _topBar.Show();
        }
        else if (!_config.ShowTopBar && _topBar is not null)
        {
            _topBar.Close();
        }
    }

    /// <summary>
    /// Briefly brings back the real Windows taskbar so you can use tray (background) apps. It hides
    /// again once it slides away, or after a minute.
    /// </summary>
    public void RevealTray()
    {
        if (_config.HideTaskbar)
        {
            _revealStarted = DateTime.Now;
            ShellNative.SetTaskbarsVisible(true);
        }

        // Win+T pops up an auto-hidden taskbar and puts focus on it.
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            ShellNative.PressWinKey('T');
        };
        delay.Start();
    }

    /// <summary>
    /// "Completely hide the taskbar": hides Explorer's taskbar windows while the dock runs and re-hides
    /// them if Windows shows them again. Turning the option off or quitting the dock shows them.
    /// </summary>
    private void ApplyTaskbar()
    {
        if (_config.HideTaskbar)
        {
            if (_revealStarted is DateTime started)
            {
                // The user asked to see the tray: wait until the taskbar slides away (or a minute passes).
                var elapsed = DateTime.Now - started;
                if (elapsed < TimeSpan.FromSeconds(3) || (ShellNative.TaskbarIsShowing() && elapsed < TimeSpan.FromMinutes(1)))
                {
                    _taskbarGuard.Start();
                    return;
                }

                _revealStarted = null;
            }

            ShellNative.SetTaskbarsVisible(false);
            _taskbarHidden = true;
            _taskbarGuard.Start();
        }
        else
        {
            _taskbarGuard.Stop();
            if (_taskbarHidden)
                ShellNative.SetTaskbarsVisible(true);
            _taskbarHidden = false;
        }
    }

    private void RestoreDesktop()
    {
        try
        {
            if (_taskbarHidden)
                ShellNative.SetTaskbarsVisible(true);
            _taskbarHidden = false;
            _appBar?.Dispose();
        }
        catch (Exception)
        {
            // Best effort during shutdown.
        }
    }

    private void Position()
    {
        if (_appBar is null)
            return;

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var band = PanelHeight + BottomMargin;
        var height = band + IconSize * (DockMath.MaxScale - 1) + LabelSpace;

        double bottom;
        if (_config.ReserveSpace)
        {
            _appBar.Register();
            bottom = _appBar.Reserve((int)Math.Ceiling(band * scale)) / scale;
        }
        else
        {
            _appBar.Dispose();
            bottom = SystemParameters.WorkArea.Bottom;
        }

        Left = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = height;
        Top = bottom - height;
        _panel.Height = PanelHeight;
        _panel.Margin = new Thickness(0, 0, 0, BottomMargin);
        _items.Margin = new Thickness(0, 0, 0, BottomMargin + 4);
    }

    private void ApplyTheme()
    {
        var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is 1;
        Brush B(uint argb) => new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

        _panel.Background = light ? B(0xE0F4F4F4) : B(0xD91E1E1E);
        _panel.BorderBrush = light ? B(0x22000000) : B(0x38FFFFFF);
        _dotBrush = light ? B(0xCC000000) : B(0xE6FFFFFF);
        _label.Background = light ? B(0xF8FFFFFF) : B(0xF0262626);
        _labelText.Foreground = light ? B(0xFF1A1A1A) : Brushes.White;
        _menuResources["DockMenuBg"] = light ? B(0xF8F9F9F9) : B(0xF0262626);
        _menuResources["DockMenuFg"] = light ? B(0xFF1A1A1A) : Brushes.White;
        _menuResources["DockMenuHover"] = light ? B(0x18000000) : B(0x26FFFFFF);
        _menuResources["DockMenuBorder"] = light ? B(0x22000000) : B(0x33FFFFFF);
        foreach (var entry in _entries)
        {
            if (entry.Dot is not null)
                entry.Dot.Fill = _dotBrush;
        }
    }

    // ---- Model -------------------------------------------------------------------------

    private void Refresh()
    {
        List<AppWindow> windows;
        try
        {
            windows = ShellNative.GetAppWindows();
        }
        catch (Exception)
        {
            return;
        }

        var groups = windows
            .GroupBy(w => DockConfig.KeyFor(w.AppId, w.ExePath))
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Running apps that aren't pinned keep the order in which they first appeared.
        _runningOrder.RemoveAll(k => !groups.ContainsKey(k));
        foreach (var key in groups.Keys.Where(k => !_config.IsPinned(k) && !_runningOrder.Contains(k)))
            _runningOrder.Add(key);

        var entries = new List<DockEntry> { new(EntryKind.Start, "start", "Start") };
        foreach (var pin in _config.Pinned)
            entries.Add(new DockEntry(EntryKind.App, pin.Key, pin.Name) { Pin = pin, Windows = groups.GetValueOrDefault(pin.Key) ?? [] });
        foreach (var key in _runningOrder.Where(k => !_config.IsPinned(k)))
        {
            var first = groups[key][0];
            var name = _names.TryGetValue(key, out var n) ? n : _names[key] = ShellNative.AppName(first.AppId, first.ExePath, first.Title);
            entries.Add(new DockEntry(EntryKind.App, key, name) { Windows = groups[key], AppId = first.AppId, ExePath = first.ExePath });
        }

        entries.Add(new DockEntry(EntryKind.Separator, "sep", ""));
        entries.Add(new DockEntry(EntryKind.Trash, "trash", "Recycle Bin"));

        var signature = string.Join("|", entries.Select(e => e.Key)) + $"#{IconSize}";
        if (signature == _signature)
        {
            // Same apps in the same places: just update windows and running dots.
            for (var i = 0; i < entries.Count; i++)
            {
                _entries[i].Windows = entries[i].Windows;
                if (_entries[i].Dot is { } dot)
                    dot.Visibility = _entries[i].Windows.Count > 0 ? Visibility.Visible : Visibility.Hidden;
            }

            return;
        }

        _signature = signature;
        _entries = entries;
        Rebuild();
    }

    private void Rebuild()
    {
        _items.Children.Clear();
        _hovered = null;
        _label.Visibility = Visibility.Collapsed;

        foreach (var entry in _entries)
        {
            if (entry.Kind == EntryKind.Separator)
            {
                entry.View = new Rectangle
                {
                    Width = 1,
                    Height = IconSize * 0.7,
                    Fill = _panel.BorderBrush,
                    Margin = new Thickness(Gap, 0, Gap, IconSize * 0.15 + 7),
                    VerticalAlignment = VerticalAlignment.Bottom,
                };
                _items.Children.Add(entry.View);
                continue;
            }

            entry.Image = new Image
            {
                Source = IconFor(entry),
                Width = IconSize,
                Height = IconSize,
                VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransform = new TranslateTransform(),
            };
            RenderOptions.SetBitmapScalingMode(entry.Image, BitmapScalingMode.HighQuality);
            entry.Dot = new Ellipse
            {
                Width = 4,
                Height = 4,
                Margin = new Thickness(0, 3, 0, 0),
                Fill = _dotBrush,
                Visibility = entry.Windows.Count > 0 ? Visibility.Visible : Visibility.Hidden,
            };

            var stack = new StackPanel { Margin = new Thickness(Gap / 2, 0, Gap / 2, 0), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Bottom };
            stack.Children.Add(entry.Image);
            stack.Children.Add(entry.Dot);
            stack.MouseEnter += (_, _) => _hovered = entry;
            stack.MouseLeave += (_, _) =>
            {
                if (_hovered == entry)
                    _hovered = null;
                UpdateLabel();
            };
            stack.MouseLeftButtonUp += (_, _) => OnClick(entry);
            stack.MouseRightButtonUp += (_, e) =>
            {
                ShowMenu(entry);
                e.Handled = true;
            };
            entry.View = stack;
            _items.Children.Add(stack);
        }
    }

    private ImageSource? IconFor(DockEntry entry)
    {
        switch (entry.Kind)
        {
            case EntryKind.Start:
                return StartIcon;
            case EntryKind.Trash:
                return ShellNative.Icon(RecycleBin, IconPixels, cache: false); // empty/full changes
        }

        if (entry.Pin is { } pin)
        {
            return pin.Kind == PinKind.AppId
                ? ShellNative.Icon("shell:AppsFolder\\" + pin.Target, IconPixels)
                : ShellNative.Icon(Environment.ExpandEnvironmentVariables(pin.Target), IconPixels);
        }

        return (entry.AppId is not null ? ShellNative.Icon("shell:AppsFolder\\" + entry.AppId, IconPixels) : null)
            ?? (entry.ExePath is not null ? ShellNative.Icon(entry.ExePath, IconPixels) : null);
    }

    /// <summary>A launcher-style grid icon for the Start button.</summary>
    internal static ImageSource StartIcon => _startIcon ??= DrawStartIcon();

    private static ImageSource DrawStartIcon()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = new LinearGradientBrush(Color.FromRgb(0x35, 0x84, 0xE4), Color.FromRgb(0x91, 0x41, 0xAC), 45);
            dc.DrawRoundedRectangle(bg, null, new Rect(6, 6, 116, 116), 28, 28);
            var dot = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
            for (var r = 0; r < 3; r++)
            {
                for (var c = 0; c < 3; c++)
                    dc.DrawRoundedRectangle(dot, null, new Rect(30 + c * 26, 30 + r * 26, 16, 16), 4, 4);
            }
        }

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // ---- Interaction -------------------------------------------------------------------

    private void Magnify(double mouseX)
    {
        MemoryTrimmer.Touch();
        if (_config.Magnify)
        {
            var widths = _entries.Select(e => e.Kind == EntryKind.Separator ? 1 + 2 * Gap : IconSize + Gap).ToList();
            var x = _root.ActualWidth / 2 - widths.Sum() / 2;
            for (var i = 0; i < _entries.Count; i++)
            {
                var center = x + widths[i] / 2;
                x += widths[i];
                if (_entries[i].Image is { } image)
                    image.Width = image.Height = IconSize * DockMath.Scale(mouseX - center, IconSize);
            }
        }

        UpdateLabel();
    }

    private void ResetMagnify()
    {
        foreach (var entry in _entries)
        {
            if (entry.Image is { } image)
                image.Width = image.Height = IconSize;
        }

        _hovered = null;
        _label.Visibility = Visibility.Collapsed;
    }

    private void UpdateLabel()
    {
        if (_hovered?.View is not { } view)
        {
            _label.Visibility = Visibility.Collapsed;
            return;
        }

        _labelText.Text = _hovered.Windows.Count > 1 ? $"{_hovered.Name} ({_hovered.Windows.Count} windows)" : _hovered.Name;
        _root.UpdateLayout();
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var top = view.TranslatePoint(new Point(view.ActualWidth / 2, 0), _root);
        Canvas.SetLeft(_label, top.X - _label.DesiredSize.Width / 2);
        Canvas.SetTop(_label, Math.Max(0, top.Y - _label.DesiredSize.Height - 8));
        _label.Visibility = Visibility.Visible;
    }

    private void OnClick(DockEntry entry)
    {
        try
        {
            switch (entry.Kind)
            {
                case EntryKind.Start:
                    ShellNative.OpenStartMenu();
                    return;
                case EntryKind.Trash:
                    ShellNative.Start("explorer.exe", "shell:RecycleBinFolder");
                    return;
            }

            if (entry.Windows.Count == 0)
            {
                Launch(entry);
                return;
            }

            // Like macOS/Windows: bring to front; click again to minimize; with several windows, cycle.
            var foreground = ShellNative.Foreground;
            var index = entry.Windows.FindIndex(w => w.Handle == foreground);
            if (index >= 0 && entry.Windows.Count == 1)
                ShellNative.Minimize(foreground);
            else
                ShellNative.Activate(entry.Windows[index >= 0 ? (index + 1) % entry.Windows.Count : 0].Handle);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open {entry.Name}: {ex.Message}", "Restyle Dock", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Launch(DockEntry entry)
    {
        if (entry.Pin is { Kind: PinKind.AppId } appPin)
            ShellNative.LaunchAppId(appPin.Target);
        else if (entry.Pin is { } pathPin)
            ShellNative.LaunchPath(Environment.ExpandEnvironmentVariables(pathPin.Target));
        else if (entry.AppId is not null)
            ShellNative.LaunchAppId(entry.AppId);
        else if (entry.ExePath is not null)
            ShellNative.LaunchPath(entry.ExePath);
        else
            return;

        Bounce(entry);
    }

    private static void Bounce(DockEntry entry)
    {
        if (entry.Image?.RenderTransform is not TranslateTransform move)
            return;
        var animation = new DoubleAnimation(0, -14, TimeSpan.FromMilliseconds(220))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        move.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    private void ShowMenu(DockEntry? entry)
    {
        var menu = new ContextMenu { Style = (Style)_menuResources["DockContextMenu"] };
        menu.Resources.MergedDictionaries.Add(_menuResources);

        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Restyle Dock", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            menu.Items.Add(item);
        }

        if (entry is { Kind: EntryKind.App })
        {
            Add(entry.Name, () => { }, enabled: false);
            Add(entry.Windows.Count > 0 ? "Open new window" : "Open", () => Launch(entry));
            if (entry.Windows.Count > 0)
            {
                Add(entry.Windows.Count == 1 ? "Close window" : $"Close all {entry.Windows.Count} windows",
                    () => entry.Windows.ForEach(w => ShellNative.Close(w.Handle)));
            }

            menu.Items.Add(new Separator());
            if (entry.Pin is not null)
                Add("Remove from Dock", () => Unpin(entry.Key));
            else if (entry.AppId is not null || entry.ExePath is not null)
                Add("Keep in Dock", () => Pin(entry));
            menu.Items.Add(new Separator());
        }

        Add(_config.Magnify ? "Turn magnification off" : "Turn magnification on", () =>
        {
            _config.Magnify = !_config.Magnify;
            SaveConfig();
        });
        Add("Dock settingsâ€¦", () => ShellNative.Start(Environment.ProcessPath!, null));
        Add("Quit dock", Close);

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void Pin(DockEntry entry)
    {
        _config.Pinned.Add(entry.AppId is not null
            ? new DockPin(entry.Name, entry.AppId, PinKind.AppId)
            : new DockPin(entry.Name, entry.ExePath!, PinKind.Path));
        SaveConfig();
    }

    private void Unpin(string key)
    {
        _config.Pinned.RemoveAll(p => p.Key == key);
        SaveConfig();
    }

    private void SaveConfig()
    {
        _config.Save(_configPath);
        _signature = null;
        Refresh();
    }

    // ---- Windows messages & events -----------------------------------------------------

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_appBar is not null && msg == _appBar.CallbackMessage)
        {
            switch ((int)wParam)
            {
                case AppBar.ABN_POSCHANGED:
                    Position();
                    break;
                case AppBar.ABN_FULLSCREENAPP:
                    // Get out of the way of fullscreen games and videos.
                    var fullscreen = lParam != IntPtr.Zero;
                    Topmost = !fullscreen;
                    Visibility = fullscreen ? Visibility.Hidden : Visibility.Visible;
                    break;
            }
        }

        if (msg == _taskbarCreatedMessage)
        {
            // Explorer restarted: it dropped our app bar and made a fresh (visible) taskbar.
            _appBar?.Forget();
            Dispatcher.BeginInvoke(() =>
            {
                Position();
                ApplyTaskbar();
                _topBar?.ExplorerRestarted();
            });
        }

        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                handled = true;
                return (IntPtr)MA_NOACTIVATE; // clicking the dock never steals focus from your app
            case WM_SETTINGCHANGE:
                if (lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
                    ApplyTheme();
                if (!_config.ReserveSpace)
                    Dispatcher.BeginInvoke(Position);
                break;
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                Dispatcher.BeginInvoke(Position);
                break;
        }

        return IntPtr.Zero;
    }

    private void AddHook(uint min, uint max)
    {
        var hook = SetWinEventHook(min, max, IntPtr.Zero, _hookProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (hook != IntPtr.Zero)
            _hooks.Add(hook);
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0)
            return; // only whole windows matter
        _refreshDebounce.Stop();
        _refreshDebounce.Start();
    }

    private void Cleanup()
    {
        _poll.Stop();
        _refreshDebounce.Stop();
        foreach (var hook in _hooks)
            UnhookWinEvent(hook);
        _hooks.Clear();
        _watcher?.Dispose();
        _taskbarGuard.Stop();
        _topBar?.Close();
        RestoreDesktop();
    }

    private enum EntryKind
    {
        App,
        Start,
        Trash,
        Separator,
    }

    private sealed class DockEntry(EntryKind kind, string key, string name)
    {
        public EntryKind Kind { get; } = kind;
        public string Key { get; } = key;
        public string Name { get; } = name;
        public DockPin? Pin { get; init; }
        public string? AppId { get; init; }
        public string? ExePath { get; init; }
        public List<AppWindow> Windows { get; set; } = [];
        public FrameworkElement? View { get; set; }
        public Image? Image { get; set; }
        public Ellipse? Dot { get; set; }
    }

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    private const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, WM_SETTINGCHANGE = 0x1A, WM_DISPLAYCHANGE = 0x7E, WM_DPICHANGED = 0x2E0;
    private const uint EVENT_OBJECT_CREATE = 0x8000, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;
    private const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;

    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
}
