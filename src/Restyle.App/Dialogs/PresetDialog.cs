using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Win32;
using Restyle.App.Controls;
using Restyle.App.Services;
using Restyle.Core.Engine;
using Restyle.Core.Presets;

namespace Restyle.App.Dialogs;

/// <summary>Shows a preset's preview and the exact list of changes, then applies on one click.</summary>
public sealed class PresetDialog : Window
{
    public enum Result
    {
        Cancel,
        Apply,
        Deleted,
    }

    private readonly Preset _preset;

    public PresetDialog(Preset preset, Dictionary<string, JsonNode?> values)
    {
        _preset = preset;
        Plan = AppServices.Current.Engine.Plan(preset.Settings);

        Title = preset.Name;
        Width = 580;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(24) };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var preview = new PresetPreview { Values = values, Margin = new Thickness(0, 0, 18, 0) };
        DockPanel.SetDock(preview, Dock.Left);
        header.Children.Add(preview);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Ui.Text(preset.Name, "SubtitleTextBlockStyle"));
        titles.Children.Add(Ui.Text(preset.Description, "Hint", new Thickness(0, 6, 0, 0)));
        header.Children.Add(titles);
        root.Children.Add(header);

        root.Children.Add(Ui.Text("What will change", "BodyStrongTextBlockStyle", new Thickness(0, 0, 0, 8)));
        root.Children.Add(Plan.IsEmpty
            ? Ui.Text("Nothing. Your PC already matches this preset.", "Hint")
            : ChangeList(Plan));

        if (Plan.Unsupported.Count > 0)
            root.Children.Add(Note("", $"Skipped on your version of Windows: {string.Join(", ", Plan.Unsupported.Select(s => s.Name))}."));
        if (Plan.UnknownIds.Count > 0)
            root.Children.Add(Note("", $"Ignored (not settings Restyle knows): {string.Join(", ", Plan.UnknownIds)}."));
        if (Plan.Changes.Any(c => c.Setting.Id == "desktopIcons" && c.After?.GetValueKind() == System.Text.Json.JsonValueKind.False))
        {
            var warning = Note("", "This hides ALL icons on your desktop (shortcuts, files, folders). Nothing is deleted. " +
                "To bring them back, right-click the desktop > View > Show desktop icons, or use Undo.");
            warning.SetValue(TextElement.FontWeightProperty, FontWeights.SemiBold);
            root.Children.Add(warning);
        }
        if (Plan.NeedsExplorerRestart)
            root.Children.Add(Note("", "Part of this needs Windows Explorer to restart. Restyle will ask you first."));

        var buttons = new DockPanel { Margin = new Thickness(0, 22, 0, 0), LastChildFill = false };
        var apply = Ui.Button("Apply", (_, _) => Close(Result.Apply), accent: true);
        apply.IsEnabled = !Plan.IsEmpty;
        apply.IsDefault = true;
        apply.Margin = new Thickness(8, 0, 0, 0);
        var cancel = Ui.Button("Cancel", (_, _) => Close(Result.Cancel));
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(apply, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        buttons.Children.Add(Ui.Button("Export…", Export_Click, tip: "Save this preset as a .json file to share or keep."));
        if (!preset.IsBuiltIn)
            buttons.Children.Add(Ui.Button("Delete", Delete_Click, tip: "Remove this preset from Restyle."));
        root.Children.Add(buttons);

        Content = root;
    }

    public ChangePlan Plan { get; }

    public Result Outcome { get; private set; } = Result.Cancel;

    private static Grid ChangeList(ChangePlan plan)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        foreach (var change in plan.Changes)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = grid.RowDefinitions.Count - 1;

            var name = Ui.Text(change.Setting.Name, margin: new Thickness(0, 3, 12, 3));
            name.ToolTip = Ui.Tip(change.Setting.Description);
            Grid.SetRow(name, row);
            grid.Children.Add(name);

            var values = new TextBlock { Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap };
            values.Inlines.Add(new System.Windows.Documents.Run(change.BeforeText)
            {
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextFillColorSecondaryBrush"),
            });
            values.Inlines.Add(new System.Windows.Documents.Run("  →  "));
            values.Inlines.Add(new System.Windows.Documents.Run(change.AfterText) { FontWeight = FontWeights.SemiBold });
            Grid.SetRow(values, row);
            Grid.SetColumn(values, 1);
            grid.Children.Add(values);
        }

        return grid;
    }

    private static UIElement Note(string glyph, string text)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Margin = new Thickness(0, 2, 8, 0),
        };
        panel.Children.Add(icon);
        panel.Children.Add(Ui.Text(text, "Hint"));
        return panel;
    }

    private void Close(Result result)
    {
        Outcome = result;
        Close();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Restyle preset (*.json)|*.json",
            FileName = PresetStore.Slug(_preset.Name) + ".json",
            Title = "Export preset",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            PresetStore.Export(_preset, dialog.FileName);
            MessageBox.Show(this, $"Saved to {dialog.FileName}", "Exported", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, $"Delete the preset \"{_preset.Name}\"? This doesn't change any of your settings.",
            "Delete preset", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        AppServices.Current.Presets.Delete(_preset);
        Close(Result.Deleted);
    }
}
