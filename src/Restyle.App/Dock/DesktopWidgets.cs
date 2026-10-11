using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Restyle.Core;
using Restyle.Core.Dock;

namespace Restyle.App.Docking;

/// <summary>
/// The desktop widgets. Every widget is available whatever the style; the style only decides which are
/// shown by default. You can move, resize, show and hide each one (Edit widgets), and that's remembered.
/// They live on the desktop, behind your windows, and run inside the dock process.
/// </summary>
internal sealed class DesktopWidgets
{
    private readonly DockWindow _dock;
    private readonly Dictionary<string, WidgetWindow> _windows = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private WidgetConfig _config = new();
    private string? _signature;
    private int _tick;
    private WeatherService? _weather;
    private MediaSource? _media;
    private Audio? _audio;
    private EditBar? _editBar;

    public DesktopWidgets(DockWindow dock)
    {
        _dock = dock;
        _timer.Tick += (_, _) =>
        {
            _tick++;
            _weather?.Tick(_tick);
            foreach (var window in _windows.Values)
                window.Tick(_tick);
        };
    }

    public static string NotePath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Restyle", "note.txt");

    public bool Editing => _editBar is not null;

    internal DockWindow Dock => _dock;

    /// <summary>
    /// Shows the widgets for these settings. The widgets are only rebuilt when their content settings change;
    /// moving, resizing, showing or hiding one just updates that window.
    /// </summary>
    public void Update(WidgetConfig config, bool force = false)
    {
        var signature = ContentSignature(config);
        if (force || signature != _signature || _weather is null)
        {
            _signature = signature;
            CloseWindows();

            // Shared by every widget: one weather lookup, one media connection, one audio device.
            _weather = new WeatherService(config);
            _media = new MediaSource();
            _audio = new Audio();
        }

        _config = config;
        Sync();
        _timer.Start();
    }

    public void Close()
    {
        ExitEditMode();
        CloseWindows();
        _signature = null;
    }

    /// <summary>The settings minus where things are placed (placement changes don't need a rebuild).</summary>
    private static string ContentSignature(WidgetConfig config)
    {
        var placements = config.Placements;
        config.Placements = [];
        try
        {
            return JsonSerializer.Serialize(config, RestyleJson.Options);
        }
        finally
        {
            config.Placements = placements;
        }
    }

    /// <summary>Opens the widgets that should be visible, closes the rest, and puts each where it belongs.</summary>
    private void Sync()
    {
        foreach (var (id, name) in WidgetConfig.AllWidgets)
        {
            var visible = _config.IsVisible(id);
            if (visible && !_windows.ContainsKey(id))
            {
                var window = Create(id, name);
                _windows[id] = window;
                window.ApplyPlacement(_config.FindPlacement(id));
                window.Show();
                if (Editing)
                    window.SetEditing(true);
            }
            else if (!visible && _windows.Remove(id, out var hidden))
            {
                hidden.Close();
            }
            else if (_windows.TryGetValue(id, out var existing))
            {
                existing.ApplyPlacement(_config.FindPlacement(id));
            }
        }

        _editBar?.Refresh(_config);
    }

