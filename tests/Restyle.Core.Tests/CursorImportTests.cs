using Restyle.Core.Platform;
using Restyle.Core.Settings;

namespace Restyle.Core.Tests;

public class CursorImportTests
{
    [Fact]
    public void Import_GuessesRolesFromFileNames()
    {
        using var pack = new TempDir();
        using var data = new TempDir();
        foreach (var f in new[] { "Normal.cur", "Busy.ani", "Text.cur", "Link.cur", "Handwriting.cur", "Unavailable.cur", "readme.txt" })
            pack.File(f);
        var registry = new FakeRegistry();

        var result = CursorPackImporter.Import(registry, pack.Path, data.Path, "Breeze");

        Assert.Equal("Breeze", result.SchemeName);
        Assert.Equal(6, result.MappedCount);
        var list = registry.GetString(@"Control Panel\Cursors\Schemes", "Breeze")!.Split(',');
        Assert.EndsWith(@"Breeze\Normal.cur", list[0]);      // Arrow
        Assert.EndsWith(@"Breeze\Busy.ani", list[3]);        // Wait
        Assert.EndsWith(@"Breeze\Handwriting.cur", list[6]); // NWPen
        Assert.EndsWith(@"Breeze\Unavailable.cur", list[7]); // No
        Assert.EndsWith(@"Breeze\Link.cur", list[14]);       // Hand
        Assert.True(File.Exists(list[0]));
        Assert.Contains("Breeze", CursorSchemeSetting.ListSchemes(registry));
    }

    [Fact]
    public void Import_PrefersInstallInf()
    {
        using var pack = new TempDir();
        using var data = new TempDir();
        pack.File("a.cur");
        pack.File("b.cur");
        pack.File("install.inf", """
            [Scheme.Reg]
            HKCU,"Control Panel\Cursors\Schemes","%SCHEME_NAME%",,"%10%\%CUR_DIR%\%pointer%,%10%\%CUR_DIR%\%help%"

            [Strings]
            CUR_DIR = "Cursors\Test"
            SCHEME_NAME = "Test"
            pointer = "b.cur"
            help = "a.cur"
            """);
        var registry = new FakeRegistry();

        CursorPackImporter.Import(registry, pack.Path, data.Path, "Test");

        var list = registry.GetString(@"Control Panel\Cursors\Schemes", "Test")!.Split(',');
        Assert.EndsWith("b.cur", list[0]);
        Assert.EndsWith("a.cur", list[1]);
    }

    [Fact]
    public void Import_FailsClearlyWithoutCursorFiles()
    {
        using var pack = new TempDir();
        pack.File("readme.txt");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            CursorPackImporter.Import(new FakeRegistry(), pack.Path, pack.Path));
        Assert.Contains("no cursor files", ex.Message);
    }

    [Fact]
    public void ImportedScheme_CanBeAppliedByName()
    {
        using var pack = new TempDir();
        using var data = new TempDir();
        pack.File("arrow.cur");
        var rig = new TestRig();
        CursorPackImporter.Import(rig.Registry, pack.Path, data.Path, "Mine");

        rig.Catalog.Get<CursorSchemeSetting>().Apply(new CursorScheme("Mine"));

        Assert.EndsWith(@"Mine\arrow.cur", rig.Registry.GetString(@"Control Panel\Cursors", "Arrow"));
        Assert.Equal(1, rig.Registry.GetDword(@"Control Panel\Cursors", "Scheme Source"));
    }
}
