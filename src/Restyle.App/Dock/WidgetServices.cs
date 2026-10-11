using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Restyle.Core.Dock;
using Windows.Media.Control;

namespace Restyle.App.Docking;

/// <summary>
/// Weather for the city set on the Widgets page, shared by every widget that shows it. Refreshes every
/// 30 minutes; does nothing at all (no internet) until a city is set.
/// </summary>
internal sealed class WeatherService : ITicking
{
    public WeatherService(WidgetConfig config)
    {
        Config = config;
        if (Enabled)
            _ = Refresh();
    }

    public event Action? Changed;

    public WidgetConfig Config { get; }

    public bool Enabled => Config.WeatherEnabled;

    public WidgetNet.Weather? Current { get; private set; }

    public string Condition { get; private set; } = "Loading…";

    /// <summary>A weather symbol (drawn with the Segoe UI Emoji font).</summary>
    public string Glyph { get; private set; } = "☁";

    public DateTime? Updated { get; private set; }

    public string Tooltip => !Enabled
        ? "Weather is off. Click to choose a city in Restyle's Widgets settings.\nIt's the only widget that goes online (Open-Meteo.com, no account)."
        : $"{Config.WeatherCity}\n" +
          (Updated is { } u ? $"Updated {u:t}. Click to refresh." : "Couldn't reach the weather service yet. Click to try again.") +
          "\nWeather data by Open-Meteo.com";

    public static readonly FontFamily SymbolFont = new("Segoe UI Emoji, Segoe UI Symbol");

    public void Tick(int tick)
    {
        if (Enabled && tick > 0 && tick % 1800 == 0)
            _ = Refresh();
    }

    /// <summary>Click on a weather widget: refresh, or (with weather off) open the Widgets page to set a city.</summary>
    public void Click()
    {
        if (Enabled)
            _ = Refresh();
        else
            ShellNative.Start(Environment.ProcessPath!, "--page widgets");
    }

    public async Task Refresh()
    {
        if (Config.WeatherLatitude is not double lat || Config.WeatherLongitude is not double lon)
            return;
        try
        {
            var w = await WidgetNet.GetWeather(lat, lon, Config.Fahrenheit ?? !RegionInfo.CurrentRegion.IsMetric);
            (Condition, Glyph) = WidgetNet.Describe(w.Code, w.IsDay);
            Current = w;
            Updated = DateTime.Now;
        }
        catch (Exception)
        {
            if (Current is null)
                Condition = "Offline";
        }

        Changed?.Invoke();
    }
}

/// <summary>
/// What's playing in any app that shows Windows media controls (Spotify, browsers, Media Player...),
/// with play/pause/skip. Shared by the media widgets; everything stays on this PC.
/// </summary>
internal sealed class MediaSource
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _artKey;
    private bool _detached;

    public MediaSource() => _ = Attach();

    public event Action? Changed;

    public bool HasMedia { get; private set; }

    public string Title { get; private set; } = "Nothing playing";

    public string Artist { get; private set; } = "Play music or a video in any app";

    public bool Playing { get; private set; }

    public ImageSource? Art { get; private set; }

    /// <summary>The app that's playing, e.g. "Spotify".</summary>
    public string Source { get; private set; } = "Now playing";

    public void PlayPause() => Do(s => s.TryTogglePlayPauseAsync());

    public void Next() => Do(s => s.TrySkipNextAsync());

    public void Previous() => Do(s => s.TrySkipPreviousAsync());

    public void Detach()
    {
        _detached = true;
        if (_manager is not null)
            _manager.CurrentSessionChanged -= OnSessionChanged;
        Unbind();
    }

    private async void Do(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> action)
    {
        try
        {
            if (_session is { } s)
                await action(s);
        }
        catch (Exception)
        {
            // The app went away; the next update shows that.
        }
    }

    private async Task Attach()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_detached)
                return;
            _manager.CurrentSessionChanged += OnSessionChanged;
            await Bind();
        }
        catch (Exception)
        {
            // Media controls aren't available on this PC.
        }
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        _dispatcher.BeginInvoke(() => _ = Bind());

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession sender, object args) =>
        _dispatcher.BeginInvoke(() => _ = Update());

    private void Unbind()
    {
        if (_session is null)
            return;
        _session.MediaPropertiesChanged -= OnMediaChanged;
        _session.PlaybackInfoChanged -= OnMediaChanged;
        _session = null;
    }

    private async Task Bind()
    {
        if (_detached)
            return;
        Unbind();
        _session = _manager?.GetCurrentSession();
        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnMediaChanged;
            _session.PlaybackInfoChanged += OnMediaChanged;
        }

        await Update();
    }

    private async Task Update()
    {
        if (_session is not { } session)
        {
            Clear();
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            HasMedia = true;
            Playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            Title = string.IsNullOrWhiteSpace(props.Title) ? "Unknown title" : props.Title;
            Artist = props.Artist ?? "";
            Source = SourceName(session.SourceAppUserModelId);

            var key = props.Title + "|" + props.Artist + "|" + props.AlbumTitle;
            if (key != _artKey)
            {
                _artKey = key;
                Art = props.Thumbnail is null ? null : await LoadArt(props.Thumbnail);
            }

            Changed?.Invoke();
        }
        catch (Exception)
        {
            Clear();
        }
    }

    private void Clear()
    {
        _artKey = null;
        HasMedia = false;
        Playing = false;
        Title = "Nothing playing";
        Artist = "Play music or a video in any app";
        Art = null;
        Source = "Now playing";
        Changed?.Invoke();
    }

    private static async Task<BitmapImage?> LoadArt(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var input = stream.AsStreamForRead();
            var memory = new MemoryStream();
            await input.CopyToAsync(memory);
            memory.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = memory;
            bitmap.DecodePixelWidth = 150;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string SourceName(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return "Now playing";
        if (appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(appId);
            return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : "Now playing";
        }

        return ShellNative.AppName(appId, null, "Now playing");
    }
}

/// <summary>Small building blocks shared by the widgets.</summary>
internal static class WidgetUi
{
    /// <summary>A borderless icon button with a soft round hover highlight.</summary>
    public static Button IconButton(string glyph, string tip, Action click, double size = 15, double width = 34, double height = 26)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = DockTheme.Icons, FontSize = size, Foreground = Brushes.White },
            Width = width,
            Height = height,
            ToolTip = tip,
            Focusable = false,
            Cursor = Cursors.Hand,
            Template = FlatButton(),
        };
        button.Click += (_, _) => click();
        return button;
    }

    public static void SetGlyph(Button button, string glyph) => ((TextBlock)button.Content).Text = glyph;

    public static ControlTemplate FlatButton()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, DockTheme.Brush(0x33FFFFFF), "Bd"));
        template.Triggers.Add(hover);
        return template;
    }

    /// <summary>Launches a pinned app, with a friendly message if it can't be opened.</summary>
    public static void Launch(DockPin pin)
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
    }

    public static ImageSource? Icon(DockPin pin, int size) => pin.Kind == PinKind.AppId
        ? ShellNative.Icon("shell:AppsFolder\\" + pin.Target, size)
        : ShellNative.Icon(Environment.ExpandEnvironmentVariables(pin.Target), size);
}
