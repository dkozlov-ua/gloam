using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Gloam.Core;
using Gloam.TestSupport;
using Gloam.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Gloam.Tests
{
    /// <summary>
    /// The real flyout, built from its XAML under the app's Fluent resources, bound to a view
    /// model over fake displays. It renders every section (a binding or resource error shows as
    /// a missing control or a blank window) and is saved as a PNG for review.
    /// </summary>
    /// <remarks>Needs an interactive desktop session, like the other WPF tests.</remarks>
    public sealed class FlyoutWindowTests(ITestOutputHelper output)
    {
        [Fact]
        public void Flyout_RendersEverySectionWithItsControls()
        {
            // The window takes the foreground; the UI tests' Gloam must not be on screen then.
            using var desktop = DesktopLock.Take();
            WpfTestHost.Run(() =>
            {
                using var data = new TempDataScope();
                var settings = new SettingsManager();
                var monitors = new FakeMonitorSource
                {
                    Monitors = { FakeMonitorSource.Monitor(1, hdr: true, white: 240), FakeMonitorSource.Monitor(2, hdr: false) },
                };
                using var apply = new GammaApplyService(new GammaRampWriter(new FakeGammaRamp()), settings);
                using var vm = new MainViewModel(settings, monitors, apply, new FakeUpdater(), new FakeStartupRegistration(),
                    post: action => action(), exit: () => { });
                vm.Start();
                vm.Media.OverrideBrightness = true;
                vm.IsMedia = true;

                if (Application.Current == null)
                {
                    var app = new App();
                    app.InitializeComponent();
                }

                var window = new MainWindow(vm) { Left = -20000, Top = -20000 };
                window.Show();
                WpfTestHost.Pump(TimeSpan.FromMilliseconds(300));
                try
                {
                    var ids = Descendants(window)
                        .Select(AutomationProperties.GetAutomationId)
                        .Where(id => !string.IsNullOrEmpty(id))
                        .ToHashSet();
                    foreach (string id in new[]
                             {
                                 "ProfileNormal", "ProfileMedia", "ProfileSdr", "DisplayPicker", "DisplayGamma22", "DisplayGamma24",
                                 "DisplayBrightness", "DisplayMediaNote", "MediaOverrideGamma",
                                 "MediaGamma22", "MediaOverrideBrightness", "MediaBrightness",
                                 "MediaAppliesTo", "StartWithWindows", "Update", "ExportDiagnostics", "Exit", "Version",
                             })
                        Assert.Contains(id, ids);
                    // Shadow-preserving dimming and dark-area visibility are not offered.
                    Assert.DoesNotContain("DisplayPreserveShadows", ids);
                    Assert.DoesNotContain("MediaShadowDetail", ids);

                    var media = Descendants(window).OfType<RadioButton>().Single(r => AutomationProperties.GetAutomationId(r) == "ProfileMedia");
                    Assert.True(media.IsChecked);
                    var note = Descendants(window).OfType<TextBlock>().Single(t => AutomationProperties.GetAutomationId(t) == "DisplayMediaNote");
                    // Shown whenever Media is on (IsVisible is no use here: the flyout hides itself
                    // when it loses activation, which a test run cannot prevent).
                    for (DependencyObject e = note; e is not Window; e = VisualTreeHelper.GetParent(e))
                        Assert.Equal(Visibility.Visible, ((UIElement)e).Visibility);

                    if (!window.IsVisible) window.Show();
                    window.UpdateLayout();
                    var bitmap = Render(window);
                    string path = Path.Combine(Path.GetTempPath(), "GloamFlyout.png");
                    using (var file = File.Create(path))
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        encoder.Save(file);
                    }
                    output.WriteLine($"Flyout rendered to {path}");

                    // Text and controls were drawn: plenty of pixels differ from the background.
                    Assert.True(DistinctFromCorner(bitmap) > 0.03, "The flyout rendered (almost) blank.");
                }
                finally
                {
                    window.CloseForExit();
                }
            });
        }

        private static RenderTargetBitmap Render(Window window)
        {
            var root = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap(
                (int)(root.ActualWidth * 2), (int)(root.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                context.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }
            bitmap.Render(visual);
            return bitmap;
        }

        private static double DistinctFromCorner(BitmapSource bitmap)
        {
            int stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            uint corner = BitConverter.ToUInt32(pixels, 0);
            int differing = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (BitConverter.ToUInt32(pixels, i) != corner) differing++;
            }
            return differing / (double)(pixels.Length / 4);
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (DependencyObject grandchild in Descendants(child))
                    yield return grandchild;
            }
        }
    }
}
