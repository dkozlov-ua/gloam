using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Gloam.Core;
using Hardcodet.Wpf.TaskbarNotification;

namespace Gloam.Services
{
    /// <summary>
    /// The notification-area icon. A left click opens or closes Gloam's window; the right-click
    /// menu switches the profile and exits. The icon shows the selected profile: in colour
    /// for Normal, with a play badge for Media, grey for SDR (all drawn by
    /// scripts/New-AppIcon.ps1).
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        internal const string NormalIconUri = "pack://application:,,,/Gloam;component/app.ico";
        internal const string MediaIconUri = "pack://application:,,,/Gloam;component/app-media.ico";
        internal const string SdrIconUri = "pack://application:,,,/Gloam;component/app-sdr.ico";

        private readonly TaskbarIcon _icon;
        private readonly BitmapFrame _normalIcon = LoadIcon(NormalIconUri);
        private readonly BitmapFrame _mediaIcon = LoadIcon(MediaIconUri);
        private readonly BitmapFrame _sdrIcon = LoadIcon(SdrIconUri);
        private DisplayProfileSelection? _shown;
        private readonly MenuItem _normal;
        private readonly MenuItem _media;
        private readonly MenuItem _sdr;

        public TrayIcon(Action toggleWindow, Action showWindow, Action<DisplayProfileSelection> selectProfile, Action exit)
        {
            // Not IsCheckable: a click would flip the mark itself, even on the profile already
            // selected. Update sets the marks from the saved selection instead.
            _normal = new MenuItem { Header = "Normal" };
            _normal.Click += (_, _) => selectProfile(DisplayProfileSelection.Normal);
            _media = new MenuItem { Header = "Media" };
            _media.Click += (_, _) => selectProfile(DisplayProfileSelection.Media);
            _sdr = new MenuItem { Header = "SDR" };
            _sdr.Click += (_, _) => selectProfile(DisplayProfileSelection.Sdr);

            var open = new MenuItem { Header = "Open Gloam", FontWeight = FontWeights.SemiBold };
            open.Click += (_, _) => showWindow();
            var quit = new MenuItem { Header = "Exit" };
            quit.Click += (_, _) => exit();

            var menu = new ContextMenu();
            menu.Items.Add(open);
            menu.Items.Add(new Separator());
            menu.Items.Add(_normal);
            menu.Items.Add(_media);
            menu.Items.Add(_sdr);
            menu.Items.Add(new Separator());
            menu.Items.Add(quit);

            _icon = new TaskbarIcon
            {
                IconSource = _normalIcon,
                ToolTipText = "Gloam",
                ContextMenu = menu,
                MenuActivation = PopupActivationMode.RightClick,
                // A single click acts at once instead of waiting out the double-click time.
                NoLeftClickDelay = true,
                LeftClickCommand = new RelayCommand(toggleWindow),
            };
        }

        /// <summary>Shows the selected profile in the icon, the tooltip and the menu's check marks.</summary>
        public void Update(DisplayProfileSelection selection)
        {
            _normal.IsChecked = selection == DisplayProfileSelection.Normal;
            _media.IsChecked = selection == DisplayProfileSelection.Media;
            _sdr.IsChecked = selection == DisplayProfileSelection.Sdr;
            _icon.ToolTipText = selection switch
            {
                DisplayProfileSelection.Media => "Gloam · Media",
                DisplayProfileSelection.Sdr => "Gloam · SDR",
                _ => "Gloam · Normal",
            };
            // Only on a change: setting IconSource rebuilds the shell icon each time.
            if (_shown != selection)
            {
                _shown = selection;
                _icon.IconSource = selection switch
                {
                    DisplayProfileSelection.Media => _mediaIcon,
                    DisplayProfileSelection.Sdr => _sdrIcon,
                    _ => _normalIcon,
                };
            }
        }

        private static BitmapFrame LoadIcon(string uri)
        {
            BitmapFrame frame = BitmapFrame.Create(new Uri(uri, UriKind.Absolute));
            frame.Freeze();
            return frame;
        }

        public void Notify(string title, string text) =>
            _icon.ShowBalloonTip(title, text, BalloonIcon.Info);

        public void Dispose() => _icon.Dispose();
    }
}
