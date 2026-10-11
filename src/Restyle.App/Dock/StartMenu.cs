using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Restyle.Core.Dock;

namespace Restyle.App.Docking;

/// <summary>
/// Restyle's own Start menu for the dock's Start button: opens in the middle of the screen in the same
/// style as the dock and widgets (light/dark and your accent color), with search, pinned apps and all apps.
/// The Windows key still opens the normal Windows Start menu.
/// </summary>
/// <remarks>
/// Built for speed with software rendering: a plain (non-layered) window with Windows' own rounded corners
/// instead of a transparent window with a drawn shadow, no open animation, and the window is hidden rather
/// than closed so it reappears instantly. App rows are created once and reused while you type.
/// </remarks>
internal sealed class StartMenu : Window
{
    private const double MenuWidth = 660;
    private static List<(string Name, string Id)>? _apps;
    private static DateTime _appsLoaded;
    private static Task<List<(string Name, string Id)>>? _loading;

    private readonly DockWindow _dock;
    private readonly bool _light = DockTheme.IsLight;
    private readonly Color _accent = AccentColor();
    private readonly TextBox _search = new();
    private readonly TextBlock _searchHint = new() { Text = "Type to search apps", IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.55 };
    private readonly TextBlock _sectionTitle = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _viewToggle;
    private readonly ContentControl _body = new();
    private readonly List<Button> _results = [];
    private readonly Dictionary<string, Button> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<char, TextBlock> _letters = [];
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _listScroll;
    private readonly TextBlock _noMatches = new() { Opacity = 0.7, Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap };
    private ScrollViewer? _pinnedView;
    private string? _pinnedSignature;
    private Brush? _selectedBrush;
    private int _selected;
    private bool _showAll;
    private bool _menuOpen;

    public StartMenu(DockWindow dock)
    {
        _dock = dock;
        Title = "Restyle Start";
        UseLayoutRounding = true; // crisp icons and text at any display scaling
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        Width = MenuWidth;
        FontFamily = DockTheme.Text;
        FontSize = 13;
        Foreground = _light ? DockTheme.Brush(0xFF1A1A1A) : Brushes.White;
        Background = _light ? DockTheme.Brush(0xFFF4F4F6) : DockTheme.Brush(0xFF1E1E22);
        BorderBrush = _light ? DockTheme.Brush(0x22000000) : DockTheme.Brush(0x30FFFFFF);
        BorderThickness = new Thickness(1);

        _viewToggle = FlatButton(new TextBlock(), ToggleView, "Switch between pinned apps and all apps");
        _viewToggle.Padding = new Thickness(10, 4, 10, 4);
        _listScroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };

