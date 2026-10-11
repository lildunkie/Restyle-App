using Restyle.Core.Dock;

namespace Restyle.Core.Tests;

public class DockTests
{
    [Fact]
    public void Config_RoundTripsAndDefaultsArePinned()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "dock.json");
        var config = new DockConfig { IconSize = 64, Magnify = false };
        config.Pinned.Add(new DockPin("Notepad", @"C:\Windows\notepad.exe"));
        config.Save(path);

        var loaded = DockConfig.Load(path);

        Assert.Equal(64, loaded.IconSize);
        Assert.False(loaded.Magnify);
        Assert.Equal(4, loaded.Pinned.Count);
        Assert.True(loaded.IsPinned(DockConfig.KeyFor(null, @"c:\windows\NOTEPAD.EXE")));
        Assert.True(loaded.IsPinned(DockConfig.KeyFor("msedge", null)));
    }

    [Fact]
    public void Config_FallsBackToDefaultsWhenCorruptOrOdd()
    {
        using var dir = new TempDir();
        Assert.Equal(48, DockConfig.Load(dir.File("bad.json", "{ nope")).IconSize);
        Assert.Equal(48, DockConfig.Load(dir.File("odd.json", """{ "iconSize": 3 }""")).IconSize);
        Assert.Equal(3, DockConfig.Load(Path.Combine(dir.Path, "missing.json")).Pinned.Count);
    }

    [Fact]
    public void Key_PrefersAppIdOverPath()
    {
        Assert.Equal("app:msedge", DockConfig.KeyFor("MSEdge", @"C:\x\msedge.exe"));
        Assert.Equal(@"path:c:\x\a.exe", DockConfig.KeyFor(null, @"C:\x\A.exe"));
        Assert.Equal("", DockConfig.KeyFor(null, null));
    }

    [Fact]
    public void Magnify_PeaksUnderMouseAndFadesOut()
    {
        Assert.Equal(DockMath.MaxScale, DockMath.Scale(0, 48), 3);
        Assert.True(DockMath.Scale(30, 48) > DockMath.Scale(60, 48));
        Assert.Equal(DockMath.Scale(-30, 48), DockMath.Scale(30, 48));
        Assert.Equal(1, DockMath.Scale(48 * 2.5, 48));
    }

    [Fact]
    public void Startup_WritesAndRemovesRunEntry()
    {
        var registry = new FakeRegistry();
        Assert.False(DockStartup.IsEnabled(registry));

        DockStartup.Enable(registry, @"C:\Apps\Restyle.exe");
        Assert.Equal("\"C:\\Apps\\Restyle.exe\" --dock", registry.Get(default, DockStartup.RunKey, DockStartup.ValueName));

        DockStartup.Disable(registry);
        Assert.False(DockStartup.IsEnabled(registry));
    }
}
