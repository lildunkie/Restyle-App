using System.Text.Json.Nodes;
using Microsoft.Win32;
using Restyle.Core.Settings;

namespace Restyle.Core.Tests;

public class SettingsTests
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Accent = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    [Fact]
    public void Toggle_UsesWindowsDefault_WhenValueMissing()
    {
        var rig = new TestRig();
        Assert.True(rig.Catalog.Get<TransparencySetting>().Read());
        Assert.True(rig.Catalog.Get<TaskbarTaskViewSetting>().Read());
        Assert.False(rig.Catalog.Get<AccentOnTitleBarsSetting>().Read());
    }

    [Fact]
    public void Toggle_RoundTrips()
    {
        var rig = new TestRig();
        var setting = rig.Catalog.Get<TransparencySetting>();

        setting.Apply(false);

        Assert.False(setting.Read());
        Assert.Equal(0, rig.Registry.Get(default, Personalize, "EnableTransparency"));
        Assert.Equal(RegistryValueKind.DWord, rig.Registry.KindOf(Personalize, "EnableTransparency"));
    }

    [Fact]
    public void DesktopIcons_IsStoredInverted_AndNeedsExplorerRestart()
    {
        var rig = new TestRig();
        var setting = rig.Catalog.Get<DesktopIconsSetting>();

        setting.Apply(false);

        Assert.Equal(1, rig.Registry.Get(default, Advanced, "HideIcons"));
        Assert.False(setting.Read());
        Assert.True(setting.RequiresExplorerRestart);
    }

    [Fact]
    public void Theme_WritesZeroForDark()
    {
        var rig = new TestRig();
        var apps = rig.Catalog.Get<AppsThemeSetting>();

        Assert.Equal(ThemeMode.Light, apps.Read()); // missing value = light
        apps.Apply(ThemeMode.Dark);

        Assert.Equal(0, rig.Registry.Get(default, Personalize, "AppsUseLightTheme"));
        Assert.Equal(ThemeMode.Dark, apps.Read());
    }

    [Fact]
    public void Theme_ParsesStringValuesFromJson()
    {
        var rig = new TestRig();
        var setting = rig.Catalog.Find("systemTheme")!;

        setting.ApplyJson(JsonValue.Create("dark"));

        Assert.Equal("\"dark\"", setting.ReadJson()!.ToJsonString());
        Assert.Equal("Dark", setting.DescribeJson(JsonValue.Create("dark")));
    }

    [Theory]
    [InlineData(17763, false, false)] // Windows 10 1809: no system theme, no Win11 taskbar
    [InlineData(19045, true, false)]  // Windows 10 22H2
    [InlineData(22631, true, true)]   // Windows 11 23H2
    public void Support_DependsOnWindowsBuild(int build, bool systemTheme, bool win11Taskbar)
    {
        var rig = new TestRig(build);
        Assert.Equal(systemTheme, rig.Catalog.Get<SystemThemeSetting>().IsSupported);
        Assert.Equal(win11Taskbar, rig.Catalog.Get<TaskbarAlignmentSetting>().IsSupported);
        Assert.Equal(win11Taskbar, rig.Catalog.Get<TaskbarWidgetsSetting>().IsSupported);
        Assert.True(rig.Catalog.Get<TaskbarTaskViewSetting>().IsSupported);
        Assert.Equal(win11Taskbar ? null : "Only available on Windows 11.",
            rig.Catalog.Get<TaskbarAlignmentSetting>().UnsupportedReason);
    }

    [Fact]
    public void TaskbarAlignment_MissingValueMeansCenter()
    {
        var rig = new TestRig();
        var setting = rig.Catalog.Get<TaskbarAlignmentSetting>();

        Assert.Equal(TaskbarAlignment.Center, setting.Read());
        setting.Apply(TaskbarAlignment.Left);
        Assert.Equal(0, rig.Registry.Get(default, Advanced, "TaskbarAl"));
    }

    [Fact]
    public void AutoHide_GoesThroughAppBarApi()
    {
        var rig = new TestRig();
        rig.Catalog.Get<TaskbarAutoHideSetting>().Apply(true);
        Assert.True(rig.System.AutoHide);
    }

    [Fact]
    public void Search_RoundTripsAllModes()
    {
        var rig = new TestRig();
        var setting = rig.Catalog.Get<TaskbarSearchSetting>();
        foreach (var mode in Enum.GetValues<SearchButtonMode>())
        {
            setting.Apply(mode);
            Assert.Equal(mode, setting.Read());
        }
    }

    [Fact]
    public void Widgets_BlockedByWindows_GivesFriendlyErrorWithSettingsLink()
    {
        var rig = new TestRig();
        rig.Registry.Blocked.Add($@"{Advanced}\TaskbarDa");

        var ex = Assert.Throws<SettingBlockedException>(() => rig.Catalog.Get<TaskbarWidgetsSetting>().Apply(false));
        Assert.Equal("ms-settings:taskbar", ex.SettingsUri);
    }

    [Fact]
    public void Accent_WritesAbgrAndPalette()
    {
        var rig = new TestRig();
        var setting = rig.Catalog.Get<AccentColorSetting>();

        setting.Apply(new AccentColor("#3584E4"));

        // 0xAABBGGRR
        Assert.Equal(unchecked((int)0xFFE48435), rig.Registry.Get(default, Accent, "AccentColorMenu"));
        var palette = Assert.IsType<byte[]>(rig.Registry.Get(default, Accent, "AccentPalette"));
        Assert.Equal(32, palette.Length);
        Assert.Equal(new byte[] { 0x35, 0x84, 0xE4 }, palette[12..15]); // base color is entry 4
        Assert.Equal(0, rig.Registry.Get(default, @"Control Panel\Desktop", "AutoColorization"));

        var read = setting.Read();
        Assert.Equal("#3584E4", read.Color);
        Assert.False(read.Automatic);
        Assert.Equal(Convert.ToHexString(palette), read.Palette);
    }

    [Fact]
    public void Accent_ShorthandPresetValueMatchesFullStoredValue()
    {
        var rig = new TestRig();
        var setting = rig.Catalog.Get<AccentColorSetting>();
        setting.Apply(new AccentColor("#3584E4"));

        Assert.True(setting.IsSameJson(setting.ReadJson(), JsonValue.Create("#3584e4")));
        Assert.False(setting.IsSameJson(setting.ReadJson(), JsonValue.Create("#FF0000")));
    }

    [Fact]
    public void Accent_RejectsInvalidColor()
    {
        var rig = new TestRig();
        Assert.ThrowsAny<Exception>(() => rig.Catalog.Find("accentColor")!.ApplyJson(JsonValue.Create("blue")));
    }

    [Fact]
    public void AccentPalette_IsLightestToDarkest()
    {
        var p = AccentPaletteBuilder.Build(0x00, 0x78, 0xD4);
        int Brightness(int entry) => p[entry * 4] + p[entry * 4 + 1] + p[entry * 4 + 2];

        for (var i = 0; i < 6; i++)
            Assert.True(Brightness(i) > Brightness(i + 1), $"entry {i} should be lighter than {i + 1}");
    }

    [Fact]
    public void Wallpaper_SetsFileAndFitStyle()
    {
        using var dir = new TempDir();
        var file = dir.File("wall.png");
        var rig = new TestRig();
        var setting = rig.Catalog.Get<WallpaperSetting>();

        setting.Apply(new Wallpaper(file, WallpaperFit.Center));

        Assert.Equal(file, rig.System.Wallpaper);
        Assert.Equal("0", rig.Registry.Get(default, @"Control Panel\Desktop", "WallpaperStyle"));
        Assert.Equal(new Wallpaper(file, WallpaperFit.Center), setting.Read());
    }

    [Fact]
    public void Wallpaper_ResolvesBuiltinPaths()
    {
        using var dir = new TempDir();
        var file = dir.File("gnome-blobs.png");
        var rig = new TestRig();
        rig.Context.ResolvePath = p => p == "builtin:gnome-blobs" ? file : p;

        rig.Catalog.Find("wallpaper")!.ApplyJson(JsonValue.Create("builtin:gnome-blobs"));

        Assert.Equal(file, rig.System.Wallpaper);
    }

    [Fact]
    public void Wallpaper_MissingFileThrowsAndLeavesWallpaperAlone()
    {
        var rig = new TestRig();
        var before = rig.System.Wallpaper;

        Assert.Throws<FileNotFoundException>(() =>
            rig.Catalog.Get<WallpaperSetting>().Apply(new Wallpaper(@"C:\does\not\exist.png")));
        Assert.Equal(before, rig.System.Wallpaper);
    }

    [Fact]
    public void Cursor_AppliesSystemSchemeByName()
    {
        var rig = new TestRig();
        rig.Registry.SetMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Control Panel\Cursors\Schemes",
            "Windows Black", @"%SystemRoot%\cursors\arrow_r.cur,%SystemRoot%\cursors\help_r.cur");
        var setting = rig.Catalog.Get<CursorSchemeSetting>();

        setting.Apply(new CursorScheme("Windows Black"));

        Assert.Equal(@"%SystemRoot%\cursors\arrow_r.cur", rig.Registry.Get(default, @"Control Panel\Cursors", "Arrow"));
        Assert.Equal(RegistryValueKind.ExpandString, rig.Registry.KindOf(@"Control Panel\Cursors", "Arrow"));
        Assert.Equal("", rig.Registry.Get(default, @"Control Panel\Cursors", "Wait"));
        Assert.Equal("Windows Black", setting.Read().Name);
        Assert.Equal(2, setting.Read().Source);
        Assert.Equal(1, rig.System.CursorReloads);
        Assert.Contains("Windows Black", CursorSchemeSetting.ListSchemes(rig.Registry));
    }

    [Fact]
    public void Cursor_WindowsDefaultWorksEvenIfNotRegistered()
    {
        var rig = new TestRig();
        rig.Catalog.Get<CursorSchemeSetting>().Apply(new CursorScheme("Windows Default"));
        Assert.Equal("", rig.Registry.Get(default, @"Control Panel\Cursors", "Arrow"));
    }

    [Fact]
    public void Cursor_UnknownSchemeFails()
    {
        var rig = new TestRig();
        Assert.Throws<KeyNotFoundException>(() =>
            rig.Catalog.Get<CursorSchemeSetting>().Apply(new CursorScheme("Nope")));
        Assert.Equal(0, rig.System.CursorReloads);
    }

    [Fact]
    public void Cursor_FullValueRestoresExactly()
    {
        var rig = new TestRig();
        var setting = rig.Catalog.Get<CursorSchemeSetting>();
        var custom = CursorSchemeSetting.Roles.ToDictionary(r => r, r => $@"C:\c\{r}.cur");

        setting.Apply(new CursorScheme("Mine", custom, 1));
        var json = setting.ReadJson();
        setting.Apply(new CursorScheme("Windows Default"));
        setting.ApplyJson(json);

        Assert.Equal(@"C:\c\Hand.cur", setting.Read().Cursors!["Hand"]);
    }
}
