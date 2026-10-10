using System.Text.Json;
using System.Text.Json.Nodes;

namespace Restyle.Core.Engine;

public sealed record ChangeEntry(string SettingId, JsonNode? Before, JsonNode? After);

/// <summary>One user action (a toggle, a preset, a restore) — undone as a unit.</summary>
public sealed record ChangeBatch(string Label, DateTimeOffset Time, List<ChangeEntry> Changes);

/// <summary>Undo stack, persisted to disk so "Undo last change" survives closing the app.</summary>
public sealed class ChangeHistory
{
    private readonly string? _filePath;
    private readonly int _capacity;
    private readonly List<ChangeBatch> _batches = [];

    public ChangeHistory(string? filePath = null, int capacity = 50)
    {
        _filePath = filePath;
        _capacity = capacity;

        if (_filePath is not null && File.Exists(_filePath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<List<ChangeBatch>>(File.ReadAllText(_filePath), RestyleJson.Options);
                if (loaded is not null)
                    _batches.AddRange(loaded);
            }
            catch (JsonException)
            {
                // A corrupt history file shouldn't stop the app; start fresh.
            }
        }
    }

    public event Action? Changed;

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<ChangeBatch> Batches => _batches;

    public ChangeBatch? Last => _batches.Count > 0 ? _batches[^1] : null;

    public void Push(ChangeBatch batch)
    {
        _batches.Add(batch);
        if (_batches.Count > _capacity)
            _batches.RemoveRange(0, _batches.Count - _capacity);
        Save();
    }

    public ChangeBatch? Pop()
    {
        var last = Last;
        if (last is not null)
        {
            _batches.RemoveAt(_batches.Count - 1);
            Save();
        }

        return last;
    }

    public void Clear()
    {
        _batches.Clear();
        Save();
    }

    private void Save()
    {
        if (_filePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_batches, RestyleJson.Options));
        }

        Changed?.Invoke();
    }
}
