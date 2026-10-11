using System.IO;

namespace Restyle.App.Services;

/// <summary>A small local log of unexpected errors (%LOCALAPPDATA%\Restyle\error.log), kept under 256 KB.</summary>
public static class ErrorLog
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Restyle", "error.log");

    public static void Write(Exception? ex)
    {
        if (ex is null)
            return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (File.Exists(Path) && new FileInfo(Path).Length > 256 * 1024)
                File.Delete(Path);
            var mode = Environment.GetCommandLineArgs().Contains("--dock") ? "dock" : "app";
            File.AppendAllText(Path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ({mode}) {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never cause a second failure.
        }
    }
}
