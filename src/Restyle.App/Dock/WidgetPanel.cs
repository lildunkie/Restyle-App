using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Restyle.Core.Dock;
using Windows.Media.Control;

namespace Restyle.App.Docking;

/// <summary>
/// The tile panel on the right: weather, analog clock, four app shortcuts, volume/brightness,
/// now playing, and a calendar with a note. Everything but weather works offline.
/// </summary>
internal sealed class WidgetPanel : StackPanel, ITicking
{
    private const double Big = 150, Small = 72, Gap = 6;
    private static readonly Brush TileBg = DockTheme.Brush(0xD9222226);
    private static readonly Brush TileBorder = DockTheme.Brush(0x26FFFFFF);

    private readonly WidgetConfig _config;
    private readonly DockWindow _dock;
    private readonly Audio _audio;
    private readonly List<ITicking> _parts = [];
    private readonly List<Action> _cleanup = [];

    public WidgetPanel(WidgetConfig config, DockWindow dock, WeatherService weatherService, MediaSource mediaSource, Audio audio)
    {
        _config = config;
        _dock = dock;
        _audio = audio;
        Width = 2 * Big + Gap;

        var weather = new WeatherTile(weatherService);
        var clock = new AnalogClock();
        _parts.Add(clock);
        var weatherTile = Tile(weather, Big, Big, weather.Background);
        if (weatherTile is GlassBorder { Glass: true } glassWeather)
            glassWeather.Tint = Color.FromArgb(0x55, 0x2C, 0x6C, 0xC8); // blue-tinted glass, like the solid tile
        Children.Add(Row(
            Captioned(weatherTile, "Weather", weatherService.Click),
            Captioned(Tile(clock, Big, Big), "Clock", () => ShellNative.Start("ms-clock:", null))));

        var apps = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < 4; i++)
            apps.Children.Add(AppTile(i, i < 3 ? Gap : 0));
        Children.Add(apps);

        var switches = new SwitchesTile(_audio);
        var media = new MediaTile(mediaSource, config.Glass);
        _parts.Add(switches);
        var mediaCaption = new TextBlock { Text = "Now playing" };
        media.CaptionChanged += text => mediaCaption.Text = text;
        Children.Add(Row(Captioned(Tile(switches, Big, Big), "Switches"), Captioned(media, mediaCaption)));

        var calendar = new CalendarTile();
        _parts.Add(calendar);
        _cleanup.Add(calendar.Save);
        Children.Add(Captioned(Tile(calendar, Width, Big), calendar.Caption));

