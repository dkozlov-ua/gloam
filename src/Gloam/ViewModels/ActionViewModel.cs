using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Gloam.ViewModels
{
    public class ActionViewModel : ObservableObject
    {
        private string _header;
        public string Header
        {
            get => _header;
            set => SetProperty(ref _header, value);
        }

        private bool _isShown = true;

        /// <summary>
        /// Hides the item without rebuilding the menu, which would tear the items out
        /// from under a still-open tray menu.
        /// </summary>
        public bool IsShown
        {
            get => _isShown;
            set => SetProperty(ref _isShown, value);
        }

        public ICommand? Command { get; }
        public bool IsSeparator { get; }

        /// <summary>
        /// Keep the tray menu open when this item is clicked, so gamma modes
        /// can be swapped quickly without reopening the menu.
        /// </summary>
        public bool StaysOpenOnClick { get; }

        public ActionViewModel(string header, ICommand? command, bool staysOpenOnClick = false)
        {
            _header = header;
            Command = command;
            IsSeparator = false;
            StaysOpenOnClick = staysOpenOnClick;
        }

        public System.Collections.IEnumerable? SubItems => null;

        public ActionViewModel(bool isSeparator)
        {
            IsSeparator = true;
            _header = string.Empty;
            Command = null!;
        }
    }
}
