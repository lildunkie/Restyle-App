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
using Restyle.App.Services;
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
    private const double LabelSpace = 44;      // room above a bottom dock for the app-name label
    private const double SideLabelSpace = 230; // room beside a side dock for the label
    private const int LargeIconPixels = 256; // used while an icon is magnified
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
    private readonly DispatcherTimer _reloadDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _configCheck = new() { Interval = TimeSpan.FromSeconds(3) };
    private string _configStamp = "";
    private readonly DispatcherTimer _lockTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    private WallpaperLibrary? _lockPictures;
    private string? _lockSignature;
    private Brush _dotBrush = Brushes.White;
    private Brush _separatorBrush = Brushes.Gray;
    private static ImageSource? _startIcon;
    private TopBar? _topBar;
    private DesktopWidgets? _widgets;
    private StartMenu? _startMenu;
    private DateTime? _revealStarted;

    public DockWindow(string configPath)
    {
        _configPath = configPath;
        _config = DockConfig.Load(configPath);

        Title = "Restyle Dock";
        UseLayoutRounding = true; // icons land on whole pixels instead of being smeared across two
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;

        _label.Child = _labelText;
        _overlay.Children.Add(_label);
        _root.Children.Add(_panel);
        _root.Children.Add(_items);
        _root.Children.Add(_overlay);
        Content = _root;

        _items.SizeChanged += (_, _) => SizePanel();
        _root.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(_root);
            Magnify(Vertical ? p.Y : p.X);
        };
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

    private DockLook Look => DockLook.For(_config.Style);

    /// <summary>On the left or right edge (icons in a column) rather than along the bottom.</summary>
    private bool Vertical => _config.Edge != DockConfig.BottomEdge;

    private double IconSize => Math.Round(_config.IconSize * Look.IconScale);

    /// <summary>The panel's thickness across the dock (its height at the bottom, its width on a side).</summary>
    private double PanelHeight => IconSize + Look.Padding;

    private bool MagnifyOn => _config.Magnify && Look.Magnify;

    /// <summary>Space between the panel's edge and the icons (the running dot sits in it).</summary>
    private double ItemInset => Math.Max(2, (Look.Padding - 7) / 2 - 2);

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
        EnsureWidgets();
        PrepareStartMenu();

        // Event-driven updates when windows appear/disappear, plus a slow safety poll.
        AddHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE);
        AddHook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);
        _poll.Start();

        var dir = System.IO.Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(dir);
        // Pick up changes made in the Restyle window right away. The settings file is saved by swapping in
        // a new copy, which Windows reports as a rename, so listen for every kind of change. A burst of
        // saves (a preset sets several dock options) is applied once, a moment after the last one.
        _watcher = new FileSystemWatcher(dir, "*.json") { EnableRaisingEvents = true };
        FileSystemEventHandler changed = (_, e) => OnConfigFileEvent(e.Name);
        _watcher.Changed += changed;
        _watcher.Created += changed;
        _watcher.Renamed += (_, e) => OnConfigFileEvent(e.Name);
        _reloadDebounce.Tick += (_, _) =>
        {
            _reloadDebounce.Stop();
            ReloadConfig();
        };
        _configStamp = ConfigStamp();

        // Safety net in case Windows ever drops a file event: check every few seconds whether it changed.
        _configCheck.Tick += (_, _) =>
        {
            if (ConfigStamp() != _configStamp)
                ReloadConfig();

            // Restyle was updated: restart on the new version (it takes over as soon as this one has closed).
            if (UpdateCheck.UpdateReady())
            {
                _configCheck.Stop();
                // Put the taskbar and screen space back, start the new version, then leave directly: this old
                // copy's normal shutdown needs parts of the program file that were just replaced.
                Cleanup();
                UpdateCheck.Relaunch("--dock");
                Environment.Exit(0);
            }
        };
        _configCheck.Start();

        // Lock screen widgets: keep the picture's calendar, weather and note up to date.
        _lockTimer.Tick += (_, _) => RefreshLockScreen();
        _lockTimer.Start();
        RefreshLockScreen();

        Refresh();
    }

    /// <summary>Redraws the lock screen widgets (only sets a new picture if something actually changed).</summary>
    private async void RefreshLockScreen()
    {
        _lockSignature = LockSignature();
        if (!_config.LockWidgets.Enabled)
            return;
        try
        {
            _lockPictures ??= new WallpaperLibrary(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_configPath)!, "Wallpapers"));
            await LockScreenWidgets.RefreshAsync(_config, _lockPictures.Resolve);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex); // never let the lock screen take the dock down
        }
    }

    /// <summary>The settings that change what the lock screen widgets show.</summary>
    private string LockSignature() => System.Text.Json.JsonSerializer.Serialize(new
    {
        _config.LockWidgets,
        _config.Widgets.Cities,
        _config.Widgets.WeatherLatitude,
        _config.Widgets.WeatherLongitude,
        _config.Widgets.Fahrenheit,
    });

    private void OnConfigFileEvent(string? name)
    {
        if (!string.Equals(name, System.IO.Path.GetFileName(_configPath), StringComparison.OrdinalIgnoreCase))
            return;
        Dispatcher.BeginInvoke(() =>
        {
            _reloadDebounce.Stop();
            _reloadDebounce.Start();
        });
    }

    /// <summary>When the settings file was last written and how big it is (changes whenever it's saved).</summary>
    private string ConfigStamp()
    {
        try
        {
            var info = new FileInfo(_configPath);
            return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "";
        }
        catch (Exception)
        {
            return _configStamp;
        }
    }

    private void ReloadConfig()
    {
        _configStamp = ConfigStamp();
        _config = DockConfig.Load(_configPath);
        _signature = null;
        ApplyTheme(); // the style may have changed
        if (LockSignature() != _lockSignature)
            RefreshLockScreen();
        Position();
        ApplyTaskbar();
        EnsureTopBar();
        EnsureWidgets();
        PrepareStartMenu();
        Refresh();
    }

    /// <summary>Puts the desktop widgets into edit mode (turning them on first if they're off).</summary>
    public void EditWidgets()
    {
        if (!_config.ShowWidgets)
        {
            _config.ShowWidgets = true;
            _config.Save(_configPath);
            EnsureWidgets();
        }

        _widgets?.EnterEditMode();
    }

    private void EnsureWidgets(bool force = false)
    {
        if (_config.ShowWidgets)
        {
            _widgets ??= new DesktopWidgets(this);
            _widgets.Update(_config.Widgets, force);
        }
        else if (_widgets is not null)
        {
            _widgets.Close();
            _widgets = null;
            MemoryTrimmer.TrimNow();
        }
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
            ReleaseTaskbarSpace(true);
            _taskbarGuard.Start();
        }
        else
        {
            _taskbarGuard.Stop();
            if (_taskbarHidden)
                ShellNative.SetTaskbarsVisible(true);
            _taskbarHidden = false;
            ReleaseTaskbarSpace(false);
        }
    }

    /// <summary>
    /// A hidden taskbar still reserves its strip at the bottom, so maximized windows would stop short of
    /// the screen edge. While it's hidden we switch on auto-hide (which gives the space back) and switch
    /// it off again afterwards — only if it was off before. A marker file remembers that we did it, so
    /// it's undone even after a crash.
    /// </summary>
    private void ReleaseTaskbarSpace(bool release)
    {
        try
        {
            var system = new Restyle.Core.Platform.WindowsSystemApi();
            if (release && !File.Exists(AutoHideMarker) && !system.GetTaskbarAutoHide())
            {
                File.WriteAllText(AutoHideMarker, "Restyle turned on taskbar auto-hide while the dock hides the taskbar.");
                system.SetTaskbarAutoHide(true);
            }
            else if (!release && File.Exists(AutoHideMarker))
            {
                system.SetTaskbarAutoHide(false);
                File.Delete(AutoHideMarker);
            }
        }
        catch (Exception)
        {
            // Best effort: worst case maximized windows leave a gap where the taskbar was.
        }
    }

    private static string AutoHideMarker => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Restyle", "taskbar-autohide-by-dock");

    private void RestoreDesktop()
    {
        try
        {
            if (_taskbarHidden)
                ShellNative.SetTaskbarsVisible(true);
            _taskbarHidden = false;
            ReleaseTaskbarSpace(false);
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

        var look = Look;
        var edge = _config.Edge;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var band = PanelHeight + look.EdgeMargin; // the strip kept free for the dock
        var depth = band + (MagnifyOn ? IconSize * (DockMath.MaxScale - 1) : 0) + (Vertical ? SideLabelSpace : LabelSpace);

        // Dock bar turned off: draw nothing (the empty window lets every click through) and reserve no space,
        // while the top bar, widgets, Start menu and hidden taskbar keep running in this process.
        _root.Visibility = _config.ShowDock ? Visibility.Visible : Visibility.Collapsed;

        var newEdge = edge switch { DockConfig.LeftEdge => AppBar.ABE_LEFT, DockConfig.RightEdge => AppBar.ABE_RIGHT, _ => AppBar.ABE_BOTTOM };
        if (_appBar.IsRegistered && _appBar.Edge != newEdge)
            _appBar.Dispose(); // moving to another edge: give the old strip back first
        _appBar.Edge = newEdge;

        double outer; // the screen edge the dock sits against
        if (_config.ReserveSpace && _config.ShowDock)
        {
            _appBar.Register();
            outer = _appBar.Reserve((int)Math.Ceiling(band * scale)) / scale;
        }
        else
        {
            _appBar.Dispose();
            var wa = SystemParameters.WorkArea;
            outer = edge switch { DockConfig.LeftEdge => wa.Left, DockConfig.RightEdge => wa.Right, _ => wa.Bottom };
        }

        var work = SystemParameters.WorkArea;
        var m = look.EdgeMargin;
        var inset = m + ItemInset;
        switch (edge)
        {
            case DockConfig.LeftEdge:
            case DockConfig.RightEdge:
                var left = edge == DockConfig.LeftEdge;
                Width = depth;
                Left = left ? outer : outer - depth;
                Top = work.Top;
                Height = work.Height;
                _panel.HorizontalAlignment = _items.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                _panel.VerticalAlignment = look.FullLength ? VerticalAlignment.Stretch : VerticalAlignment.Center;
                _items.VerticalAlignment = VerticalAlignment.Center;
                _items.Orientation = Orientation.Vertical;
                _panel.Margin = left ? new Thickness(m, 0, 0, 0) : new Thickness(0, 0, m, 0);
                _items.Margin = left ? new Thickness(inset, 0, 0, 0) : new Thickness(0, 0, inset, 0);
                _panel.Width = PanelHeight;
                _panel.Height = double.NaN;
                break;

            default:
                Left = 0;
                Width = SystemParameters.PrimaryScreenWidth;
                Height = depth;
                Top = outer - depth;
                _panel.VerticalAlignment = _items.VerticalAlignment = VerticalAlignment.Bottom;
                _panel.HorizontalAlignment = look.FullLength ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
                _items.HorizontalAlignment = HorizontalAlignment.Center;
                _items.Orientation = Orientation.Horizontal;
                _panel.Margin = new Thickness(0, 0, 0, m);
                _items.Margin = new Thickness(0, 0, 0, inset);
                _panel.Height = PanelHeight;
                _panel.Width = double.NaN;
                break;
        }

        _panel.CornerRadius = new CornerRadius(_config.Style == DockConfig.PillStyle ? PanelHeight / 2 : look.Radius);
        _panel.BorderThickness = look.FullLength
            ? edge switch { DockConfig.LeftEdge => new Thickness(0, 0, 1, 0), DockConfig.RightEdge => new Thickness(1, 0, 0, 0), _ => new Thickness(0, 1, 0, 0) }
            : new Thickness(1);
        SizePanel();
    }

    /// <summary>The panel hugs the icons (plus padding), or spans the whole edge for the bar style.</summary>
    private void SizePanel()
    {
        if (Look.FullLength)
        {
            _panel.Width = Vertical ? PanelHeight : double.NaN;
            _panel.Height = Vertical ? double.NaN : PanelHeight;
        }
        else if (Vertical)
        {
            _panel.Height = _items.ActualHeight + 2 * Pad;
        }
        else
        {
            _panel.Width = _items.ActualWidth + 2 * Pad;
        }
    }

    private void ApplyTheme()
    {
        var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is 1;
        Brush B(uint argb) => new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        Brush G(uint top, uint bottom) => new LinearGradientBrush(((SolidColorBrush)B(top)).Color, ((SolidColorBrush)B(bottom)).Color, 90);

        switch (_config.Style)
        {
            case DockConfig.GlassStyle:
                // Frosted glass: a see-through milky panel, brighter at the top, with a light hairline edge.
                _panel.Background = light ? G(0xA6FFFFFF, 0x66F0F2F8) : G(0x80A4A4B8, 0x4C3A3A4A);
                _panel.BorderBrush = light ? G(0xFFFFFFFF, 0x80FFFFFF) : G(0xD9FFFFFF, 0x33FFFFFF);
                break;
            case DockConfig.ClearStyle:
                // No panel at all, just the icons (an almost invisible fill keeps right-click working).
                _panel.Background = B(0x01000000);
                _panel.BorderBrush = Brushes.Transparent;
                break;
            case DockConfig.BarStyle:
                _panel.Background = light ? B(0xF2F3F3F3) : B(0xF01C1C1F);
                _panel.BorderBrush = light ? B(0x22000000) : B(0x30FFFFFF);
                break;
            case DockConfig.PillStyle:
                _panel.Background = light ? B(0xF2FAFAFA) : B(0xEB222226);
                _panel.BorderBrush = light ? B(0x22000000) : B(0x38FFFFFF);
                break;
            default:
                _panel.Background = light ? B(0xE0F4F4F4) : B(0xD91E1E1E);
                _panel.BorderBrush = light ? B(0x22000000) : B(0x38FFFFFF);
                break;
        }

        _panel.Effect = Look.Shadow
            ? new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = _config.Style == DockConfig.GlassStyle ? 0.22 : 0.35 }
            : null;
        _separatorBrush = light ? B(0x33000000) : B(0x40FFFFFF);
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

    /// <summary>What each dock style changes: shape, spacing, icon size and effects.</summary>
    private sealed record DockLook(double Radius, double EdgeMargin, double Padding, double IconScale, bool Magnify, bool Shadow, bool FullLength)
    {
        public static DockLook For(string style) => style switch
        {
            DockConfig.GlassStyle => new(22, 8, 20, 1, Magnify: true, Shadow: true, FullLength: false),
            DockConfig.ClearStyle => new(16, 6, 19, 1, Magnify: true, Shadow: false, FullLength: false),
            DockConfig.BarStyle => new(0, 0, 16, 0.8, Magnify: false, Shadow: false, FullLength: true),
            DockConfig.PillStyle => new(0, 12, 14, 0.72, Magnify: false, Shadow: true, FullLength: false),
            _ => new(16, 6, 19, 1, Magnify: true, Shadow: true, FullLength: false),
        };
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

        var signature = string.Join("|", entries.Select(e => e.Key)) + $"#{IconSize}#{_config.Style}#{_config.Edge}";
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

        // Bottom dock: icon above its running dot. Side dock: the dot sits between the icon and the screen edge.
        var edge = _config.Edge;
        var across = IconSize * 0.15 + 7; // keeps separators lined up with the icons, clear of the dot
        foreach (var entry in _entries)
        {
            if (entry.Kind == EntryKind.Separator)
            {
                entry.View = edge switch
                {
                    DockConfig.LeftEdge => new Rectangle { Height = 1, Width = IconSize * 0.7, Margin = new Thickness(across, Gap, 0, Gap), HorizontalAlignment = HorizontalAlignment.Left },
                    DockConfig.RightEdge => new Rectangle { Height = 1, Width = IconSize * 0.7, Margin = new Thickness(0, Gap, across, Gap), HorizontalAlignment = HorizontalAlignment.Right },
                    _ => new Rectangle { Width = 1, Height = IconSize * 0.7, Margin = new Thickness(Gap, 0, Gap, across), VerticalAlignment = VerticalAlignment.Bottom },
                };
                ((Rectangle)entry.View).Fill = _separatorBrush;
                _items.Children.Add(entry.View);
                continue;
            }

            entry.RestIcon = IconFor(entry, RestPixels);
            entry.LargeIcon = null; // loaded the first time this icon is magnified
            entry.Image = new Image
            {
                Source = entry.RestIcon,
                Width = IconSize,
                Height = IconSize,
                VerticalAlignment = Vertical ? VerticalAlignment.Center : VerticalAlignment.Bottom,
                RenderTransform = new TranslateTransform(),
            };
            RenderOptions.SetBitmapScalingMode(entry.Image, BitmapScalingMode.HighQuality);
            entry.Dot = new Ellipse
            {
                Width = 4,
                Height = 4,
                Margin = edge switch { DockConfig.LeftEdge => new Thickness(0, 0, 3, 0), DockConfig.RightEdge => new Thickness(3, 0, 0, 0), _ => new Thickness(0, 3, 0, 0) },
                VerticalAlignment = VerticalAlignment.Center,
                Fill = _dotBrush,
                Visibility = entry.Windows.Count > 0 ? Visibility.Visible : Visibility.Hidden,
            };

            var stack = new StackPanel { Background = Brushes.Transparent };
            switch (edge)
            {
                case DockConfig.LeftEdge:
                    stack.Orientation = Orientation.Horizontal;
                    stack.HorizontalAlignment = HorizontalAlignment.Left;
                    stack.Margin = new Thickness(0, Gap / 2, 0, Gap / 2);
                    stack.Children.Add(entry.Dot);
                    stack.Children.Add(entry.Image);
                    break;
                case DockConfig.RightEdge:
                    stack.Orientation = Orientation.Horizontal;
                    stack.HorizontalAlignment = HorizontalAlignment.Right;
                    stack.Margin = new Thickness(0, Gap / 2, 0, Gap / 2);
                    stack.Children.Add(entry.Image);
                    stack.Children.Add(entry.Dot);
                    break;
                default:
                    stack.VerticalAlignment = VerticalAlignment.Bottom;
                    stack.Margin = new Thickness(Gap / 2, 0, Gap / 2, 0);
                    stack.Children.Add(entry.Image);
                    stack.Children.Add(entry.Dot);
                    break;
            }

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

    /// <summary>The exact pixel size icons are shown at when not magnified (so they're drawn 1:1, razor sharp).</summary>
    private int RestPixels => (int)Math.Round(IconSize * VisualTreeHelper.GetDpi(this).DpiScaleX);

    private ImageSource? IconFor(DockEntry entry, int pixels)
    {
        switch (entry.Kind)
        {
            case EntryKind.Start:
                return StartIcon;
            case EntryKind.Trash:
                return ShellNative.Icon(RecycleBin, pixels, cache: false); // empty/full changes
        }

        if (entry.Pin is { } pin)
        {
            return pin.Kind == PinKind.AppId
                ? ShellNative.Icon("shell:AppsFolder\\" + pin.Target, pixels)
                : ShellNative.Icon(Environment.ExpandEnvironmentVariables(pin.Target), pixels);
        }

        return (entry.AppId is not null ? ShellNative.Icon("shell:AppsFolder\\" + entry.AppId, pixels) : null)
            ?? (entry.ExePath is not null ? ShellNative.Icon(entry.ExePath, pixels) : null);
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

    /// <param name="mouse">The mouse position along the dock (x for a bottom dock, y for a side dock).</param>
    private void Magnify(double mouse)
    {
        MemoryTrimmer.Touch();
        if (MagnifyOn)
        {
            var sizes = _entries.Select(e => e.Kind == EntryKind.Separator ? 1 + 2 * Gap : IconSize + Gap).ToList();
            var length = Vertical ? _root.ActualHeight : _root.ActualWidth;
            var pos = length / 2 - sizes.Sum() / 2;
            for (var i = 0; i < _entries.Count; i++)
            {
                var center = pos + sizes[i] / 2;
                pos += sizes[i];
                if (_entries[i].Image is not { } image)
                    continue;
                var entry = _entries[i];
                var scale = DockMath.Scale(mouse - center, IconSize);
                image.Width = image.Height = Math.Round(IconSize * scale); // whole sizes: no shimmer while zooming

                // Zoomed: switch to the big icon so it's sharp instead of stretched; back to the exact-size one at rest.
                if (scale > 1.02)
                    image.Source = entry.LargeIcon ??= IconFor(entry, LargeIconPixels) ?? entry.RestIcon;
                else if (image.Source != entry.RestIcon)
                    image.Source = entry.RestIcon;
            }
        }

        UpdateLabel();
    }

    private void ResetMagnify()
    {
        foreach (var entry in _entries)
        {
            if (entry.Image is { } image)
            {
                image.Width = image.Height = IconSize;
                image.Source = entry.RestIcon;
            }
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
        var size = _label.DesiredSize;
        switch (_config.Edge)
        {
            case DockConfig.LeftEdge: // beside the icon, toward the middle of the screen
                var right = view.TranslatePoint(new Point(view.ActualWidth, view.ActualHeight / 2), _root);
                Canvas.SetLeft(_label, right.X + 10);
                Canvas.SetTop(_label, right.Y - size.Height / 2);
                break;
            case DockConfig.RightEdge:
                var left = view.TranslatePoint(new Point(0, view.ActualHeight / 2), _root);
                Canvas.SetLeft(_label, Math.Max(0, left.X - size.Width - 10));
                Canvas.SetTop(_label, left.Y - size.Height / 2);
                break;
            default: // above the icon
                var top = view.TranslatePoint(new Point(view.ActualWidth / 2, 0), _root);
                Canvas.SetLeft(_label, top.X - size.Width / 2);
                Canvas.SetTop(_label, Math.Max(0, top.Y - size.Height - 8));
                break;
        }

        _label.Visibility = Visibility.Visible;
    }

    private void OnClick(DockEntry entry)
    {
        try
        {
            switch (entry.Kind)
            {
                case EntryKind.Start:
                    OpenStart();
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

    /// <summary>The launch "bounce": away from the screen edge the dock sits on.</summary>
    private void Bounce(DockEntry entry)
    {
        if (entry.Image?.RenderTransform is not TranslateTransform move)
            return;
        var distance = _config.Edge == DockConfig.LeftEdge ? 14 : -14;
        var animation = new DoubleAnimation(0, distance, TimeSpan.FromMilliseconds(220))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        move.BeginAnimation(Vertical ? TranslateTransform.XProperty : TranslateTransform.YProperty, animation);
    }

    internal static string StyleName(string style) => style switch
    {
        DockConfig.GlassStyle => "Liquid glass",
        DockConfig.ClearStyle => "Transparent",
        DockConfig.BarStyle => "Bar",
        DockConfig.PillStyle => "Minimal pill",
        _ => "Mac",
    };

    /// <summary>The Start button: Restyle's centered Start menu (if turned on) or the Windows one. Click again to close.</summary>
    public void OpenStart()
    {
        if (!_config.RestyleStart)
        {
            ShellNative.OpenStartMenu();
            return;
        }

        if (_startMenu is { IsVisible: true })
        {
            _startMenu.HideMenu();
            return;
        }

        // The click that hid it (by taking focus away) shouldn't immediately reopen it.
        if (_startMenu is not null && (DateTime.Now - _startMenu.HiddenAt).TotalMilliseconds < 300)
            return;

        EnsureStartMenu().ShowCentered();
    }

    /// <summary>One Start menu, kept hidden between uses so it opens instantly; rebuilt if the theme changed.</summary>
    private StartMenu EnsureStartMenu()
    {
        if (_startMenu is { IsStale: true })
        {
            _startMenu.Close();
            _startMenu = null;
        }

        if (_startMenu is null)
        {
            var menu = new StartMenu(this);
            menu.Closed += (_, _) =>
            {
                if (_startMenu == menu)
                    _startMenu = null;
            };
            _startMenu = menu;
        }

        return _startMenu;
    }

    /// <summary>Reads the app list and builds the Start menu in the background, ready for the first click.</summary>
    private void PrepareStartMenu()
    {
        if (!_config.RestyleStart)
            return;
        StartMenu.Preload().ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            if (_config.RestyleStart && _startMenu is not { IsVisible: true })
                EnsureStartMenu().Warm();
            StartMenu.PrefetchIcons();
        }, DispatcherPriority.ApplicationIdle), TaskScheduler.Default);
    }

    /// <summary>A context menu in the dock's style (also used by the desktop widgets).</summary>
    internal ContextMenu CreateMenu()
    {
        var menu = new ContextMenu { Style = (Style)_menuResources["DockContextMenu"] };
        menu.Resources.MergedDictionaries.Add(_menuResources);
        return menu;
    }

    private void ShowMenu(DockEntry? entry)
    {
        var menu = CreateMenu();

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
        // Quick switches; every option is on the Dock page.
        var nextStyle = DockConfig.Styles[(Array.IndexOf(DockConfig.Styles, _config.Style) + 1) % DockConfig.Styles.Length];
        Add($"Style: {StyleName(_config.Style)} (next: {StyleName(nextStyle)})", () =>
        {
            _config.Style = nextStyle;
            SaveConfig();
        });
        var nextEdge = DockConfig.Edges[(Array.IndexOf(DockConfig.Edges, _config.Edge) + 1) % DockConfig.Edges.Length];
        Add($"Move to the {nextEdge} of the screen", () =>
        {
            _config.Edge = nextEdge;
            SaveConfig();
        });
        Add(_config.ShowWidgets ? "Edit widgets (move, resize, add, hide)" : "Show desktop widgets", EditWidgets);
        Add("Dock settings…", () => ShellNative.Start(Environment.ProcessPath!, "--page dock"));
        if (_config.ShowWidgets)
            Add("Widget settings…", () => ShellNative.Start(Environment.ProcessPath!, "--page widgets"));
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
                EnsureWidgets(force: true); // re-attach to the new desktop window
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
        _configCheck.Stop();
        _reloadDebounce.Stop();
        _lockTimer.Stop();
        _taskbarGuard.Stop();
        _topBar?.Close();
        _widgets?.Close();
        _startMenu?.Close();
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
        public ImageSource? RestIcon { get; set; }
        public ImageSource? LargeIcon { get; set; }
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