    private WidgetWindow Create(string id, string name)
    {
        // Default spots: widgets on the left start past the usual column of desktop icons. Text-only widgets
        // let clicks through to the desktop (except while editing).
        FrameworkElement content;
        Func<Rect, Size, Point> spot;
        bool clickThrough = false, activatable = false, fit = false;
        switch (id)
        {
            case "map":
                content = new WorldMap(_config.Cities);
                spot = (wa, s) => new Point(wa.Left + 120, wa.Top + 28);
                clickThrough = true;
                break;
            case "clock":
                content = new ClockWidget(_config);
                spot = (wa, s) => new Point(wa.Left + 130, Math.Max(wa.Top + 250, wa.Bottom - s.Height - 64));
                clickThrough = _config.NewsFeed is null; // only catches clicks when it shows headlines you can open
                break;
            case "tiles":
                content = new WidgetPanel(_config, _dock, _weather!, _media!, _audio!);
                spot = (wa, s) => new Point(wa.Right - s.Width - 36, wa.Top + 28);
                activatable = true; // the calendar note takes typing
                fit = true;
                break;
            case "center-clock":
                content = new CenterClock(_weather!);
                spot = (wa, s) => new Point(wa.Left + (wa.Width - s.Width) / 2, Math.Max(wa.Top + 20, wa.Top + wa.Height * 0.30 - s.Height / 2));
                clickThrough = true;
                break;
            case "pills":
                content = new GlassPills(_media!, _audio!, _config.Glass);
                spot = (wa, s) => new Point(wa.Left + (wa.Width - s.Width) / 2, wa.Bottom - s.Height - 16);
                break;
            case "weather":
                content = new WeatherChip(_weather!, _config.Glass);
                spot = (wa, s) => new Point(wa.Left + 110, wa.Top + 22);
                break;
            case "apps":
                content = new AppColumn(_dock, _config.Glass);
                spot = (wa, s) => new Point(wa.Left + 110, wa.Top + Math.Max(90, (wa.Height - s.Height) / 2));
                break;
            case "bars":
                content = new AudioBars(_audio!);
                spot = (wa, s) => new Point(wa.Right - s.Width - 52, wa.Top + 34);
                clickThrough = true;
                break;
            case "date":
                content = new DateBlock();
                spot = (wa, s) => new Point(wa.Right - s.Width - 44, wa.Top + 96);
                clickThrough = true;
                break;
            default: // "corner-clock"
                content = new CornerClock();
                spot = (wa, s) => new Point(wa.Left + 120, wa.Bottom - s.Height - 44);
                clickThrough = true;
                break;
        }

        return new WidgetWindow(this, id, name, content, spot, activatable, fit, clickThrough);
    }

    private void CloseWindows()
    {
        _timer.Stop();
        foreach (var window in _windows.Values)
            window.Close();
        _windows.Clear();
        _media?.Detach();
        _media = null;
        _weather = null;
        _audio = null;
    }

    // ---- Editing -----------------------------------------------------------------------

    /// <summary>Edit mode: widgets come to the front with a frame; drag to move, −/+ to resize, × to hide.</summary>
    public void EnterEditMode()
    {
        if (Editing)
            return;
        _editBar = new EditBar(this);
        _editBar.Refresh(_config);
        _editBar.Show();
        foreach (var window in _windows.Values)
            window.SetEditing(true);
    }

    public void ExitEditMode()
    {
        if (_editBar is null)
            return;
        var bar = _editBar;
        _editBar = null;
        bar.Close();
        foreach (var window in _windows.Values)
            window.SetEditing(false);
    }

    /// <summary>Saves a change to one widget's placement (the dock then reloads and applies it).</summary>
    public void SavePlacement(string id, Action<WidgetPlacement> change)
    {
        var config = DockConfig.Load(DockHost.ConfigPath);
        change(config.Widgets.Placement(id));
        config.Save(DockHost.ConfigPath);
        change(_config.Placement(id)); // apply right away too, without waiting for the reload
        if (_windows.TryGetValue(id, out var window))
            window.ApplyPlacement(_config.Placement(id));
        if (_config.IsVisible(id) != _windows.ContainsKey(id))
            Sync();
    }

    public void SetVisible(string id, bool visible) => SavePlacement(id, p => p.Visible = visible);

    /// <summary>Every widget back to the style's defaults: default spots, sizes and which are shown.</summary>
    public void ResetLayout()
    {
        var config = DockConfig.Load(DockHost.ConfigPath);
        config.Widgets.ResetPlacements();
        config.Save(DockHost.ConfigPath);
        _config.ResetPlacements();
        foreach (var window in _windows.Values)
            window.ApplyPlacement(null);
        Sync();
    }

    public static void OpenSettings() => ShellNative.Start(Environment.ProcessPath!, "--page widgets");
}

/// <summary>Something that wants a call every second (most widgets only act every few ticks).</summary>
internal interface ITicking
{
    void Tick(int tick);
}

