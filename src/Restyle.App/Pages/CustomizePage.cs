using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Restyle.App.Controls;
using Restyle.App.Docking;
using Restyle.App.Services;
using Restyle.Core;
using Restyle.Core.Dock;
using Restyle.Core.Settings;
using ThemeMode = Restyle.Core.Settings.ThemeMode;

namespace Restyle.App.Pages;

/// <summary>
/// How Windows looks: light/dark, colors, wallpaper, lock screen picture, desktop icons and mouse pointer.
/// Every change applies right away (Windows itself is the live preview) and is undoable.
/// </summary>
public sealed class CustomizePage : SettingsPage
{
    private static readonly (string Hex, string Name)[] Swatches =
    [
        ("#0078D4", "Windows blue"), ("#3584E4", "GNOME blue"), ("#3DAEE9", "Plasma blue"), ("#00B294", "Teal"),
        ("#26A269", "Green"), ("#E5A50A", "Yellow"), ("#E66100", "Orange"), ("#E01B24", "Red"),
        ("#E3008C", "Pink"), ("#9141AC", "Purple"), ("#5E6B7D", "Slate"), ("#986A44", "Brown"),
    ];

    private const string WindowsWallpaper = @"%SystemRoot%\Web\Wallpaper\Windows\img0.jpg";
    private const string WindowsLockScreen = @"%SystemRoot%\Web\Screen\img100.jpg";

    public CustomizePage()
        : base("Appearance", "Changes apply right away. Hover over any option for a plain-language explanation. Changed your mind? Use Undo last change.")
    {
        Section("Light and dark");
        Combo(Get<AppsThemeSetting>(), (ThemeMode.Light, "Light"), (ThemeMode.Dark, "Dark"));
        Combo(Get<SystemThemeSetting>(), (ThemeMode.Light, "Light"), (ThemeMode.Dark, "Dark"));

        Section("Colors");
        Root.Children.Add(AccentCard());
        Switch(Get<AccentOnTitleBarsSetting>());
        Switch(Get<AccentOnTaskbarSetting>());
        Switch(Get<TransparencySetting>());

        Section("Wallpaper");
        Root.Children.Add(WallpaperCard());
        Switch(Get<DesktopIconsSetting>());

        Section("Lock screen");
        Root.Children.Add(LockScreenCard());
        Root.Children.Add(Pointer("Add a calendar, weather, your note and world times to the lock screen", "Widgets", "widgets"));

        Section("Mouse pointer");
        Root.Children.Add(CursorCard());

        Refresh();
    }

    // ---- Accent color ------------------------------------------------------------------

