using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Restyle.Core.Dock;

namespace Restyle.App.Docking;

// ---- "Glass clock" layout (Night Sky): a big centered clock, and media + volume pills above the dock ----

/// <summary>A big centered clock with the weather and date underneath.</summary>
internal sealed class CenterClock : StackPanel, ITicking
{
    private readonly WeatherService _weather;
    private readonly TextBlock _time = new() { FontSize = 112, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") };
    private readonly TextBlock _glyph = new() { FontFamily = WeatherService.SymbolFont, FontSize = 26, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _temp = new() { FontSize = 26, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _weatherRow = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -10, 0, 6) };
    private readonly TextBlock _date = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.95 };
    private string? _shownMinute;

    public CenterClock(WeatherService weather)
    {
        _weather = weather;
        Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.35 }; // readable over bright clouds
        _weatherRow.Children.Add(_glyph);
        _weatherRow.Children.Add(_temp);
        Children.Add(_time);
        Children.Add(_weatherRow);
        Children.Add(_date);
        weather.Changed += ShowWeather;
        ShowWeather();
        Tick(0);
    }

    public void Tick(int tick)
    {
        var now = DateTime.Now;
        var minute = now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        if (minute == _shownMinute)
            return;
        _shownMinute = minute;
        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        _time.Text = now.ToString(format.ShortTimePattern.Replace("tt", "").Trim(), CultureInfo.CurrentCulture);
        _date.Text = $"{now:dddd}, {now.ToString(format.MonthDayPattern, CultureInfo.CurrentCulture)}";
    }

    private void ShowWeather()
    {
        // With weather off, the line simply isn't there (nothing goes online).
        _weatherRow.Visibility = _weather.Current is null ? Visibility.Collapsed : Visibility.Visible;
        _glyph.Text = _weather.Glyph;
        _temp.Text = _weather.Current is { } w ? $"{w.Temperature:0}°" : "";
        ToolTip = _weather.Tooltip;
    }
}

/// <summary>Two glass pills: what's playing (with play/pause and next) and a volume bar.</summary>
internal sealed class GlassPills : StackPanel, ITicking
{
    private const double PillWidth = 330;