/// <summary>
/// A borderless, transparent window pinned to the desktop: always behind normal windows and owned by
/// the desktop, so "Show desktop" (Win+D) leaves it visible. In edit mode it comes to the front with a
/// frame you can drag, plus size and hide buttons.
/// </summary>
internal sealed class WidgetWindow : Window
{
    private readonly DesktopWidgets _owner;
    private readonly string _id;
    private readonly FrameworkElement _content;
    private readonly Func<Rect, Size, Point> _defaultSpot;
    private readonly bool _activatable;
    private readonly bool _fit;
    private readonly bool _clickThrough;
    private readonly Grid _editFrame = new() { Visibility = Visibility.Collapsed, Background = DockTheme.Brush(0x1AFFFFFF), Cursor = Cursors.SizeAll };
    private WidgetPlacement? _placement;
    private double _appliedScale = 1;
    private bool _editing;
    private IntPtr _hwnd;

    public WidgetWindow(DesktopWidgets owner, string id, string name, FrameworkElement content, Func<Rect, Size, Point> defaultSpot,
        bool activatable = false, bool fit = false, bool clickThrough = false)
    {
        _owner = owner;
        _id = id;
        _content = content;
        _defaultSpot = defaultSpot;
        _activatable = activatable;
        _fit = fit;
        _clickThrough = clickThrough;

        Title = "Restyle Widgets";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true; // whole pixels: crisp icons and text
        FontFamily = DockTheme.Text;
        Foreground = Brushes.White;
        Left = -10000; // off screen until the first layout tells us our size

        BuildEditFrame(name);
        var root = new Grid();
        root.Children.Add(content);
        root.Children.Add(_editFrame);
        Content = root;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => Fit();
        SizeChanged += (_, _) => Place();
        MouseRightButtonUp += (_, e) =>
        {
            if (!_editing)
            {
                ShowMenu();
                e.Handled = true;
            }
        };
    }

    public void Tick(int tick)
    {
        if (_content is ITicking ticking)
            ticking.Tick(tick);
    }

    /// <summary>Where you put this widget and how big (null = the style's default).</summary>
    public void ApplyPlacement(WidgetPlacement? placement)
    {
        _placement = placement;
        if (IsLoaded)
            Fit();
    }

    // ---- Edit mode ---------------------------------------------------------------------