    private Border AccentCard()
    {
        var setting = Get<AccentColorSetting>();

        var sample = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 0) };
        var hex = new TextBox { Width = 92, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = Ui.Tip("Type any color as #RRGGBB, e.g. #3584E4.") };
        var use = Ui.Button("Use", (_, _) => UseHex(setting, hex.Text));
        use.Margin = new Thickness(8, 0, 0, 0);
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(sample);
        header.Children.Add(hex);
        header.Children.Add(use);

        var swatches = new WrapPanel { Margin = new Thickness(0, 12, 0, 4) };
        var swatchBorders = new List<(string Hex, Border Ring)>();
        foreach (var (color, name) in Swatches)
        {
            var ring = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(2),
                Child = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(6), Background = Brush(color) },
            };
            var button = new Button
            {
                Style = (Style)FindResource("LinkButton"),
                Content = ring,
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = Ui.Tip($"{name} ({color})"),
            };
            button.Click += (_, _) => Apply(setting, JsonValue.Create(color));
            swatches.Children.Add(button);
            swatchBorders.Add((color, ring));
        }

        var auto = new CheckBox { Style = (Style)FindResource("Switch"), Margin = new Thickness(0, 6, 0, 0) };
        auto.Click += (_, _) =>
        {
            var current = Read<AccentColor>(setting);
            Apply(setting, Json(auto.IsChecked == true ? AccentColor.Auto : current with { Automatic = false }));
        };
        var autoRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(auto, Dock.Right);
        autoRow.Children.Add(auto);
        autoRow.Children.Add(Ui.Text("Pick a color from my wallpaper automatically (updates the next time the wallpaper changes)", "Hint"));

        var below = new StackPanel();
        below.Children.Add(swatches);
        below.Children.Add(autoRow);

        OnRefresh(() =>
        {
            var current = Read<AccentColor>(setting);
            hex.Text = current.Color;
            sample.Background = Brush(current.Color);
            auto.IsChecked = current.Automatic;
            foreach (var (color, ring) in swatchBorders)
            {
                var selected = !current.Automatic && color.Equals(current.Color, StringComparison.OrdinalIgnoreCase);
                if (selected)
                    ring.SetResourceReference(Border.BorderBrushProperty, "TextFillColorPrimaryBrush");
                else
                    ring.BorderBrush = Brushes.Transparent;
            }
        });

        return Row(setting, header, below);
    }

    private static void UseHex(ISetting setting, string text)
    {
        try
        {
            var (r, g, b) = ColorMath.Parse(text);
            Apply(setting, JsonValue.Create(ColorMath.ToHex(r, g, b)));
        }
        catch (FormatException ex)
        {
            MainWindow.Instance.ShowMessage(ex.Message, MessageKind.Warning);
        }
    }

    private static SolidColorBrush Brush(string hex)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch (FormatException)
        {
            return new SolidColorBrush(Colors.Gray);
        }
    }

    // ---- Wallpaper ---------------------------------------------------------------------

    private Border WallpaperCard()
    {
        var setting = Get<WallpaperSetting>();

        var fit = new ComboBox { MinWidth = 140, ToolTip = Ui.Tip("How the picture fits your screen. 'Fill' works best for most pictures.") };
        foreach (var f in Enum.GetValues<WallpaperFit>())
            fit.Items.Add(new ComboBoxItem { Content = f.ToString(), Tag = f });
        fit.SelectionChanged += (_, _) =>
        {
            if (!Loading && fit.SelectedItem is ComboBoxItem { Tag: WallpaperFit f })
                Apply(setting, Json(Read<Wallpaper>(setting) with { Fit = f }));
        };

        var tiles = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var rings = new List<(string File, Border Ring)>();

        void AddTile(string label, ImageSource? image, string pathForSetting, string fileForMatch)
        {
            var ring = Tile(label, image, () => Apply(setting, Json(new Wallpaper(pathForSetting, CurrentFit()))), tiles);
            rings.Add((fileForMatch, ring));
        }

        foreach (var w in S.Wallpapers.All)
            AddTile(w.Name, S.Wallpapers.Thumbnail(WallpaperLibrary.Prefix + w.Id), WallpaperLibrary.Prefix + w.Id, S.Wallpapers.FileFor(w));
        var windows = Environment.ExpandEnvironmentVariables(WindowsWallpaper);
        if (File.Exists(windows))
            AddTile("Windows", S.Wallpapers.Thumbnail(windows), WindowsWallpaper, windows);
        Tile("Browse…", null, BrowseWallpaper, tiles);

        var current = Ui.Text("", "Hint", new Thickness(0, 4, 0, 0));
        var below = new StackPanel();
        below.Children.Add(tiles);
        below.Children.Add(current);

        OnRefresh(() =>
        {
            var value = Read<Wallpaper>(setting);
            fit.SelectedItem = fit.Items.Cast<ComboBoxItem>().First(i => (WallpaperFit)i.Tag == value.Fit);
            current.Text = value.Path.Length > 0 ? $"Current: {value.Path}" : "Current: none (solid color)";
            foreach (var (file, ring) in rings)
            {
                if (string.Equals(file, value.Path, StringComparison.OrdinalIgnoreCase))
                    ring.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
                else
                    ring.BorderBrush = Brushes.Transparent;
            }
        });

        return Row(setting, fit, below);

        WallpaperFit CurrentFit() => fit.SelectedItem is ComboBoxItem { Tag: WallpaperFit f } ? f : WallpaperFit.Fill;

        void BrowseWallpaper()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose a wallpaper",
                Filter = "Pictures|*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
                Apply(setting, Json(new Wallpaper(dialog.FileName, CurrentFit())));
        }
    }

    // ---- Lock screen -------------------------------------------------------------------

    private Border LockScreenCard()
    {
        var setting = Get<LockScreenSetting>();
        var spotlight = Ui.Button("Use Windows Spotlight", (_, _) => Apply(setting, JsonValue.Create(LockScreenSetting.Spotlight)),
            tip: "Go back to Windows' daily changing pictures.");
        spotlight.Margin = new Thickness(0);

        var tiles = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var rings = new List<(string File, Border Ring)>();
        foreach (var (path, name) in S.Wallpapers.LockScreens)
        {
            var value = path;
            var ring = Tile(name, S.Wallpapers.Thumbnail(path), () => Apply(setting, JsonValue.Create(value)), tiles);
            rings.Add((S.Wallpapers.LockFileFor(path), ring));
        }

        var windows = Environment.ExpandEnvironmentVariables(WindowsLockScreen);
        if (File.Exists(windows))
            rings.Add((windows, Tile("Windows", S.Wallpapers.Thumbnail(windows), () => Apply(setting, JsonValue.Create(WindowsLockScreen)), tiles)));
        Tile("Browse…", null, BrowseLockScreen, tiles);

        var current = Ui.Text("", "Hint", new Thickness(0, 4, 0, 0));
        var below = new StackPanel();
        below.Children.Add(tiles);
        below.Children.Add(current);

        OnRefresh(() =>
        {
            var value = Read<string>(setting);
            current.Text = $"Current: {(value == LockScreenSetting.Spotlight ? "Windows Spotlight (changes daily)" : value)}";
            foreach (var (file, ring) in rings)
            {
                if (string.Equals(file, value, StringComparison.OrdinalIgnoreCase))
                    ring.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
                else
                    ring.BorderBrush = Brushes.Transparent;
            }
        });

        return Row(setting, spotlight, below);

        void BrowseLockScreen()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose a lock screen picture",
                Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp|All files|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
                Apply(setting, JsonValue.Create(dialog.FileName));
        }
    }

    /// <summary>A clickable wallpaper thumbnail. Returns the highlight ring so it can mark the current one.</summary>
    private Border Tile(string label, ImageSource? image, Action click, Panel parent)
    {
        UIElement picture = image is null
            ? new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
            : new Image { Source = image, Stretch = Stretch.UniformToFill };

        var frame = new Border
        {
            Width = 128,
            Height = 72,
            CornerRadius = new CornerRadius(5),
            ClipToBounds = true,
            Child = picture,
        };
        frame.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
        var ring = new Border { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(7), Padding = new Thickness(2), Child = frame, BorderBrush = Brushes.Transparent };

        var stack = new StackPanel();
        stack.Children.Add(ring);
        stack.Children.Add(Ui.Text(label, "Hint", new Thickness(4, 2, 0, 0)));

        var button = new Button { Style = (Style)FindResource("LinkButton"), Content = stack, Margin = new Thickness(0, 0, 8, 8), ToolTip = Ui.Tip(label) };
        button.Click += (_, _) => click();
        parent.Children.Add(button);
        return ring;
    }

    // ---- Cursors -----------------------------------------------------------------------

    private Border CursorCard()
    {
        var setting = Get<CursorSchemeSetting>();
        var combo = new ComboBox { MinWidth = 220 };
        combo.SelectionChanged += (_, _) =>
        {
            if (!Loading && combo.SelectedItem is string name)
                Apply(setting, JsonValue.Create(name));
        };

        var import = Ui.Button("Import a cursor pack from a folder…", (_, _) => ImportCursors(setting),
            tip: "Pick a folder with .cur/.ani files (for example an unzipped download). Restyle copies them to its own folder; nothing in Windows is replaced.");
        import.Margin = new Thickness(0, 12, 0, 0);
        import.HorizontalAlignment = HorizontalAlignment.Left;

        OnRefresh(() =>
        {
            var currentName = setting.Describe(Read<CursorScheme>(setting));
            var names = CursorSchemeSetting.ListSchemes(S.Catalog.Context.Registry).ToList();
            if (!names.Contains(currentName, StringComparer.OrdinalIgnoreCase))
                names.Insert(0, currentName);
            combo.ItemsSource = names;
            combo.SelectedItem = names.First(n => n.Equals(currentName, StringComparison.OrdinalIgnoreCase));
        });

        return Row(setting, combo, import);
    }

    private void ImportCursors(ISetting setting)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the folder with your cursor files" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            var result = CursorPackImporter.Import(S.Catalog.Context.Registry, dialog.FolderName, S.DataDirectory);
            Apply(setting, JsonValue.Create(result.SchemeName));
            if (result.MissingRoles.Count > 0)
            {
                MainWindow.Instance.ShowMessage(
                    $"Imported \"{result.SchemeName}\" with {result.MappedCount} pointers. The pack didn't include {result.MissingRoles.Count} pointer(s) ({string.Join(", ", result.MissingRoles)}), so Windows' standard ones are used for those.",
                    MessageKind.Info);
            }
        }
        catch (Exception ex)
        {
            MainWindow.Instance.ShowMessage($"Couldn't import that folder: {ex.Message}", MessageKind.Error);
        }
    }
}
