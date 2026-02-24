using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WindSound.App.Interop;

public static class WindowGlassHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtMainWindow = 2;

    public static void Apply(Window window)
    {
        if (window is null)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var dark = 1;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            var backdrop = DwmsbtMainWindow;
            _ = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));

            var margins = new Margins(-1);
            _ = DwmExtendFrameIntoClientArea(handle, ref margins);
        }
        catch
        {
            // Ignore unsupported DWM attributes (e.g., older Windows builds).
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins pMarInset);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public Margins(int all)
        {
            Left = all;
            Right = all;
            Top = all;
            Bottom = all;
        }

        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }
}
