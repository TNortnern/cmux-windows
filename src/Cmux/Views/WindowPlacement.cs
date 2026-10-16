using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Cmux.Views;

/// <summary>
/// Keeps a restored window reachable: saved positions can point at a monitor that is gone,
/// one with a different DPI, or the -32000 "minimized" sentinel.
/// </summary>
internal static class WindowPlacement
{
    private const int TitleBarHeightPx = 40;
    private const uint MONITOR_DEFAULTTONULL = 0;
    private const int ASFW_ANY = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>True when a saved position is plausible (not the minimized sentinel, not NaN).</summary>
    public static bool IsPlausible(double left, double top, double width, double height) =>
        !double.IsNaN(left) && !double.IsNaN(top) && width > 0 && height > 0
        && left > -20000 && top > -20000;

    /// <summary>
    /// If the window's title bar isn't on any monitor, center the window on the primary work area.
    /// Call after the window has a handle (Loaded).
    /// </summary>
    public static void EnsureOnScreen(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || window.WindowState != WindowState.Normal || !GetWindowRect(hwnd, out var r))
            return;

        // The title bar must be grabbable, so test that strip rather than the whole window.
        var titleBar = new RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Top + TitleBarHeightPx };
        if (MonitorFromRect(ref titleBar, MONITOR_DEFAULTTONULL) != IntPtr.Zero)
            return;

        var work = SystemParameters.WorkArea; // primary monitor, in DIPs
        window.Width = Math.Min(window.Width, work.Width);
        window.Height = Math.Min(window.Height, work.Height);
        window.Left = work.Left + (work.Width - window.Width) / 2;
        window.Top = work.Top + (work.Height - window.Height) / 2;
    }

    /// <summary>Restores (if minimized), shows and focuses the window.</summary>
    public static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        EnsureOnScreen(window);
        SetForegroundWindow(new WindowInteropHelper(window).Handle);
    }

    /// <summary>
    /// Called by a second launch before it asks the running instance to come forward: Windows only
    /// lets the process that received the user's click hand foreground rights to another process.
    /// </summary>
    public static void AllowAnyProcessToTakeForeground() => AllowSetForegroundWindow(ASFW_ANY);
}
