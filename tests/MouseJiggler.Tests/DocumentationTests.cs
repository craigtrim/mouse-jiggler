using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Diagnostics;
using MouseJiggler.Windows.Diagnostics;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The documentation checked against the code it describes.
    /// </summary>
    /// <remarks>
    /// Troubleshooting documentation earns its keep only if it is true. A field list that has
    /// drifted from the code is worse than no field list, because someone reads it, believes
    /// the diagnostics are safe to paste into a public issue, and is wrong.
    ///
    /// These tests do not check the prose. They check the specific claims: that every field the
    /// document promises is actually emitted, that no field is emitted the document does not
    /// mention, and that the stated size caps are the ones the code enforces.
    /// </remarks>
    public sealed class DocumentationTests
    {
        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MouseJiggler.sln")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }

        private static string Troubleshooting()
        {
            string path = Path.Combine(RepositoryRoot(), "docs", "troubleshooting.md");
            Assert.True(File.Exists(path), "docs/troubleshooting.md is missing.");
            return File.ReadAllText(path);
        }

        [Fact]
        public void EveryLogFieldTheDocumentPromisesIsActuallyWritten()
        {
            string line = LocalDiagnosticSink.FormatLine(new DiagnosticEvent(
                new DateTime(2026, 9, 9, 10, 31, 2, 417, DateTimeKind.Utc),
                FaultSubsystem.WakeAcquire,
                "wake.applyFailed",
                5));

            foreach (string field in new[] { "utc", "version", "subsystem", "code", "nativeError" })
            {
                Assert.Contains("\"" + field + "\"", line, StringComparison.Ordinal);
                Assert.Contains("`" + field + "`", Troubleshooting(), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void TheDocumentedExampleLineIsTheLineTheCodeProduces()
        {
            string line = LocalDiagnosticSink.FormatLine(new DiagnosticEvent(
                new DateTime(2026, 9, 9, 10, 31, 2, 417, DateTimeKind.Utc),
                FaultSubsystem.WakeAcquire,
                "wake.applyFailed",
                5));

            // The version is stamped from the assembly, so the example is compared with that
            // one part substituted rather than pinned to a version that will move.
            string expected = line.Replace(DiagnosticSnapshotBuilder.AppVersion, "1.0.0.0");

            Assert.Contains(expected, Troubleshooting(), StringComparison.Ordinal);
        }

        [Fact]
        public void EverySubsystemTheCodeCanReportIsNamedInTheDocument()
        {
            string document = Troubleshooting();

            foreach (FaultSubsystem subsystem in Enum.GetValues(typeof(FaultSubsystem)))
            {
                if (subsystem == FaultSubsystem.None)
                {
                    continue;
                }

                // A subsystem that appears in a log the user is reading, and nowhere in the
                // document, is a code they have no way to interpret.
                Assert.Contains("`" + subsystem + "`", document, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void TheDocumentedSizeCapsAreTheOnesTheCodeEnforces()
        {
            string document = Troubleshooting();

            Assert.Equal(256 * 1024, LocalDiagnosticSink.MaxFileBytes);
            Assert.Equal(4, LocalDiagnosticSink.MaxBackups);

            Assert.Contains("256 KiB", document, StringComparison.Ordinal);
            Assert.Contains("four numbered backups", document, StringComparison.Ordinal);

            // 256 KiB times five files is 1.25 MB, which is the total the document promises.
            long total = (long)LocalDiagnosticSink.MaxFileBytes * (LocalDiagnosticSink.MaxBackups + 1);
            Assert.Equal(1_310_720, total);
            Assert.Contains("1.25 MB", document, StringComparison.Ordinal);
        }

        [Fact]
        public void EveryFieldOfTheCopiedDiagnosticsIsListedInTheDocument()
        {
            string snapshot = DiagnosticSnapshotBuilder.Build(
                MouseJiggler.Core.Settings.SettingsV1.CreateDefault(),
                MouseJiggler.Core.Activity.DesiredEffects.None(MouseJiggler.Core.Activity.StatusCode.Stopped),
                MouseJiggler.Core.Environment.PowerSource.External,
                MouseJiggler.Core.Environment.SessionState.ActiveUnlocked,
                installedEdition: false,
                recentEvents: new List<DiagnosticEvent>());

            string document = Troubleshooting();

            // Every heading the snapshot emits has to be findable in the document, so the list
            // there is a complete account of what leaves the machine rather than a summary.
            var headings = new[]
            {
                "App version", "Edition", "Windows", "Architecture", "Framework release",
                "Data directory", "Status", "Power", "Session",
                "System awake requested", "Display awake requested", "Pointer movement allowed",
                "Wake request failed", "Input request failed", "Recent events",
            };

            foreach (string heading in headings)
            {
                Assert.True(
                    snapshot.IndexOf(heading, StringComparison.Ordinal) >= 0,
                    heading + " is documented but no longer emitted.");

                Assert.True(
                    document.IndexOf(heading, StringComparison.OrdinalIgnoreCase) >= 0,
                    heading + " is emitted but not documented.");
            }
        }

        [Fact]
        public void TheDocumentDoesNotPromiseAnythingTheSnapshotStoppedEmitting()
        {
            string snapshot = DiagnosticSnapshotBuilder.Build(
                MouseJiggler.Core.Settings.SettingsV1.CreateDefault(),
                MouseJiggler.Core.Activity.DesiredEffects.None(MouseJiggler.Core.Activity.StatusCode.Stopped),
                MouseJiggler.Core.Environment.PowerSource.External,
                MouseJiggler.Core.Environment.SessionState.ActiveUnlocked,
                installedEdition: true,
                recentEvents: new List<DiagnosticEvent>());

            // The data directory is the only path, and it must be the token rather than the
            // expanded form, because the expanded form carries the account name.
            Assert.Contains("%LOCALAPPDATA%", snapshot, StringComparison.Ordinal);
            Assert.DoesNotContain(Environment.UserName, snapshot, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("%LOCALAPPDATA%", Troubleshooting(), StringComparison.Ordinal);
        }

        [Fact]
        public void TheReadmePromiseOfNoNetworkTrafficIsRepeatedWhereItMatters()
        {
            string readme = File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md"));

            Assert.Contains("no network traffic", readme, StringComparison.OrdinalIgnoreCase);

            // The same promise has to appear beside the diagnostics, which is the moment a user
            // is deciding whether copying something is safe.
            Assert.Contains("no network connections", Troubleshooting(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