    private void BuildEditFrame(string name)
    {
        _editFrame.Children.Add(new Rectangle
        {
            Stroke = DockTheme.Brush(0xE6FFFFFF),
            StrokeThickness = 2,
            StrokeDashArray = [4, 3],
            RadiusX = 10,
            RadiusY = 10,
            IsHitTestVisible = false,
        });

        var label = new Border
        {
            Background = DockTheme.Brush(0xE6202024),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Child = new TextBlock { Text = name.Split(" (")[0], FontSize = 12, FontWeight = FontWeights.SemiBold },
        };
        _editFrame.Children.Add(label);

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(6),
            Background = DockTheme.Brush(0xE6202024),
            Cursor = Cursors.Arrow,
        };
        tools.Children.Add(WidgetUi.IconButton("\uE738", "Smaller", () => Resize(-0.1), 12, 28, 26));
        tools.Children.Add(WidgetUi.IconButton("\uE710", "Bigger", () => Resize(0.1), 12, 28, 26));
        tools.Children.Add(WidgetUi.IconButton("\uE711", "Hide this widget (add it back with \"Add widget\")", () => _owner.SetVisible(_id, false), 12, 28, 26));
        tools.MouseLeftButtonDown += (_, e) => e.Handled = true; // clicking a button shouldn't start a drag
        _editFrame.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Child = tools });

        _editFrame.MouseLeftButtonDown += (_, e) =>
        {
            if (e.Handled)
                return;
            DragMove(); // returns when the mouse is released
            SavePosition();
        };
    }

    public void SetEditing(bool editing)
    {
        _editing = editing;
        _editFrame.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        _content.IsHitTestVisible = !editing; // in edit mode a click anywhere grabs the widget
        if (_hwnd != IntPtr.Zero)
        {
            var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            ex = _clickThrough && !editing ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)ex);
        }

        // While editing, widgets float above your windows so you can see and reach them all.
        Topmost = editing;
        if (!editing && _hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void Resize(double step)
    {
        var scale = Math.Round(Math.Clamp((_placement?.Scale ?? 1) + step, 0.6, 1.8), 2);
        var center = Center();
        _owner.SavePlacement(_id, p =>
        {
            p.Scale = scale;
            p.X ??= center.X; // keep it centered where it is while it grows or shrinks
            p.Y ??= center.Y;
        });
    }

    private void SavePosition()
    {
        var center = Center();
        _owner.SavePlacement(_id, p =>
        {
            p.X = center.X;
            p.Y = center.Y;
        });
    }

    /// <summary>This widget's center as fractions of the usable screen area.</summary>
    private Point Center()
    {
        var wa = SystemParameters.WorkArea;
        return new Point(
            Math.Round(Math.Clamp((Left + ActualWidth / 2 - wa.Left) / wa.Width, 0, 1), 4),
            Math.Round(Math.Clamp((Top + ActualHeight / 2 - wa.Top) / wa.Height, 0, 1), 4));
    }

    private void ShowMenu()
    {
        var menu = _owner.Dock.CreateMenu();
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("Edit widgets (move, resize, hide)", _owner.EnterEditMode);
        Add("Hide this widget", () => _owner.SetVisible(_id, false));
        menu.Items.Add(new Separator());
        Add("Widget settings\u2026", DesktopWidgets.OpenSettings);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    // ---- Window plumbing ---------------------------------------------------------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
        if (!_activatable)
            ex |= WS_EX_NOACTIVATE;
        if (_clickThrough && !_editing)
            ex |= WS_EX_TRANSPARENT; // mouse clicks go to whatever is underneath (desktop icons)
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)ex);

        // Owned by the desktop window: stays on the desktop and survives Win+D.
        var desktop = FindWindow("Progman", null);
        if (desktop != IntPtr.Zero)
            SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, desktop);

        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        if (!_editing)
            SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Applies your size, shrinking the tile panel if it wouldn't fit the screen, then positions it.</summary>
    private void Fit()
    {
        var scale = _placement?.Scale ?? 1;
        if (_fit)
        {
            _content.LayoutTransform = Transform.Identity;
            _content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var available = SystemParameters.WorkArea.Height - 56;
            scale = Math.Min(scale, Math.Max(0.5, available / Math.Max(1, _content.DesiredSize.Height)));
        }

        if (Math.Abs(scale - _appliedScale) > 0.005 || _fit)
            _content.LayoutTransform = Math.Abs(scale - 1) < 0.005 ? Transform.Identity : new ScaleTransform(scale, scale);
        _appliedScale = scale;
        Place();
    }

    private void Place()
    {
        if (ActualWidth <= 0)
            return;
        var wa = SystemParameters.WorkArea;
        var size = new Size(ActualWidth, ActualHeight);
        var p = _placement is { X: double x, Y: double y }
            ? new Point(wa.Left + x * wa.Width - size.Width / 2, wa.Top + y * wa.Height - size.Height / 2)
            : _defaultSpot(wa, size);

        // Keep it on screen even if the screen got smaller since you placed it.
        Left = Math.Clamp(p.X, wa.Left, Math.Max(wa.Left, wa.Right - size.Width));
        Top = Math.Clamp(p.Y, wa.Top, Math.Max(wa.Top, wa.Bottom - size.Height));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_WINDOWPOSCHANGING when !_editing:
                // Whatever happens (clicks, activation), stay at the bottom of the window stack.
                var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                pos.hwndInsertAfter = HWND_BOTTOM;
                pos.flags &= ~SWP_NOZORDER;
                Marshal.StructureToPtr(pos, lParam, false);
                break;
            case WM_MOUSEACTIVATE when !_activatable:
                handled = true;
                return MA_NOACTIVATE;
            case WM_SETTINGCHANGE when (int)wParam == SPI_SETDESKWALLPAPER:
                Dispatcher.BeginInvoke(() => GlassBorder.RefreshAll(this), DispatcherPriority.Background); // glass shows the new wallpaper
                break;
            case WM_SETTINGCHANGE:
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                Dispatcher.BeginInvoke(Fit, DispatcherPriority.Background);
                break;
        }

        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    private const int GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TRANSPARENT = 0x20;
    private const int SPI_SETDESKWALLPAPER = 0x14;
    private const int WM_WINDOWPOSCHANGING = 0x46, WM_MOUSEACTIVATE = 0x21, WM_SETTINGCHANGE = 0x1A, WM_DISPLAYCHANGE = 0x7E, WM_DPICHANGED = 0x2E0;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private static readonly IntPtr MA_NOACTIVATE = new(3);

    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

