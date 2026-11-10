using System;
using System.Runtime.InteropServices;

namespace Gloam.Interop
{
    public static class User32
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;

            public void Initialize() => cb = Marshal.SizeOf(this);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        public const int MONITOR_DEFAULTTONULL = 0;
        public const int MONITOR_DEFAULTTOPRIMARY = 1;
        public const int MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public Dxgi.RECT rcMonitor;
            public Dxgi.RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        /// <summary>
        /// Desktop bounds of a live HMONITOR. Lets callers match a current monitor
        /// handle against bounds captured at enumeration time, since HMONITOR values
        /// go stale across display-configuration changes.
        /// Returns <c>rcMonitor</c> — the FULL screen rect, including the taskbar.
        /// </summary>
        public static bool TryGetMonitorBounds(IntPtr hMonitor, out Dxgi.RECT bounds)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (hMonitor != IntPtr.Zero && GetMonitorInfo(hMonitor, ref mi))
            {
                bounds = mi.rcMonitor;
                return true;
            }
            bounds = default;
            return false;
        }

        /// <summary>
        /// Working area (rcWork) of a live HMONITOR: the desktop rect EXCLUDING the taskbar
        /// and any other appbars. Use this for placing windows that must not overlap the
        /// taskbar (the flyout). Returns false if the handle is invalid.
        /// </summary>
        public static bool TryGetMonitorWorkArea(IntPtr hMonitor, out Dxgi.RECT work)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (hMonitor != IntPtr.Zero && GetMonitorInfo(hMonitor, ref mi))
            {
                work = mi.rcWork;
                return true;
            }
            work = default;
            return false;
        }
        
        public const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
        public const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    }
}
