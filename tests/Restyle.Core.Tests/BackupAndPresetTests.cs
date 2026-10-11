using System.Text.Json.Nodes;
using Restyle.Core.Backup;
using Restyle.Core.Presets;
using Restyle.Core.Settings;

namespace Restyle.Core.Tests;

public class BackupAndPresetTests
{
    private const string SamplePreset = """
        {
          // comments are allowed
          "name": "Test",
          "description": "d",
          "settings": {
            "appsTheme": "dark",
            "accentColor": "#3584E4",
            "taskbarAlignment": "left",
            "cursorScheme": "Windows Default",
          }
        }
        """;

    [Fact]
    public void Original_IsCapturedOnlyOnce()
    {
        using var dir = new TempDir();
        var rig = new TestRig();
        var store = new SnapshotStore(dir.Path);

        Assert.True(store.EnsureOriginal(rig.Engine));
        rig.Engine.Set("appsTheme", JsonValue.Create("dark"));
        Assert.False(store.EnsureOriginal(rig.Engine));

        Assert.Equal("\"light\"", store.LoadOriginal()!.Settings["appsTheme"]!.ToJsonString());
    }

    [Fact]
    public void RestoreOriginal_UndoesEverythingAPresetChanged()
    {
        using var dir = new TempDir();
        var rig = new TestRig();
        rig.Catalog.Get<AccentColorSetting>().Apply(new AccentColor("#0078D4"));
        var store = new SnapshotStore(dir.Path);
        store.EnsureOriginal(rig.Engine);
        var before = rig.Engine.Capture();

        var preset = Preset.Parse(SamplePreset);
        rig.Engine.Apply(rig.Engine.Plan(preset.Settings), preset.Name);
        Assert.Equal(ThemeMode.Dark, rig.Catalog.Get<AppsThemeSetting>().Read());

        var restore = rig.Engine.Plan(store.LoadOriginal()!.Settings);
        var result = rig.Engine.Apply(restore, "Restore original settings");

        Assert.Empty(result.Failures);
        var after = rig.Engine.Capture();
        foreach (var (id, value) in before)
            Assert.True(JsonNode.DeepEquals(value, after[id]), $"{id} was not restored");

        // The restore itself is undoable.
        Assert.Equal("Restore original settings", rig.History.Last!.Label);
    }

    [Fact]
    public void Backups_SaveListAndDelete()
    {
        using var dir = new TempDir();
        var rig = new TestRig();
        var store = new SnapshotStore(dir.Path);

        var saved = store.SaveBackup("Before experimenting", rig.Engine);
        var listed = Assert.Single(store.ListBackups());
        Assert.Equal("Before experimenting", listed.Name);

        store.DeleteBackup(saved);
        Assert.Empty(store.ListBackups());
    }

    [Fact]
    public void Preset_ParsesShorthandValues()
    {
        var preset = Preset.Parse(SamplePreset);
        var plan = new TestRig().Engine.Plan(preset.Settings);

        // cursorScheme is already "Windows Default", so only three settings actually change.
        Assert.Equal(3, plan.Changes.Count);
        Assert.DoesNotContain(plan.Changes, c => c.Setting.Id == "cursorScheme");
        Assert.Equal("#3584E4", preset.GetString("accentColor"));
        Assert.Contains(plan.Changes, c => c.Setting.Id == "accentColor" && c.AfterText == "#3584E4");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "name": "x", "settings": {} }""")]
    [InlineData("""{ "settings": { "appsTheme": "dark" } }""")]
    public void Preset_RejectsInvalidFiles(string json) =>
        Assert.Throws<InvalidDataException>(() => Preset.Parse(json));

    [Fact]
    public void Presets_ExportImportRoundTrip()
    {
        using var dir = new TempDir();
        var store = new PresetStore(Path.Combine(dir.Path, "builtin"), Path.Combine(dir.Path, "user"));
        var rig = new TestRig();
        var mine = store.SaveUserPreset(new Preset { Name = "My Setup!", Settings = rig.Engine.Capture() });
        Assert.EndsWith("my-setup.json", mine.FilePath);

        var exported = Path.Combine(dir.Path, "shared.json");
        PresetStore.Export(mine, exported);
        var imported = store.Import(exported);

        Assert.EndsWith("my-setup-2.json", imported.FilePath);
        Assert.Equal(2, store.LoadAll().Count);
        Assert.True(rig.Engine.Plan(imported.Settings).IsEmpty);
    }

    [Fact]
    public void Presets_FallBackToEmbeddedCopiesAndReportBadFiles()
    {
        using var dir = new TempDir();
        dir.File(@"user\broken.json", "{ not json");
        var store = new PresetStore(Path.Combine(dir.Path, "missing"), Path.Combine(dir.Path, "user"), () => [SamplePreset]);

        var all = store.LoadAll();

        Assert.True(Assert.Single(all).IsBuiltIn);
        Assert.Contains(store.LoadErrors, e => e.StartsWith("broken.json"));
    }

    [Fact]
    public void ShippedPresets_AreValidAndUseOnlyKnownSettings()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Presets");
        var files = Directory.GetFiles(folder, "*.json");
        Assert.True(files.Length >= 4, "expected the four built-in presets");

        var catalog = new TestRig().Catalog;
        foreach (var file in files)
        {
            var preset = Preset.Parse(File.ReadAllText(file));
            foreach (var (id, value) in preset.Settings)
            {
                // Dock and widget settings live in the app (they control the dock process); they're plain on/off values.
                if (id is "dock" or "dockHideTaskbar" or "dockTopBar" or "desktopWidgets" or "dockStartMenu" or "dockBar" or "widgetGlass")
                {
                    Assert.True(value?.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False,
                        $"{Path.GetFileName(file)}: '{id}' must be true or false");
                    continue;
                }

                if (id == "widgetLayout")
                {
                    Assert.True(value?.ToString() is "panel" or "glass" or "corners", $"{Path.GetFileName(file)}: unknown widgetLayout '{value}'");
                    continue;
                }

                if (id == "lockScreen")
                {
                    var path = value?.ToString() ?? "";
                    Assert.True(path == "spotlight" || path.StartsWith("builtin:") && path.EndsWith("-lock") || path.Contains('\\'),
                        $"{Path.GetFileName(file)}: lockScreen should be \"spotlight\", \"builtin:<wallpaper>-lock\" or a file path");
                    continue;
                }

                if (id is "dockStyle" or "dockPosition")
                {
                    var allowed = id == "dockStyle" ? Restyle.Core.Dock.DockConfig.Styles : Restyle.Core.Dock.DockConfig.Edges;
                    Assert.True(allowed.Contains(value?.ToString()), $"{Path.GetFileName(file)}: unknown {id} '{value}'");
                    continue;
                }

                var setting = catalog.Find(id);
                Assert.True(setting is not null, $"{Path.GetFileName(file)}: unknown setting '{id}'");
                setting.DescribeJson(value); // must not throw
                Assert.True(setting.IsSameJson(value, value), $"{Path.GetFileName(file)}: '{id}' value doesn't parse");
            }
        }
    }
}
