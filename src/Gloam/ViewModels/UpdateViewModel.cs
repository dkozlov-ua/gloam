using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gloam.Core;
using Gloam.Services;

namespace Gloam.ViewModels
{
    /// <summary>
    /// The update button in the window's footer: "Check for updates", then "Update to X.Y.Z"
    /// when GitHub has a newer release, which downloads its setup and runs it. The setup waits
    /// for Gloam to exit, updates it and starts it again, so the app exits right after.
    /// </summary>
    public sealed class UpdateViewModel : ObservableObject
    {
        // Release notes are shown as plain text under the offer; a long body is cut so the
        // window stays on screen. The full notes are on the release page.
        internal const int MaxNotesLength = 1500;

        private readonly IUpdater _updater;
        private readonly Action _exit;
        private UpdateRelease? _available;

        public UpdateViewModel(IUpdater updater, Action exit)
        {
            _updater = updater ?? throw new ArgumentNullException(nameof(updater));
            _exit = exit ?? throw new ArgumentNullException(nameof(exit));
            ActCommand = new AsyncRelayCommand(ActAsync, () => !IsBusy);
        }

        public IAsyncRelayCommand ActCommand { get; }

        public string Version => "v" + (_updater.CurrentVersion?.ToString(3) ?? "?");

        private string _actionText = "Check for updates";
        public string ActionText
        {
            get => _actionText;
            private set => SetProperty(ref _actionText, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                    ActCommand.NotifyCanExecuteChanged();
            }
        }

        private string _status = string.Empty;
        public string Status
        {
            get => _status;
            private set
            {
                if (SetProperty(ref _status, value))
                    OnPropertyChanged(nameof(HasStatus));
            }
        }

        public bool HasStatus => Status.Length > 0;

        private string _notes = string.Empty;
        /// <summary>The newer release's notes; empty until one is found.</summary>
        public string Notes
        {
            get => _notes;
            private set
            {
                if (SetProperty(ref _notes, value))
                    OnPropertyChanged(nameof(HasNotes));
            }
        }

        public bool HasNotes => Notes.Length > 0;

        /// <summary>Checks for a newer release, or, once one is found, downloads and installs it.</summary>
        public Task ActAsync() => _available is { } release ? InstallAsync(release) : CheckAsync();

        private async Task CheckAsync()
        {
            IsBusy = true;
            ActionText = "Checking…";
            Status = string.Empty;
            try
            {
                UpdateCheckResult check = await _updater.CheckAsync();
                switch (check.Status)
                {
                    case UpdateCheckStatus.Available when check.Release is { } release:
                        string version = Text(release.Version);
                        Notes = release.Notes.Length > MaxNotesLength
                            ? release.Notes[..MaxNotesLength].TrimEnd() + "\n…"
                            : release.Notes;
                        if (!_updater.IsInstallation)
                        {
                            ActionText = "Check for updates";
                            Status = $"Gloam {version} is available. This copy is not an installation, so it " +
                                     $"cannot update itself: run the setup from {UpdateService.ReleasesPageUrl}.";
                            return;
                        }
                        _available = release;
                        ActionText = $"Update to {version}";
                        Status = $"Gloam {version} is available. Updating closes Gloam, installs it and starts it again.";
                        return;
                    case UpdateCheckStatus.UpToDate:
                        ActionText = "Check for updates";
                        Status = "You have the latest version.";
                        return;
                    default:
                        ActionText = "Check for updates";
                        Status = $"Could not check for updates: {check.Message ?? "unknown error"}";
                        return;
                }
            }
            catch (Exception ex)
            {
                ActionText = "Check for updates";
                Status = $"Could not check for updates: {Reason(ex)}";
                Log.Info($"UpdateViewModel: checking failed: {ex}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task InstallAsync(UpdateRelease release)
        {
            string version = Text(release.Version);
            IsBusy = true;
            ActionText = "Downloading…";
            Status = $"Downloading Gloam {version}.";
            try
            {
                string setup = await _updater.DownloadAsync(release);
                ActionText = "Installing…";
                Status = "The setup closes Gloam, updates it and starts it again.";
                _updater.StartInstaller(setup, release.Sha256);
                Log.Info($"UpdateViewModel: installing {release.Tag}; exiting for the setup.");
                // Busy for good: the setup waits for this process to exit.
                _exit();
            }
            catch (Exception ex)
            {
                IsBusy = false;
                ActionText = $"Update to {version}";
                Status = $"The update failed: {Reason(ex)}";
                Log.Error($"UpdateViewModel: updating to {version} failed: {ex}");
            }
        }

        private static string Text(Version version) => version.ToString(3);

        private static string Reason(Exception ex) => ex switch
        {
            TaskCanceledException => "GitHub did not answer in time",
            HttpRequestException { InnerException: { } inner } => inner.Message,
            IOException or HttpRequestException => ex.Message,
            _ => ex.GetBaseException().Message,
        };
    }
}