    private readonly MediaSource _media;
    private readonly Audio _audio;
    private readonly Ellipse _art = new() { Width = 38, Height = 38 };
    private readonly TextBlock _artGlyph = new() { Text = "", FontFamily = DockTheme.Icons, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _artist = new() { FontSize = 11.5, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _playPause;
    private readonly Button _next;
    private readonly Button _mute;
    private readonly LevelSlider _volume = new("", horizontal: true) { Height = 26, ToolTip = "Volume (drag, or use the mouse wheel)" };

    public GlassPills(MediaSource media, Audio audio, bool glass)
    {
        _media = media;
        _audio = audio;

        // Media pill
        var artHolder = new Grid { Width = 38, Height = 38, Margin = new Thickness(0, 0, 12, 0) };
        artHolder.Children.Add(new Ellipse { Fill = DockTheme.Brush(0x33FFFFFF) });
        artHolder.Children.Add(_artGlyph);
        artHolder.Children.Add(_art);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_title);
        text.Children.Add(_artist);
        _playPause = WidgetUi.IconButton("", "Play / pause", media.PlayPause, 14, 32, 32);
        _next = WidgetUi.IconButton("", "Next", media.Next, 14, 32, 32);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(_playPause);
        buttons.Children.Add(_next);
        var mediaRow = new DockPanel { Margin = new Thickness(10, 8, 12, 8) };
        DockPanel.SetDock(artHolder, Dock.Left);
        DockPanel.SetDock(buttons, Dock.Right);
        mediaRow.Children.Add(artHolder);
        mediaRow.Children.Add(buttons);
        mediaRow.Children.Add(text);
        Children.Add(Pill(mediaRow, 27, glass, new Thickness(0, 0, 0, 10)));

        // Volume pill
        _mute = WidgetUi.IconButton("", "Mute / unmute", () =>
        {
            _audio.Muted = !_audio.Muted;
            Tick(0);
        }, 14, 32, 30);
        var volumeRow = new DockPanel { Margin = new Thickness(12, 7, 20, 7) };
        DockPanel.SetDock(_mute, Dock.Left);
        _volume.Margin = new Thickness(10, 0, 0, 0);
        volumeRow.Children.Add(_mute);
        volumeRow.Children.Add(_volume);
        Children.Add(Pill(volumeRow, 22, glass, new Thickness(0)));

        _volume.ValueChanged += v =>
        {
            _audio.Volume = (float)v;
            if (_audio.Muted && v > 0)
                _audio.Muted = false;
            WidgetUi.SetGlyph(_mute, VolumeGlyph(v, false));
        };
        media.Changed += ShowMedia;
        ShowMedia();
        Tick(0);
    }

    public void Tick(int tick)
    {
        if (tick % 2 != 0 || _volume.IsDragging)
            return;
        var volume = _audio.Volume;
        _volume.IsEnabled = volume is not null;
        _volume.Value = _audio.Muted ? 0 : volume ?? 0;
        WidgetUi.SetGlyph(_mute, VolumeGlyph(volume ?? 0, _audio.Muted));
    }

    private static string VolumeGlyph(double v, bool muted) =>
        muted ? "" : v < 0.01 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";

    private void ShowMedia()
    {
        _title.Text = _media.Title;
        _artist.Text = _media.HasMedia ? _media.Artist : "Play music or a video in any app";
        _playPause.Visibility = _next.Visibility = _media.HasMedia ? Visibility.Visible : Visibility.Collapsed;
        WidgetUi.SetGlyph(_playPause, _media.Playing ? "" : "");
        _art.Fill = _media.Art is { } art ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : null;
    }

    internal static Border Pill(UIElement content, double radius, bool glass, Thickness margin) => new GlassBorder
    {
        Glass = glass,
        Width = PillWidth,
        CornerRadius = new CornerRadius(radius),
        Background = glass ? null : DockTheme.Brush(0xD9222226),
        BorderBrush = glass ? null : DockTheme.Brush(0x26FFFFFF),
        BorderThickness = new Thickness(1),
        Margin = margin,
        Child = content,
    };
}

// ---- "Corners" layout (Moonlit): weather top-left, big date top-right, two-tone clock bottom-left ----

/// <summary>The warm palette of the corners layout.</summary>
internal static class Moonlit
{
    public static readonly Brush Gold = DockTheme.Brush(0xFFE8C77E);
    public static readonly Brush Orange = DockTheme.Brush(0xFFD9864E);
    public static readonly Brush Cyan = DockTheme.Brush(0xFF8FD3EA);
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
}

/// <summary>A small glass chip with the weather symbol, city and temperature.</summary>
internal sealed class WeatherChip : GlassBorder
{
    private readonly WeatherService _weather;
    private readonly TextBlock _glyph = new() { FontFamily = WeatherService.SymbolFont, FontSize = 26, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _city = new() { FontSize = 12, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _temp = new() { FontSize = 13, Foreground = Moonlit.Cyan };

    public WeatherChip(WeatherService weather, bool glass)
    {
        _weather = weather;
        Glass = glass;
        Background = glass ? null : DockTheme.Brush(0xB3222226);
        CornerRadius = new CornerRadius(14);
        BorderThickness = new Thickness(1);
        Cursor = Cursors.Hand;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 16, 8) };
        row.Children.Add(_glyph);
        row.Children.Add(new Rectangle { Width = 1, Fill = DockTheme.Brush(0x66FFFFFF), Margin = new Thickness(12, 2, 12, 2) });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_city);
        text.Children.Add(_temp);
        row.Children.Add(text);
        Child = row;

        MouseLeftButtonUp += (_, _) => weather.Click();
        weather.Changed += Show;
        Show();
    }

