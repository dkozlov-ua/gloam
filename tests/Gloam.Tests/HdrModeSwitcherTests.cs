using Gloam.Core;
using Xunit;

namespace Gloam.Tests
{
    /// <summary>Normal and Media put HDR-capable displays in HDR; SDR takes them out of it.</summary>
    public sealed class HdrModeSwitcherTests
    {
        private readonly FakeMonitorSource _monitors = new()
        {
            Monitors =
            {
                FakeMonitorSource.Monitor(1, hdr: true),
                FakeMonitorSource.Monitor(2, hdr: false, capable: true),
                FakeMonitorSource.Monitor(3, hdr: false, capable: false),
            },
        };

        [Theory]
        [InlineData(DisplayProfileSelection.Normal, true)]
        [InlineData(DisplayProfileSelection.Media, true)]
        [InlineData(DisplayProfileSelection.Sdr, false)]
        public void WantsHdr_OnlySdrTurnsItOff(DisplayProfileSelection selection, bool hdr)
        {
            Assert.Equal(hdr, HdrModeSwitcher.WantsHdr(selection));
        }

        [Fact]
        public void Sdr_TurnsHdrOffOnTheDisplaysThatHaveIt()
        {
            var hdr = new FakeHdrSwitch(_monitors);

            int switched = new HdrModeSwitcher(hdr).Apply(_monitors, () => DisplayProfileSelection.Sdr);

            Assert.Equal(1, switched);
            Assert.Equal(new[] { (@"\\.\DISPLAY1", false) }, hdr.Switches);
        }

        [Theory]
        [InlineData(DisplayProfileSelection.Normal)]
        [InlineData(DisplayProfileSelection.Media)]
        public void NormalAndMedia_TurnHdrOnWhereTheDisplayCanShowIt(DisplayProfileSelection selection)
        {
            var hdr = new FakeHdrSwitch(_monitors);

            int switched = new HdrModeSwitcher(hdr).Apply(_monitors, () => selection);

            // Display 1 is already in HDR and display 3 cannot do it; only display 2 changes.
            Assert.Equal(1, switched);
            Assert.Equal(new[] { (@"\\.\DISPLAY2", true) }, hdr.Switches);
        }

        [Fact]
        public void DisplaysAlreadyInTheMode_AreLeftAlone()
        {
            var hdr = new FakeHdrSwitch(_monitors);
            var switcher = new HdrModeSwitcher(hdr);
            switcher.Apply(_monitors, () => DisplayProfileSelection.Sdr);
            hdr.Switches.Clear();

            Assert.Equal(0, switcher.Apply(_monitors, () => DisplayProfileSelection.Sdr));
            Assert.Empty(hdr.Switches);
        }

        [Fact]
        public void RefusedSwitch_IsNotCounted()
        {
            var hdr = new FakeHdrSwitch(_monitors) { Accepts = false };

            Assert.Equal(0, new HdrModeSwitcher(hdr).Apply(_monitors, () => DisplayProfileSelection.Sdr));
            Assert.True(_monitors.Monitors[0].IsHdrActive);
        }
    }
}
