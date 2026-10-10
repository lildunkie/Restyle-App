using System.Runtime.InteropServices;

namespace Restyle.App.Docking;

/// <summary>
/// Registers a window as a Windows "app bar" (SHAppBarMessage), the documented way for docks and
/// toolbars to reserve screen space so maximized windows don't cover them.
/// </summary>
internal sealed class AppBar(IntPtr hwnd, bool top = false) : IDisposable
{
    public const int ABN_POSCHANGED = 1;
    public const int ABN_FULLSCREENAPP = 2;

    private const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
    private const uint ABE_TOP = 1, ABE_BOTTOM = 3;

    public uint CallbackMessage { get; } = RegisterWindowMessage(top ? "RestyleTopBarAppBar" : "RestyleDockAppBar");

    public bool IsRegistered { get; private set; }

    public void Register()
    {
        if (IsRegistered)
            return;
        var data = Data();
        data.uCallbackMessage = CallbackMessage;
        IsRegistered = SHAppBarMessage(ABM_NEW, ref data) != UIntPtr.Zero;
    }

    /// <summary>
    /// Reserves a band of <paramref name="thicknessPx"/> physical pixels at the top or bottom of the primary screen.
    /// </summary>
    /// <returns>The outer edge (physical px) the bar should sit against: its top for a top bar, its bottom otherwise.</returns>
    public int Reserve(int thicknessPx)
    {
        var data = Data();
        data.uEdge = top ? ABE_TOP : ABE_BOTTOM;
        data.rc = new RECT { Left = 0, Right = GetSystemMetrics(0), Top = 0, Bottom = GetSystemMetrics(1) };
        if (top)
            data.rc.Bottom = data.rc.Top + thicknessPx;
        else
            data.rc.Top = data.rc.Bottom - thicknessPx;

        SHAppBarMessage(ABM_QUERYPOS, ref data); // Windows moves the rect clear of other bars

        if (top)
            data.rc.Bottom = data.rc.Top + thicknessPx;
        else
            data.rc.Top = data.rc.Bottom - thicknessPx;
        SHAppBarMessage(ABM_SETPOS, ref data);
        return top ? data.rc.Top : data.rc.Bottom;
    }

    /// <summary>Explorer forgets app bars when it restarts; call this so the next Register() re-adds us.</summary>
    public void Forget() => IsRegistered = false;

    public void Dispose()
    {
        if (!IsRegistered)
            return;
        var data = Data();
        SHAppBarMessage(ABM_REMOVE, ref data);
        IsRegistered = false;
    }

    private APPBARDATA Data() => new() { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
