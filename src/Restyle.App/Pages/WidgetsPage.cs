using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Restyle.App.Controls;
using Restyle.App.Docking;
using Restyle.App.Services;
using Restyle.Core.Dock;

namespace Restyle.App.Pages;

/// <summary>All the widgets: on the desktop (style, layout, cities), their weather and news, and on the lock screen.</summary>
public sealed class WidgetsPage : SettingsPage
{
    private readonly TextBox _city = new() { Width = 200, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _feed = new() { Width = 260, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _weatherStatus = Ui.Text("", "Hint");
    private readonly TextBlock _newsStatus = Ui.Text("", "Hint");

    public WidgetsPage()
        : base("Widgets",
            "Clocks, weather, media, calendar and more, on your desktop and on the lock screen. " +
            "Desktop widgets run with the Restyle dock and sit behind your windows.")
    {
        DesktopSection();
        WeatherAndNewsSection();
        LockScreenSection();
        Refresh();
    }

    public override void Refresh()
    {
        base.Refresh();
        var w = Config.Widgets;
        _city.Text = w.WeatherCity ?? "";
        _weatherStatus.Text = w.WeatherEnabled
            ? $"Showing weather for {w.WeatherCity}. Data by Open-Meteo.com."
            : "Off. Type a city (e.g. \"Austin, Texas\" or \"Paris\") and click Set city. Uses the internet.";
        _feed.Text = w.NewsFeed ?? "";
        _newsStatus.Text = w.NewsFeed is null
            ? "Off: the clock shows Wi-Fi, battery and volume instead. Paste an RSS feed address to show headlines (uses the internet)."
            : "Showing headlines from this feed. Click a headline to open it in your browser.";
    }

    // ---- Desktop ------------------------------------------------------------------------

    private void DesktopSection()
    {
        Section("On the desktop");
        Root.Children.Add(Row("Show desktop widgets", "They sit on your desktop behind your windows and stay visible with Show desktop (Windows + D).",
            DockSwitch(c => c.ShowWidgets, (c, v) => c.ShowWidgets = v)));

        Root.Children.Add(Row("Style",
            "Tiles: world clock, big clock and a column of tiles. Glass clock: a big centered clock with media and volume pills. " +
            "Corners: big date and clock in the corners, music bars and an app column. Each style remembers your own arrangement.",
            DockChoice([(WidgetConfig.PanelLayout, "Tiles"), (WidgetConfig.GlassLayout, "Glass clock"), (WidgetConfig.CornersLayout, "Corners")],
                c => c.Widgets.Layout, (c, v) => c.Widgets.Layout = v)));

        Root.Children.Add(Row("Liquid glass", "Draw the widgets as frosted glass that shows a soft blur of your wallpaper.",
            DockSwitch(c => c.Widgets.Glass, (c, v) => c.Widgets.Glass = v)));

        var editButtons = new StackPanel { Orientation = Orientation.Horizontal };
        editButtons.Children.Add(Ui.Button("Edit on the desktop", (_, _) => EditOnDesktop(), accent: true,
            tip: "Drag widgets anywhere, resize them with − and +, hide them, or add more."));
        editButtons.Children.Add(Ui.Button("Reset layout", (_, _) => UpdateDock(c => c.Widgets.ResetPlacements(), refresh: true),
            tip: "Put every widget back where the style places it, at normal size."));
        Root.Children.Add(Row("Move and resize widgets",
            "Opens edit mode on your desktop. You can also right-click the dock or any widget and choose Edit widgets.", editButtons));

        var list = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (id, name) in WidgetConfig.AllWidgets)
        {
            var box = new CheckBox { Content = name.Split(" (")[0], Tag = id, Margin = new Thickness(0, 4, 18, 4), MinWidth = 200 };
            box.Click += (_, _) => UpdateDock(c => c.Widgets.Placement(id).Visible = box.IsChecked == true);
            OnRefresh(() => box.IsChecked = Config.Widgets.IsVisible(id));
            list.Children.Add(box);
        }

        var listPanel = new StackPanel();
        listPanel.Children.Add(Ui.Text("Show these widgets", "BodyStrongTextBlockStyle"));
        listPanel.Children.Add(Ui.Text("Every widget works with every style; the style just picks which are on to begin with.", "Hint"));
        listPanel.Children.Add(list);
        Root.Children.Add(Ui.Card(listPanel));

        Root.Children.Add(Row("World clock cities", "Choose which cities the world clock map shows (and the lock screen's world times).",
            Ui.Button("Choose cities…", (_, _) => ChooseCities())));
    }

    private void EditOnDesktop()
    {
        if (!DockHost.IsRunning)
        {
            MainWindow.Instance.ShowMessage("Turn the dock on first: the desktop widgets run with it.", MessageKind.Info, "Go to Dock", () => MainWindow.Instance.ShowPage("dock"));
            return;
        }

        DockHost.RequestWidgetEditing();
        MainWindow.Instance.ShowMessage("Your widgets are in edit mode on the desktop. Drag them anywhere, then click Done.", MessageKind.Success);
        Window.GetWindow(this)!.WindowState = WindowState.Minimized; // out of the way so you can see them
    }

    private void ChooseCities()
    {
        var chosen = DockConfig.Load(DockHost.ConfigPath).Widgets.Cities.Select(c => c.Name).ToHashSet();
        var list = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Width = 520 };
        foreach (var city in WidgetConfig.CityChoices())
            list.Children.Add(new CheckBox { Content = city.Name, Tag = city, IsChecked = chosen.Contains(city.Name), Width = 170, Margin = new Thickness(0, 3, 0, 3) });

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(Ui.Text("World clock cities", "SubtitleTextBlockStyle"));
        root.Children.Add(Ui.Text("Pick up to 12. Times come from Windows, so this works offline.", "Hint", new Thickness(0, 4, 0, 0)));
        root.Children.Add(list);
        var dialog = new Window
        {
            Title = "World clock cities",
            Owner = Window.GetWindow(this),
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = root,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var save = Ui.Button("Save", (_, _) =>
        {
            var picked = list.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => (WorldCity)b.Tag).Take(12).ToList();
            UpdateDock(c => c.Widgets.Cities = picked.Count > 0 ? picked : WidgetConfig.DefaultCities());
            dialog.Close();
            MainWindow.Instance.ShowMessage($"The world clock now shows {picked.Count} cities.", MessageKind.Success);
        }, accent: true);
        save.IsDefault = true;
        var cancel = Ui.Button("Cancel", (_, _) => dialog.Close());
        cancel.IsCancel = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);
        dialog.ShowDialog();
    }

    // ---- Weather and news ---------------------------------------------------------------

    private void WeatherAndNewsSection()
    {
        Section("Weather and news");
        var weatherBox = new StackPanel { Orientation = Orientation.Horizontal };
        weatherBox.Children.Add(_city);
        weatherBox.Children.Add(Ui.Button("Set city", (_, _) => _ = SetCity(), tip: "Looks up the city on Open-Meteo.com (free, no account)."));
        weatherBox.Children.Add(Ui.Button("Weather off", (_, _) => UpdateDock(c =>
        {
            c.Widgets.WeatherCity = null;
            c.Widgets.WeatherLatitude = c.Widgets.WeatherLongitude = null;
        }, refresh: true)));
        Root.Children.Add(Row("Weather", _weatherStatus, weatherBox));

        Root.Children.Add(Row("Temperature", "Automatic uses °F in the US and °C elsewhere, based on your region.",
            DockChoice(new (bool?, string)[] { (null, "Automatic"), (true, "°F"), (false, "°C") }, c => c.Widgets.Fahrenheit, (c, v) => c.Widgets.Fahrenheit = v)));

        var newsBox = new StackPanel { Orientation = Orientation.Horizontal };
        newsBox.Children.Add(_feed);
        newsBox.Children.Add(Ui.Button("Use feed", (_, _) => SetFeed(), tip: "Any RSS or Atom news feed address."));
        newsBox.Children.Add(Ui.Button("News off", (_, _) => UpdateDock(c => c.Widgets.NewsFeed = null, refresh: true)));
        Root.Children.Add(Row("Headlines under the clock", _newsStatus, newsBox));

        Root.Children.Add(Ui.Text(
            "Privacy: weather and news are the only parts of Restyle that use the internet, and both stay off until you set them here. " +
            "Weather sends only the city's coordinates to Open-Meteo.com; news only downloads the feed you enter. " +
            "Everything else (clocks, calendar, note, media, volume, brightness) works on your PC only.",
            "Hint", new Thickness(2, 4, 0, 0)));
    }

    private async Task SetCity()
    {
        var query = _city.Text.Trim();
        if (query.Length == 0)
        {
            MainWindow.Instance.ShowMessage("Type a city name first, for example \"Chicago\" or \"Lyon, France\".", MessageKind.Info);
            return;
        }

        _weatherStatus.Text = "Looking it up…";
        try
        {
            var place = await WidgetNet.FindPlace(query);
            if (place is null)
            {
                MainWindow.Instance.ShowMessage($"Couldn't find a city called \"{query}\". Check the spelling, or add the state or country after a comma.", MessageKind.Warning);
                Refresh();
                return;
            }

            UpdateDock(c =>
            {
                c.Widgets.WeatherCity = place.Name;
                c.Widgets.WeatherLatitude = Math.Round(place.Latitude, 2);
                c.Widgets.WeatherLongitude = Math.Round(place.Longitude, 2);
            }, refresh: true);
            MainWindow.Instance.ShowMessage($"The weather widgets now show {place.Name}.", MessageKind.Success);
        }
        catch (Exception ex)
        {
            MainWindow.Instance.ShowMessage($"Couldn't reach the weather service: {ex.Message}", MessageKind.Error);
            Refresh();
        }
    }

    private void SetFeed()
    {
        var url = _feed.Text.Trim();
        if (!WidgetNet.IsWebLink(url))
        {
            MainWindow.Instance.ShowMessage("Paste a full feed address starting with https://", MessageKind.Info);
            return;
        }

        UpdateDock(c => c.Widgets.NewsFeed = url, refresh: true);
        MainWindow.Instance.ShowMessage("Headlines will appear under the desktop clock in a moment.", MessageKind.Success);
    }

    // ---- Lock screen ---------------------------------------------------------------------

    /// <summary>Widgets drawn onto the lock screen picture: on/off, which ones, and which side.</summary>
    private void LockScreenSection()
    {
        Section("On the lock screen");
        var setting = Get<LockScreenSetting>();
        var enabled = SwitchBox(() => { });
        var options = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var boxes = new List<CheckBox>();
        foreach (var (id, name) in LockWidgetConfig.All)
        {
            var box = new CheckBox { Content = name, Tag = id, Margin = new Thickness(0, 2, 18, 2) };
            boxes.Add(box);
            options.Children.Add(box);
        }

        var side = new ComboBox { MinWidth = 120, Margin = new Thickness(6, 0, 0, 0) };
        side.Items.Add(new ComboBoxItem { Content = "Right side", Tag = "right" });
        side.Items.Add(new ComboBoxItem { Content = "Left side", Tag = "left" });
        options.Children.Add(side);

        Root.Children.Add(Row("Show widgets on the lock screen", Ui.Text(
            "Calendar, weather, your Today note and world times, drawn onto your lock screen picture on frosted-glass panels. " +
            "Windows doesn't let apps put live widgets on the lock screen, so Restyle redraws the picture every 15 minutes while the dock runs. " +
            "The clock and date are Windows' own.", "Hint"), enabled, options));
        Root.Children.Add(Pointer("Choose the lock screen picture the widgets go on", "Appearance", "appearance"));

        void Save(Action<LockWidgetConfig> change)
        {
            if (Loading)
                return;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var config = DockConfig.Load(DockHost.ConfigPath);
                var wasOn = config.LockWidgets.Enabled;
                change(config.LockWidgets);
                if (config.LockWidgets.Enabled && !wasOn)
                {
                    // Remember the plain picture the widgets go on (Spotlight's daily pictures can't be drawn on).
                    var plain = setting.Read();
                    if (plain == LockScreenSetting.Spotlight || plain.Length == 0)
                    {
                        MainWindow.Instance.ShowMessage("Pick a lock screen picture first: widgets can't be drawn on Windows Spotlight's changing pictures.",
                            MessageKind.Info, "Go to Appearance", () => MainWindow.Instance.ShowPage("appearance"));
                        Refresh();
                        return;
                    }

                    config.LockWidgets.Base = plain;
                }

                config.Save(DockHost.ConfigPath);
                if (config.LockWidgets.Enabled)
                    LockScreenWidgets.RefreshNow(config, S.Catalog.Context.ResolvePath);
                else if (wasOn)
                    LockScreenWidgets.RestorePlain(config, S.Catalog.Context.ResolvePath);
                MainWindow.Instance.ShowMessage(config.LockWidgets.Enabled
                    ? "Lock screen updated. Press Windows + L to see it."
                    : "Lock screen widgets are off; your plain lock screen picture is back.", MessageKind.Success);
                Refresh();
            }
            catch (Exception ex)
            {
                MainWindow.Instance.ShowMessage($"Couldn't update the lock screen: {ex.Message}", MessageKind.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        enabled.Click += (_, _) => Save(lw => lw.Enabled = enabled.IsChecked == true);
        foreach (var box in boxes)
            box.Click += (_, _) => Save(lw => lw.Widgets = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList());
        side.SelectionChanged += (_, _) =>
        {
            if (side.SelectedItem is ComboBoxItem { Tag: string s })
                Save(lw => lw.Side = s);
        };

        OnRefresh(() =>
        {
            var lw = Config.LockWidgets;
            enabled.IsChecked = lw.Enabled;
            foreach (var box in boxes)
            {
                box.IsChecked = lw.Widgets.Contains((string)box.Tag);
                box.IsEnabled = lw.Enabled;
            }

            side.SelectedItem = side.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == lw.Side);
            side.IsEnabled = lw.Enabled;
        });
    }
}