        Unloaded += (_, _) => _cleanup.ForEach(a => a());
    }

    public void Tick(int tick) => _parts.ForEach(p => p.Tick(tick));

    // ---- Layout helpers ----------------------------------------------------------------

    private static StackPanel Row(FrameworkElement left, FrameworkElement right)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        left.Margin = new Thickness(0, 0, Gap, 0);
        row.Children.Add(left);
        row.Children.Add(right);
        return row;
    }

    /// <summary>A rounded tile: frosted glass when glass is on, otherwise dark (or the given brush).</summary>
    private Border Tile(UIElement content, double width, double height, Brush? background = null) => new GlassBorder
    {
        Glass = _config.Glass,
        Width = width,
        Height = height,
        CornerRadius = new CornerRadius(width < 100 ? 16 : 20),
        Background = _config.Glass ? null : background ?? TileBg,
        BorderBrush = _config.Glass ? null : TileBorder,
        BorderThickness = new Thickness(1),
        Child = content,
    };

    private static StackPanel Captioned(FrameworkElement tile, string caption, Action? click = null) =>
        Captioned(tile, new TextBlock { Text = caption }, click);

    private static StackPanel Captioned(FrameworkElement tile, TextBlock caption, Action? click = null)
    {
        caption.FontSize = 11.5;
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        caption.Margin = new Thickness(0, 4, 0, 10);
        caption.MaxWidth = tile.Width;
        if (click is not null)
        {
            tile.Cursor = Cursors.Hand;
            tile.MouseLeftButtonUp += (_, _) => click();
        }

        var stack = new StackPanel();
        stack.Children.Add(tile);
        stack.Children.Add(caption);
        return stack;
    }

    // ---- App shortcuts -----------------------------------------------------------------

    private FrameworkElement AppTile(int index, double rightMargin)
    {
        var pin = index < _config.Apps.Count ? _config.Apps[index] : WidgetConfig.DefaultApps()[index];
        var icon = pin.Kind == PinKind.AppId
            ? ShellNative.Icon("shell:AppsFolder\\" + pin.Target, 64)
            : ShellNative.Icon(Environment.ExpandEnvironmentVariables(pin.Target), 64);
        var image = new Image { Source = icon, Width = 40, Height = 40 };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var tile = Tile(image, Small, Small);
        tile.ToolTip = $"{pin.Name}\nRight-click to choose a different app";
        var stack = Captioned(tile, pin.Name, () =>
        {
            try
            {
                if (pin.Kind == PinKind.AppId)
                    ShellNative.LaunchAppId(pin.Target);
                else
                    ShellNative.LaunchPath(Environment.ExpandEnvironmentVariables(pin.Target));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't open {pin.Name}: {ex.Message}", "Restyle Widgets", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
        stack.Margin = new Thickness(0, 0, rightMargin, 0);
        tile.MouseRightButtonUp += (_, e) =>
        {
            ChooseApp(index);
            e.Handled = true;
        };
        return stack;
    }

    /// <summary>Right-click an app tile: pick any app from the dock to put there.</summary>
    private void ChooseApp(int index)
    {
        var menu = _dock.CreateMenu();
        var config = DockConfig.Load(DockHost.ConfigPath);
        foreach (var pin in config.Pinned)
        {
            var item = new MenuItem { Header = pin.Name };
            item.Click += (_, _) => SetApp(index, pin);
            menu.Items.Add(item);
        }

        if (config.Pinned.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Keep apps in the dock to choose them here", IsEnabled = false });
        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Reset all four to the defaults" };
        reset.Click += (_, _) => Save(c => c.Widgets.Apps = WidgetConfig.DefaultApps());
        menu.Items.Add(reset);
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private static void SetApp(int index, DockPin pin) => Save(c =>
    {
        while (c.Widgets.Apps.Count < 4)
            c.Widgets.Apps.Add(WidgetConfig.DefaultApps()[c.Widgets.Apps.Count]);
        c.Widgets.Apps[index] = pin;
    });

    private static void Save(Action<DockConfig> change)
    {
        var config = DockConfig.Load(DockHost.ConfigPath);
        change(config);
        config.Save(DockHost.ConfigPath); // the dock reloads and redraws the widgets
    }
}

/// <summary>Current weather (from the shared <see cref="WeatherService"/>). Off until a city is set.</summary>
internal sealed class WeatherTile : Grid
{
    private readonly WeatherService _weather;
    private readonly TextBlock _city = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 26, 0) };
    private readonly TextBlock _temp = new() { FontSize = 40, FontWeight = FontWeights.Light, Margin = new Thickness(0, -2, 0, 0) };
    private readonly TextBlock _glyph = new() { FontFamily = WeatherService.SymbolFont, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _condition = new() { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _range = new() { FontSize = 12, Opacity = 0.85 };

    public WeatherTile(WeatherService weather)
    {
        _weather = weather;
        Margin = new Thickness(14, 12, 14, 12);
        Background = new LinearGradientBrush(Color.FromRgb(0x4A, 0x90, 0xE2), Color.FromRgb(0x2C, 0x5C, 0xB0), 90);

        var text = new StackPanel();
        text.Children.Add(_city);
        text.Children.Add(_temp);
        var bottom = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        bottom.Children.Add(_condition);
        bottom.Children.Add(_range);
        Children.Add(text);
        Children.Add(_glyph);
        Children.Add(bottom);

        weather.Changed += Show;
        Show();
    }

    /// <summary>The tile brush; the tile border takes it so the gradient fills the rounded corners.</summary>
    public new Brush Background { get; }

    private void Show()
    {
        var w = _weather.Current;
        _city.Text = _weather.Enabled ? _weather.Config.WeatherCity ?? "Weather" : "Weather";
        _temp.Text = w is null ? "--°" : $"{w.Temperature:0}°";
        _glyph.Text = _weather.Glyph;
        _condition.Text = _weather.Enabled ? _weather.Condition : "Click to set your city";
        _range.Text = w is not null ? $"H:{w.High:0}°  L:{w.Low:0}°" : _weather.Enabled ? "" : "Uses the internet";
        ToolTip = _weather.Tooltip;
    }
}

/// <summary>A round clock face with a sweeping second hand.</summary>
internal sealed class AnalogClock : FrameworkElement, ITicking
{
    private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public void Tick(int tick) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
            return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = size / 2 - 10;
        dc.DrawEllipse(DockTheme.Brush(0xFF161618), new Pen(DockTheme.Brush(0x40FFFFFF), 1), c, r, r);

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var h = 1; h <= 12; h++)
        {
            var a = h * Math.PI / 6;
            var text = new FormattedText(h.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, r * 0.2, Brushes.White, dpi);
            var p = new Point(c.X + Math.Sin(a) * r * 0.78, c.Y - Math.Cos(a) * r * 0.78);
            dc.DrawText(text, new Point(p.X - text.Width / 2, p.Y - text.Height / 2));
        }

        var now = DateTime.Now;
        void Hand(double turns, double length, double width, Brush brush, double tail = 0.12)
        {
            var a = turns * 2 * Math.PI;
            var dx = Math.Sin(a);
            var dy = -Math.Cos(a);
            var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, new Point(c.X - dx * r * tail, c.Y - dy * r * tail), new Point(c.X + dx * r * length, c.Y + dy * r * length));
        }

        Hand((now.Hour % 12 + now.Minute / 60.0) / 12, 0.5, 4, Brushes.White);
        Hand((now.Minute + now.Second / 60.0) / 60, 0.72, 3, Brushes.White);
        var orange = DockTheme.Brush(0xFFFF9F0A);
        Hand(now.Second / 60.0, 0.8, 1.4, orange, 0.2);
        dc.DrawEllipse(orange, null, c, 3.5, 3.5);
    }
}

/// <summary>Two big vertical sliders: speaker volume and screen brightness.</summary>
internal sealed class SwitchesTile : UniformGrid, ITicking
{
    private readonly Audio _audio;
    private readonly LevelSlider _volume = new("") { ToolTip = "Volume (drag, or use the mouse wheel)" };
    private readonly LevelSlider _brightness = new("") { ToolTip = "Screen brightness" };
    private readonly DispatcherTimer _brightnessDelay = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _brightnessSupported;

    public SwitchesTile(Audio audio)
    {
        _audio = audio;
        Columns = 2;
        Margin = new Thickness(14);
        _volume.Margin = new Thickness(4, 0, 6, 0);
        _brightness.Margin = new Thickness(6, 0, 4, 0);
        Children.Add(_volume);
        Children.Add(_brightness);

        _volume.ValueChanged += v =>
        {
            _audio.Volume = (float)v;
            if (_audio.Muted && v > 0)
                _audio.Muted = false;
        };
        _brightness.ValueChanged += _ =>
        {
            _brightnessDelay.Stop();
            _brightnessDelay.Start();
        };
        _brightnessDelay.Tick += (_, _) =>
        {
            _brightnessDelay.Stop();
            var percent = (int)Math.Round(_brightness.Value * 100);
            Task.Run(() => Brightness.Set(percent));
        };

        _brightness.IsEnabled = false;
        _brightness.Opacity = 0.45;
        Tick(0);
    }

    public void Tick(int tick)
    {
        if (tick % 2 == 0 && !_volume.IsDragging)
        {
            var volume = _audio.Volume;
            _volume.IsEnabled = volume is not null;
            _volume.Value = _audio.Muted ? 0 : volume ?? 0;
        }

        // WMI is slow: check brightness in the background, and not often.
        if (tick % 30 == 0 && !_brightness.IsDragging)
        {
            _ = Task.Run(Brightness.Read).ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                _brightnessSupported = t.Result is not null;
                _brightness.IsEnabled = _brightnessSupported;
                _brightness.Opacity = _brightnessSupported ? 1 : 0.45;
                _brightness.ToolTip = _brightnessSupported ? "Screen brightness" : "This screen's brightness can't be changed from Windows (common on desktop monitors).";
                if (t.Result is int level && !_brightness.IsDragging)
                    _brightness.Value = level / 100.0;
            }), TaskScheduler.Default);
        }
    }
}

