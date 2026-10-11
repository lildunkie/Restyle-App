using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Restyle.App.Controls;
using Restyle.App.Dialogs;
using Restyle.App.Services;
using Restyle.Core.Presets;

namespace Restyle.App.Pages;

/// <summary>Home screen: one card per preset. Clicking a card shows what will change before applying.</summary>
public sealed class PresetsPage : UserControl, IPage
{
    private readonly StackPanel _root = new() { Margin = new Thickness(28, 20, 28, 28) };

    public PresetsPage()
    {
        Content = new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Refresh();
    }

    private static AppServices S => AppServices.Current;

    public void Refresh()
    {
        _root.Children.Clear();
        _root.Children.Add(Ui.Text("Presets", "PageTitle"));
        _root.Children.Add(Ui.Text(
            "Pick a look. You'll see exactly what will change before anything is applied, and you can always undo it.",
            "PageSubtitle"));

        var presets = S.Presets.LoadAll();
        var current = S.Engine.Capture();

        _root.Children.Add(CardList(presets.Where(p => p.IsBuiltIn), current));

        _root.Children.Add(Ui.Text("Your presets", "SectionHeader", new Thickness(2, 24, 0, 8)));
        var mine = presets.Where(p => !p.IsBuiltIn).ToList();
        _root.Children.Add(mine.Count > 0
            ? CardList(mine, current)
            : Ui.Text("You haven't saved any yet. Set things up the way you like on the Appearance, Taskbar & Start, Dock and Widgets pages, then save it here as a preset.",
                "Hint", new Thickness(2, 0, 0, 8)));

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(Ui.Button("Save my current look as a preset…", SaveCurrent_Click, accent: true,
            tip: "Saves all your current Restyle settings as a preset you can re-apply or share."));
        actions.Children.Add(Ui.Button("Import a preset…", Import_Click,
            tip: "Add a preset .json file someone shared with you."));
        actions.Children.Add(Ui.Button("Open presets folder", OpenFolder_Click,
            tip: "Presets are small JSON files. You can edit them in Notepad; changes show up here when you return to this page."));
        _root.Children.Add(actions);

        foreach (var error in S.Presets.LoadErrors)
            _root.Children.Add(Ui.Text($"Couldn't read preset {error}", "Hint", new Thickness(2, 8, 0, 0)));
    }

    private WrapPanel CardList(IEnumerable<Preset> presets, Dictionary<string, JsonNode?> current)
    {
        var panel = new WrapPanel();
        foreach (var preset in presets)
        {
            var values = Merge(current, preset);
            var body = new StackPanel();
            body.Children.Add(new PresetPreview { Values = values, Margin = new Thickness(0, 0, 0, 10) });
            body.Children.Add(Ui.Text(preset.Name, "BodyStrongTextBlockStyle"));
            var desc = Ui.Text(preset.Description, "Hint", new Thickness(0, 2, 0, 0));
            desc.Height = 48;
            desc.TextTrimming = TextTrimming.WordEllipsis;
            body.Children.Add(desc);

            var card = new Button
            {
                Style = (Style)FindResource("CardButton"),
                Content = body,
                Width = 262,
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 12, 12),
                ToolTip = Ui.Tip($"{preset.Description}\n\nClick to see what will change."),
            };
            card.Click += (_, _) => Open(preset, values);
            panel.Children.Add(card);
        }

        return panel;
    }

    internal static Dictionary<string, JsonNode?> Merge(Dictionary<string, JsonNode?> current, Preset preset)
    {
        var values = new Dictionary<string, JsonNode?>(current);
        foreach (var (id, value) in preset.Settings)
            values[id] = value;
        return values;
    }

    private void Open(Preset preset, Dictionary<string, JsonNode?> values)
    {
        var dialog = new PresetDialog(preset, values) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();

        switch (dialog.Outcome)
        {
            case PresetDialog.Result.Apply:
                MainWindow.Instance.Run(() => S.Engine.Apply(dialog.Plan, preset.Name),
                    $"Applied \"{preset.Name}\". Don't like it? Click Undo last change.");
                break;
            case PresetDialog.Result.Deleted:
                MainWindow.Instance.ShowMessage($"Deleted preset \"{preset.Name}\".", MessageKind.Info);
                Refresh();
                break;
        }
    }

    private void SaveCurrent_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Ask(Window.GetWindow(this), "Save as preset",
            "Give your preset a name:", "My setup");
        if (name is null)
            return;

        try
        {
            S.Presets.SaveUserPreset(new Preset
            {
                Name = name,
                Description = $"Saved on {DateTime.Now:d}.",
                Settings = S.Engine.Capture(),
            });
            MainWindow.Instance.ShowMessage($"Saved \"{name}\". It's under Your presets.", MessageKind.Success);
        }
        catch (Exception ex)
        {
            MainWindow.Instance.ShowMessage($"Couldn't save the preset: {ex.Message}", MessageKind.Error);
        }

        Refresh();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Restyle preset (*.json)|*.json", Title = "Import a preset" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            var preset = S.Presets.Import(dialog.FileName);
            MainWindow.Instance.ShowMessage($"Imported \"{preset.Name}\". Click it to see what it changes.", MessageKind.Success);
        }
        catch (Exception ex)
        {
            MainWindow.Instance.ShowMessage($"That file isn't a valid Restyle preset: {ex.Message}", MessageKind.Error);
        }

        Refresh();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(S.Presets.UserDirectory);
        MainWindow.OpenUri(S.Presets.UserDirectory);
    }
}
