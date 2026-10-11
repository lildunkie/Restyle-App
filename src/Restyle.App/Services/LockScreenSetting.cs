using System.IO;
using System.Security.Cryptography;
using Restyle.App.Docking;
using Restyle.Core.Dock;
using Restyle.Core.Platform;
using Restyle.Core.Settings;
using Windows.Storage;
using Windows.System.UserProfile;

namespace Restyle.App.Services;

/// <summary>
/// The picture on the lock screen (Windows.System.UserProfile.LockScreen, per user, no admin).
/// The value is a picture path ("builtin:night-sky-lock" or a file) or "spotlight" for Windows Spotlight.
/// </summary>
public sealed class LockScreenSetting(SettingContext context) : Setting<string>(context)
{
    public const string Spotlight = "spotlight";

    // Windows Spotlight's on/off switch, the same per-user value the Settings app uses. Setting a picture
    // turns it off; Undo/Restore turns it back on if Spotlight was what you had.
    private const string ContentDelivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string SpotlightValue = "RotatingLockScreenEnabled";

    public override string Id => "lockScreen";
    public override string Name => "Lock screen";
    public override string Description =>
        "The picture shown on the lock screen and sign-in screen. Restyle's built-in ones match its wallpapers, framed for the lock screen clock.";

    protected override int MinimumBuild => 10240;

    public override string Read()
    {
        if (Registry.GetDword(ContentDelivery, SpotlightValue) is 1)
            return Spotlight;

        var uri = LockScreen.OriginalImageFile;

        // Showing Restyle's picture with widgets drawn on it: report the plain picture underneath.
        if (uri is { IsFile: true } && LockScreenWidgets.IsComposite(uri.LocalPath)
            && DockConfig.Load(DockHost.ConfigPath).LockWidgets is { Enabled: true, Base: { Length: > 0 } plain })
            return plain;

        if (uri is { IsFile: true } && File.Exists(uri.LocalPath))
            return uri.LocalPath;

        // The original file is gone (e.g. a cleaned-up download): keep a copy of what's showing so it can come back.
        return SaveCurrentCopy() ?? "";
    }

    public override void Apply(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        // Lock screen widgets on: this becomes the picture they're drawn on.
        var config = DockConfig.Load(DockHost.ConfigPath);
        if (config.LockWidgets.Enabled)
        {
            config.LockWidgets.Base = value;
            config.Save(DockHost.ConfigPath);
            if (!value.Equals(Spotlight, StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(Context.ResolvePath(value)))
                    throw new FileNotFoundException($"The lock screen picture no longer exists: {value}", value);
                LockScreenWidgets.RefreshNow(config, Context.ResolvePath);
                return;
            }
        }

        if (value.Equals(Spotlight, StringComparison.OrdinalIgnoreCase))
        {
            Registry.SetDword(ContentDelivery, SpotlightValue, 1);
            return;
        }

        var path = Context.ResolvePath(value);
        if (!File.Exists(path))
            throw new FileNotFoundException($"The lock screen picture no longer exists: {path}", path);

        // WinRT calls complete on a background thread; run them there so the UI thread never waits on itself.
        Task.Run(async () =>
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            await LockScreen.SetImageFileAsync(file);
        }).GetAwaiter().GetResult();
    }

    protected override bool IsSame(string current, string target)
    {
        string Full(string p) => p.Equals(Spotlight, StringComparison.OrdinalIgnoreCase) ? Spotlight
            : string.IsNullOrWhiteSpace(p) ? "" : Path.GetFullPath(Context.ResolvePath(p));
        return string.Equals(Full(current), Full(target), StringComparison.OrdinalIgnoreCase);
    }

    public override string Describe(string value)
    {
        if (value.Equals(Spotlight, StringComparison.OrdinalIgnoreCase))
            return "Windows Spotlight";
        if (string.IsNullOrWhiteSpace(value))
            return "(unknown)";
        var name = value.StartsWith(WallpaperLibrary.Prefix, StringComparison.OrdinalIgnoreCase)
            ? value[WallpaperLibrary.Prefix.Length..]
            : Path.GetFileNameWithoutExtension(value);
        return name.EndsWith(WallpaperLibrary.LockSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^WallpaperLibrary.LockSuffix.Length] : name;
    }

    /// <summary>Saves the lock screen picture that's showing now to %LOCALAPPDATA%\Restyle\LockScreen.</summary>
    private string? SaveCurrentCopy()
    {
        try
        {
            using var stream = LockScreen.GetImageStream().AsStreamForRead();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            var folder = Path.Combine(Context.DataDirectory, "LockScreen");
            var file = Path.Combine(folder, $"saved-{Convert.ToHexString(SHA256.HashData(bytes))[..12]}.jpg");
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(file, bytes);
            }

            return file;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
