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

    public const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;
    private const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;

    public uint CallbackMessage { get; } = RegisterWindowMessage(top ? "RestyleTopBarAppBar" : "RestyleDockAppBar");

    public bool IsRegistered { get; private set; }

    /// <summary>The screen edge the bar sits on (ABE_*). Defaults to top for the top bar, bottom otherwise.</summary>
    public uint Edge { get; set; } = top ? ABE_TOP : ABE_BOTTOM;

    public void Register()
    {
        if (IsRegistered)
            return;
        var data = Data();
        data.uCallbackMessage = CallbackMessage;
        IsRegistered = SHAppBarMessage(ABM_NEW, ref data) != UIntPtr.Zero;
    }

    /// <summary>
    /// Reserves a band of <paramref name="thicknessPx"/> physical pixels along <see cref="Edge"/> of the primary screen.
    /// </summary>
    /// <returns>The outer edge (physical px) the bar should sit against: its top/bottom/left/right side respectively.</returns>
    public int Reserve(int thicknessPx)
    {
        var data = Data();
        data.uEdge = Edge;
        data.rc = new RECT { Left = 0, Right = GetSystemMetrics(0), Top = 0, Bottom = GetSystemMetrics(1) };
        Fit(ref data.rc, thicknessPx);
        SHAppBarMessage(ABM_QUERYPOS, ref data); // Windows moves the rect clear of other bars
        Fit(ref data.rc, thicknessPx);
        SHAppBarMessage(ABM_SETPOS, ref data);
        return Edge switch
        {
            ABE_TOP => data.rc.Top,
            ABE_LEFT => data.rc.Left,
            ABE_RIGHT => data.rc.Right,
            _ => data.rc.Bottom,
        };
    }

    private void Fit(ref RECT rc, int thickness)
    {
        switch (Edge)
        {
            case ABE_TOP: rc.Bottom = rc.Top + thickness; break;
            case ABE_LEFT: rc.Right = rc.Left + thickness; break;
            case ABE_RIGHT: rc.Left = rc.Right - thickness; break;
            default: rc.Top = rc.Bottom - thickness; break;
        }
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