/// <summary>A tall rounded slider that fills from the bottom, like the macOS/iOS control center.</summary>
internal sealed class LevelSlider : FrameworkElement
{
    private static readonly Typeface IconFace = new(DockTheme.Icons, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly string _glyph;
    private readonly bool _horizontal;
    private double _value;

    /// <param name="horizontal">A thin horizontal bar (for the glass volume pill) instead of a tall pill.</param>
    public LevelSlider(string glyph, bool horizontal = false)
    {
        _glyph = glyph;
        _horizontal = horizontal;
        Cursor = Cursors.Hand;
    }

    public event Action<double>? ValueChanged;

    public bool IsDragging => IsMouseCaptured;

    public double Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 1);
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled)
            return;
        CaptureMouse();
        SetFromMouse(e);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured)
            SetFromMouse(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!IsEnabled)
            return;
        Set(Value + (e.Delta > 0 ? 0.05 : -0.05));
        e.Handled = true;
    }

    private void SetFromMouse(MouseEventArgs e) => Set(_horizontal
        ? e.GetPosition(this).X / Math.Max(1, ActualWidth)
        : 1 - e.GetPosition(this).Y / Math.Max(1, ActualHeight));

    private void Set(double value)
    {
        Value = value;
        ValueChanged?.Invoke(Value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (_horizontal)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // whole area clickable
            var bar = new Rect(0, (h - 6) / 2, w, 6);
            dc.DrawRoundedRectangle(DockTheme.Brush(0x40FFFFFF), null, bar, 3, 3);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(bar.X, bar.Y, Math.Max(6, w * _value), 6), 3, 3);
            return;
        }

        var radius = Math.Min(w / 2, 18);
        var track = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(Brushes.Transparent, null, track, radius, radius); // whole area clickable
        dc.PushClip(new RectangleGeometry(track, radius, radius));
        dc.DrawRectangle(DockTheme.Brush(0xFF4A4A4E), null, track);
        var fill = h * _value;
        dc.DrawRectangle(DockTheme.Brush(0xFFE8E8EA), null, new Rect(0, h - fill, w, fill));
        dc.Pop();

        var icon = new FormattedText(_glyph, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, IconFace, 18,
            _value > 0.18 ? DockTheme.Brush(0xFF3A3A3C) : Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(icon, new Point((w - icon.Width) / 2, h - icon.Height - 10));
    }
}

