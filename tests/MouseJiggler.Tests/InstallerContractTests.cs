using System;
using System.IO;
using MouseJiggler.Windows.Diagnostics;
using MouseJiggler.Windows.Settings;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The names the uninstaller has to know, checked against the names the app actually uses.
    /// </summary>
    /// <remarks>
    /// The installer script is Pascal in a separate file, so nothing in the compiler connects it
    /// to these constants. Rename the log file or the quarantine prefix and the application keeps
    /// working perfectly, the installer keeps compiling, and the only thing that changes is that
    /// "delete my settings and diagnostics" quietly stops deleting some of them. The user is told
    /// their data is gone while it is still on disk, which is the worst of both outcomes.
    ///
    /// So the names are asserted from the far side. This is a text search over the script rather
    /// than a behavioural test, and it cannot say the deletion works. What it can say is that a
    /// rename will break a test instead of a promise.
    /// </remarks>
    public sealed class InstallerContractTests
    {
        private static string ReadInstallerScript()
        {
            string path = RepositoryFile("installer", "MouseJiggler.iss");
            Assert.True(File.Exists(path), "The installer script was not found at " + path + ".");
            return File.ReadAllText(path);
        }

        private static string RepositoryFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MouseJiggler.sln")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);

            string path = directory!.FullName;
            foreach (string part in parts)
            {
                path = Path.Combine(path, part);
            }

            return path;
        }

        [Theory]
        [InlineData(SettingsPaths.SettingsFileName)]
        [InlineData(SettingsPaths.BackupFileName)]
        [InlineData(SettingsPaths.LockFileName)]
        [InlineData(SettingsPaths.InvalidBackupPrefix)]
        [InlineData(SettingsPaths.TempFilePrefix)]
        [InlineData(LocalDiagnosticSink.LogFileName)]
        [InlineData(SettingsPaths.LogsDirectoryName)]
        public void EveryOwnedDataFileNameAppearsInTheUninstaller(string name)
        {
            Assert.Contains(name, ReadInstallerScript(), StringComparison.Ordinal);
        }

        [Fact]
        public void TheUninstallerDeletesAsManyNumberedLogBackupsAsTheSinkKeeps()
        {
            // The sink rotates to .1 through .4. The uninstaller loops to a literal 4, because
            // Pascal cannot see the constant. If the sink ever keeps more, the extra ones would
            // survive a request to delete them.
            Assert.Equal(4, LocalDiagnosticSink.MaxBackups);
            Assert.Contains("for Index := 1 to 4 do", ReadInstallerScript(), StringComparison.Ordinal);
        }

        [Fact]
        public void TheDataDirectoryIsNamedRatherThanComputed()
        {
            // A wrong constant here points a delete at somebody else's folder, so the exact
            // expansion is worth pinning rather than trusting.
            Assert.Equal("MouseJiggler", SettingsPaths.ProductDirectoryName);
            Assert.Contains(
                @"ExpandConstant('{localappdata}\MouseJiggler')",
                ReadInstallerScript(),
                StringComparison.Ordinal);
        }

        [Fact]
        public void DataIsOnlyDeletedOnceTheSettingsLockCanBeTaken()
        {
            string script = ReadInstallerScript();

            // The installed copy is known to be closed by this point. A portable copy of the same
            // user shares this directory and is not, and it can be in another session where no
            // per-session kernel object reaches it. Deleting underneath it would not even take
            // effect: it holds the state in memory and writes it back on its next save.
            Assert.Contains("function SettingsAreUnlocked(", script, StringComparison.Ordinal);
            Assert.Contains("if not SettingsAreUnlocked(DataDir) then", script, StringComparison.Ordinal);
        }

        [Fact]
        public void TheDataDirectoryItselfIsNeverRemovedRecursively()
        {
            // RemoveDir refuses a directory that still holds anything, which is the wanted
            // behaviour: a file this product did not create is not this product's to remove.
            // DelTree would take it and everything beside it.
            string script = ReadInstallerScript();

            Assert.DoesNotContain("DelTree", script, StringComparison.Ordinal);
        }

        [Fact]
        public void ASilentUninstallNeverDeletesData()
        {
            // Nobody is there to be asked, and keeping data is the recoverable answer.
            Assert.Contains("if UninstallSilent then", ReadInstallerScript(), StringComparison.Ordinal);
        }
    }
}
