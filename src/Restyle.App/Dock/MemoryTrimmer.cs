using System.Runtime;
using System.Windows.Threading;

namespace Restyle.App.Docking;

/// <summary>
/// Keeps the long-running dock process lean: after startup and whenever things go quiet, it
/// compacts the .NET heap and returns the freed memory to Windows. (It deliberately doesn't force
/// Windows to page out in-use memory; that only makes the numbers look smaller and the dock slower.)
/// </summary>
internal static class MemoryTrimmer
{
    private static DispatcherTimer? _timer;
    private static DateTime _lastActivity = DateTime.Now;

    /// <summary>Call on user interaction; trimming waits until the dock has been idle for a while.</summary>
    public static void Touch() => _lastActivity = DateTime.Now;

    public static void Start(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.ApplicationIdle, (_, _) =>
        {
            if (DateTime.Now - _lastActivity > TimeSpan.FromSeconds(20))
                TrimNow();
        }, dispatcher);

        // First trim shortly after startup, once icons and windows have been built.
        var first = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher) { Interval = TimeSpan.FromSeconds(8) };
        first.Tick += (_, _) =>
        {
            first.Stop();
            TrimNow();
        };
        first.Start();
    }

    public static void TrimNow()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }
}
