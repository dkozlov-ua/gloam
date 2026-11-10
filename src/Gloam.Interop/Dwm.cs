using System;
using System.Runtime.InteropServices;

namespace Gloam.Interop
{
    public static class Dwm
    {
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// Asks Windows 11 to round a borderless window's corners and give it the system
        /// border and shadow. Earlier Windows ignores it; failure is harmless.
        /// </summary>
        public static void TryRoundCorners(IntPtr hwnd)
        {
            int preference = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
    }
}