/// <summary>What's playing in any app (from the shared <see cref="MediaSource"/>), with play/pause/skip.</summary>
internal sealed class MediaTile : GlassBorder
{
    private readonly MediaSource _media;
    private readonly Brush _plain;
    private readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _artist = new() { FontSize = 11.5, Opacity = 0.85, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _placeholder = new() { Text = "\uE8D6", FontFamily = DockTheme.Icons, FontSize = 44, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 30) };
    private readonly Button _playPause;
    private readonly StackPanel _controls = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

    public MediaTile(MediaSource media, bool glass)
    {
        _media = media;
        Glass = glass;
        _plain = glass ? Brushes.Transparent : DockTheme.Brush(0xD9222226);
        Width = Height = 150;
        CornerRadius = new CornerRadius(20);
        BorderBrush = glass ? null : DockTheme.Brush(0x26FFFFFF);
        BorderThickness = new Thickness(1);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 12, 8) };
        info.Children.Add(_title);
        info.Children.Add(_artist);
        _controls.Children.Add(WidgetUi.IconButton("\uE892", "Previous", media.Previous));
        _controls.Children.Add(_playPause = WidgetUi.IconButton("\uE768", "Play / pause", media.PlayPause));
        _controls.Children.Add(WidgetUi.IconButton("\uE893", "Next", media.Next));
        _controls.Margin = new Thickness(0, 4, 0, 0);
        info.Children.Add(_controls);

        var shade = new Border
        {
            CornerRadius = new CornerRadius(0, 0, 20, 20),
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = 84,
            Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(glass ? (byte)110 : (byte)190, 0, 0, 0), 90),
        };
        var grid = new Grid();
        grid.Children.Add(_placeholder);
        grid.Children.Add(shade);
        grid.Children.Add(info);
        Child = grid;

        media.Changed += Show;
        Show();
    }

    public event Action<string>? CaptionChanged;

    private void Show()
    {
        _title.Text = _media.Title;
        _artist.Text = _media.Artist;
        _controls.Visibility = _media.HasMedia ? Visibility.Visible : Visibility.Collapsed;
        WidgetUi.SetGlyph(_playPause, _media.Playing ? "\uE769" : "\uE768");
        Background = _media.Art is { } art ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : _plain;
        _placeholder.Visibility = _media.Art is null ? Visibility.Visible : Visibility.Collapsed;
        CaptionChanged?.Invoke(_media.Source);
    }
}

