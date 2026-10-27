using System.Runtime.InteropServices;

namespace Allycord.Bridge;

static partial class Win32
{
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x00000080;
    const int DWMWA_CLOAKED = 14;

    /// <summary>
    /// Roughly what Alt+Tab shows: visible, not cloaked (hidden UWP/background hosts), not a tool
    /// window (overlays, tray helpers). Filters the share picker down to real apps and games.
    /// </summary>
    public static bool IsAltTabWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;
        if ((GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial long GetWindowLongPtrW(IntPtr hwnd, int index);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
