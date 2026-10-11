using System.IO;
using System.Windows;
using Restyle.App.Docking;
using Restyle.App.Services;

namespace Restyle.App;

public partial class App : Application
{
    private const string ShowEvent = @"Local\Restyle.App.Show";
    private static readonly string OpenPageFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Restyle", "open-page");
    private static Mutex? _instance;

    /// <summary>When Restyle is opened again, bring this window to the front (on the page that was asked for).</summary>
    private static void ListenForReopen(MainWindow window)
    {
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
        new Thread(() =>
        {
            while (true)
            {
                show.WaitOne();
                string? page = null;
                try
                {
                    page = File.ReadAllText(OpenPageFile).Trim();
                    File.Delete(OpenPageFile);
                }
                catch (Exception)
                {
                }

                window.Dispatcher.BeginInvoke(() => window.BringToFront(page));
            }
        }) { IsBackground = true, Name = "Reopen listener" }.Start();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Write unexpected errors to %LOCALAPPDATA%\Restyle\error.log (stays on this PC) instead of
        // disappearing silently; a hiccup in one widget shouldn't take the whole dock down.
        var isDock = e.Args.Contains("--dock", StringComparer.OrdinalIgnoreCase);
        DispatcherUnhandledException += (_, args) =>
        {
            ErrorLog.Write(args.Exception);
            args.Handled = true;

            // An error while closing (e.g. an old copy whose program file was replaced by an update) must not
            // leave a windowless Restyle running in the background: just exit.
            if (!isDock && MainWindow is { } main && PresentationSource.FromVisual(main) is null)
            {
                Environment.Exit(0);
                return;
            }
            if (!isDock)
                MessageBox.Show($"Something went wrong: {args.Exception.Message}", "Restyle", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ErrorLog.Write(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write(args.Exception);
            args.SetObserved();
        };

        // Started by an older copy that's handing over after an update: let it finish closing first.
        UpdateCheck.WaitForReplacedProcess(e.Args);

        // Restyle draws simple, mostly static UI. Software rendering looks identical and avoids
        // loading the GPU driver's Direct3D stack, which on its own costs 25-70 MB per process.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        if (e.Args.Contains("--dock", StringComparer.OrdinalIgnoreCase) || DockAutoStart.LaunchedAtSignIn())
        {
            DockHost.Run(this);
            return;
        }

        // "--page <name>" (from the dock and widget menus): open straight on that page.
        var pageIndex = Array.FindIndex(e.Args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        var page = pageIndex >= 0 && pageIndex + 1 < e.Args.Length ? e.Args[pageIndex + 1].ToLowerInvariant() : null;

        // One settings window at a time: several copies would each keep their own (stale) view of the
        // settings and undo history. Opening Restyle again just brings the existing window forward.
        _instance = new Mutex(true, @"Local\Restyle.App", out var isFirst);
        if (!isFirst)
        {
            // Tell the open window which page to show (e.g. "widgets" from a widget's menu), then step aside.
            try
            {
                File.WriteAllText(OpenPageFile, page ?? "");
            }
            catch (Exception)
            {
            }

            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent))
                show.Set();
            Shutdown();
            return;
        }

        // Windows 11 Fluent look that follows light/dark mode automatically.
        ThemeMode = ThemeMode.System;

        bool firstRun;
        try
        {
            // Safety net: before the user changes anything, remember how things were.
            var services = AppServices.Current;
            firstRun = services.Snapshots.EnsureOriginal(services.Engine);

            // Settings added after the original snapshot was taken: the lock screen hasn't been touched by
            // Restyle yet, so its current picture is the original; before Restyle there was no dock.
            services.Snapshots.AddMissingToOriginal(["lockScreen", "dock"], id => id == "dock"
                ? System.Text.Json.Nodes.JsonValue.Create(false)
                : services.Catalog.Find(id)?.ReadJson());
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Restyle couldn't save a backup of your current settings, so it won't make any changes.\n\n{(ex.InnerException ?? ex).Message}",
                "Restyle", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow(page);
        MainWindow = window;
        window.Show();
        ListenForReopen(window);

        if (firstRun)
        {
            window.ShowMessage(
                "Welcome! Restyle saved a copy of your current settings, so you can always go back with Backups > Restore my original settings.",
                MessageKind.Success);
        }
    }
}
