using System;
using System.Windows;

namespace Gloam.Services
{
    /// <summary>
    /// Where the window opens: in the corner of the work area by the notification area, like
    /// a tray flyout. The work area already excludes the taskbar, wherever it docks.
    /// </summary>
    internal static class FlyoutPlacement
    {
        /// <summary>The gap between the window and the screen edge / taskbar, in DIPs.</summary>
        public const double Margin = 12;

        /// <summary>
        /// The window's top-left corner. <paramref name="anchor"/> is a point (in the same
        /// coordinates as <paramref name="workArea"/>): the window goes to the work area's
        /// corner nearest it. A window taller or wider than the work area is pinned to its top
        /// or left edge instead of running off-screen.
        /// </summary>
        public static Point Place(Rect workArea, Size window, Point anchor)
        {
            bool right = anchor.X >= workArea.Left + workArea.Width / 2;
            bool bottom = anchor.Y >= workArea.Top + workArea.Height / 2;

            double left = right ? workArea.Right - window.Width - Margin : workArea.Left + Margin;
            double top = bottom ? workArea.Bottom - window.Height - Margin : workArea.Top + Margin;

            left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - window.Width));
            top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - window.Height));
            return new Point(left, top);
        }

        /// <summary>
        /// A point in the notification area's corner of a monitor: the end of the taskbar,
        /// found from the side where the work area stops short of the monitor. The tray sits at
        /// the right end of a horizontal taskbar and the bottom of a vertical one. With no
        /// visible taskbar (auto-hide) it is the usual bottom-right.
        /// </summary>
        public static Point TrayCorner(Rect monitor, Rect workArea)
        {
            if (workArea.Top > monitor.Top) return new Point(monitor.Right, monitor.Top);         // taskbar on top
            if (workArea.Left > monitor.Left) return new Point(monitor.Left, monitor.Bottom);     // on the left
            return new Point(monitor.Right, monitor.Bottom);                                       // bottom, right or hidden
        }

        /// <summary>The tallest the window may be in <paramref name="workArea"/>.</summary>
        public static double MaxHeight(Rect workArea) => Math.Max(200, workArea.Height - 2 * Margin);
    }
}