        var root = new Grid { Margin = new Thickness(24, 16, 24, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(SearchBox());

        var header = new DockPanel { Margin = new Thickness(4, 18, 0, 8), LastChildFill = false };
        header.Children.Add(_sectionTitle);
        DockPanel.SetDock(_viewToggle, Dock.Right);
        header.Children.Add(_viewToggle);
        Grid.SetRow(header, 1);
        root.Children.Add(header);

        Grid.SetRow(_body, 2);
        root.Children.Add(_body);

        var footer = Footer();
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        Content = root;

        Activated += (_, _) => _search.Focus();
        // Hide when you click elsewhere, but not in the middle of Windows' activation change.
        Deactivated += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!_menuOpen && !IsActive)
                HideMenu();
        });
        PreviewKeyDown += OnKey;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)GetWindowLongPtr(hwnd, -20);
            SetWindowLongPtr(hwnd, -20, (IntPtr)(ex | 0x80)); // tool window: not in Alt+Tab
            var round = 2; // DWMWCP_ROUND: Windows 11 draws rounded corners and a shadow for us
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        };

        ShowPinned();
    }

    /// <summary>When it was last hidden (so the click that hid it doesn't reopen it).</summary>
    public DateTime HiddenAt { get; private set; }

    /// <summary>True if light/dark mode or the accent color changed since it was built.</summary>
    public bool IsStale => _light != DockTheme.IsLight || _accent != AccentColor();

    /// <summary>Builds the window and lays it out in the background, so the first click opens it instantly.</summary>
    public void Warm()
    {
        new WindowInteropHelper(this).EnsureHandle();
        var root = (FrameworkElement)Content;
        root.Measure(new Size(MenuWidth, 640));
        root.Arrange(new Rect(0, 0, MenuWidth, 640));
    }

    public void ShowCentered()
    {
        var work = SystemParameters.WorkArea;
        Height = Math.Min(640, work.Height - 60);
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
        _showAll = false;
        if (_search.Text.Length > 0)
            _search.Text = ""; // shows the pinned apps
        else
            ShowPinned();
        Show();
        Activate();
        _search.Focus();
    }

    public void HideMenu()
    {
        if (!IsVisible)
            return;
        HiddenAt = DateTime.Now;
        Hide();
    }

    // ---- Views -------------------------------------------------------------------------

    private void ToggleView()
    {
        _showAll = !_showAll;
        if (_search.Text.Length > 0)
            _search.Text = "";
        else if (_showAll)
            ShowAll();
        else
            ShowPinned();
        _search.Focus();
    }

    private void ShowPinned()
    {
        _sectionTitle.Text = "Pinned";
        ((TextBlock)_viewToggle.Content).Text = "All apps  \u203A";
        _results.Clear();

        // Only rebuild the tiles when the pins (or the installed apps) changed.
        var config = DockConfig.Load(DockHost.ConfigPath);
        var installed = AppsIfReady() is { } apps ? new HashSet<string>(apps.Select(a => a.Id), StringComparer.OrdinalIgnoreCase) : null;
        var pins = config.StartPins.Where(p => installed is null || p.Kind == PinKind.Path || installed.Contains(p.Target)).Take(24).ToList();
        var signature = string.Join("|", pins.Select(p => p.Key));
        if (_pinnedView is null || signature != _pinnedSignature)
        {
            _pinnedSignature = signature;
            var grid = new WrapPanel();
            foreach (var pin in pins)
            {
                var icon = new Image { Width = 36, Height = 36, Margin = new Thickness(0, 4, 0, 6) };
                AppIcons.Load(pin.Kind == PinKind.AppId ? "shell:AppsFolder\\" + pin.Target : Environment.ExpandEnvironmentVariables(pin.Target), 48, icon);
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                var stack = new StackPanel();
                stack.Children.Add(icon);
                stack.Children.Add(new TextBlock { Text = pin.Name, FontSize = 12, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 92 });
                var tile = FlatButton(stack, () => Launch(pin), pin.Name);
                tile.Width = 98;
                tile.Height = 86;
                tile.Margin = new Thickness(0, 0, 2, 4);
                tile.MouseRightButtonUp += (_, e) =>
                {
                    AppMenu(pin.Name, pin.Target, pin.Kind);
                    e.Handled = true;
                };
                grid.Children.Add(tile);
            }

            if (grid.Children.Count == 0)
                grid.Children.Add(new TextBlock { Text = "Nothing pinned yet. Right-click an app in All apps to pin it here.", Opacity = 0.7, Margin = new Thickness(4) });
            _pinnedView = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        }

        _body.Content = _pinnedView;
    }

    private void ShowAll()
    {
        _sectionTitle.Text = "All apps";
        ((TextBlock)_viewToggle.Content).Text = "\u2039  Back";
        ShowList(Apps(), letters: true);
        _listScroll.ScrollToTop();
    }

    private void ShowSearch(string query)
    {
        _sectionTitle.Text = "Best matches";
        ((TextBlock)_viewToggle.Content).Text = _showAll ? "\u2039  Back" : "All apps  \u203A";
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = Apps()
            .Where(a => words.All(w => a.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(a => a.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .ThenBy(a => a.Name.Length)
            .Take(30)
            .ToList();
        if (matches.Count == 0)
        {
            _results.Clear();
            _noMatches.Text = $"No apps match \"{query}\". Press Enter to search Windows instead.";
            _body.Content = _noMatches;
            return;
        }

        ShowList(matches, letters: false);
        _listScroll.ScrollToTop();
    }

    /// <summary>Fills the list with rows that are created once and reused, so typing stays smooth.</summary>
    private void ShowList(List<(string Name, string Id)> apps, bool letters)
    {
        _results.Clear();
        _selected = 0;
        _list.Children.Clear();
        char? letter = null;
        foreach (var (name, id) in apps)
        {
            var first = char.ToUpperInvariant(name[0]);
            if (letters && first != letter)
            {
                letter = first;
                if (!_letters.TryGetValue(first, out var heading))
                    _letters[first] = heading = new TextBlock { Text = char.IsLetter(first) ? first.ToString() : "#", FontWeight = FontWeights.SemiBold, Opacity = 0.6, Margin = new Thickness(10, 10, 0, 4) };
                _list.Children.Add(heading);
            }

            var row = Row(name, id);
            _list.Children.Add(row);
            _results.Add(row);
        }

        _body.Content = _listScroll;
        Highlight();
    }

    private Button Row(string name, string id)
    {
        if (_rows.TryGetValue(id, out var existing))
            return existing;

        var icon = new Image { Width = 28, Height = 28, Margin = new Thickness(0, 0, 12, 0) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520 });
        var pin = new DockPin(name, id, PinKind.AppId);
        var button = FlatButton(content, () => Launch(pin), null);
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Padding = new Thickness(10, 5, 10, 5);
        button.MouseRightButtonUp += (_, e) =>
        {
            AppMenu(name, id, PinKind.AppId);
            e.Handled = true;
        };
        _rows[id] = button;
        AppIcons.Load("shell:AppsFolder\\" + id, 32, icon);
        return button;
    }

    private void Highlight()
    {
        _selectedBrush ??= new SolidColorBrush(Color.FromArgb(_light ? (byte)50 : (byte)70, _accent.R, _accent.G, _accent.B));
        for (var i = 0; i < _results.Count; i++)
            _results[i].Background = i == _selected && _search.Text.Length > 0 ? _selectedBrush : Brushes.Transparent;
        if (_selected < _results.Count && _search.Text.Length > 0)
            _results[_selected].BringIntoView();
    }

    // ---- Pieces ------------------------------------------------------------------------

    private Border SearchBox()
    {
        _search.Background = Brushes.Transparent;
        _search.BorderThickness = new Thickness(0);
        _search.Foreground = Foreground;
        _search.CaretBrush = Foreground;
        _search.FontSize = 14;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) =>
        {
            _searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var query = _search.Text.Trim();
            if (query.Length > 0)
                ShowSearch(query);
            else if (_showAll)
                ShowAll();
            else
                ShowPinned();
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock { Text = "", FontFamily = DockTheme.Icons, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Opacity = 0.75 });
        var field = new Grid();
        field.Children.Add(_searchHint);
        field.Children.Add(_search);
        Grid.SetColumn(field, 1);
        grid.Children.Add(field);

        var box = new Border
        {
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(16, 0, 16, 0),
            Background = _light ? DockTheme.Brush(0xFFFFFFFF) : DockTheme.Brush(0xFF2C2C31),
            BorderThickness = new Thickness(0, 0, 0, 2),
            BorderBrush = new SolidColorBrush(_accent),
            Child = grid,
            Margin = new Thickness(0, 8, 0, 0),
        };
        return box;
    }

    private DockPanel Footer()
    {
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        footer.Children.Add(new Border { Height = 1, Background = _light ? DockTheme.Brush(0x1A000000) : DockTheme.Brush(0x22FFFFFF), Margin = new Thickness(0, 0, 0, 12) });
        DockPanel.SetDock(footer.Children[0], Dock.Top);

        var windowsStart = FlatButton(new TextBlock { Text = "Windows Start menu" }, () => { HideMenu(); ShellNative.OpenStartMenu(); }, "Open the regular Windows Start menu (files, recommendations, power options)");
        windowsStart.Padding = new Thickness(10, 6, 10, 6);
        footer.Children.Add(windowsStart);

        foreach (var (glyph, tip, action) in new (string, string, Action)[]
                 {
                     ("", "Power", ShowPowerMenu),
                     ("", "Lock", () => { HideMenu(); ShellNative.Lock(); }),
                     ("", "Settings", () => { HideMenu(); ShellNative.Start("ms-settings:", null); }),
                     ("", "File Explorer", () => { HideMenu(); ShellNative.Start("explorer.exe", null); }),
                 })
        {
            var button = FlatButton(new TextBlock { Text = glyph, FontFamily = DockTheme.Icons, FontSize = 16 }, action, tip);
            button.Width = 40;
            button.Height = 36;
            DockPanel.SetDock(button, Dock.Right);
            footer.Children.Add(button);
        }

        return footer;
    }

    private Button FlatButton(UIElement content, Action click, string? tip)
    {
        var button = new Button
        {
            Content = content,
            ToolTip = tip,
            Focusable = false,
            Cursor = Cursors.Hand,
            Foreground = Foreground,
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Template = ButtonTemplate(),
        };
        button.Click += (_, _) => click();
        return button;
    }

    private ControlTemplate ButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, new TemplateBindingExtension(HorizontalContentAlignmentProperty));
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, _light ? DockTheme.Brush(0x14000000) : DockTheme.Brush(0x1FFFFFFF), "Bd"));
        template.Triggers.Add(hover);
        return template;
    }

    // ---- Actions -----------------------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HideMenu();
                e.Handled = true;
                break;
            case Key.Down when _results.Count > 0:
                _selected = Math.Min(_selected + 1, _results.Count - 1);
                Highlight();
                e.Handled = true;
                break;
            case Key.Up when _results.Count > 0:
                _selected = Math.Max(_selected - 1, 0);
                Highlight();
                e.Handled = true;
                break;
            case Key.Enter when _search.Text.Trim().Length > 0:
                if (_selected < _results.Count)
                    _results[_selected].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                else
                {
                    // Nothing matched: hand the query to Windows search.
                    HideMenu();
                    ShellNative.Start("search-ms:query=" + Uri.EscapeDataString(_search.Text.Trim()), null);
                }

                e.Handled = true;
                break;
        }
    }

    private void Launch(DockPin pin)
    {
        HideMenu();
        try
        {
            if (pin.Kind == PinKind.AppId)
                ShellNative.LaunchAppId(pin.Target);
            else
                ShellNative.LaunchPath(Environment.ExpandEnvironmentVariables(pin.Target));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open {pin.Name}: {ex.Message}", "Restyle Start", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AppMenu(string name, string target, PinKind kind)
    {
        var menu = _dock.CreateMenu();
        var config = DockConfig.Load(DockHost.ConfigPath);
        var pin = new DockPin(name, target, kind);
        var pinned = config.StartPins.Any(p => p.Key == pin.Key);

        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("Open", () => Launch(pin));
        Add(pinned ? "Unpin from Start" : "Pin to Start", () =>
        {
            Save(c =>
            {
                if (pinned)
                    c.StartPins.RemoveAll(p => p.Key == pin.Key);
                else
                    c.StartPins.Add(pin);
            });
            if (!_showAll && _search.Text.Length == 0)
                ShowPinned();
        });
        if (!config.IsPinned(pin.Key))
            Add("Keep in Dock", () => Save(c => c.Pinned.Add(pin)));

        _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (IsLoaded && !IsActive)
                Activate();
        };
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void ShowPowerMenu()
    {
        var menu = _dock.CreateMenu();
        foreach (var (header, action) in new (string, Action)[]
                 {
                     ("Sleep", () => SetSuspendState(false, false, false)),
                     ("Sign out", () => Run("shutdown.exe", "/l")),
                     ("Restart", () => Run("shutdown.exe", "/r /t 0")),
                     ("Shut down", () => Run("shutdown.exe", "/s /t 0")),
                 })
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                HideMenu();
                action();
            };
            menu.Items.Add(item);
        }

        _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private static void Run(string file, string args) =>
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();

    private static void Save(Action<DockConfig> change)
    {
        var config = DockConfig.Load(DockHost.ConfigPath);
        change(config);
        config.Save(DockHost.ConfigPath);
    }

    // ---- Data --------------------------------------------------------------------------

    /// <summary>
    /// Reads the installed apps on a background thread (it takes about a second), so the menu opens
    /// instantly. The dock calls this at startup; the list is refreshed at most every two minutes.
    /// </summary>
    public static Task Preload()
    {
        if (_loading is not null && (!_loading.IsCompleted || DateTime.Now - _appsLoaded < TimeSpan.FromMinutes(2)))
            return _loading;
        var done = new TaskCompletionSource<List<(string Name, string Id)>>();
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(ShellNative.ListApps());
            }
            catch (Exception)
            {
                done.SetResult(_apps ?? []);
            }
        }) { IsBackground = true, Name = "Start menu app list" };
        thread.SetApartmentState(ApartmentState.STA); // the Shell objects need an STA thread
        thread.Start();
        _loading = done.Task;
        _appsLoaded = DateTime.Now;
        return _loading;
    }

    /// <summary>The app list; only waits for it the very first time (later refreshes happen in the background).</summary>
    private static List<(string Name, string Id)> Apps()
    {
        Preload();
        if (_loading!.IsCompleted || (_apps is null && _loading.Wait(TimeSpan.FromSeconds(5))))
            _apps = _loading.Result;
        return _apps ?? [];
    }

    /// <summary>The app list if it has been read already, otherwise null (never waits).</summary>
    private static List<(string Name, string Id)>? AppsIfReady()
    {
        if (_loading is { IsCompleted: true })
            _apps = _loading.Result;
        return _apps;
    }

    /// <summary>Starts loading every app's icon in the background, so lists show icons right away.</summary>
    public static void PrefetchIcons()
    {
        foreach (var (_, id) in AppsIfReady() ?? [])
            AppIcons.Load("shell:AppsFolder\\" + id, 32, null);
    }

    /// <summary>The Windows accent color (DWM stores it as 0xAABBGGRR).</summary>
    private static Color AccentColor()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
            return Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
        return Color.FromRgb(0x00, 0x78, 0xD4);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("powrprof.dll")] private static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);
}

