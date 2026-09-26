using System.Runtime.InteropServices;

namespace Jiaolong.App.Platform;

/// <summary>
/// DWM helpers - all pure P/Invoke, no NuGet packages (the build environment is offline).
///
///  * <see cref="EnableMica"/>   - Windows 11 system backdrop (the closest we can get to Apple's
///                                 frosted glass without WinUI/WinAppSDK; WPF cannot sample
///                                 pixels outside its own window, so real refraction is impossible)
///  * <see cref="EnableRoundedCorners"/> - forces the Win11 rounded corner even with WindowStyle=None
/// </summary>
internal static class Dwm
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWMSBT_AUTO = 0;
    private const int DWMWMSBT_NONE = 1;
    private const int DWMWMSBT_MAINWINDOW = 2;      // Mica
    private const int DWMWMSBT_TRANSIENTWINDOW = 3; // Acrylic
    private const int DWMWMCORNER_ROUND = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static bool Apply(IntPtr hwnd, int attribute, int value)
    {
        if (hwnd == IntPtr.Zero) return false;
        try { return DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0; }
        catch { return false; }
    }

    /// <summary>Win11 22H2+ (build 22621) has Mica. Returns false on older systems.</summary>
    public static bool SupportsMica()
    {
        try
        {
            int build = Environment.OSVersion.Version.Build;
            return build >= 22621;
        }
        catch { return false; }
    }

    /// <summary>Requests the Mica system backdrop. True when DWM accepted the request.</summary>
    public static bool EnableMica(IntPtr hwnd) => Apply(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMWMSBT_MAINWINDOW);

    /// <summary>Undoes the backdrop (falls back to our own painted gradient).</summary>
    public static void DisableBackdrop(IntPtr hwnd) => Apply(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMWMSBT_AUTO);

    public static void EnableRoundedCorners(IntPtr hwnd) =>
        Apply(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWMCORNER_ROUND);

    /// <summary>Dark non-client area (used as a belt-and-braces fallback if our chrome fails).</summary>
    public static void EnableDarkTitleBar(IntPtr hwnd) =>
        Apply(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;

    /// <summary>
    /// Restores WS_CAPTION, WS_THICKFRAME, WS_MINIMIZEBOX and WS_MAXIMIZEBOX
    /// so Windows DWM natively plays smooth window transition animations
    /// (grow/shrink/zoom without tearing) and enables native Aero Snap.
    /// </summary>
    public static void EnableNativeWindowAnimations(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int style = GetWindowLong(hwnd, GWL_STYLE);
            SetWindowLong(hwnd, GWL_STYLE, style | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        }
        catch { }
    }
}
