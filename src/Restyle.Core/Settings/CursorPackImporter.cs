using System.Text.RegularExpressions;
using Microsoft.Win32;
using Restyle.Core.Platform;

namespace Restyle.Core.Settings;

/// <summary>
/// Imports a folder of .cur/.ani files as a per-user pointer scheme. Uses the pack's
/// install.inf when present (that's the standard format); otherwise guesses from file names.
/// Files are copied to %LOCALAPPDATA%\Restyle\Cursors, never into Windows folders.
/// </summary>
public static partial class CursorPackImporter
{
    // Keywords per role, checked first as exact file names, then (for longer words) as substrings.
    private static readonly Dictionary<string, string[]> Keywords = new()
    {
        ["Arrow"] = ["arrow", "normal", "pointer", "default", "select", "normal select"],
        ["Help"] = ["help", "question", "help select"],
        ["AppStarting"] = ["working", "appstarting", "background", "progress", "work", "working in background"],
        ["Wait"] = ["busy", "wait", "loading"],
        ["Crosshair"] = ["cross", "crosshair", "precision", "precision select"],
        ["IBeam"] = ["text", "ibeam", "beam", "text select"],
        ["NWPen"] = ["handwriting", "pen", "nwpen"],
        ["No"] = ["unavailable", "no", "not-allowed", "forbidden", "unavail", "not allowed"],
        ["SizeNS"] = ["vert", "ns", "sizens", "vertical", "vertical resize", "ns-resize"],
        ["SizeWE"] = ["horz", "we", "sizewe", "horizontal", "horizontal resize", "ew-resize"],
        ["SizeNWSE"] = ["dgn1", "nwse", "sizenwse", "diagonal1", "diag1", "diagonal resize 1", "nwse-resize"],
        ["SizeNESW"] = ["dgn2", "nesw", "sizenesw", "diagonal2", "diag2", "diagonal resize 2", "nesw-resize"],
        ["SizeAll"] = ["move", "sizeall", "all-scroll"],
        ["UpArrow"] = ["alternate", "up", "uparrow", "alternate select"],
        ["Hand"] = ["link", "hand", "link select"],
        ["Pin"] = ["pin", "location", "location select"],
        ["Person"] = ["person", "person select"],
    };

    public sealed record Result(string SchemeName, int MappedCount, IReadOnlyList<string> MissingRoles);

    public static Result Import(IRegistry registry, string folder, string dataDirectory, string? schemeName = null)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Folder not found: {folder}");

        var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cur", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ani", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (files.Count == 0)
            throw new InvalidOperationException("That folder has no cursor files (.cur or .ani).");

        var name = SanitizeName(schemeName ?? new DirectoryInfo(folder).Name);
        var mapping = MapFromInf(folder, files) ?? MapFromFileNames(files);
        if (!mapping.ContainsKey("Arrow"))
            throw new InvalidOperationException("Couldn't tell which file is the normal pointer. Make sure the pack includes an install.inf or a file named like 'Normal' or 'Arrow'.");

        var destination = Path.Combine(dataDirectory, "Cursors", name);
        Directory.CreateDirectory(destination);

        var installed = new Dictionary<string, string>();
        foreach (var (role, source) in mapping)
        {
            var target = Path.Combine(destination, Path.GetFileName(source));
            File.Copy(source, target, overwrite: true);
            installed[role] = target;
        }

        var list = string.Join(",", CursorSchemeSetting.Roles.Select(r => installed.GetValueOrDefault(r, "")));
        registry.Set(@"Control Panel\Cursors\Schemes", name, list, RegistryValueKind.ExpandString);

        var missing = CursorSchemeSetting.Roles.Where(r => !installed.ContainsKey(r)).ToList();
        return new Result(name, installed.Count, missing);
    }

    /// <summary>Reads the role order from the [Scheme.Reg] line and resolves %names% via [Strings].</summary>
    internal static Dictionary<string, string>? MapFromInf(string folder, IReadOnlyList<string> files)
    {
        var inf = Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).FirstOrDefault();
        if (inf is null)
            return null;

        var lines = File.ReadAllLines(inf);
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inStrings = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inStrings = line.Equals("[Strings]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inStrings || line.StartsWith(';'))
                continue;

            var eq = line.IndexOf('=');
            if (eq > 0)
                strings[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"');
        }

        var schemeLine = lines.FirstOrDefault(l => l.Contains(@"Cursors\Schemes", StringComparison.OrdinalIgnoreCase));
        if (schemeLine is null)
            return null;

        var quoted = QuotedRegex().Matches(schemeLine);
        if (quoted.Count == 0)
            return null;

        var parts = quoted[^1].Groups[1].Value.Split(',');
        var byName = files.GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>();
        for (var i = 0; i < parts.Length && i < CursorSchemeSetting.Roles.Length; i++)
        {
            var last = parts[i].Trim().Split('\\')[^1];
            if (last.StartsWith('%') && last.EndsWith('%') && last.Length > 2)
                last = strings.GetValueOrDefault(last[1..^1], "");
            if (byName.TryGetValue(Path.GetFileName(last), out var file))
                result[CursorSchemeSetting.Roles[i]] = file;
        }

        return result.Count > 0 ? result : null;
    }

    internal static Dictionary<string, string> MapFromFileNames(IReadOnlyList<string> files)
    {
        var result = new Dictionary<string, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Stem(string f) => Path.GetFileNameWithoutExtension(f).ToLowerInvariant().Replace('_', ' ');

        // Pass 1: exact names. Pass 2: substrings of 4+ characters (avoids "no" matching "normal").
        foreach (var exact in new[] { true, false })
        {
            foreach (var (role, words) in Keywords)
            {
                if (result.ContainsKey(role))
                    continue;

                var match = files.FirstOrDefault(f => !used.Contains(f) && words.Any(w =>
                    exact ? Stem(f) == w : w.Length >= 4 && Stem(f).Contains(w)));
                if (match is not null)
                {
                    result[role] = match;
                    used.Add(match);
                }
            }
        }

        return result;
    }

    private static string SanitizeName(string name)
    {
        var clean = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != ',').ToArray()).Trim();
        return clean.Length == 0 ? "Imported cursors" : clean;
    }

    [GeneratedRegex("\"([^\"]*)\"")]
    private static partial Regex QuotedRegex();
}
