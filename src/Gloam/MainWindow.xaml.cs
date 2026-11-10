using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Gloam.Interop;
using Gloam.Services;
using Gloam.ViewModels;

namespace Gloam
{
    /// <summary>
    /// The flyout: opens by the notification area, hides when it loses focus (or on Esc), and
    /// is never closed while the app runs.
    /// </summary>
    public partial class MainWindow : Window
    {
        // A tray click first deactivates (and so hides) an open flyout, then arrives as a
        // click. Within this long after hiding, a click means "close it", not "open it again".
        private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(300);

        private DateTime _hiddenAtUtc = DateTime.MinValue;
        private bool _closing;

        public MainWindow(MainViewModel viewModel)
        {
            ArgumentNullException.ThrowIfNull(viewModel);
            InitializeComponent();
            DataContext = viewModel;

            SourceInitialized += (_, _) => Dwm.TryRoundCorners(new WindowInteropHelper(this).Handle);

            Deactivated += (_, _) => HideFlyout();
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) HideFlyout();
            };
        }

        /// <summary>Opens the flyout, or closes it if it is open (a tray click).</summary>
        public void Toggle()
        {
            if (IsVisible)
            {
                HideFlyout();
                return;
            }
            if (DateTime.UtcNow - _hiddenAtUtc < ReopenGuard) return;
            ShowFlyout();
        }

        public void ShowFlyout()
        {
            if (!IsVisible)
            {
                // Shown off-screen first: the size it lays out to decides where it goes, and it
                // must not flash where it was last.
                Left = -32000;
                Top = -32000;
                Show();
                Place();
            }
            Activate();
        }

        public void HideFlyout()
        {
            if (!IsVisible) return;
            _hiddenAtUtc = DateTime.UtcNow;
            Hide();
        }

        /// <summary>Lets the window close: only when the app exits.</summary>
        public void CloseForExit()
        {
            _closing = true;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Alt+F4 hides the flyout like any other way of dismissing it.
            if (!_closing)
            {
                e.Cancel = true;
                HideFlyout();
            }
            base.OnClosing(e);
        }

        private void Hide_Click(object sender, RoutedEventArgs e) => HideFlyout();

        private void Place()
        {
            if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return;
            var toDips = target.TransformFromDevice;

            // The tray is on the primary monitor's taskbar.
            IntPtr monitor = User32.MonitorFromPoint(default, 1 /* MONITOR_DEFAULTTOPRIMARY */);
            Rect workArea = SystemParameters.WorkArea;
            Rect bounds = new(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            if (User32.TryGetMonitorWorkArea(monitor, out var work) && User32.TryGetMonitorBounds(monitor, out var full))
            {
                workArea = new Rect(toDips.Transform(new Point(work.Left, work.Top)), toDips.Transform(new Point(work.Right, work.Bottom)));
                bounds = new Rect(toDips.Transform(new Point(full.Left, full.Top)), toDips.Transform(new Point(full.Right, full.Bottom)));
            }
            Point anchor = FlyoutPlacement.TrayCorner(bounds, workArea);

            MaxHeight = FlyoutPlacement.MaxHeight(workArea);
            UpdateLayout();
            Point position = FlyoutPlacement.Place(workArea, new Size(ActualWidth, ActualHeight), anchor);
            Left = position.X;
            Top = position.Y;
        }
    }
}
