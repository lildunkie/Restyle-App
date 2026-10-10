namespace Restyle.Core.Platform;

/// <summary>
/// The non-registry Windows calls the settings need. Kept tiny so tests can fake it.
/// </summary>
public interface ISystemApi
{
    string GetWallpaper();

    /// <summary>Sets the wallpaper via SystemParametersInfo (persists and broadcasts the change).</summary>
    void SetWallpaper(string path);

    /// <summary>Reloads cursors from HKCU\Control Panel\Cursors via SystemParametersInfo(SPI_SETCURSORS).</summary>
    void ReloadCursors();

    bool GetTaskbarAutoHide();

    void SetTaskbarAutoHide(bool autoHide);

    /// <summary>Broadcasts WM_SETTINGCHANGE with the given area string (e.g. "ImmersiveColorSet").</summary>
    void BroadcastSettingChange(string area);

    /// <summary>Restarts Windows Explorer (closes File Explorer windows). Only call after asking the user.</summary>
    void RestartExplorer();
}
