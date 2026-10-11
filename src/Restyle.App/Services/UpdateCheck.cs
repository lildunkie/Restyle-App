using System.Diagnostics;
using System.IO;

namespace Restyle.App.Services;

/// <summary>
/// Notices when Restyle.exe has been replaced by a newer version while this copy is running, so the dock
/// can restart itself and the settings window can offer to. (An old copy left running after an update
/// can't load the parts of itself it hasn't used yet, and starts failing in odd ways.)
/// </summary>
public static class UpdateCheck
{
    private static readonly string? ExePath = Environment.ProcessPath;
    private static readonly string StartStamp = Stamp();
    private static string? _seen;

    /// <summary>
    /// True once a newer Restyle.exe is in place and has stopped changing (checked on two calls in a row,
    /// so a file that's still being copied is never started).
    /// </summary>
    public static bool UpdateReady()
    {
        var now = Stamp();
        if (now.Length == 0 || now == StartStamp)
        {
            _seen = null;
            return false;
        }

        if (now != _seen)
        {
            _seen = now; // first sighting: wait one more check to be sure the copy has finished
            return false;
        }

        return CanOpen();
    }

    /// <summary>Starts the new version, which waits for this process to exit before taking over.</summary>
    public static void Relaunch(string arguments) =>
        Process.Start(new ProcessStartInfo(ExePath!, $"{arguments} --replace {Environment.ProcessId}".Trim()) { UseShellExecute = false })?.Dispose();

    /// <summary>Called at startup: if we're replacing an older copy, wait (briefly) for it to finish closing.</summary>
    public static void WaitForReplacedProcess(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--replace", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out var pid))
            return;
        try
        {
            using var old = Process.GetProcessById(pid);
            if (!old.WaitForExit(10_000) && old.ProcessName.Equals("Restyle", StringComparison.OrdinalIgnoreCase))
            {
                old.Kill(); // the old copy got stuck closing; it has nothing unsaved, so end it
                old.WaitForExit(3_000);
            }
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (InvalidOperationException)
        {
            // Exited while we looked.
        }
    }

    private static string Stamp()
    {
        try
        {
            if (ExePath is null || !File.Exists(ExePath))
                return "";
            var info = new FileInfo(ExePath);
            return $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static bool CanOpen()
    {
        try
        {
            using var _ = new FileStream(ExePath!, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (IOException)
        {
            return false; // still being written
        }
    }
}