/// <summary>
/// Loads app icons on a background thread (Windows takes ~100 ms per icon), so the Start menu never waits
/// for them. Icons for rows on screen jump ahead of the background prefetch. Results are cached.
/// </summary>
internal static class AppIcons
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.BlockingCollection<Request> Visible = [];
    private static readonly System.Collections.Concurrent.BlockingCollection<Request> Prefetch = [];
    private static Thread? _worker;

    private sealed record Request(string Name, int Size, Image? Target, Dispatcher? Dispatcher);

    /// <summary>Sets <paramref name="target"/>'s image when the icon is ready (null target = just prefetch).</summary>
    public static void Load(string parsingName, int size, Image? target)
    {
        var key = $"{parsingName}|{size}";
        if (Cache.TryGetValue(key, out var cached))
        {
            if (target is not null)
                target.Source = cached;
            return;
        }

        EnsureWorker();
        (target is null ? Prefetch : Visible).Add(new Request(parsingName, size, target, target?.Dispatcher));
    }

    private static void EnsureWorker()
    {
        if (_worker is not null)
            return;
        _worker = new Thread(Work) { IsBackground = true, Name = "Start menu icons", Priority = ThreadPriority.BelowNormal };
        _worker.SetApartmentState(ApartmentState.STA); // shell icon APIs need an STA thread
        _worker.Start();
    }

    private static void Work()
    {
        System.Collections.Concurrent.BlockingCollection<Request>[] queues = [Visible, Prefetch];
        while (true)
        {
            System.Collections.Concurrent.BlockingCollection<Request>.TakeFromAny(queues, out var request); // visible rows first
            var key = $"{request!.Name}|{request.Size}";
            if (!Cache.TryGetValue(key, out var image))
            {
                image = ShellNative.Icon(request.Name, request.Size, cache: false); // frozen bitmap: safe to hand to the UI thread
                Cache[key] = image;
            }

            if (request.Target is { } target)
                request.Dispatcher!.BeginInvoke(() => target.Source = image);
        }
    }
}