/// <summary>The floating bar shown while editing widgets: add hidden widgets, reset the layout, Done.</summary>
internal sealed class EditBar : Window
{
    private readonly DesktopWidgets _owner;
    private WidgetConfig _config = new();

    public EditBar(DesktopWidgets owner)
    {
        _owner = owner;
        Title = "Restyle Widget Editing";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        FontFamily = DockTheme.Text;
        Foreground = Brushes.White;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 6, 6, 6) };
        row.Children.Add(new TextBlock { Text = "Editing widgets: drag to move, \u2212 / + to resize, \u00D7 to hide", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), FontSize = 13 });
        row.Children.Add(TextButton("Add widget \u25BE", ShowAddMenu));
        row.Children.Add(TextButton("Reset layout", _owner.ResetLayout));
        row.Children.Add(TextButton("Settings\u2026", DesktopWidgets.OpenSettings));
        var done = TextButton("Done", _owner.ExitEditMode);
        done.Background = DockTheme.Brush(0xFF3A7BFF);
        row.Children.Add(done);

        Content = new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = DockTheme.Brush(0xF0202024),
            BorderBrush = DockTheme.Brush(0x33FFFFFF),
            BorderThickness = new Thickness(1),
            Child = row,
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)GetWindowLongPtr(hwnd, -20);
            SetWindowLongPtr(hwnd, -20, (IntPtr)(ex | 0x80 /* tool window */ | 0x8000000 /* no activate */));
        };
        SizeChanged += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + 12;
        };
    }

    public void Refresh(WidgetConfig config) => _config = config;

    private Button TextButton(string text, Action click)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 13, Foreground = Brushes.White, Margin = new Thickness(12, 5, 12, 6) },
            Margin = new Thickness(4, 0, 0, 0),
            Focusable = false,
            Cursor = Cursors.Hand,
            Background = DockTheme.Brush(0x26FFFFFF),
            Template = PillTemplate(),
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static ControlTemplate PillTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        template.VisualTree = border;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, 0.85, "Bd"));
        template.Triggers.Add(hover);
        return template;
    }

    private void ShowAddMenu()
    {
        var menu = _owner.Dock.CreateMenu();
        foreach (var (id, name) in WidgetConfig.AllWidgets)
        {
            var shown = _config.IsVisible(id);
            var item = new MenuItem { Header = (shown ? "\u2713  " : "      ") + name };
            item.Click += (_, _) => _owner.SetVisible(id, !shown);
            menu.Items.Add(item);
        }

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}

/// <summary>The big clock and date, with three headlines (or, with news off, a few local status lines).</summary>
internal sealed class ClockWidget : StackPanel, ITicking
{
    private const int PerPage = 3;

