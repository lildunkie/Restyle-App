using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Restyle.App.Docking;

/// <summary>
/// Runs the dock as its own process ("Restyle.exe --dock") so the main window can stay closed.
/// Only one dock runs at a time; the main app stops it through a named event.
/// </summary>
public static class DockHost
{
    private const string MutexName = @"Local\Restyle.Dock";
    private const string QuitEventName = @"Local\Restyle.Dock.Quit";

    private static Mutex? _instance;

    public static string ConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Restyle", "dock.json");

    public static bool IsRunning
    {
        get
        {
            if (!Mutex.TryOpenExisting(MutexName, out var mutex))
                return false;
            mutex.Dispose();
            return true;
        }
    }

    public static void Run(Application app)
    {
        _instance = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            app.Shutdown(); // a dock is already running
            return;
        }

        var window = new DockWindow(ConfigPath);
        app.MainWindow = window;
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        MemoryTrimmer.Start(app.Dispatcher);

        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        new Thread(() =>
        {
            quit.WaitOne();
            app.Dispatcher.BeginInvoke(window.Close);
        }) { IsBackground = true, Name = "Dock quit listener" }.Start();
    }

    public static void Start() =>
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--dock") { UseShellExecute = false })?.Dispose();

    public static void Stop()
    {
        if (EventWaitHandle.TryOpenExisting(QuitEventName, out var quit))
        {
            quit.Set();
            quit.Dispose();
        }
    }
}