/// <summary>"Today" with a note you can type, and this month's calendar.</summary>
internal sealed class CalendarTile : Grid, ITicking
{
    private readonly TextBox _note = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Background = Brushes.Transparent,
        Foreground = Brushes.White,
        CaretBrush = Brushes.White,
        BorderThickness = new Thickness(0),
        FontSize = 12.5,
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        Padding = new Thickness(0),
    };
    private readonly TextBlock _placeholder = new() { Text = "Add a note…", FontSize = 12.5, Opacity = 0.5, IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 0) };
    private readonly UniformGrid _month = new() { Columns = 7, Cursor = Cursors.Hand, ToolTip = "Open the Windows calendar" };
    private readonly DispatcherTimer _saveDelay = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _shownDay;
    private bool _dirty;

    public CalendarTile()
    {
        Margin = new Thickness(14, 12, 12, 10);
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

        var left = new DockPanel { Margin = new Thickness(0, 0, 10, 0) };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new Border { Width = 3, Height = 14, CornerRadius = new CornerRadius(1.5), Background = DockTheme.Brush(0xFFFF453A), Margin = new Thickness(0, 0, 6, 0) });
        header.Children.Add(new TextBlock { Text = "Today", FontWeight = FontWeights.SemiBold, FontSize = 13 });
        DockPanel.SetDock(header, Dock.Top);
        left.Children.Add(header);
        var noteArea = new Grid();
        noteArea.Children.Add(_placeholder);
        noteArea.Children.Add(_note);
        left.Children.Add(noteArea);
        Children.Add(left);

        Grid.SetColumn(_month, 1);
        _month.MouseLeftButtonUp += (_, _) => ShellNative.PressWinKey('N');
        Children.Add(_month);

        try
        {
            if (File.Exists(DesktopWidgets.NotePath))
                _note.Text = File.ReadAllText(DesktopWidgets.NotePath);
        }
        catch (Exception)
        {
        }

        _placeholder.Visibility = _note.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _note.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _note.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _dirty = true;
            _saveDelay.Stop();
            _saveDelay.Start();
        };
        _saveDelay.Tick += (_, _) =>
        {
            _saveDelay.Stop();
            Save();
        };

        Caption = new TextBlock();
        BuildMonth();
    }

    public TextBlock Caption { get; }

    /// <summary>The note is kept in %LOCALAPPDATA%\Restyle\note.txt, on this PC only.</summary>
    public void Save()
    {
        if (!_dirty)
            return;
        _dirty = false;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DesktopWidgets.NotePath)!);
            File.WriteAllText(DesktopWidgets.NotePath, _note.Text);
        }
        catch (Exception)
        {
        }
    }

    public void Tick(int tick)
    {
        if (tick % 30 == 0 && DateTime.Today != _shownDay)
            BuildMonth();
    }

    private void BuildMonth()
    {
        var today = DateTime.Today;
        _shownDay = today;
        Caption.Text = today.ToString("Y", CultureInfo.CurrentCulture);
        _month.Children.Clear();

        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        var first = (int)format.FirstDayOfWeek;
        for (var i = 0; i < 7; i++)
            _month.Children.Add(Cell(format.ShortestDayNames[(first + i) % 7], 0.6, FontWeights.SemiBold));

        var start = new DateTime(today.Year, today.Month, 1);
        var blanks = ((int)start.DayOfWeek - first + 7) % 7;
        for (var i = 0; i < blanks; i++)
            _month.Children.Add(new Border());
        var days = DateTime.DaysInMonth(today.Year, today.Month);
        for (var d = 1; d <= days; d++)
        {
            var cell = Cell(d.ToString(CultureInfo.CurrentCulture), d < today.Day ? 0.55 : 1, d == today.Day ? FontWeights.Bold : FontWeights.Normal);
            if (d == today.Day)
            {
                cell.Background = DockTheme.Brush(0xFFFF453A);
                cell.CornerRadius = new CornerRadius(8);
            }

            _month.Children.Add(cell);
        }

        _month.Rows = (int)Math.Ceiling((7 + blanks + days) / 7.0);
    }

    private static Border Cell(string text, double opacity, FontWeight weight) => new()
    {
        Margin = new Thickness(1),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            FontWeight = weight,
            Opacity = opacity,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };
}
