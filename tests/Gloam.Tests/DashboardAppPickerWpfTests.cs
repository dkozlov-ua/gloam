using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Gloam.Core;
using Gloam.Services;
using Gloam.ViewModels;
using Xunit;

namespace Gloam.Tests
{
    public sealed class DashboardAppPickerWpfTests
    {
        [Fact]
        public void Picker_StaysOpenAcrossLiveRefresh_AndAddButtonPersistsTypedApp()
        {
            RunSta(() =>
            {
                string originalData = AppPaths.DataDir;
                string originalRoaming = AppPaths.RoamingDataDir;
                string root = Path.Combine(Path.GetTempPath(), $"gloam-picker-wpf-{Guid.NewGuid():N}");
                Application? app = null;
                DashboardWindow? window = null;
                NightModeService? nightMode = null;
                try
                {
                    AppPaths.UseDataDirectoriesForCurrentProcess(
                        Path.Combine(root, "data"), Path.Combine(root, "roaming"));
                    app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/Gloam;component/Themes/Tokens.xaml")
                    });

                    var settings = new SettingsManager();
                    nightMode = new NightModeService(settings.NightMode);
                    var update = new UpdateService();
                    window = new DashboardWindow(
                        new MonitorManager(), settings, nightMode, update,
                        (_, _, _, _, _) => { })
                    {
                        ShowInTaskbar = false,
                        Left = -10000,
                        Top = -10000,
                        Opacity = 0
                    };
                    window.Show();
                    Pump(TimeSpan.FromMilliseconds(100));

                    // The Displays header scrolls with the content rather than staying pinned.
                    Assert.True(HasVisualAncestor<ScrollViewer>(
                        Assert.IsType<Grid>(window.FindName("DashboardHeader"))));
                    SaveScreenshotIfRequested(window, "dashboard-top.png");

                    var viewModel = Assert.IsType<DashboardViewModel>(window.DataContext);
                    Assert.Equal(update.DisplayVersion,
                        Assert.IsType<TextBlock>(window.FindName("AppVersionLabel")).Text);
                    var exclusionItem = Assert.Single(viewModel.Items.OfType<AppExclusionItem>());
                    exclusionItem.RunningApps.Add("photoshop.exe");

                    var items = Assert.IsType<ItemsControl>(window.FindName("DashboardItems"));
                    items.UpdateLayout();
                    var presenter = Assert.IsType<ContentPresenter>(
                        items.ItemContainerGenerator.ContainerFromItem(exclusionItem));
                    presenter.ApplyTemplate();
                    var picker = Assert.IsType<ComboBox>(
                        FindVisualChild<ComboBox>(presenter, "ExcludedAppPicker"));
                    var add = Assert.IsType<Button>(
                        FindVisualChild<Button>(presenter, "AddExcludedAppButton"));

                    picker.IsDropDownOpen = true;
                    viewModel.Refresh(reEnumerate: false); // same path as a night-mode blend tick
                    Pump(TimeSpan.FromMilliseconds(1100));

                    Assert.True(picker.IsDropDownOpen);
                    Assert.Same(presenter,
                        items.ItemContainerGenerator.ContainerFromItem(exclusionItem));

                    picker.Text = @"C:\Program Files\Adobe\Adobe Photoshop\Photoshop";
                    picker.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
                    Pump(TimeSpan.FromMilliseconds(50));
                    Assert.EndsWith("Photoshop", exclusionItem.NewAppText, StringComparison.OrdinalIgnoreCase);
                    Assert.NotNull(add.Command);
                    Assert.True(add.Command.CanExecute(add.CommandParameter));
                    add.Command.Execute(add.CommandParameter);
                    Pump(TimeSpan.FromMilliseconds(50));

                    Assert.Equal("Photoshop.exe", Assert.Single(exclusionItem.ExcludedApps).AppName);
                    Assert.Equal("Photoshop.exe", Assert.Single(settings.ExcludedApps).AppName);

                    var gamerPicker = Assert.IsType<ComboBox>(window.FindName("GamerAppPicker"));
                    var gamerAdd = Assert.IsType<Button>(window.FindName("AddGamerProfileButton"));
                    gamerPicker.Text = @"C:\Games\Arena\arena";
                    gamerPicker.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
                    Assert.NotNull(gamerAdd.Command);
                    gamerAdd.Command.Execute(gamerAdd.CommandParameter);
                    Pump(TimeSpan.FromMilliseconds(50));

                    GamerProfileEditorItem editor = Assert.Single(viewModel.GamerMode.Profiles);
                    GamerProfileRule stored = Assert.Single(settings.GamerProfiles);
                    Assert.Equal("arena.exe", stored.AppName);
                    Assert.Equal(GamerPictureIntent.Reference, settings.MediaProfile.PictureIntent);

                    // Look edits go to the one Media profile, never to a library entry.
                    GamerProfileEditorItem media = viewModel.GamerMode.MediaProfile;
                    media.PictureIntent = GamerPictureIntent.NightOps;
                    Pump(TimeSpan.FromMilliseconds(350));
                    GamerProfileRule storedMedia = settings.MediaProfile;
                    Assert.Equal(GamerPictureIntent.NightOps, storedMedia.PictureIntent);
                    Assert.Equal(GamerNightPolicy.NightOps, storedMedia.NightPolicy);
                    Assert.True(storedMedia.ShadowDetailStrength > 0);
                    Assert.Equal(0.0, storedMedia.NightOpsMelanopicCeiling);
                    Assert.Equal(string.Empty, storedMedia.AppName);
                    editor.SetExecutablePath(Path.Combine(root, "games", "arena.exe"));
                    editor.Enabled = true;
                    Pump(TimeSpan.FromMilliseconds(350));
                    Assert.True(Assert.Single(settings.GamerProfiles).Enabled);
                    viewModel.IsGameProfileAdvancedExpanded = true;
                    window.UpdateLayout();
                    var dashboardRunning = Assert.IsType<CheckBox>(
                        FindVisualChild<CheckBox>(window, "ApplyWhileRunningBox"));
                    Assert.False(dashboardRunning.IsChecked);
                    dashboardRunning.IsChecked = true;
                    Pump(TimeSpan.FromMilliseconds(350));
                    Assert.True(media.ApplyWhileRunning);
                    Assert.True(settings.MediaProfile.ApplyWhileRunning);

                    Assert.Equal("Night Play", media.AvailablePictureIntents[3].ToString());

                    // The status-strip selector is the same sticky choice as --profile-set.
                    var selectionBox = Assert.IsType<ComboBox>(window.FindName("ProfileSelectionBox"));
                    Assert.Equal(DisplayProfileSelection.Automatic, selectionBox.SelectedValue);
                    selectionBox.SelectedValue = DisplayProfileSelection.Media;
                    Pump(TimeSpan.FromMilliseconds(80));
                    Assert.Equal(DisplayProfileSelection.Media, settings.ProfileSelection);
                    Assert.Equal("MEDIA · MANUAL", viewModel.GamerModeStateTitle);
                    Assert.True(settings.TrySetProfileSelection(DisplayProfileSelection.Automatic));
                    Pump(TimeSpan.FromMilliseconds(80));
                    Assert.Equal(DisplayProfileSelection.Automatic, selectionBox.SelectedValue);

                    var largeLibrary = settings.GamerProfiles;
                    largeLibrary[0].HdrCapability = GamerHdrCapability.Detected;
                    for (int i = 0; i < 120; i++)
                    {
                        largeLibrary.Add(new GamerProfileRule
                        {
                            AppName = $"game-{i:000}.exe",
                            ExecutablePath = Path.Combine(root, "games", $"game-{i:000}.exe"),
                            DisplayName = $"Game {i:000}",
                            HdrCapability = i == 0
                                ? GamerHdrCapability.UserConfirmed
                                : GamerHdrCapability.Unknown,
                            LastUsedUtc = i < 8 ? DateTime.UtcNow.AddMinutes(-i) : null
                        });
                    }
                    settings.SetGamerProfiles(largeLibrary);
                    Pump(TimeSpan.FromMilliseconds(260));

                    var dashboardProfileList = Assert.IsType<ListBox>(window.FindName("DashboardGameProfileList"));
                    Assert.Equal(5, dashboardProfileList.Items.Count);
                    ScrollViewer dashboardProfileScroll = Assert.Single(
                        FindVisualChildren<ScrollViewer>(dashboardProfileList));
                    Assert.Equal(Visibility.Collapsed,
                        dashboardProfileScroll.ComputedVerticalScrollBarVisibility);
                    Assert.Equal(Visibility.Collapsed,
                        dashboardProfileScroll.ComputedHorizontalScrollBarVisibility);
                    Rect recentCountBounds = BoundsIn(
                        Assert.IsType<TextBlock>(window.FindName("DashboardProfileCount")), window);
                    Rect viewAllBounds = BoundsIn(
                        Assert.IsType<Button>(window.FindName("DashboardShowAllProfilesButton")), window);
                    Rect libraryPaneBounds = BoundsIn(
                        Assert.IsType<Border>(window.FindName("DashboardGameLibraryPane")), window);
                    Assert.True(recentCountBounds.Right + 4 <= viewAllBounds.Left);
                    Assert.True(viewAllBounds.Right <= libraryPaneBounds.Right - 9);
                    Assert.True(viewAllBounds.Width >= 48);

                    // Game Lab is always shown on the Dashboard; nothing can collapse it.
                    var gameLabSection = Assert.IsType<StackPanel>(window.FindName("GameLabSection"));
                    Assert.False(HasVisualAncestor<Expander>(gameLabSection));
                    Assert.Null(window.FindName("GamerModeExpander"));
                    var workspace = Assert.IsType<Grid>(window.FindName("LibraryWorkspace"));
                    Assert.Equal(Visibility.Visible, workspace.Visibility);
                    // The library sits in the Dashboard's ScrollViewer, so its host has a fixed
                    // height: the list must stay bounded (and virtualized) inside it.
                    Assert.True(dashboardProfileList.ActualHeight <= workspace.ActualHeight);
                    gameLabSection.BringIntoView();
                    Pump(TimeSpan.FromMilliseconds(120));
                    SaveScreenshotIfRequested(window, "dashboard-game-library.png");

                    // Selecting a game no longer swaps the editor: the Media profile stays put.
                    Assert.NotNull(viewModel.GamerMode.SelectedProfile);
                    viewModel.GamerMode.SelectedProfile = viewModel.GamerMode.FilteredProfiles.Last();
                    Pump(TimeSpan.FromMilliseconds(60));
                    var mediaEditor = Assert.IsType<Grid>(window.FindName("MediaProfileEditor"));
                    Assert.Same(viewModel.GamerMode.MediaProfile, mediaEditor.DataContext);
                    viewModel.GamerMode.MediaProfile.PictureIntent = GamerPictureIntent.CompetitiveClarity;
                    Pump(TimeSpan.FromMilliseconds(260));
                    SaveScreenshotIfRequested(window, "dashboard-game-library-detail.png");

                    viewModel.GamerMode.MediaProfile.PictureIntent = GamerPictureIntent.NightOps;
                    Expander advanced = Assert.Single(
                        FindVisualChildren<Expander>(workspace),
                        expander => Equals(expander.Header, "Advanced signal & display controls"));
                    advanced.IsExpanded = true;
                    Pump(TimeSpan.FromMilliseconds(260));
                    Assert.True(advanced.ActualHeight < workspace.ActualHeight);

                    // Per-game name and HDR-support fields are gone with per-game settings.
                    Assert.Null(FindVisualChild<TextBox>(workspace, "AdvancedProfileNameBox"));
                    Assert.Null(FindVisualChild<ComboBox>(workspace, "AdvancedHdrSupportBox"));
                    Rect expectedSignalBounds = BoundsIn(
                        Assert.IsType<ComboBox>(FindVisualChild<ComboBox>(workspace, "AdvancedExpectedSignalBox")),
                        workspace);
                    Rect targetDisplaysBounds = BoundsIn(
                        Assert.IsType<ComboBox>(FindVisualChild<ComboBox>(workspace, "AdvancedTargetDisplaysBox")),
                        workspace);
                    var gammaBox = Assert.IsType<ComboBox>(FindVisualChild<ComboBox>(workspace, "AdvancedGammaModeBox"));
                    Rect gammaBounds = BoundsIn(gammaBox, workspace);
                    // The native Windows curve is not offered as a gamma choice.
                    gammaBox.IsDropDownOpen = true;
                    Pump(TimeSpan.FromMilliseconds(80));
                    for (int i = 0; i < gammaBox.Items.Count; i++)
                    {
                        var choice = Assert.IsType<GamerChoice<GammaMode>>(gammaBox.Items[i]);
                        var container = Assert.IsType<ComboBoxItem>(gammaBox.ItemContainerGenerator.ContainerFromIndex(i));
                        Assert.Equal(
                            choice.Value == GammaMode.WindowsDefault ? Visibility.Collapsed : Visibility.Visible,
                            container.Visibility);
                    }
                    gammaBox.IsDropDownOpen = false;
                    Pump(TimeSpan.FromMilliseconds(40));
                    Rect paperWhiteBounds = BoundsIn(
                        Assert.IsType<TextBox>(FindVisualChild<TextBox>(workspace, "AdvancedPaperWhiteBox")),
                        workspace);
                    Rect peakNitsBounds = BoundsIn(
                        Assert.IsType<TextBox>(FindVisualChild<TextBox>(workspace, "AdvancedPeakNitsBox")),
                        workspace);
                    // Night-mode controls are hidden.
                    Assert.False(Assert.IsType<ComboBox>(
                        FindVisualChild<ComboBox>(workspace, "AdvancedNightBehaviorBox")).IsVisible);
                    Assert.False(Assert.IsType<TextBox>(
                        FindVisualChild<TextBox>(workspace, "AdvancedMelanopicCeilingBox")).IsVisible);

                    AssertAligned(expectedSignalBounds.Top, targetDisplaysBounds.Top);
                    AssertAligned(gammaBounds.Top, paperWhiteBounds.Top, peakNitsBounds.Top);
                    AssertAligned(expectedSignalBounds.Right, gammaBounds.Right);
                    AssertAligned(targetDisplaysBounds.Left, paperWhiteBounds.Left);
                    AssertAligned(targetDisplaysBounds.Right, peakNitsBounds.Right);
                    Assert.True(expectedSignalBounds.Right < targetDisplaysBounds.Left);
                    Assert.True(gammaBounds.Right < paperWhiteBounds.Left);
                    Assert.True(paperWhiteBounds.Right < peakNitsBounds.Left);
                    var runningBox = Assert.IsType<CheckBox>(
                        FindVisualChild<CheckBox>(workspace, "ApplyWhileRunningBox"));
                    Assert.True(runningBox.IsChecked);
                    runningBox.IsChecked = false;
                    Pump(TimeSpan.FromMilliseconds(350));
                    Assert.False(settings.MediaProfile.ApplyWhileRunning);
                    SaveScreenshotIfRequested(window, "dashboard-game-lab-advanced.png");
                    advanced.IsExpanded = false;

                    var discovered = Enumerable.Range(0, 80)
                        .Select(i => new DiscoveredGame(
                            $"Found Game {i:000}",
                            $"found-{i:000}.exe",
                            Path.Combine(root, "found", $"found-{i:000}.exe"),
                            i % 2 == 0 ? "Steam" : "Epic"))
                        .ToArray();
                    viewModel.GamerMode.SetDiscoveryResults(
                        discovered, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    foreach (DiscoveredGameItem item in viewModel.GamerMode.DiscoveredGames.Take(3))
                        item.IsSelected = true;
                    Pump(TimeSpan.FromMilliseconds(80));
                    Assert.Equal(Visibility.Visible,
                        Assert.IsType<Border>(window.FindName("GameScanPanel")).Visibility);
                    Assert.Equal(Visibility.Collapsed, workspace.Visibility);
                    Assert.Equal(80,
                        Assert.IsType<ListBox>(window.FindName("DiscoveredGamesList")).Items.Count);
                    SaveScreenshotIfRequested(window, "dashboard-game-lab-scan-review.png");
                }
                finally
                {
                    try { window?.Close(); } catch { }
                    nightMode?.Dispose();
                    // Deliberately NOT app.Shutdown(). The window is already closed above,
                    // and Shutdown is a PROCESS-level teardown: it tears down WPF's input
                    // stack for the whole test run, after which Mouse.Capture silently fails
                    // in every later test (ProbePlacementDragTests, which exists because a
                    // capture bug froze the calibration window, cannot then verify anything).
                    // An idle Application object costs the test process nothing.
                    _ = app;
                    AppPaths.UseDataDirectoriesForCurrentProcess(originalData, originalRoaming);
                    try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
                }
            });
        }

