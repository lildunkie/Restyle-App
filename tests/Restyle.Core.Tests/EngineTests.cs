using System.Text.Json.Nodes;
using Restyle.Core.Engine;
using Restyle.Core.Settings;

namespace Restyle.Core.Tests;

public class EngineTests
{
    private static Dictionary<string, JsonNode?> Values(params (string Id, object Value)[] values) =>
        values.ToDictionary(v => v.Id, v => (JsonNode?)JsonValue.Create(v.Value));

    [Fact]
    public void Set_AppliesAndRecordsUndo()
    {
        var rig = new TestRig();

        var result = rig.Engine.Set("transparency", JsonValue.Create(false));

        Assert.Single(result.Applied);
        Assert.Empty(result.Failures);
        Assert.False(rig.Catalog.Get<TransparencySetting>().Read());
        Assert.Single(rig.History.Batches);
    }

    [Fact]
    public void UndoLast_RestoresPreviousValue()
    {
        var rig = new TestRig();
        rig.Engine.Set("appsTheme", JsonValue.Create("dark"));

        var undo = rig.Engine.UndoLast();

        Assert.NotNull(undo);
        Assert.Equal(ThemeMode.Light, rig.Catalog.Get<AppsThemeSetting>().Read());
        Assert.Empty(rig.History.Batches);
        Assert.Null(rig.Engine.UndoLast());
    }

    [Fact]
    public void UndoLast_RevertsAWholePresetAtOnce()
    {
        var rig = new TestRig();
        rig.Engine.Set("transparency", JsonValue.Create(false));
        var plan = rig.Engine.Plan(Values(("appsTheme", "dark"), ("systemTheme", "dark"), ("taskbarAlignment", "left")));
        rig.Engine.Apply(plan, "GNOME-like");

        rig.Engine.UndoLast();

        Assert.Equal(ThemeMode.Light, rig.Catalog.Get<AppsThemeSetting>().Read());
        Assert.Equal(TaskbarAlignment.Center, rig.Catalog.Get<TaskbarAlignmentSetting>().Read());
        Assert.False(rig.Catalog.Get<TransparencySetting>().Read()); // earlier change untouched
    }

    [Fact]
    public void Plan_SkipsValuesThatAlreadyMatch()
    {
        var rig = new TestRig();
        var plan = rig.Engine.Plan(Values(("transparency", true), ("appsTheme", "dark")));

        var change = Assert.Single(plan.Changes);
        Assert.Equal("appsTheme", change.Setting.Id);
        Assert.Equal("Light", change.BeforeText);
        Assert.Equal("Dark", change.AfterText);
    }

    [Fact]
    public void Plan_ReportsUnsupportedAndUnknownSettings()
    {
        var rig = new TestRig(build: 19045); // Windows 10
        var plan = rig.Engine.Plan(Values(("taskbarAlignment", "left"), ("madeUp", 1), ("appsTheme", "dark")));

        Assert.Single(plan.Changes);
        Assert.Equal("taskbarAlignment", Assert.Single(plan.Unsupported).Id);
        Assert.Equal("madeUp", Assert.Single(plan.UnknownIds));
    }

    [Fact]
    public void Apply_NoChangesRecordsNothing()
    {
        var rig = new TestRig();
        rig.Engine.Set("transparency", JsonValue.Create(true));
        Assert.Empty(rig.History.Batches);
        Assert.Empty(rig.System.Broadcasts);
    }

    [Fact]
    public void Apply_BroadcastsEachAreaOnce()
    {
        var rig = new TestRig();
        var plan = rig.Engine.Plan(Values(
            ("appsTheme", "dark"), ("systemTheme", "dark"), ("transparency", false),
            ("taskbarAlignment", "left"), ("taskbarTaskView", false)));

        rig.Engine.Apply(plan, "test");

        Assert.Equal(new[] { "ImmersiveColorSet", "TraySettings" }, rig.System.Broadcasts);
    }

    [Fact]
    public void Apply_OneFailureDoesNotStopTheRest_AndOnlySuccessesAreUndoable()
    {
        var rig = new TestRig();
        rig.Registry.Blocked.Add(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDa");
        var plan = rig.Engine.Plan(Values(("taskbarWidgets", false), ("appsTheme", "dark")));

        var result = rig.Engine.Apply(plan, "test");

        var failure = Assert.Single(result.Failures);
        Assert.Equal("ms-settings:taskbar", failure.SettingsUri);
        Assert.Equal("appsTheme", Assert.Single(result.Applied).Setting.Id);
        Assert.Equal("appsTheme", Assert.Single(rig.History.Last!.Changes).SettingId);
    }

    [Fact]
    public void Apply_FlagsExplorerRestartOnlyWhenNeeded()
    {
        var rig = new TestRig();
        Assert.False(rig.Engine.Set("appsTheme", JsonValue.Create("dark")).NeedsExplorerRestart);
        Assert.True(rig.Engine.Set("desktopIcons", JsonValue.Create(false)).NeedsExplorerRestart);
        Assert.Equal(0, rig.System.ExplorerRestarts); // the engine never restarts Explorer by itself
    }

    [Fact]
    public void Capture_ReadsOnlySupportedSettings()
    {
        var win10 = new TestRig(build: 19045).Engine.Capture();
        var win11 = new TestRig().Engine.Capture();

        Assert.DoesNotContain("taskbarAlignment", win10.Keys);
        Assert.Contains("taskbarAlignment", win11.Keys);
        Assert.Contains("cursorScheme", win11.Keys);
    }

    [Fact]
    public void History_PersistsToDisk()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "history.json");
        var rig = new TestRig();
        var engine = new SettingsEngine(rig.Catalog, new ChangeHistory(path));
        engine.Set("appsTheme", JsonValue.Create("dark"), "Dark apps");

        var reloaded = new SettingsEngine(rig.Catalog, new ChangeHistory(path));
        Assert.Equal("Dark apps", reloaded.History.Last!.Label);
        reloaded.UndoLast();
        Assert.Equal(ThemeMode.Light, rig.Catalog.Get<AppsThemeSetting>().Read());
    }

    [Fact]
    public void History_IsCapped()
    {
        var history = new ChangeHistory(capacity: 3);
        for (var i = 0; i < 5; i++)
            history.Push(new ChangeBatch($"#{i}", DateTimeOffset.Now, []));

        Assert.Equal(new[] { "#2", "#3", "#4" }, history.Batches.Select(b => b.Label));
    }
}
