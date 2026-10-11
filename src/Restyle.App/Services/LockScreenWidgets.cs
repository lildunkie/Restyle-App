using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Restyle.App.Docking;
using Restyle.Core.Dock;
using Windows.Storage;
using Windows.System.UserProfile;

namespace Restyle.App.Services;

/// <summary>
/// Widgets on the lock screen. Windows doesn't let apps put live widgets there, so Restyle draws them
/// (calendar, weather, your note, world times) onto the lock screen picture on frosted-glass panels, and the
/// dock refreshes the picture every 15 minutes and whenever something changes. The clock is Windows' own.
/// </summary>
public static class LockScreenWidgets
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Restyle", "LockScreen");

    private static string? _lastHash;
    private static (DateTime When, string Key, WidgetNet.Weather Weather)? _weather;

    /// <summary>True for the pictures Restyle composes (so the plain picture underneath can be reported instead).</summary>
    public static bool IsComposite(string? path) =>
        path is not null && Path.GetFileName(path).StartsWith("composite-", StringComparison.OrdinalIgnoreCase)
        && Path.GetFullPath(path).StartsWith(Folder, StringComparison.OrdinalIgnoreCase);

    /// <summary>Fetches the weather for the lock screen (cached for 25 minutes). Null when weather is off or offline.</summary>
    internal static async Task<WidgetNet.Weather?> GetWeatherAsync(DockConfig config)
    {
        var w = config.Widgets;
        if (!config.LockWidgets.Widgets.Contains("weather") || w.WeatherLatitude is not double lat || w.WeatherLongitude is not double lon)
            return null;
        var fahrenheit = w.Fahrenheit ?? !RegionInfo.CurrentRegion.IsMetric;
        var key = $"{lat}|{lon}|{fahrenheit}";
        if (_weather is { } cached && cached.Key == key && DateTime.Now - cached.When < TimeSpan.FromMinutes(25))
            return cached.Weather;
        try
        {
            var weather = await WidgetNet.GetWeather(lat, lon, fahrenheit).ConfigureAwait(false);
            _weather = (DateTime.Now, key, weather);
            return weather;
        }
        catch (Exception)
        {
            return _weather?.Weather; // offline: keep the last reading
        }
    }

    /// <summary>
    /// Draws the widgets onto the plain picture and sets it as the lock screen (only if it changed).
    /// Must run on a UI (STA) thread; the slow parts run in the background.
    /// </summary>
    internal static async Task RefreshAsync(DockConfig config, Func<string, string> resolve, bool force = false)
    {
        if (!config.LockWidgets.Enabled || BasePicture(config, resolve) is not { } picture)
            return;
        var weather = await GetWeatherAsync(config);
        var png = Render(picture, config, weather);
        await Task.Run(() => SetIfChangedAsync(png, force));
    }

    /// <summary>Same as <see cref="RefreshAsync"/>, for callers that can't await (it waits a few seconds at most).</summary>
    internal static void RefreshNow(DockConfig config, Func<string, string> resolve)
    {
        if (!config.LockWidgets.Enabled || BasePicture(config, resolve) is not { } picture)
            return;
        var fetch = Task.Run(() => GetWeatherAsync(config));
        var weather = fetch.Wait(TimeSpan.FromSeconds(6)) ? fetch.Result : _weather?.Weather;
        var png = Render(picture, config, weather);
        Task.Run(() => SetIfChangedAsync(png, force: true)).GetAwaiter().GetResult();
    }

    /// <summary>Widgets turned off: put the plain picture (or Windows Spotlight) back.</summary>
    internal static void RestorePlain(DockConfig config, Func<string, string> resolve)
    {
        _lastHash = null;
        if (config.LockWidgets.Base is not { Length: > 0 } plain)
            return;
        if (plain.Equals(LockScreenSetting.Spotlight, StringComparison.OrdinalIgnoreCase))
        {
            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "RotatingLockScreenEnabled", 1, RegistryValueKind.DWord);
            return;
        }

        var path = resolve(plain);
        if (File.Exists(path))
            Task.Run(() => SetImageAsync(path)).GetAwaiter().GetResult();
    }

    private static string? BasePicture(DockConfig config, Func<string, string> resolve)
    {
        var plain = config.LockWidgets.Base;
        if (string.IsNullOrWhiteSpace(plain) || plain.Equals(LockScreenSetting.Spotlight, StringComparison.OrdinalIgnoreCase))
            return null; // nothing to draw on (Windows Spotlight changes its own pictures)
        try
        {
            var path = resolve(plain);
            return File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task SetIfChangedAsync(byte[] png, bool force)
    {
        var hash = Convert.ToHexString(SHA256.HashData(png));
        var showing = LockScreen.OriginalImageFile is { IsFile: true } uri ? uri.LocalPath : null;
        if (!force && hash == _lastHash && IsComposite(showing))
            return;

        // Alternate between two files so Windows always sees a new picture.
        Directory.CreateDirectory(Folder);
        var a = Path.Combine(Folder, "composite-a.png");
        var file = string.Equals(showing, a, StringComparison.OrdinalIgnoreCase) ? Path.Combine(Folder, "composite-b.png") : a;
        await File.WriteAllBytesAsync(file, png);
        await SetImageAsync(file);
        _lastHash = hash;
    }

    private static async Task SetImageAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        await LockScreen.SetImageFileAsync(file);
    }

    // ---- Drawing -----------------------------------------------------------------------

    private static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI");
    private static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");

    /// <summary>The finished lock screen picture as PNG bytes, at the screen's resolution.</summary>
    internal static byte[] Render(string picturePath, DockConfig config, WidgetNet.Weather? weather)
    {
        int width = Math.Max(GetSystemMetrics(0), 1280), height = Math.Max(GetSystemMetrics(1), 720);
        var u = height / 1080.0;

        var picture = new BitmapImage();
        picture.BeginInit();
        picture.UriSource = new Uri(Path.GetFullPath(picturePath));
        picture.DecodePixelWidth = width;
        picture.CacheOption = BitmapCacheOption.OnLoad;
        picture.EndInit();
        picture.Freeze();
        var blurred = WallpaperBackdrop.Build(picturePath);

        var root = new Grid { Width = width, Height = height, ClipToBounds = true };
        root.Children.Add(new Image { Source = picture, Stretch = Stretch.UniformToFill });

        var lw = config.LockWidgets;
        var stack = new StackPanel
        {
            Width = 380,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = lw.Side == "left" ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Margin = lw.Side == "left" ? new Thickness(90 * u, 120 * u, 0, 0) : new Thickness(0, 120 * u, 90 * u, 0),
            LayoutTransform = new ScaleTransform(u, u), // designed at 1080p, scaled to the screen
        };
        TextElement.SetForeground(stack, Brushes.White);
        TextElement.SetFontFamily(stack, Text);

        var panels = new List<Border>();
        void Add(UIElement? content)
        {
            if (content is null)
                return;
            var panel = Panel(content);
            panels.Add(panel);
            stack.Children.Add(panel);
        }

        foreach (var id in LockWidgetConfig.All.Select(w => w.Id).Where(lw.Widgets.Contains))
        {
            Add(id switch
            {
                "weather" => weather is null ? null : WeatherPanel(weather, config.Widgets.WeatherCity),
                "calendar" => CalendarPanel(),
                "cities" => CitiesPanel(config.Widgets.Cities),
                _ => NotePanel(),
            });
        }

        root.Children.Add(stack);
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        // Frosted glass: each panel shows the blurred part of the picture that's right behind it.
        if (blurred is not null)
        {
            foreach (var panel in panels)
            {
                var box = panel.TransformToAncestor(root).TransformBounds(new Rect(panel.RenderSize));
                panel.Background = new ImageBrush(blurred)
                {
                    ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                    Viewbox = new Rect(box.X / width, box.Y / height, box.Width / width, box.Height / height),
                    Stretch = Stretch.Fill,
                };
            }

            root.UpdateLayout();
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Border Panel(UIElement content) => new()
    {
        CornerRadius = new CornerRadius(22),
        Margin = new Thickness(0, 0, 0, 14),
        Background = DockTheme.Brush(0x66202028),
        Child = new Border
        {
            CornerRadius = new CornerRadius(22),
            Background = DockTheme.Brush(0x4A14141C), // tint over the blur keeps white text readable
            BorderBrush = new LinearGradientBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), 90),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(22, 16, 22, 18),
            Child = content,
        },
    };

    private static TextBlock Line(string text, double size, FontWeight? weight = null, double opacity = 1, FontFamily? font = null) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Opacity = opacity,
        FontFamily = font ?? Text,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static UIElement WeatherPanel(WidgetNet.Weather w, string? city)
    {
        var (condition, glyph) = WidgetNet.Describe(w.Code, w.IsDay);
        var grid = new Grid();
        var left = new StackPanel();
        left.Children.Add(Line(city?.Split(',')[0] ?? "Weather", 15, FontWeights.SemiBold));
        left.Children.Add(Line($"{w.Temperature:0}°", 54, FontWeights.Light, font: Display));
        left.Children.Add(Line($"{condition}  ·  H:{w.High:0}°  L:{w.Low:0}°", 14, opacity: 0.9));
        left.Children.Add(Line($"Updated {DateTime.Now:t}", 12, opacity: 0.6));
        grid.Children.Add(left);
        grid.Children.Add(new TextBlock { Text = glyph, FontFamily = WeatherService.SymbolFont, FontSize = 44, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 0, 0) });
        return grid;
    }

    private static UIElement CalendarPanel()
    {
        var today = DateTime.Today;
        var culture = CultureInfo.CurrentCulture;
        var format = culture.DateTimeFormat;
        var panel = new StackPanel();
        panel.Children.Add(Line(today.ToString("Y", culture), 16, FontWeights.SemiBold));

        var grid = new UniformGrid { Columns = 7, Margin = new Thickness(0, 8, 0, 0) };
        var first = (int)format.FirstDayOfWeek;
        for (var i = 0; i < 7; i++)
            grid.Children.Add(Cell(format.ShortestDayNames[(first + i) % 7], 0.6, FontWeights.SemiBold, null));
        var start = new DateTime(today.Year, today.Month, 1);
        var blanks = ((int)start.DayOfWeek - first + 7) % 7;
        for (var i = 0; i < blanks; i++)
            grid.Children.Add(new Border());
        var accent = AccentBrush();
        for (var d = 1; d <= DateTime.DaysInMonth(today.Year, today.Month); d++)
            grid.Children.Add(Cell(d.ToString(culture), d < today.Day ? 0.55 : 1, d == today.Day ? FontWeights.Bold : FontWeights.Normal, d == today.Day ? accent : null));
        panel.Children.Add(grid);
        return panel;
    }

    private static Border Cell(string text, double opacity, FontWeight weight, Brush? highlight) => new()
    {
        Height = 30,
        Margin = new Thickness(2),
        CornerRadius = new CornerRadius(15),
        Background = highlight,
        Child = new TextBlock { Text = text, FontSize = 14, FontWeight = weight, Opacity = opacity, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    /// <summary>Each city's difference from your time ("+14 h"), which stays correct however old the picture is.</summary>
    private static UIElement? CitiesPanel(List<WorldCity> cities)
    {
        var now = DateTime.UtcNow;
        var local = TimeZoneInfo.Local.GetUtcOffset(now);
        var rows = new StackPanel();
        rows.Children.Add(Line("World times", 16, FontWeights.SemiBold));
        foreach (var city in cities.Take(6))
        {
            TimeSpan offset;
            try
            {
                offset = TimeZoneInfo.FindSystemTimeZoneById(city.TimeZone).GetUtcOffset(now) - local;
            }
            catch (Exception)
            {
                continue;
            }

            var hours = offset.TotalHours;
            var text = Math.Abs(hours) < 0.01 ? "same time" : $"{(hours > 0 ? "+" : "−")}{Math.Abs(hours):0.#} h";
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var value = Line(text, 15, opacity: 0.85);
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
            row.Children.Add(Line(city.Name, 15));
            rows.Children.Add(row);
        }

        return rows.Children.Count > 1 ? rows : null;
    }

    private static UIElement? NotePanel()
    {
        string note;
        try
        {
            note = File.Exists(DesktopWidgets.NotePath) ? File.ReadAllText(DesktopWidgets.NotePath).Trim() : "";
        }
        catch (Exception)
        {
            note = "";
        }

        if (note.Length == 0)
            return null; // nothing to show
        var panel = new StackPanel();
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), Background = AccentBrush(), Margin = new Thickness(0, 0, 8, 0) });
        header.Children.Add(Line("Today", 16, FontWeights.SemiBold));
        panel.Children.Add(header);
        panel.Children.Add(new TextBlock { Text = note, FontSize = 15, TextWrapping = TextWrapping.Wrap, MaxHeight = 5 * 21, TextTrimming = TextTrimming.WordEllipsis });
        return panel;
    }

    private static SolidColorBrush AccentBrush()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
            return new SolidColorBrush(Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16)));
        return new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
