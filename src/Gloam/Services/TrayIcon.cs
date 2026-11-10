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
    /// menu switches the profile and exits.
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        private readonly TaskbarIcon _icon;
        private readonly MenuItem _normal;
        private readonly MenuItem _media;

        public TrayIcon(Action toggleWindow, Action showWindow, Action<DisplayProfileSelection> selectProfile, Action exit)
        {
            // Not IsCheckable: a click would flip the mark itself, even on the profile already
            // selected. Update sets the marks from the saved selection instead.
            _normal = new MenuItem { Header = "Normal" };
            _normal.Click += (_, _) => selectProfile(DisplayProfileSelection.Normal);
            _media = new MenuItem { Header = "Media" };
            _media.Click += (_, _) => selectProfile(DisplayProfileSelection.Media);

            var open = new MenuItem { Header = "Open Gloam", FontWeight = FontWeights.SemiBold };
            open.Click += (_, _) => showWindow();
            var quit = new MenuItem { Header = "Exit" };
            quit.Click += (_, _) => exit();

            var menu = new ContextMenu();
            menu.Items.Add(open);
            menu.Items.Add(new Separator());
            menu.Items.Add(_normal);
            menu.Items.Add(_media);
            menu.Items.Add(new Separator());
            menu.Items.Add(quit);

            _icon = new TaskbarIcon
            {
                IconSource = BitmapFrame.Create(new Uri("pack://application:,,,/Gloam;component/app.ico", UriKind.Absolute)),
                ToolTipText = "Gloam",
                ContextMenu = menu,
                MenuActivation = PopupActivationMode.RightClick,
                // A single click acts at once instead of waiting out the double-click time.
                NoLeftClickDelay = true,
                LeftClickCommand = new RelayCommand(toggleWindow),
            };
        }

        /// <summary>Shows the selected profile in the tooltip and the menu's check marks.</summary>
        public void Update(DisplayProfileSelection selection)
        {
            bool media = selection == DisplayProfileSelection.Media;
            _normal.IsChecked = !media;
            _media.IsChecked = media;
            _icon.ToolTipText = media ? "Gloam · Media" : "Gloam · Normal";
        }

        public void Notify(string title, string text) =>
            _icon.ShowBalloonTip(title, text, BalloonIcon.Info);

        public void Dispose() => _icon.Dispose();
    }
}
