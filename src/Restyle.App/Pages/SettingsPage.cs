using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Restyle.App.Controls;
using Restyle.App.Docking;
using Restyle.App.Services;
using Restyle.Core;
using Restyle.Core.Dock;
using Restyle.Core.Settings;

namespace Restyle.App.Pages;

/// <summary>
/// What every settings page shares: a scrolling column with a title, cards with a name, explanation and
/// control, and live refresh. Windows settings go through the engine (so they're undoable); dock, widget and
/// Start menu options are saved to the dock's settings file, which a running dock picks up immediately.
/// </summary>
public abstract class SettingsPage : UserControl, IPage
{
    private readonly List<Action> _refreshers = [];

    protected SettingsPage(string title, string subtitle)
    {
        Root = new StackPanel { Margin = new Thickness(28, 20, 28, 28), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        Content = new ScrollViewer { Content = Root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Root.Children.Add(Ui.Text(title, "PageTitle"));
        Root.Children.Add(Ui.Text(subtitle, "PageSubtitle"));
    }

    protected StackPanel Root { get; }

    /// <summary>True while controls are being filled in from the current settings (so they don't apply changes).</summary>
    protected bool Loading { get; private set; }

    /// <summary>The dock's settings as of the last refresh.</summary>
    protected DockConfig Config { get; private set; } = new();

    protected static AppServices S => AppServices.Current;

    protected static T Get<T>() where T : ISetting => S.Catalog.Get<T>();

    public virtual void Refresh()
    {
        Loading = true;
        try
        {
            Config = DockConfig.Load(DockHost.ConfigPath);
            foreach (var refresh in _refreshers)
            {
                try
                {
                    refresh();
                }
                catch (Exception)
                {
                    // One unreadable setting shouldn't blank the whole page.
                }
            }
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>Runs when the page refreshes, to show the current value in a control.</summary>
    protected void OnRefresh(Action refresh) => _refreshers.Add(refresh);

    protected void Section(string title) => Root.Children.Add(Ui.Text(title, "SectionHeader"));

    // ---- Windows settings (through the engine, undoable) --------------------------------

    protected static void Apply(ISetting setting, JsonNode? value) =>
        MainWindow.Instance.Run(() => S.Engine.Set(setting.Id, value),
            $"{setting.Name}: {setting.DescribeJson(value)}");

    protected static JsonNode? Json<T>(T value) => JsonSerializer.SerializeToNode(value, RestyleJson.Options);

    protected static T Read<T>(ISetting setting) => setting.ReadJson().Deserialize<T>(RestyleJson.Options)!;

    /// <summary>A card with the setting's name and explanation on the left and its control on the right.</summary>
    protected static Border Row(ISetting setting, FrameworkElement control, UIElement? below = null)
    {
        var card = Row(setting.Name, Ui.Text(setting.UnsupportedReason ?? setting.Description, "Hint"), control, below);
        card.ToolTip = Ui.Tip(setting.Description);
        if (!setting.IsSupported)
        {
            ((UIElement)card.Child).IsEnabled = false;
            card.ToolTip = Ui.Tip($"{setting.UnsupportedReason} {setting.Description}");
        }

        return card;
    }

    protected void Combo<T>(ISetting setting, params (T Value, string Label)[] options) where T : struct, Enum
    {
        var combo = new ComboBox { MinWidth = 160 };
        foreach (var (value, label) in options)
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });

        combo.SelectionChanged += (_, _) =>
        {
            if (!Loading && combo.SelectedItem is ComboBoxItem { Tag: T value })
                Apply(setting, Json(value));
        };
        if (setting.IsSupported)
        {
            OnRefresh(() =>
            {
                var current = Read<T>(setting);
                combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag is T t && t.Equals(current));
            });
        }

        Root.Children.Add(Row(setting, combo));
    }

    protected void Switch(ISetting setting)
    {
        var box = SwitchBox(() => { });
        box.Click += (_, _) => Apply(setting, JsonValue.Create(box.IsChecked == true));
        if (setting.IsSupported)
            OnRefresh(() => box.IsChecked = Read<bool>(setting));
        Root.Children.Add(Row(setting, box));
    }

    // ---- Dock, widget and Start menu options (saved to the dock's settings) --------------

    /// <summary>Saves a change to the dock's settings; a running dock applies it right away.</summary>
    protected void UpdateDock(Action<DockConfig> change, bool refresh = false)
    {
        if (Loading)
            return;
        var config = DockConfig.Load(DockHost.ConfigPath);
        change(config);
        config.Save(DockHost.ConfigPath);
        Config = config;
        if (refresh)
            Refresh();
    }

    /// <summary>An on/off switch for a dock option, kept in sync with the dock's settings.</summary>
    protected CheckBox DockSwitch(Func<DockConfig, bool> get, Action<DockConfig, bool> set)
    {
        CheckBox? box = null;
        box = SwitchBox(() => UpdateDock(c => set(c, box!.IsChecked == true)));
        OnRefresh(() => box.IsChecked = get(Config));
        return box;
    }

    /// <summary>A drop-down for a dock option, kept in sync with the dock's settings.</summary>
    protected ComboBox DockChoice<T>(IEnumerable<(T Value, string Label)> options, Func<DockConfig, T> get, Action<DockConfig, T> set)
    {
        var combo = new ComboBox { MinWidth = 150 };
        foreach (var (value, label) in options)
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        combo.SelectionChanged += (_, _) =>
        {
            if (!Loading && combo.SelectedItem is ComboBoxItem { Tag: T value })
                UpdateDock(c => set(c, value));
        };
        OnRefresh(() => combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, get(Config))) ?? combo.Items[0]);
        return combo;
    }

    protected CheckBox SwitchBox(Action onClick)
    {
        var box = new CheckBox { Style = (Style)Application.Current.FindResource("Switch") };
        box.Click += (_, _) =>
        {
            if (!Loading)
                onClick();
        };
        return box;
    }

    // ---- Layout ------------------------------------------------------------------------

    protected static Border Row(string title, string hint, FrameworkElement control) => Row(title, Ui.Text(hint, "Hint"), control);

    protected static Border Row(string title, UIElement detail, FrameworkElement control, UIElement? below = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(Ui.Text(title));
        text.Children.Add(detail);
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        UIElement content = grid;
        if (below is not null)
        {
            var stack = new StackPanel();
            stack.Children.Add(grid);
            stack.Children.Add(below);
            content = stack;
        }

        return Ui.Card(content);
    }

    /// <summary>A hint with a button that jumps to another page (e.g. "Turn the dock on" → Dock).</summary>
    protected static Border Pointer(string text, string button, string page)
    {
        var go = Ui.Button(button, (_, _) => MainWindow.Instance.ShowPage(page));
        go.Margin = new Thickness(0);
        return Row(text, Ui.Text("", "Hint"), go);
    }
}
