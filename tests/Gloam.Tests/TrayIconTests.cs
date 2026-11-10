using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Gloam.Services;
using Xunit;

namespace Gloam.Tests
{
    /// <summary>The tray's three icons: all ship, at the tray sizes, and tell the profiles apart.</summary>
    public sealed class TrayIconTests
    {
        [Theory]
        [InlineData(TrayIcon.NormalIconUri)]
        [InlineData(TrayIcon.MediaIconUri)]
        [InlineData(TrayIcon.SdrIconUri)]
        public void Icon_IsAResourceWithTheTraySizes(string uri)
        {
            BitmapDecoder decoder = Decode(uri);

            int[] sizes = decoder.Frames.Select(f => f.PixelWidth).ToArray();
            // 16 and 20 px are the tray at 100% and 125% scaling, 24 and 32 at 150% and 200%.
            Assert.Subset(sizes.ToHashSet(), new[] { 16, 20, 24, 32 }.ToHashSet());
            Assert.Contains(256, sizes);
        }

        [Fact]
        public void MediaIcon_DiffersFromNormalByItsBadgeInTheCorner()
        {
            // The tray-size frames are compared, since that is where the badge must still show.
            byte[] normal = Pixels(Frame(TrayIcon.NormalIconUri, 16));
            byte[] media = Pixels(Frame(TrayIcon.MediaIconUri, 16));

            int Changed(int x0, int y0) => Enumerable.Range(y0, 8).SelectMany(y => Enumerable.Range(x0, 8), (y, x) => (y * 16 + x) * 4)
                .Count(i => Math.Abs(normal[i] - media[i]) + Math.Abs(normal[i + 1] - media[i + 1]) + Math.Abs(normal[i + 2] - media[i + 2]) > 60);

            Assert.True(Changed(8, 8) >= 12, $"{Changed(8, 8)} pixels changed in the bottom-right quarter");
            Assert.Equal(0, Changed(0, 0)); // the rest of the mark is the same
        }

        [Fact]
        public void HdrIcons_AreInColourAndSdrIsGrey()
        {
            // SDR is told apart from the two HDR profiles by having no colour at all.
            Assert.True(MeanChroma(Frame(TrayIcon.NormalIconUri, 16)) > 60);
            Assert.True(MeanChroma(Frame(TrayIcon.MediaIconUri, 16)) > 60);
            double sdr = MeanChroma(Frame(TrayIcon.SdrIconUri, 16));
            Assert.True(sdr < 12, $"SDR chroma {sdr:F0}");
        }

        private static BitmapDecoder Decode(string uri)
        {
            // The pack:// scheme is registered by WPF's Application type; touch it before
            // the Uri is parsed, or "pack://application:,,," reads as a bad port.
            _ = Application.Current;
            using var stream = Application.GetResourceStream(new Uri(uri, UriKind.Absolute))!.Stream;
            return BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }

        private static BitmapSource Frame(string uri, int size) =>
            Decode(uri).Frames.First(f => f.PixelWidth == size);

        private static byte[] Pixels(BitmapSource frame)
        {
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int stride = bgra.PixelWidth * 4;
            byte[] pixels = new byte[stride * bgra.PixelHeight];
            bgra.CopyPixels(pixels, stride, 0);
            return pixels;
        }

        /// <summary>Mean of max(R,G,B) − min(R,G,B), weighted by alpha: 0 for pure greys.</summary>
        private static double MeanChroma(BitmapSource frame)
        {
            byte[] pixels = Pixels(frame);
            double chroma = 0, weight = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                double alpha = pixels[i + 3] / 255.0;
                int max = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
                int min = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
                chroma += (max - min) * alpha;
                weight += alpha;
            }
            return chroma / weight;
        }
    }
}
