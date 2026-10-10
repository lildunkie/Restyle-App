using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Restyle.Core.Platform;

/// <summary>Real implementation using documented Win32 APIs only.</summary>
public sealed class WindowsSystemApi : ISystemApi
{
    public string GetWallpaper()
    {
        var buffer = new StringBuilder(1024);
        if (!Native.SystemParametersInfoGet(Native.SPI_GETDESKWALLPAPER, (uint)buffer.Capacity, buffer, 0))
            throw new InvalidOperationException("Windows did not return the current wallpaper.");
        return buffer.ToString();
    }

    public void SetWallpaper(string path)
    {
        if (!Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, path,
                Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE))
            throw new InvalidOperationException($"Windows refused to set the wallpaper (error {Marshal.GetLastWin32Error()}).");
    }

    public void ReloadCursors()
    {
        if (!Native.SystemParametersInfo(Native.SPI_SETCURSORS, 0, null,
                Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE))
            throw new InvalidOperationException($"Windows refused to reload cursors (error {Marshal.GetLastWin32Error()}).");
    }

    public bool GetTaskbarAutoHide()
    {
        var data = NewAppBarData();
        var state = (uint)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref data);
        return (state & Native.ABS_AUTOHIDE) != 0;
    }

    public void SetTaskbarAutoHide(bool autoHide)
    {
        var data = NewAppBarData();
        data.hWnd = Native.FindWindow("Shell_TrayWnd", null);
        data.lParam = (IntPtr)(autoHide ? Native.ABS_AUTOHIDE : 0);
        Native.SHAppBarMessage(Native.ABM_SETSTATE, ref data);
    }

    public void BroadcastSettingChange(string area)
    {
        Native.SendMessageTimeout(Native.HWND_BROADCAST, Native.WM_SETTINGCHANGE, IntPtr.Zero, area,
            Native.SMTO_ABORTIFHUNG, 1000, out _);
    }

    public void RestartExplorer()
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try
            {
                p.Kill();
                p.WaitForExit(5000);
            }
            catch
            {
                // Process already gone or not ours; ignore.
            }
            finally
            {
                p.Dispose();
            }
        }

        // Windows normally relaunches the shell by itself; start it only if it didn't.
        for (var i = 0; i < 30; i++)
        {
            Thread.Sleep(100);
            if (Native.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero)
                return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose();
    }

    private static Native.APPBARDATA NewAppBarData() =>
        new() { cbSize = (uint)Marshal.SizeOf<Native.APPBARDATA>() };

    private static class Native
    {
        public const uint SPI_SETDESKWALLPAPER = 0x0014;
        public const uint SPI_GETDESKWALLPAPER = 0x0073;
        public const uint SPI_SETCURSORS = 0x0057;
        public const uint SPIF_UPDATEINIFILE = 0x01;
        public const uint SPIF_SENDCHANGE = 0x02;

        public const uint ABM_GETSTATE = 0x04;
        public const uint ABM_SETSTATE = 0x0A;
        public const uint ABS_AUTOHIDE = 0x01;

        public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
        public const uint WM_SETTINGCHANGE = 0x001A;
        public const uint SMTO_ABORTIFHUNG = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SystemParametersInfoGet(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string? pvParam, uint fWinIni);

        [DllImport("shell32.dll")]
        public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
    }
}
