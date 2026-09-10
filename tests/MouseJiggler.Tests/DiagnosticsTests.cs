using System;
using System.Collections.Generic;
using System.IO;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Diagnostics;
using MouseJiggler.Core.Environment;
using MouseJiggler.Core.Settings;
using MouseJiggler.Tests.Fakes;
using MouseJiggler.Windows.Diagnostics;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// Bounded diagnostics from issue #11: the volume stays bounded, a disk failure is survivable,
    /// and nothing that identifies the user reaches the output.
    /// </summary>
    public sealed class DiagnosticsTests
    {
        private static DateTime At(int second) => new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc).AddSeconds(second);

        [Fact]
        public void TheRingKeepsTheMostRecentHundredEvents()
        {
            var ring = new DiagnosticRing();

            for (int i = 0; i < 500; i++)
            {
                // A distinct code each time, so repeat suppression does not apply.
                ring.Record(new DiagnosticEvent(At(i), FaultSubsystem.InputSend, "code." + i, null));
            }

            Assert.Equal(DiagnosticRing.Capacity, ring.Count);

            IReadOnlyList<DiagnosticEvent> snapshot = ring.Snapshot();
            Assert.Equal("code.400", snapshot[0].Code);
            Assert.Equal("code.499", snapshot[snapshot.Count - 1].Code);
        }

        [Fact]
        public void RepeatsOfOneCodeAreSummarisedRatherThanRecordedEverySecond()
        {
            var ring = new DiagnosticRing();

            // A failing subsystem on a one-second heartbeat: without suppression the ring would
            // hold 100 seconds of the same line and nothing else.
            for (int second = 0; second < 300; second++)
            {
                ring.Record(new DiagnosticEvent(At(second), FaultSubsystem.WakeAcquire, "wake.acquireFailed", 5));
            }

            // One initial record plus one per minute.
            Assert.InRange(ring.Count, 2, 6);
        }

        [Fact]
        public void ARecoveryIsAlwaysRecordedEvenAfterSuppression()
        {
            var ring = new DiagnosticRing();

            Assert.True(ring.Record(new DiagnosticEvent(At(0), FaultSubsystem.WakeAcquire, "wake.acquireFailed", 5)));
            Assert.False(ring.Record(new DiagnosticEvent(At(1), FaultSubsystem.WakeAcquire, "wake.acquireFailed", 5)));

            ring.NoteRecovery(FaultSubsystem.WakeAcquire, "wake.acquireFailed");

            Assert.True(ring.Record(new DiagnosticEvent(At(2), FaultSubsystem.WakeAcquire, "wake.acquireFailed", 5)));
        }

        [Fact]
        public void ASummaryCountsTheRepeatsItStandsInFor()
        {
            var ring = new DiagnosticRing();

            Assert.True(ring.Record(new DiagnosticEvent(At(0), FaultSubsystem.InputSend, "input.sendFailed", 5)));

            // Fifty-eight more inside the same minute, all dropped.
            for (int second = 1; second < 59; second++)
            {
                Assert.False(ring.Record(new DiagnosticEvent(At(second), FaultSubsystem.InputSend, "input.sendFailed", 5)));
            }

            // The one past the window is kept, and says what happened in between. Without the
            // count this line is indistinguishable from a single isolated failure.
            Assert.True(ring.Record(new DiagnosticEvent(At(61), FaultSubsystem.InputSend, "input.sendFailed", 5)));

            IReadOnlyList<DiagnosticEvent> events = ring.Snapshot();

            Assert.Equal(2, events.Count);
            Assert.Equal(0, events[0].SuppressedRepeats);
            Assert.Equal(58, events[1].SuppressedRepeats);
        }

        [Fact]
        public void TheCountReachesBothTheLogLineAndTheCopiedText()
        {
            var ring = new DiagnosticRing();

            Assert.True(ring.Record(new DiagnosticEvent(At(0), FaultSubsystem.WakeAcquire, "wake.acquireFailed", 5)));
            Assert.False(ring.Record(new DiagnosticEvent(At(10), FaultSubsystem.WakeAcquire, "wake.acquireFailed", 5)));
            Assert.True(ring.Record(new DiagnosticEvent(At(61), FaultSubsystem.WakeAcquire, "wake.acquireFailed", 5)));

            IReadOnlyList<DiagnosticEvent> events = ring.Snapshot();
            DiagnosticEvent summary = events[events.Count - 1];

            Assert.Contains("\"suppressedRepeats\":1", LocalDiagnosticSink.FormatLine(summary), StringComparison.Ordinal);

            // An ordinary line carries no such field rather than a zero that means nothing.
            Assert.DoesNotContain("suppressedRepeats", LocalDiagnosticSink.FormatLine(events[0]), StringComparison.Ordinal);
        }

        [Fact]
        public void OneHundredThousandEventsLeaveBothTheRingAndTheFilesBounded()
        {
            using (var temp = new TemporaryDirectory())
            {
                var ring = new DiagnosticRing();
                int tick = 0;

                using (var sink = new LocalDiagnosticSink(temp.Path, ring, () => At(tick)))
                {
                    sink.SetDiskLogging(true);

                    for (int i = 0; i < 100_000; i++)
                    {
                        tick = i;
                        sink.Record(FaultSubsystem.InputSend, "input.sendFailed", 5);
                    }

                    Assert.True(ring.Count <= DiagnosticRing.Capacity);

                    long total = 0;
                    foreach (string file in Directory.GetFiles(temp.Path))
                    {
                        total += new FileInfo(file).Length;
                    }

                    // 256 KiB plus four backups.
                    Assert.True(total <= (LocalDiagnosticSink.MaxFileBytes * (LocalDiagnosticSink.MaxBackups + 1)),
                        "Log files grew to " + total + " bytes.");
                }
            }
        }

        [Fact]
        public void NothingIsWrittenToDiskWhileLoggingIsOff()
        {
            using (var temp = new TemporaryDirectory())
            {
                var ring = new DiagnosticRing();

                using (var sink = new LocalDiagnosticSink(temp.Path, ring))
                {
                    // Off by default.
                    for (int i = 0; i < 50; i++)
                    {
                        sink.Record(FaultSubsystem.PowerQuery, "power.queryFailed." + i);
                    }

                    Assert.Empty(Directory.GetFiles(temp.Path));

                    // The in-memory ring still has the history, so Copy diagnostics works.
                    Assert.Equal(50, ring.Count);
                }
            }
        }

        [Fact]
        public void ALogWriteFailureDisablesLoggingWithoutThrowing()
        {
            using (var temp = new TemporaryDirectory())
            {
                var ring = new DiagnosticRing();
                string logDirectory = Path.Combine(temp.Path, "logs");

                using (var sink = new LocalDiagnosticSink(logDirectory, ring))
                {
                    sink.SetDiskLogging(true);

                    // A file where the log directory should be, so creating it must fail.
                    File.WriteAllText(logDirectory, "not a directory");

                    sink.Record(FaultSubsystem.InputSend, "input.sendFailed");

                    Assert.True(sink.DiskLoggingFailed);

                    // The engine carries on: the event is still in the ring.
                    Assert.Equal(1, ring.Count);

                    // And a second record does not throw either.
                    sink.Record(FaultSubsystem.InputSend, "input.other");
                }
            }
        }

        [Fact]
        public void ALoggedLineCarriesNoFieldThatCouldHoldUserData()
        {
            var entry = new DiagnosticEvent(At(0), FaultSubsystem.InputSend, "input.sendFailed", 5);

            string line = LocalDiagnosticSink.FormatLine(entry);

            Assert.Contains("\"code\":\"input.sendFailed\"", line);
            Assert.Contains("\"nativeError\":5", line);

            // The record has no free-text field at all, which is what keeps user data out.
            Assert.DoesNotContain("message", line);
            Assert.DoesNotContain("path", line);
            Assert.DoesNotContain("user", line);
        }

        [Fact]
        public void CopiedDiagnosticsContainNoUserNameOrExpandedPath()
        {
            SettingsV1 settings = SettingsV1.CreateDefault();
            DesiredEffects effects = DesiredEffects.None(StatusCode.Stopped);

            var events = new List<DiagnosticEvent>
            {
                new DiagnosticEvent(At(0), FaultSubsystem.WakeAcquire, "wake.acquireFailed", 5),
            };

            string text = DiagnosticSnapshotBuilder.Build(
                settings, effects, PowerSource.External, SessionState.ActiveUnlocked, installedEdition: true, recentEvents: events);

            Assert.Contains("%LOCALAPPDATA%\\MouseJiggler", text);
            Assert.Contains("wake.acquireFailed", text);

            // The real account name and profile path must not appear.
            string userName = System.Environment.UserName;
            Assert.DoesNotContain(userName, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(System.Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ClearingLogsRemovesOnlyTheOwnedFiles()
        {
            using (var temp = new TemporaryDirectory())
            {
                var ring = new DiagnosticRing();

                using (var sink = new LocalDiagnosticSink(temp.Path, ring))
                {
                    sink.SetDiskLogging(true);
                    sink.Record(FaultSubsystem.InputSend, "input.sendFailed");

                    string unrelated = Path.Combine(temp.Path, "something-else.txt");
                    File.WriteAllText(unrelated, "keep me");

                    Assert.True(sink.ClearLogs().Succeeded);

                    Assert.False(File.Exists(sink.LogFilePath));
                    Assert.True(File.Exists(unrelated));
                }
            }
        }
    }
}