    private void Show()
    {
        _glyph.Text = _weather.Glyph;
        _city.Text = _weather.Enabled ? _weather.Config.WeatherCity?.Split(',')[0] ?? "Weather" : "Weather";
        _temp.Text = _weather.Current is { } w ? $"{w.Temperature:0}°  {_weather.Condition}" : _weather.Enabled ? _weather.Condition : "Click to set your city";
        ToolTip = _weather.Tooltip;
    }
}

/// <summary>A slim glass column of app icons down the left side, with Start at the top.</summary>
internal sealed class AppColumn : GlassBorder
{
    public AppColumn(DockWindow dock, bool glass)
    {
        Glass = glass;
        Background = glass ? null : DockTheme.Brush(0xB3222226);
        CornerRadius = new CornerRadius(20);
        BorderThickness = new Thickness(1);

        var stack = new StackPanel { Margin = new Thickness(6, 8, 6, 8) };
        stack.Children.Add(IconTile(DockWindow.StartIcon, "Start", () => dock.OpenStart()));
        stack.Children.Add(new Rectangle { Height = 1, Fill = DockTheme.Brush(0x40FFFFFF), Margin = new Thickness(8, 4, 8, 4) });
        var config = DockConfig.Load(DockHost.ConfigPath);
        foreach (var pin in config.StartPins)
        {
            if (stack.Children.Count >= 12)
                break;
            if (WidgetUi.Icon(pin, 48) is { } icon) // apps that aren't installed are skipped
                stack.Children.Add(IconTile(icon, pin.Name, () => WidgetUi.Launch(pin)));
        }

        Child = stack;
        ToolTip = "Your pinned apps. Right-click an app in the Start menu to pin or unpin it.";
    }

    private static Button IconTile(ImageSource icon, string name, Action click)
    {
        var image = new Image { Source = icon, Width = 28, Height = 28 };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var button = new Button
        {
            Content = image,
            Width = 44,
            Height = 42,
            ToolTip = name,
            Focusable = false,
            Cursor = Cursors.Hand,
            Template = WidgetUi.FlatButton(),
        };
        button.Click += (_, _) => click();
        return button;
    }
}

/// <summary>Thin bars that dance to whatever is playing (the speaker's live level; nothing is recorded).</summary>
internal sealed class AudioBars : FrameworkElement
{
    private const int Count = 26;
    private readonly Audio _audio;
    private readonly double[] _levels = new double[Count];
    private readonly double[] _resting = new double[Count];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private readonly Random _random = new();
    private int _quietTicks;