        private static void Pump(TimeSpan duration)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = duration
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        private static T? FindVisualChild<T>(DependencyObject root, string name)
            where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match && match.Name == name) return match;
                var nested = FindVisualChild<T>(child, name);
                if (nested != null) return nested;
            }
            return null;
        }

        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
            where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (T nested in FindVisualChildren<T>(child))
                    yield return nested;
            }
        }

        private static bool HasVisualAncestor<T>(DependencyObject child)
            where T : DependencyObject
        {
            DependencyObject? current = VisualTreeHelper.GetParent(child);
            while (current != null)
            {
                if (current is T) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private static Rect BoundsIn(FrameworkElement element, Visual ancestor)
        {
            Point origin = element.TransformToAncestor(ancestor).Transform(new Point(0, 0));
            return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
        }

        private static void AssertAligned(params double[] coordinates)
        {
            Assert.NotEmpty(coordinates);
            double expected = coordinates[0];
            foreach (double coordinate in coordinates.Skip(1))
                Assert.InRange(coordinate, expected - 0.75, expected + 0.75);
        }

        private static void SaveScreenshotIfRequested(Window window, string fileName)
        {
            string? outputDirectory = Environment.GetEnvironmentVariable("GLOAM_UI_SCREENSHOT_DIR");
            if (string.IsNullOrWhiteSpace(outputDirectory)) return;

            Directory.CreateDirectory(outputDirectory);
            double originalOpacity = window.Opacity;
            window.Opacity = 1;
            window.UpdateLayout();
            DpiScale dpi = VisualTreeHelper.GetDpi(window);
            int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
            int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(
                width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream output = File.Create(Path.Combine(outputDirectory, fileName));
            encoder.Save(output);
            window.Opacity = originalOpacity;
        }

        private static void RunSta(Action body)
        {
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            failure?.Throw();
        }
    }
}
