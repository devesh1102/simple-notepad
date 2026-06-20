using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SimpleNotepad;

/// <summary>
/// Paints a window's Win32 title bar light or dark to match the app theme via DWM.
/// </summary>
internal static class TitleBarThemer
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // DWMWA_USE_IMMERSIVE_DARK_MODE.
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>
    /// Applies the title bar theme. Safe to call before the native handle exists (no-op then);
    /// re-call from OnSourceInitialized once the handle is created.
    /// </summary>
    public static void Apply(Window window, bool isLight)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var useDark = isLight ? 0 : 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
    }
}
