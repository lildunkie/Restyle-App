using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Restyle.App.Docking;

/// <summary>A top-level window that would normally get a taskbar button.</summary>
public sealed record AppWindow(IntPtr Handle, string Title, string? ExePath, string? AppId);

/// <summary>
/// The documented Win32/shell calls the dock needs: listing app windows, finding which app they
/// belong to, icons, activating windows and launching apps.
/// </summary>
internal static class ShellNative
{
    public const string FileExplorerAppId = "Microsoft.Windows.Explorer";

    private static readonly Dictionary<string, ImageSource?> IconCache = [];

    // ---- Windows -----------------------------------------------------------------------

    /// <summary>Windows that Windows itself would show on the taskbar (same rules as Alt+Tab).</summary>
    public static List<AppWindow> GetAppWindows()
    {
        var own = Environment.ProcessId;
        var result = new List<AppWindow>();
        EnumWindows((hwnd, _) =>
        {
            if (IsTaskbarWindow(hwnd))
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != own)
                {
                    var exe = ProcessPath(pid);
                    result.Add(new AppWindow(hwnd, Title(hwnd), exe, AppIdFor(hwnd, pid, exe)));
                }
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool IsTaskbarWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0)
            return false;

        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        var appWindow = (ex & WS_EX_APPWINDOW) != 0;
        if (!appWindow && ((ex & WS_EX_TOOLWINDOW) != 0 || (ex & WS_EX_NOACTIVATE) != 0 || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero))
            return false;

        // Cloaked = on another virtual desktop, or a suspended Store app.
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        var cls = ClassName(hwnd);
        return cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    public static IntPtr Foreground => GetForegroundWindow();

    public static void Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    public static void Minimize(IntPtr hwnd) => ShowWindow(hwnd, SW_MINIMIZE);

    public static void Close(IntPtr hwnd) => PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    // ---- App identity ------------------------------------------------------------------

    private static string? AppIdFor(IntPtr hwnd, uint pid, string? exe)
    {
        // 1. Explicit AppUserModelID on the window (Edge, File Explorer, Store app frames...).
        var id = WindowAppId(hwnd);
        if (id is not null)
            return id;

        // 2. File Explorer folder windows don't publish an ID; use the one their Start menu entry has.
        if (exe is not null && Path.GetFileName(exe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            return FileExplorerAppId;

        // 3. Store apps are hosted by ApplicationFrameHost; the real app lives in a child CoreWindow.
        if (exe is not null && Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            var core = FindWindowEx(hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            if (core != IntPtr.Zero)
            {
                GetWindowThreadProcessId(core, out var corePid);
                return ProcessAppId(corePid);
            }
        }

        // 4. Packaged desktop apps (e.g. Windows Terminal) know their own ID.
        return ProcessAppId(pid);
    }

    private static string? WindowAppId(IntPtr hwnd)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null)
                return null;
            try
            {
                var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                if (store.GetValue(ref key, out var value) != 0)
                    return null;
                var text = value.vt == VT_LPWSTR ? Marshal.PtrToStringUni(value.pointer) : null;
                PropVariantClear(ref value);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ProcessAppId(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return null;
        try
        {
            uint length = 512;
            var buffer = new StringBuilder((int)length);
            return GetApplicationUserModelId(process, ref length, buffer) == 0 ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static string? ProcessPath(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return null;
        try
        {
            var size = 1024;
            var buffer = new StringBuilder(size);
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>A friendly app name: the Start menu name for apps with an ID, else the program's description.</summary>
    public static string AppName(string? appId, string? exePath, string fallback)
    {
        if (appId is not null && ShellDisplayName("shell:AppsFolder\\" + appId) is { Length: > 0 } name)
            return name;
        if (exePath is not null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(info.FileDescription))
                    return info.FileDescription.Trim();
            }
            catch (Exception)
            {
            }

            return Path.GetFileNameWithoutExtension(exePath);
        }

        return fallback;
    }

    // ---- Launching ---------------------------------------------------------------------

    public static void LaunchAppId(string appId) => Start("explorer.exe", "shell:AppsFolder\\" + appId);

    public static void LaunchPath(string path) => Start(path, null);

    public static void Start(string file, string? args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = true };
        if (args is not null)
            info.Arguments = args;
        Process.Start(info)?.Dispose();
    }

    /// <summary>
    /// Hides or shows the Windows taskbar on every monitor. Only the windows' visibility changes;
    /// restarting Explorer or showing them again restores everything.
    /// </summary>
    public static void SetTaskbarsVisible(bool visible)
    {
        EnumWindows((hwnd, _) =>
        {
            if (ClassName(hwnd) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" && IsWindowVisible(hwnd) != visible)
                ShowWindow(hwnd, visible ? SW_SHOWNA : SW_HIDE);
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>Who owns a window (any window, no taskbar filtering). Null for our own windows.</summary>
    public static AppWindow? Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId)
            return null;
        var exe = ProcessPath(pid);
        return new AppWindow(hwnd, Title(hwnd), exe, AppIdFor(hwnd, pid, exe));
    }

    public static bool IsDesktopOrShell(IntPtr hwnd) =>
        ClassName(hwnd) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    /// <summary>True while the (auto-hiding) taskbar is actually on screen.</summary>
    public static bool TaskbarIsShowing()
    {
        var tray = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        return tray != IntPtr.Zero && IsWindowVisible(tray) && GetWindowRect(tray, out var r) && r.Top < GetSystemMetrics(1) - 6;
    }

    /// <summary>Presses Windows + key (e.g. 'A' = Quick Settings, 'N' = notifications, 'T' = taskbar).</summary>
    public static void PressWinKey(char key)
    {
        var vk = (byte)char.ToUpperInvariant(key);
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static void Lock() => LockWorkStation();

    /// <summary>Opens the Start menu by pressing the Windows key.</summary>
    public static void OpenStartMenu()
    {
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    // ---- Icons -------------------------------------------------------------------------

    /// <summary>A high-resolution icon for a file path or shell name (e.g. "shell:AppsFolder\ID").</summary>
    public static ImageSource? Icon(string parsingName, int size, bool cache = true)
    {
        var key = $"{parsingName}|{size}";
        if (cache && IconCache.TryGetValue(key, out var cached))
            return cached;

        ImageSource? image = null;
        try
        {
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var item);
            try
            {
                var factory = (IShellItemImageFactory)item;
                if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out var hbitmap) == 0)
                {
                    image = FromHBitmap(hbitmap);
                    DeleteObject(hbitmap);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        catch (Exception)
        {
            image = null;
        }

        if (cache)
            IconCache[key] = image;
        return image;
    }

    /// <summary>
    /// Every app in the Start menu's "All apps" list (desktop and Store apps), as name + ID.
    /// The ID launches it via shell:AppsFolder\ID and gives its icon.
    /// </summary>
    public static List<(string Name, string Id)> ListApps()
    {
        var apps = new List<(string, string)>();
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        try
        {
            foreach (var item in shell.NameSpace("shell:AppsFolder").Items())
            {
                string name = item.Name;
                string id = item.Path;
                // Skip loose links to documents/uninstallers ("file:///...") that Windows also lists here.
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id) && !id.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    apps.Add((name, id));
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }

        return apps.OrderBy(a => a.Item1, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string? ShellDisplayName(string parsingName)
    {
        try
        {
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var item);
            try
            {
                item.GetDisplayName(0, out var ptr);
                var name = Marshal.PtrToStringUni(ptr);
                Marshal.FreeCoTaskMem(ptr);
                return name;
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Copies a 32-bit premultiplied-alpha HBITMAP into a WPF bitmap (keeping transparency).</summary>
    private static BitmapSource? FromHBitmap(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0 || bm.bmBitsPixel != 32)
            return null;

        var width = bm.bmWidth;
        var height = bm.bmHeight;
        var pixels = new byte[width * height * 4];
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbitmap, 0, (uint)height, pixels, ref header, 0) == 0)
                return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(GetWindowTextLength(hwnd) + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // ---- Interop declarations ----------------------------------------------------------

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x8000000;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    private const int SW_RESTORE = 9, SW_MINIMIZE = 6, SW_HIDE = 0, SW_SHOWNA = 8;
    private const uint WM_CLOSE = 0x0010;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const ushort VT_LPWSTR = 31;
    private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;
    private const byte VK_LWIN = 0x5B;
    private const uint KEYEVENTF_KEYUP = 0x2;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr pointer; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder id);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pvar);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
