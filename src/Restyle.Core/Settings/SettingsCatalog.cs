namespace Restyle.Core.Settings;

/// <summary>The list of every setting the app knows about. Register new settings here.</summary>
public sealed class SettingsCatalog
{
    public SettingsCatalog(SettingContext context)
    {
        Context = context;
        All =
        [
            new AppsThemeSetting(context),
            new SystemThemeSetting(context),
            new AccentColorSetting(context),
            new AccentOnTitleBarsSetting(context),
            new AccentOnTaskbarSetting(context),
            new TransparencySetting(context),
            new TaskbarAlignmentSetting(context),
            new TaskbarAutoHideSetting(context),
            new TaskbarSearchSetting(context),
            new TaskbarWidgetsSetting(context),
            new TaskbarTaskViewSetting(context),
            new WallpaperSetting(context),
            new CursorSchemeSetting(context),
            new DesktopIconsSetting(context),
        ];
    }

    public SettingContext Context { get; }

    public IReadOnlyList<ISetting> All { get; }

    public ISetting? Find(string id) =>
        All.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public T Get<T>() where T : ISetting => All.OfType<T>().Single();
}