    public AudioBars(Audio audio)
    {
        _audio = audio;
        Width = 300;
        Height = 58;
        IsHitTestVisible = false;
        var seed = new Random(3);
        for (var i = 0; i < Count; i++)
            _resting[i] = 0.12 + seed.NextDouble() * 0.25 + (i > Count - 7 ? 0.35 * seed.NextDouble() : 0);
        _timer.Tick += (_, _) => Step();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Step()
    {
        var peak = Math.Clamp(_audio.Peak * 1.6, 0, 1);
        if (peak < 0.01)
        {
            // Silence: settle into a calm resting shape and check far less often.
            if (++_quietTicks > 15)
                _timer.Interval = TimeSpan.FromMilliseconds(600);
        }
        else
        {
            _quietTicks = 0;
            _timer.Interval = TimeSpan.FromMilliseconds(70);
        }

        // Scroll left; the newest level enters on the right, with a little variation between neighbors.
        Array.Copy(_levels, 1, _levels, 0, Count - 1);
        _levels[Count - 1] = peak * (0.7 + 0.3 * _random.NextDouble());
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var gap = ActualWidth / Count;
        var barWidth = Math.Max(2, gap * 0.28);
        for (var i = 0; i < Count; i++)
        {
            var level = Math.Max(_levels[i], _quietTicks > 15 ? _resting[i] : _resting[i] * 0.5);
            var h = Math.Max(3, level * ActualHeight);
            dc.DrawRoundedRectangle(Moonlit.Gold, null, new Rect(i * gap + (gap - barWidth) / 2, ActualHeight - h, barWidth, h), barWidth / 2, barWidth / 2);
        }
    }
}

/// <summary>The big date in the top-right corner: "wed", then "may 2023" beside a large "03".</summary>
internal sealed class DateBlock : StackPanel, ITicking
{
    private readonly TextBlock _weekday = new() { FontSize = 120, FontWeight = FontWeights.Light, FontFamily = Moonlit.Display, Foreground = Moonlit.Gold, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -30, 0, -34) };
    private readonly TextBlock _month = new() { FontSize = 46, FontWeight = FontWeights.Light, FontFamily = Moonlit.Display, Foreground = Moonlit.Orange, Margin = new Thickness(0, 0, 0, -14) };
    private readonly TextBlock _year = new() { FontSize = 20, FontFamily = Moonlit.Display, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _day = new() { FontSize = 140, FontWeight = FontWeights.Light, FontFamily = Moonlit.Display, Foreground = Moonlit.Cyan, Margin = new Thickness(-4, -36, 0, -30) };
    private DateTime _shown;

    public DateBlock()
    {
        HorizontalAlignment = HorizontalAlignment.Right;
        Margin = new Thickness(24, 34, 10, 30); // the big letters reach past their boxes; keep them inside the window
        Children.Add(_weekday);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var monthYear = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        monthYear.Children.Add(_month);
        monthYear.Children.Add(_year);
        row.Children.Add(monthYear);
        row.Children.Add(_day);
        Children.Add(row);
        Tick(0);
    }

    public void Tick(int tick)
    {
        var today = DateTime.Today;
        if (today == _shown)
            return;
        _shown = today;
        var culture = CultureInfo.CurrentCulture;
        _weekday.Text = culture.DateTimeFormat.GetAbbreviatedDayName(today.DayOfWeek).ToLower(culture);
        _month.Text = culture.DateTimeFormat.GetAbbreviatedMonthName(today.Month).ToLower(culture).TrimEnd('.');
        _year.Text = today.Year.ToString(culture);
        _day.Text = today.Day.ToString("00", culture);
    }
}

/// <summary>The two-tone clock in the bottom-left corner: hours, a slanted line, minutes.</summary>
internal sealed class CornerClock : StackPanel, ITicking
{
    private readonly TextBlock _hours = new() { FontSize = 104, FontWeight = FontWeights.Light, FontFamily = Moonlit.Display, Foreground = Moonlit.Gold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _minutes = new() { FontSize = 104, FontWeight = FontWeights.Light, FontFamily = Moonlit.Display, Foreground = Moonlit.Cyan, VerticalAlignment = VerticalAlignment.Center };
    private string? _shownMinute;

    public CornerClock()
    {
        Orientation = Orientation.Horizontal;
        Children.Add(_hours);
        Children.Add(new Rectangle
        {
            Width = 4,
            Height = 92,
            RadiusX = 2,
            RadiusY = 2,
            Fill = Moonlit.Orange,
            Margin = new Thickness(18, 10, 22, 0),
            RenderTransform = new SkewTransform(-12, 0),
            RenderTransformOrigin = new Point(0.5, 0.5),
        });
        Children.Add(_minutes);
        Tick(0);
    }

    public void Tick(int tick)
    {
        var now = DateTime.Now;
        var minute = now.ToString("HHmm", CultureInfo.InvariantCulture);
        if (minute == _shownMinute)
            return;
        _shownMinute = minute;
        var twentyFourHour = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H');
        _hours.Text = now.ToString(twentyFourHour ? "HH" : "%h", CultureInfo.CurrentCulture);
        _minutes.Text = now.ToString("mm", CultureInfo.CurrentCulture);
    }
}
