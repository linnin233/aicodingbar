using System.Runtime.InteropServices;

namespace ClaudeMonitor.Native;

public static class TaskbarInterop
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")]
    public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

    public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [DllImport("shcore.dll")]
    public static extern uint GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    public static extern int GetCurrentThemeName(System.Text.StringBuilder pszThemeName, int cchMaxName, System.Text.StringBuilder? pszColorScheme, int cchMaxColor, System.Text.StringBuilder? pszSizeName, int cchMaxSize);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    public const int SM_CLEANBOOT = 67;

    public static bool IsDarkMode()
    {
        try
        {
            const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            var value = Microsoft.Win32.Registry.GetValue(key, "AppsUseLightTheme", 1);
            return value is int v && v == 0;
        }
        catch { return false; }
    }

    public static Rect GetTaskbarRect()
    {
        var hwnd = FindWindow("Shell_TrayWnd", null);
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
            return new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return new Rect(0, 0, 0, 0);
    }

    public static Rect GetNotificationAreaRect()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return new Rect(0, 0, 0, 0);

        var notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero) return new Rect(0, 0, 0, 0);

        if (GetWindowRect(notify, out var rect))
            return new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return new Rect(0, 0, 0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    public record struct Rect(int X, int Y, int Width, int Height);
}
