using Gloam.Services;
using Xunit;

namespace Gloam.Tests
{
    /// <summary>
    /// Tests for the Run-key value and its repair decision. Registry access itself is not
    /// unit-testable, so <see cref="StartupManager.RunValue"/> and
    /// <see cref="StartupManager.NeedsRepair"/> are exercised as pure functions; the UI tests
    /// cover the registry round trip under their own profile.
    /// </summary>
    public class StartupManagerTests
    {
        private const string CurrentExe = @"C:\Users\Dev\AppData\Local\Programs\Gloam\Gloam.exe";

        [Fact]
        public void RunValue_QuotesThePathAndStartsInTheTray()
        {
            Assert.Equal($"\"{CurrentExe}\" --background", StartupManager.RunValue(CurrentExe));
        }

        [Theory]
        [InlineData("\"C:\\Users\\Dev\\AppData\\Local\\Programs\\Gloam\\Gloam.exe\" --background")]   // exact
        [InlineData("\"c:\\users\\dev\\appdata\\local\\programs\\gloam\\gloam.exe\" --BACKGROUND")]   // case differs
        [InlineData("  \"C:\\Users\\Dev\\AppData\\Local\\Programs\\Gloam\\Gloam.exe\" --background  ")] // padded
        public void NeedsRepair_TheCurrentCommand_IsLeftAlone(string registered)
        {
            Assert.False(StartupManager.NeedsRepair(registered, CurrentExe));
        }

        [Theory]
        // Before --background, logon opened the window: the old value is rewritten.
        [InlineData("\"C:\\Users\\Dev\\AppData\\Local\\Programs\\Gloam\\Gloam.exe\"")]
        [InlineData(@"C:\Users\Dev\AppData\Local\Programs\Gloam\Gloam.exe")]
        [InlineData("\"C:\\Program Files\\HDR-Gamma-Adjust\\Gloam.exe\"")]              // legacy install dir
        [InlineData("\"C:\\Deleted\\Nowhere\\Gloam.exe\" --background")]                // another location
        [InlineData("\"C:\\Users\\Dev\\AppData\\Local\\GloamApp\\current\\Gloam.exe\"")] // Velopack layout
        public void NeedsRepair_AnotherCommand_IsRewritten(string registered)
        {
            Assert.True(StartupManager.NeedsRepair(registered, CurrentExe));
        }

        [Fact]
        public void NeedsRepair_NoRunValue_HasNothingToRepair()
        {
            // No Run value (startup disabled) means there is nothing to repair.
            Assert.False(StartupManager.NeedsRepair(null, CurrentExe));
        }
    }
}
