using System;
using MouseJiggler.App;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The command line, which is a contract rather than a convenience.
    /// </summary>
    /// <remarks>
    /// The installer runs this executable with arguments and reads its exit code, so a change to
    /// either is a change to something outside this repository. The switch names appear in
    /// installer/MouseJiggler.iss and the exit codes are branched on there.
    ///
    /// Nothing here starts a process. The parser is exercised directly, so a case that would be
    /// awkward to reach through the installer, such as an argument that looks like a path, is
    /// just as easy to state as any other.
    /// </remarks>
    public sealed class CommandLineTests
    {
        [Fact]
        public void NoArgumentsMeansAnOrdinaryInteractiveLaunch()
        {
            CommandLineOptions options = CommandLineOptions.Parse(Array.Empty<string>());

            Assert.False(options.HasError);
            Assert.False(options.QuietStartup);
            Assert.False(options.ShowSettings);
            Assert.False(options.ShutdownForUpdate);
            Assert.False(options.InstallInitialize);
            Assert.Null(options.StartupEnabled);
        }

        [Fact]
        public void TheStartupSwitchIsWhatKeepsSignInQuiet()
        {
            CommandLineOptions options = CommandLineOptions.Parse(new[] { "--startup" });

            // This is what stops the Settings window appearing over whatever the user is doing
            // as they sign in, so it is the one switch a startup entry must carry.
            Assert.True(options.QuietStartup);
            Assert.False(options.HasError);
        }

        [Theory]
        [InlineData("--show-settings")]
        [InlineData("--shutdown-for-update")]
        public void EachRecognisedSwitchIsAccepted(string switchName)
        {
            CommandLineOptions options = CommandLineOptions.Parse(new[] { switchName });

            Assert.False(options.HasError);
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("false", false)]
        public void InstallerInitializationCarriesItsStartupDecision(string value, bool expected)
        {
            CommandLineOptions options = CommandLineOptions.Parse(
                new[] { "--install-initialize", "--startup-enabled", value });

            Assert.False(options.HasError);
            Assert.True(options.InstallInitialize);
            Assert.Equal(expected, options.StartupEnabled);
        }

        [Fact]
        public void InitializationWithoutItsDecisionIsRefused()
        {
            // The installer always passes both. One without the other means something has gone
            // wrong in the installer script, and guessing a default would silently register or
            // unregister startup on the strength of a bug.
            Assert.True(CommandLineOptions.Parse(new[] { "--install-initialize" }).HasError);
        }

        [Fact]
        public void ADecisionWithoutInitializationIsAlsoRefused()
        {
            Assert.True(CommandLineOptions.Parse(new[] { "--startup-enabled", "true" }).HasError);
        }

        [Fact]
        public void AMissingValueAfterStartupEnabledIsRefusedRatherThanReadingPastTheEnd()
        {
            Assert.True(CommandLineOptions.Parse(new[] { "--install-initialize", "--startup-enabled" }).HasError);
        }

        [Theory]
        [InlineData("True")]
        [InlineData("TRUE")]
        [InlineData("yes")]
        [InlineData("1")]
        [InlineData("")]
        public void OnlyTheExactLowercaseBooleansAreAccepted(string value)
        {
            // Case-sensitive on purpose. The installer writes exactly "true" or "false", so
            // anything else did not come from the installer, and accepting it would widen the
            // contract to whatever a future caller happened to send.
            Assert.True(CommandLineOptions.Parse(new[] { "--install-initialize", "--startup-enabled", value }).HasError);
        }

        [Theory]
        [InlineData("--Startup")]
        [InlineData("--STARTUP")]
        [InlineData("-startup")]
        [InlineData("/startup")]
        public void SwitchNamesAreExactRatherThanApproximate(string argument)
        {
            Assert.True(CommandLineOptions.Parse(new[] { argument }).HasError);
        }

        [Theory]
        [InlineData(@"C:\Windows\System32\calc.exe")]
        [InlineData(@"\\server\share\payload.exe")]
        [InlineData("settings.json")]
        [InlineData("--startup=true")]
        [InlineData("http://example.invalid/x")]
        public void NothingIsEverTreatedAsAPathToOpenOrRun(string argument)
        {
            // An unrecognised argument is an error, never a file to act on. A jiggler that
            // opened whatever it was handed would be a useful thing to put in a shortcut.
            CommandLineOptions options = CommandLineOptions.Parse(new[] { argument });

            Assert.True(options.HasError);
            Assert.False(options.QuietStartup);
            Assert.False(options.InstallInitialize);
        }

        [Fact]
        public void AnUnknownArgumentAmongValidOnesStillFailsTheWholeLine()
        {
            Assert.True(CommandLineOptions.Parse(new[] { "--startup", "--wat" }).HasError);
            Assert.True(CommandLineOptions.Parse(new[] { "--wat", "--startup" }).HasError);
        }

        [Fact]
        public void RepeatingASwitchIsHarmless()
        {
            CommandLineOptions options = CommandLineOptions.Parse(new[] { "--startup", "--startup" });

            Assert.False(options.HasError);
            Assert.True(options.QuietStartup);
        }

        [Fact]
        public void AMissingArgumentArrayIsAProgrammingErrorRatherThanAnEmptyLine()
        {
            Assert.Throws<ArgumentNullException>(() => CommandLineOptions.Parse(null!));
        }

        [Fact]
        public void TheSwitchNamesAreTheOnesTheInstallerSends()
        {
            // If one of these is renamed, the installer keeps sending the old name and the
            // process exits 2 in the middle of an install. Naming them here means the rename
            // breaks a test rather than an installation.
            Assert.Equal("--startup", Program.StartupSwitch);
            Assert.Equal("--show-settings", Program.ShowSettingsSwitch);
            Assert.Equal("--shutdown-for-update", Program.ShutdownForUpdateSwitch);
            Assert.Equal("--install-initialize", Program.InstallInitializeSwitch);
        }

        [Fact]
        public void TheExitCodesAreTheOnesTheInstallerBranchesOn()
        {
            Assert.Equal(0, ExitCodes.Success);
            Assert.Equal(2, ExitCodes.InvalidArguments);
            Assert.Equal(3, ExitCodes.OwnershipConflict);
            Assert.Equal(4, ExitCodes.PersistenceFailed);
        }
    }
}
