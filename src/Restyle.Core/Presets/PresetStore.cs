namespace Restyle.Core.Presets;

/// <summary>
/// Loads built-in presets from the Presets folder next to the app (editable without recompiling),
/// falling back to copies embedded in the exe, plus the user's own presets from %LOCALAPPDATA%.
/// </summary>
public sealed class PresetStore(string builtInDirectory, string userDirectory, Func<IEnumerable<string>>? embeddedFallback = null)
{
    /// <summary>Presets are tiny; anything bigger than this is not a preset.</summary>
    public const long MaxFileSize = 256 * 1024;

    public string BuiltInDirectory { get; } = builtInDirectory;

    public string UserDirectory { get; } = userDirectory;

    /// <summary>Problems found while loading (shown to the user, never fatal).</summary>
    public List<string> LoadErrors { get; } = [];

    public IReadOnlyList<Preset> LoadAll()
    {
        LoadErrors.Clear();
        var builtIn = LoadFolder(BuiltInDirectory, isBuiltIn: true);
        if (builtIn.Count == 0 && embeddedFallback is not null)
        {
            foreach (var json in embeddedFallback())
            {
                try
                {
                    var p = Preset.Parse(json);
                    p.IsBuiltIn = true;
                    builtIn.Add(p);
                }
                catch (Exception ex)
                {
                    LoadErrors.Add($"Built-in preset: {ex.Message}");
                }
            }
        }

        var user = LoadFolder(UserDirectory, isBuiltIn: false);
        return builtIn.OrderBy(p => p.Order).ThenBy(p => p.Name)
            .Concat(user.OrderBy(p => p.Name))
            .ToList();
    }

    public Preset SaveUserPreset(Preset preset)
    {
        Directory.CreateDirectory(UserDirectory);
        var path = UniquePath(UserDirectory, Slug(preset.Name));
        File.WriteAllText(path, preset.ToJson());
        preset.FilePath = path;
        preset.IsBuiltIn = false;
        return preset;
    }

    /// <summary>Validates a preset file and copies it into the user's preset folder.</summary>
    public Preset Import(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("File not found.", path);
        if (info.Length > MaxFileSize)
            throw new InvalidDataException("That file is too large to be a Restyle preset.");

        var preset = Preset.Parse(File.ReadAllText(path));
        return SaveUserPreset(preset);
    }

    public static void Export(Preset preset, string path) => File.WriteAllText(path, preset.ToJson());

    public void Delete(Preset preset)
    {
        if (!preset.IsBuiltIn && preset.FilePath is not null &&
            preset.FilePath.StartsWith(UserDirectory, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(preset.FilePath);
        }
    }

    private List<Preset> LoadFolder(string directory, bool isBuiltIn)
    {
        var list = new List<Preset>();
        if (!Directory.Exists(directory))
            return list;

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                if (new FileInfo(file).Length > MaxFileSize)
                    throw new InvalidDataException("file is too large");
                var preset = Preset.Parse(File.ReadAllText(file));
                preset.FilePath = file;
                preset.IsBuiltIn = isBuiltIn;
                list.Add(preset);
            }
            catch (Exception ex)
            {
                LoadErrors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return list;
    }

    /// <summary>A file-name-safe version of a preset name, e.g. "My Setup!" → "my-setup".</summary>
    public static string Slug(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "preset" : slug;
    }

    private static string UniquePath(string directory, string slug)
    {
        var path = Path.Combine(directory, slug + ".json");
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(directory, $"{slug}-{i}.json");
        return path;
    }
}