    private readonly WidgetConfig _config;
    private readonly TextBlock _time = new() { FontSize = 66, FontWeight = FontWeight.FromOpenTypeWeight(350), FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") };
    private readonly TextBlock _date = new() { FontSize = 22, Margin = new Thickness(2, -6, 0, 10) };
    private readonly TextBlock[] _lines = new TextBlock[PerPage];
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 12, 0, 0) };
    private readonly Audio _audio = new();
    private List<WidgetNet.Headline> _headlines = [];
    private int _page;
    private string? _shownMinute;

    public ClockWidget(WidgetConfig config)
    {
        _config = config;
        Width = 480;
        Children.Add(_time);
        Children.Add(_date);
        for (var i = 0; i < PerPage; i++)
        {
            var line = new TextBlock { FontSize = 13.5, Margin = new Thickness(2, 1, 0, 1), TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.92 };
            var index = i;
            line.MouseLeftButtonUp += (_, _) => OpenHeadline(index);
            _lines[i] = line;
            Children.Add(line);
        }

        Children.Add(_dots);
        Tick(0);
        _ = LoadNews();
    }

    public void Tick(int tick)
    {
        var now = DateTime.Now;
        var minute = now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        if (minute != _shownMinute)
        {
            _shownMinute = minute;
            var format = CultureInfo.CurrentCulture.DateTimeFormat;
            _time.Text = now.ToString(format.ShortTimePattern.Replace("tt", "").Trim(), CultureInfo.CurrentCulture);
            _date.Text = $"{now:dddd}, {now.ToString(format.MonthDayPattern, CultureInfo.CurrentCulture)}";
        }

        if (_config.NewsFeed is null)
        {
            if (tick % 5 == 0)
                ShowStatus();
        }
        else
        {
            if (tick > 0 && tick % 12 == 0 && _headlines.Count > PerPage)
                ShowPage((_page + 1) % Pages);
            if (tick > 0 && tick % 1800 == 0)
                _ = LoadNews();
        }
    }

    private int Pages => Math.Max(1, (int)Math.Ceiling(_headlines.Count / (double)PerPage));

    private async Task LoadNews()
    {
        if (_config.NewsFeed is not { } feed)
        {
            ShowStatus();
            return;
        }

        try
        {
            _headlines = await WidgetNet.GetHeadlines(feed);
            if (_headlines.Count == 0)
                _headlines = [new WidgetNet.Headline("No headlines in this news feed.", null)];
        }
        catch (Exception)
        {
            if (_headlines.Count == 0)
                _headlines = [new WidgetNet.Headline("Couldn't load the news. Will try again later.", null)];
        }

        ShowPage(Math.Min(_page, Pages - 1));
    }

    private void ShowPage(int page)
    {
        _page = page;
        for (var i = 0; i < PerPage; i++)
        {
            var index = page * PerPage + i;
            var headline = index < _headlines.Count ? _headlines[index] : null;
            _lines[i].Text = headline?.Title ?? "";
            _lines[i].Cursor = WidgetNet.IsWebLink(headline?.Link) ? Cursors.Hand : null;
            _lines[i].ToolTip = headline?.Title;
        }

        _dots.Children.Clear();
        if (Pages < 2)
            return;
        for (var i = 0; i < Pages; i++)
        {
            var target = i;
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(0, 0, 9, 0),
                Fill = Brushes.White,
                Opacity = i == page ? 1 : 0.45,
                Cursor = Cursors.Hand,
            };
            dot.MouseLeftButtonUp += (_, _) => ShowPage(target);
            _dots.Children.Add(dot);
        }
    }

    private void OpenHeadline(int line)
    {
        var index = _page * PerPage + line;
        if (_config.NewsFeed is not null && index < _headlines.Count && WidgetNet.IsWebLink(_headlines[index].Link))
            ShellNative.Start(_headlines[index].Link!, null);
    }

    /// <summary>With news off: what's going on with this PC, all read locally.</summary>
    private void ShowStatus()
    {
        var lines = new List<string>();
        var network = Connectivity.CurrentNetwork();
        lines.Add(network is null ? "Not connected to the internet" : network.IsWifi ? $"Connected to Wi-Fi: {network.Name}" : $"Connected: {network.Name}");

        var battery = Battery.Read();
        if (battery.HasBattery)
        {
            lines.Add(battery.Charging
                ? battery.Percent >= 100 ? "Battery fully charged" : $"Battery {battery.Percent}%, charging"
                : battery.Remaining is { } left ? $"Battery {battery.Percent}%, about {(int)left.TotalHours} h {left.Minutes} min left" : $"Battery {battery.Percent}%");
        }

        var volume = _audio.Volume;
        lines.Add(volume is null ? "No speakers found" : _audio.Muted ? $"Sound muted ({_audio.DeviceName})" : $"Volume {volume * 100:0}% on {_audio.DeviceName}");

        var now = DateTime.Now;
        lines.Add($"Week {ISOWeek.GetWeekOfYear(now)}, day {now.DayOfYear} of {now.Year}");

        for (var i = 0; i < PerPage; i++)
        {
            _lines[i].Text = i < lines.Count ? lines[i] : "";
            _lines[i].Cursor = null;
        }
    }
}
