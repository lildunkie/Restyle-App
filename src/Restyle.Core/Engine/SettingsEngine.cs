using System.Text.Json.Nodes;
using Restyle.Core.Settings;

namespace Restyle.Core.Engine;

/// <summary>A single setting that would change, with before/after values.</summary>
public sealed record PlannedChange(ISetting Setting, JsonNode? Before, JsonNode? After)
{
    public string BeforeText => Setting.DescribeJson(Before);

    public string AfterText => Setting.DescribeJson(After);
}

/// <summary>What applying a set of values would do — shown to the user before a preset is applied.</summary>
public sealed class ChangePlan
{
    public List<PlannedChange> Changes { get; } = [];

    /// <summary>Settings in the target that this version of Windows doesn't support (skipped).</summary>
    public List<ISetting> Unsupported { get; } = [];

    /// <summary>Keys in the target JSON that don't match any known setting (ignored).</summary>
    public List<string> UnknownIds { get; } = [];

    public bool IsEmpty => Changes.Count == 0;

    public bool NeedsExplorerRestart => Changes.Any(c => c.Setting.RequiresExplorerRestart);
}

public sealed record SettingFailure(ISetting Setting, string Message, string? SettingsUri = null);

public sealed class ApplyResult
{
    public List<PlannedChange> Applied { get; } = [];

    public List<SettingFailure> Failures { get; } = [];

    public bool NeedsExplorerRestart => Applied.Any(c => c.Setting.RequiresExplorerRestart);
}

/// <summary>
/// Applies changes, broadcasts them once, and records them for undo.
/// All UI changes go through here so every change is undoable.
/// </summary>
public sealed class SettingsEngine(SettingsCatalog catalog, ChangeHistory history)
{
    public SettingsCatalog Catalog { get; } = catalog;

    public ChangeHistory History { get; } = history;

    /// <summary>Works out which of the target values actually differ from the current state.</summary>
    public ChangePlan Plan(IReadOnlyDictionary<string, JsonNode?> target)
    {
        var plan = new ChangePlan();
        foreach (var (id, value) in target)
        {
            var setting = Catalog.Find(id);
            if (setting is null)
            {
                plan.UnknownIds.Add(id);
                continue;
            }

            if (!setting.IsSupported)
            {
                plan.Unsupported.Add(setting);
                continue;
            }

            JsonNode? before;
            try
            {
                before = setting.ReadJson();
            }
            catch (Exception)
            {
                before = null; // Can't read it (rare); still allow applying.
            }

            if (!setting.IsSameJson(before, value))
                plan.Changes.Add(new PlannedChange(setting, before, value?.DeepClone()));
        }

        return plan;
    }

    public ApplyResult Apply(ChangePlan plan, string label) => Apply(plan, label, recordUndo: true);

    /// <summary>Convenience for a single control on the Customize page.</summary>
    public ApplyResult Set(string settingId, JsonNode? value, string? label = null)
    {
        var setting = Catalog.Find(settingId) ?? throw new KeyNotFoundException(settingId);
        var plan = Plan(new Dictionary<string, JsonNode?> { [settingId] = value });
        return Apply(plan, label ?? $"Change {setting.Name.ToLowerInvariant()}");
    }

    /// <summary>Reverts the most recent change batch. Returns null if there's nothing to undo.</summary>
    public ApplyResult? UndoLast()
    {
        var batch = History.Pop();
        if (batch is null)
            return null;

        var target = new Dictionary<string, JsonNode?>();
        foreach (var entry in Enumerable.Reverse(batch.Changes))
            target.TryAdd(entry.SettingId, entry.Before);

        return Apply(Plan(target), batch.Label, recordUndo: false);
    }

    /// <summary>Reads every supported setting — used for snapshots and "save as preset".</summary>
    public Dictionary<string, JsonNode?> Capture()
    {
        var values = new Dictionary<string, JsonNode?>();
        foreach (var setting in Catalog.All.Where(s => s.IsSupported))
        {
            try
            {
                values[setting.Id] = setting.ReadJson();
            }
            catch (Exception)
            {
                // Skip anything unreadable rather than failing the whole snapshot.
            }
        }

        return values;
    }

    private ApplyResult Apply(ChangePlan plan, string label, bool recordUndo)
    {
        var result = new ApplyResult();
        foreach (var change in plan.Changes)
        {
            try
            {
                change.Setting.ApplyJson(change.After);
                result.Applied.Add(change);
            }
            catch (SettingBlockedException ex)
            {
                result.Failures.Add(new SettingFailure(change.Setting, ex.Message, ex.SettingsUri));
            }
            catch (Exception ex)
            {
                result.Failures.Add(new SettingFailure(change.Setting, ex.Message));
            }
        }

        foreach (var area in result.Applied.SelectMany(c => c.Setting.BroadcastAreas).Distinct())
            Catalog.Context.System.BroadcastSettingChange(area);

        if (recordUndo && result.Applied.Count > 0)
        {
            History.Push(new ChangeBatch(label, DateTimeOffset.Now,
                result.Applied.Select(c => new ChangeEntry(c.Setting.Id, c.Before?.DeepClone(), c.After?.DeepClone())).ToList()));
        }

        return result;
    }
}
