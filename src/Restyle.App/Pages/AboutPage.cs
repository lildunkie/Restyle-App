using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Restyle.App.Controls;
using Restyle.App.Services;

namespace Restyle.App.Pages;

public sealed class AboutPage : UserControl
{
    public AboutPage()
    {
        var s = AppServices.Current;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        var root = new StackPanel { Margin = new Thickness(28, 20, 28, 28), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        root.Children.Add(Ui.Text("About Restyle", "PageTitle"));
        root.Children.Add(Ui.Text($"Version {version} · Running on {s.Os.DisplayName}", "PageSubtitle"));

        root.Children.Add(Ui.Card(Paragraphs(
            ("How it works",
             "Restyle only changes the same per-user settings you'd find in Windows Settings (colors, taskbar, wallpaper, mouse pointers). " +
             "It never modifies or replaces Windows system files, doesn't install themes or patches, never needs administrator rights, " +
             "and doesn't run in the background: when you close this window, it's gone."),
            ("Safety",
             "The first time you opened Restyle it saved your original settings. Every change is recorded so you can undo it, " +
             "and Backups > Restore my original settings puts everything back."),
            ("Your version of Windows",
             s.Os.IsWindows11
                 ? "All features are available."
                 : "Some taskbar options (alignment, Widgets) only exist on Windows 11 and are turned off here."),
            ("Wallpapers",
             "The built-in wallpapers are original artwork drawn by Restyle itself, free to use for anything."))));

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Ui.Button("Open Restyle's data folder", (_, _) => OpenFolder(s.DataDirectory),
            tip: "Backups, your presets, history and generated wallpapers live here."));
#if !STORE
        // Store apps are installed to a protected folder, so this is only offered in the download version.
        buttons.Children.Add(Ui.Button("Open built-in presets folder", (_, _) => OpenFolder(s.Presets.BuiltInDirectory),
            tip: "The presets that ship with Restyle. Edit them with any text editor."));
#endif
        root.Children.Add(buttons);
    }

    private static StackPanel Paragraphs(params (string Title, string Body)[] items)
    {
        var panel = new StackPanel();
        foreach (var (title, body) in items)
        {
            panel.Children.Add(Ui.Text(title, "BodyStrongTextBlockStyle", new Thickness(0, 6, 0, 2)));
            panel.Children.Add(Ui.Text(body, "Hint", new Thickness(0, 0, 0, 8)));
        }

        return panel;
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        MainWindow.OpenUri(path);
    }
}
