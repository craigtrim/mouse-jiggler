using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Commands;
using MouseJiggler.Core.Diagnostics;
using MouseJiggler.Core.Environment;
using MouseJiggler.Core.Input;
using MouseJiggler.Core.Settings;
using MouseJiggler.Tests.Fakes;
using MouseJiggler.Windows.Diagnostics;
using MouseJiggler.Windows.Settings;
using MouseJiggler.Windows.Storage;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The recovery and privacy halves of issue #11: what a retry is still not allowed to do,
    /// that a failing subsystem cannot turn recovery into a tight loop, that a log the app
    /// cannot write is survivable, and that no name, identifier, path, title or coordinate
    /// belonging to the user reaches either a log line or the text Copy diagnostics produces.
    /// </summary>
    /// <remarks>
    /// Every clock here is a fixed function and every duration is a number, so nothing sleeps.
    /// The retry cadence itself, 1, 5 and 30 seconds and then every 30, lives in
    /// MouseJiggler.App.Runtime.ActivityCoordinator, which this assembly deliberately does not
    /// reference. What is reachable from here is the rule that matters more: a recovered
    /// capability re-enters the same eligibility ladder as everything else, so no retry can
    /// resurrect an app that a Stop, the battery gate, a schedule end or an Exit has silenced.
    /// </remarks>
    public sealed class RecoveryAndSanitizationTests
    {
        /// <summary>Stand-ins for the things a diagnostic must never carry.</summary>
        private const string FakeAccountName = "wjenkins";
        private const string FakeSid = "S-1-5-21-1806384426-1425521274-3352445193-1013";
        private const string FakeWindowTitle = "Payroll 2026 - Excel";
        private const string FakePointerPosition = "1287,904";
        private const string FakeDocumentPath = "C:\\Users\\wjenkins\\Documents\\Payroll 2026.xlsx";

        /// <summary>
        /// The entire permitted vocabulary of a log line. A code carrying an account name, a
        /// SID, a path, a window title or a pointer position could not match it: hyphens,
        /// spaces, commas, colons and backslashes all fall outside.
        /// </summary>
        private static readonly Regex SanitizedLine = new Regex(
            "^\\{\"utc\":\"\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z\"," +
            "\"version\":\"[0-9A-Za-z.]+\"," +
            "\"subsystem\":\"[A-Za-z]+\"," +
            "\"code\":\"[A-Za-z0-9.]+\"" +
            "(,\"nativeError\":-?\\d+)?\\}$",
            RegexOptions.CultureInvariant);

        private static DateTime Utc(int second) =>
            new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc).AddSeconds(second);

        private static SettingsV1 Running(
            RunMode mode = RunMode.Manual,
            bool scheduleEnabled = false,
            bool pauseOnBattery = true)
        {
            SettingsV1 d = SettingsV1.CreateDefault();
            return new SettingsV1(
                d.SchemaVersion, d.Revision, stopped: false, mode,
                scheduleEnabled, d.ScheduleStart, d.ScheduleEnd, SettingsV1.AllDaysMask,
                pauseOnBattery, keepDisplayOn: true, jiggleMouse: true,
                d.IntervalSeconds, d.DiagnosticLogging, d.StartupInitialized, d.FirstRunCompleted);
        }

        private static SettingsV1 WithScheduleStart(SettingsV1 d, string scheduleStart)
        {
            return new SettingsV1(
                d.SchemaVersion, d.Revision, d.Stopped, d.RunMode,
                d.ScheduleEnabled, scheduleStart, d.ScheduleEnd, d.DayMask,
                d.PauseOnBattery, d.KeepDisplayOn, d.JiggleMouse,
                d.IntervalSeconds, d.DiagnosticLogging, d.StartupInitialized, d.FirstRunCompleted);
        }

        /// <summary>
        /// The world one moment after a retry: the latched fault has been cleared, so nothing
        /// in the capability state is holding the app back any more.
        /// </summary>
        private static EnvironmentSnapshot AfterRetry(
            PowerSource power = PowerSource.External,
            SessionState session = SessionState.ActiveUnlocked,
            bool exiting = false,
            DateTime? utcNow = null,
            CapabilityState wake = CapabilityState.Ok,
            CapabilityState input = CapabilityState.Ok)
        {
            return new EnvironmentSnapshot(
                utcNow ?? Utc(0), TimeZoneInfo.Utc, 1_000, power, session,
                suspended: false, exiting: exiting, inputDesktopAvailable: true,
                wakeCapability: wake, inputCapability: input, settingsAvailable: true);
        }

        [Fact]
        public void ARetryThatClearsALatchedFaultStillCannotRunWhileTheUserHasStopped()
        {
            var policy = new ActivityPolicy();
            SettingsV1 stopped = ActivityCommandReducer.Reduce(CommandKind.Stop, Running()).SettingsToPersist!;

            // A retry re-attempts failed work. It is not a disguised Start, so it leaves the
            // saved intent exactly as the user left it.
            CommandOutcome retry = ActivityCommandReducer.Reduce(CommandKind.Retry, stopped);
            Assert.Null(retry.SettingsToPersist);
            Assert.False(retry.EndsProcess);

            DesiredEffects effects = policy.Evaluate(stopped, AfterRetry());

            Assert.Equal(StatusCode.Stopped, effects.Status);
            Assert.False(effects.SystemAwake);
            Assert.False(effects.DisplayAwake);
            Assert.False(effects.MayJiggle);
        }

        [Fact]
        public void ARetryThatClearsALatchedFaultStillCannotRunOnBatteryWhilePauseOnBatteryIsSet()
        {
            var policy = new ActivityPolicy();
            SettingsV1 settings = Running(pauseOnBattery: true);

            // Working capabilities and a running app: the only thing between here and a jiggle
            // is the battery gate, so a retry that bypassed a gate would show up here.
            DesiredEffects onBattery = policy.Evaluate(settings, AfterRetry(power: PowerSource.Battery));

            Assert.Equal(StatusCode.PausedOnBattery, onBattery.Status);
            Assert.False(onBattery.SystemAwake);
            Assert.False(onBattery.MayJiggle);

            // Proof that the gate, rather than some other condition, is what suppressed it.
            DesiredEffects onMains = policy.Evaluate(settings, AfterRetry(power: PowerSource.External));
            Assert.True(onMains.SystemAwake);
            Assert.True(onMains.MayJiggle);
        }

        [Fact]
        public void ARetryThatClearsALatchedFaultStillCannotRunOnceTheScheduleWindowHasEnded()
        {
            var policy = new ActivityPolicy();
            SettingsV1 scheduled = Running(RunMode.Scheduled, scheduleEnabled: true);

            // The prepared window is 08:00 to 17:00, and the end is exclusive.
            var lastMinuteInside = new DateTime(2026, 9, 9, 16, 59, 0, DateTimeKind.Utc);
            var firstMinuteOutside = new DateTime(2026, 9, 9, 17, 0, 0, DateTimeKind.Utc);

            DesiredEffects inside = policy.Evaluate(scheduled, AfterRetry(utcNow: lastMinuteInside));
            Assert.Equal(StatusCode.RunningScheduled, inside.Status);
            Assert.True(inside.SystemAwake);

            DesiredEffects outside = policy.Evaluate(scheduled, AfterRetry(utcNow: firstMinuteOutside));

            Assert.Equal(StatusCode.WaitingForSchedule, outside.Status);
            Assert.False(outside.SystemAwake);
            Assert.False(outside.DisplayAwake);
            Assert.False(outside.MayJiggle);
        }

        [Fact]
        public void NothingIsRequestedAfterExitEvenWithARetryOutstanding()
        {
            var policy = new ActivityPolicy();
            SettingsV1 settings = Running();

            CommandOutcome exit = ActivityCommandReducer.Reduce(CommandKind.Exit, settings);
            Assert.True(exit.EndsProcess);
            Assert.True(exit.ClearsEffectsImmediately);

            // A wake request that is currently failing is exactly the state a retry is queued
            // against. While the app is alive, that state still asks for the request.
            DesiredEffects alive = policy.Evaluate(settings, AfterRetry(wake: CapabilityState.Transient));
            Assert.True(alive.SystemAwake);
            Assert.Equal(StatusCode.Error, alive.Status);

            // Once Exit has been issued the same state asks for nothing at all, so a retry
            // arriving afterwards has nothing left to re-acquire.
            DesiredEffects exiting = policy.Evaluate(settings, AfterRetry(exiting: true, wake: CapabilityState.Transient));

            Assert.Equal(StatusCode.Stopped, exiting.Status);
            Assert.False(exiting.SystemAwake);
            Assert.False(exiting.DisplayAwake);
            Assert.False(exiting.MayJiggle);
        }

        [Fact]
        public void ARetryQueuedBeforeAStopIsPowerlessAfterIt()
        {
            var policy = new ActivityPolicy();
            const long QueuedAt = 7;

            AppCommand queuedRetry = AppCommand.Create(CommandKind.Retry, QueuedAt);
            Assert.True(queuedRetry.IsCurrent(QueuedAt));

            // Stop increments the generation, which is the whole point of carrying one: a
            // callback already in flight can no longer speak for the app.
            CommandOutcome stop = ActivityCommandReducer.Reduce(CommandKind.Stop, Running());
            Assert.False(queuedRetry.IsCurrent(QueuedAt + 1));

            // And even if a superseded retry did run, the intent it would evaluate against is
            // the stopped document, so there is no path back to a running app.
            DesiredEffects effects = policy.Evaluate(stop.SettingsToPersist!, AfterRetry());
            Assert.Equal(StatusCode.Stopped, effects.Status);
            Assert.False(effects.SystemAwake);
        }

        [Fact]
        public void ASuccessfulJiggleIsNeverFollowedByAnotherInsideTheConfiguredInterval()
        {
            const int IntervalSeconds = 30;
            const long IntervalMs = IntervalSeconds * 1000L;
            const long LastJiggleMs = 100_000;
            const uint LongIdle = 3_600_000;

            // The heartbeat ticks once a second; this samples ten times faster to be harsher
            // than the real one. Nothing inside the interval may become due, or a subsystem
            // recovering on a fast schedule would turn into a stream of pointer movement.
            for (long offset = 1; offset < IntervalMs; offset += 100)
            {
                Assert.False(
                    JiggleEligibility.IsDue(LongIdle, LastJiggleMs + offset, 0, LastJiggleMs, IntervalSeconds),
                    "A jiggle became due " + offset + " ms after the previous one.");
            }

            Assert.True(JiggleEligibility.IsDue(LongIdle, LastJiggleMs + IntervalMs, 0, LastJiggleMs, IntervalSeconds));
        }

        [Fact]
        public void AFailingSubsystemIsLoggedAtMostOncePerMinuteRatherThanEverySecond()
        {
            using (var temp = new TemporaryDirectory())
            {
                var ring = new DiagnosticRing();
                int second = 0;

                using (var sink = new LocalDiagnosticSink(temp.Path, ring, () => Utc(second)))
                {
                    sink.SetDiskLogging(true);

                    // Five minutes of a subsystem failing on every heartbeat while its retry
                    // schedule keeps re-attempting it.
                    for (second = 0; second < 300; second++)
                    {
                        sink.Record(FaultSubsystem.WakeAcquire, "wake.acquireFailed", 5);
                    }

                    string[] lines = File.ReadAllLines(sink.LogFilePath);

                    // One line opens the incident, then no more than one a minute.
                    Assert.InRange(lines.Length, 2, 6);

                    // Exactly one line per kept event: the writer adds nothing of its own.
                    Assert.Equal(ring.Count, lines.Length);
                }
            }
        }

        [Fact]
        public void AHeldLogFileThatCannotBeRotatedStopsLoggingWithoutStoppingTheEngine()
        {
            using (var temp = new TemporaryDirectory())
            {
                string logDirectory = Path.Combine(temp.Path, "Logs");
                Directory.CreateDirectory(logDirectory);

                string logPath = Path.Combine(logDirectory, LocalDiagnosticSink.LogFileName);

                // A full log that the next record has to rotate, held open by something else:
                // a backup tool, an editor, an antivirus scan. A disk with no space left
                // arrives at the same catch by the same IOException.
                File.WriteAllBytes(logPath, new byte[LocalDiagnosticSink.MaxFileBytes]);

                var ring = new DiagnosticRing();

                using (var held = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.None))
                using (var sink = new LocalDiagnosticSink(logDirectory, ring, () => Utc(0)))
                {
                    sink.SetDiskLogging(true);

                    sink.Record(FaultSubsystem.WakeAcquire, "wake.acquireFailed", 5);

                    Assert.True(sink.DiskLoggingFailed);

                    // The engine carries on, and the event is still there for Copy diagnostics.
                    Assert.Equal(1, ring.Count);

                    // Nothing was rotated into place, and the failure recorded no fault about
                    // itself, which is how a broken log would become a write loop.
                    Assert.Single(Directory.GetFiles(logDirectory));

                    sink.Record(FaultSubsystem.InputSend, "input.sendFailed", 5);
                    Assert.Equal(2, ring.Count);
                    Assert.Single(Directory.GetFiles(logDirectory));

                    GC.KeepAlive(held);
                }

                // The held file was never appended to either.
                Assert.Equal(LocalDiagnosticSink.MaxFileBytes, new FileInfo(logPath).Length);
            }
        }

        [Fact]
        public void ADeniedLogFileIsSurvivedAndLoggingIsNeverSilentlyResumed()
        {
            using (var temp = new TemporaryDirectory())
            {
                string logDirectory = Path.Combine(temp.Path, "Logs");
                Directory.CreateDirectory(logDirectory);

                string logPath = Path.Combine(logDirectory, LocalDiagnosticSink.LogFileName);
                File.WriteAllBytes(logPath, new byte[0]);
                File.SetAttributes(logPath, FileAttributes.ReadOnly);

                try
                {
                    var ring = new DiagnosticRing();

                    using (var sink = new LocalDiagnosticSink(logDirectory, ring, () => Utc(0)))
                    {
                        sink.SetDiskLogging(true);
                        sink.Record(FaultSubsystem.ConfigSave, "settings.accessDenied", -2147024891);

                        Assert.True(sink.DiskLoggingFailed);
                        Assert.Equal(1, ring.Count);
                        Assert.Equal(0, new FileInfo(logPath).Length);

                        // The obstacle goes away and the user switches logging back on. Disk
                        // logging still stays off for the rest of the session, because a sink
                        // that re-attempts a denied write on every event is the loop this
                        // latch exists to prevent.
                        File.SetAttributes(logPath, FileAttributes.Normal);
                        sink.SetDiskLogging(true);
                        sink.Record(FaultSubsystem.ConfigSave, "settings.io", -2147024864);

                        Assert.Equal(0, new FileInfo(logPath).Length);
                        Assert.Equal(2, ring.Count);
                    }
                }
                finally
                {
                    File.SetAttributes(logPath, FileAttributes.Normal);
                }
            }
        }

        [Fact]
        public void WithLoggingOffNotEvenTheLogDirectoryIsCreated()
        {
            using (var temp = new TemporaryDirectory())
            {
                string logDirectory = Path.Combine(temp.Path, "Logs");
                var ring = new DiagnosticRing();
                int second = 0;

                using (var sink = new LocalDiagnosticSink(logDirectory, ring, () => Utc(second)))
                {
                    // Enough distinct events that the file would have rotated more than once
                    // had logging been on.
                    for (second = 0; second < 5_000; second++)
                    {
                        sink.Record(FaultSubsystem.InputSend, "input.sendFailed." + second, 5);
                    }

                    // Switching it on and off again without recording in between must also
                    // leave nothing behind.
                    sink.SetDiskLogging(true);
                    sink.SetDiskLogging(false);
                    sink.Record(FaultSubsystem.PowerQuery, "power.queryFailed");

                    Assert.False(Directory.Exists(logDirectory));

                    // The history is still in memory, which is what Copy diagnostics reads.
                    Assert.Equal(DiagnosticRing.Capacity, ring.Count);
                }

                Assert.Empty(Directory.GetDirectories(temp.Path));
            }
        }

        [Fact]
        public void TheDiagnosticRecordHasNoFieldThatCouldCarryUserData()
        {
            PropertyInfo[] properties = typeof(DiagnosticEvent).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            var names = new List<string>();
            foreach (PropertyInfo property in properties)
            {
                names.Add(property.Name);
            }

            names.Sort(StringComparer.Ordinal);

            // A free-text field is how user data ends up in a log, so the record has a fixed
            // shape. Adding a message, a path or an exception here breaks this on purpose.
            //
            // SuppressedRepeats is a count of dropped duplicates. It is listed rather than
            // exempted: every field on this record has to be argued for once, in this list, and
            // the argument for a count is that a number of events says nothing about who caused
            // them. The string check below is what keeps that argument honest.
            Assert.Equal(
                new[] { "Code", "NativeErrorCode", "Subsystem", "SuppressedRepeats", "UtcTimestamp" },
                names);

            Assert.Empty(typeof(DiagnosticEvent).GetFields(BindingFlags.Public | BindingFlags.Instance));

            foreach (PropertyInfo property in properties)
            {
                if (property.PropertyType == typeof(string))
                {
                    Assert.Equal("Code", property.Name);
                }
            }
        }

        [Fact]
        public void NoDiagnosticEntryPointAcceptsAMessageAPathOrAnException()
        {
            var surface = new List<MethodInfo>();
            surface.AddRange(typeof(IDiagnosticSink).GetMethods());
            surface.AddRange(typeof(LocalDiagnosticSink).GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
            surface.AddRange(typeof(DiagnosticRing).GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
            surface.Add(typeof(DiagnosticSnapshotBuilder).GetMethod("Build")!);

            foreach (MethodInfo method in surface)
            {
                if (method.IsSpecialName)
                {
                    continue;
                }

                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Assert.False(
                        typeof(Exception).IsAssignableFrom(parameter.ParameterType),
                        method.DeclaringType!.Name + "." + method.Name + " accepts an exception.");

                    // The one string a caller may hand in is the stable fault code. There is
                    // deliberately no parameter for a message, a path, a title or an account.
                    if (parameter.ParameterType == typeof(string))
                    {
                        Assert.Equal("code", parameter.Name);
                    }
                }
            }
        }

        [Theory]
        [InlineData(FakeAccountName)]
        [InlineData(FakeSid)]
        [InlineData(FakeWindowTitle)]
        [InlineData(FakePointerPosition)]
        [InlineData(FakeDocumentPath)]
        public void UserTextInTheSettingsFileIsRejectedWithoutBeingEchoedIntoTheFaultCode(string poison)
        {
            // The schedule times are the only free text a settings document carries, which
            // makes them the one route by which a user's own words could reach a diagnostic.
            OperationResult validated = SettingsValidator.Validate(WithScheduleStart(SettingsV1.CreateDefault(), poison));

            Assert.False(validated.Succeeded);
            Assert.NotNull(validated.Code);
            Assert.StartsWith("settings.", validated.Code!, StringComparison.Ordinal);
            Assert.Matches("^[A-Za-z0-9.]+$", validated.Code!);
            Assert.DoesNotContain(poison, validated.Code!, StringComparison.OrdinalIgnoreCase);

            var entry = new DiagnosticEvent(Utc(0), validated.Subsystem, validated.Code!, validated.NativeErrorCode);
            Assert.DoesNotContain(poison, LocalDiagnosticSink.FormatLine(entry), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task EveryLineWrittenForARealStorageFailureIsATimestampASubsystemAndACode()
        {
            using (var temp = new TemporaryDirectory())
            {
                string logDirectory = Path.Combine(temp.Path, "Logs");
                var ring = new DiagnosticRing();

                using (var sink = new LocalDiagnosticSink(logDirectory, ring, () => Utc(0)))
                {
                    sink.SetDiskLogging(true);

                    IReadOnlyList<string> codes = await RecordRealStorageFailuresAsync(PoisonedRoot(temp), sink);
                    Assert.Equal(3, codes.Count);

                    // A status change goes through the same writer, so it is checked too.
                    sink.RecordTransition(StatusCode.RunningManual, StatusCode.Error);

                    string[] lines = File.ReadAllLines(sink.LogFilePath);
                    Assert.Equal(4, lines.Length);
                    Assert.Equal(ring.Count, lines.Length);

                    Assert.All(lines, line => Assert.Matches(SanitizedLine, line));
                }
            }
        }

        [Fact]
        public async Task NeitherTheLogNorTheCopiedDiagnosticsCarryTheAccountNameSidPathOrTitle()
        {
            using (var temp = new TemporaryDirectory())
            {
                string root = PoisonedRoot(temp);
                string logDirectory = Path.Combine(temp.Path, "Logs");
                var ring = new DiagnosticRing();

                using (var sink = new LocalDiagnosticSink(logDirectory, ring, () => Utc(0)))
                {
                    sink.SetDiskLogging(true);

                    // Real failures raised under a directory named after an account, a SID, a
                    // window title and a pointer position, over a settings file whose own text
                    // is all of those things again.
                    IReadOnlyList<string> codes = await RecordRealStorageFailuresAsync(root, sink);

                    string log = File.ReadAllText(sink.LogFilePath);
                    string copied = DiagnosticSnapshotBuilder.Build(
                        SettingsV1.CreateDefault(),
                        DesiredEffects.None(StatusCode.Error),
                        PowerSource.External,
                        SessionState.ActiveUnlocked,
                        installedEdition: false,
                        recentEvents: ring.Snapshot());

                    // The faults really did travel the whole pipeline, so what follows is an
                    // absence from a populated log rather than from an empty one.
                    foreach (string code in codes)
                    {
                        Assert.Contains(code, log, StringComparison.Ordinal);
                        Assert.Contains(code, copied, StringComparison.Ordinal);
                    }

                    foreach (string poison in new[]
                             {
                                 FakeAccountName, FakeSid, FakeWindowTitle, FakePointerPosition,
                                 FakeDocumentPath, root, temp.Path, SettingsPaths.SettingsFileName,
                             })
                    {
                        Assert.DoesNotContain(poison, log, StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain(poison, copied, StringComparison.OrdinalIgnoreCase);
                    }

                    // The data directory is named by an unexpanded token instead.
                    Assert.Contains("%LOCALAPPDATA%\\MouseJiggler", copied, StringComparison.Ordinal);
                }
            }
        }

        [Fact]
        public void TheCopiedDiagnosticsNameNeitherTheRealAccountNorItsSid()
        {
            var events = new List<DiagnosticEvent>
            {
                new DiagnosticEvent(Utc(0), FaultSubsystem.ConfigSave, "settings.io", -2147024864),
            };

            string copied = DiagnosticSnapshotBuilder.Build(
                SettingsV1.CreateDefault(),
                DesiredEffects.None(StatusCode.Stopped),
                PowerSource.Battery,
                SessionState.Locked,
                installedEdition: true,
                recentEvents: events);

            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                SecurityIdentifier? sid = identity.User;
                Assert.NotNull(sid);

                // The SID is the identifier that survives a rename, so it is the one worth
                // naming explicitly here.
                Assert.DoesNotContain(sid!.Value, copied, StringComparison.OrdinalIgnoreCase);

                // And the account it belongs to, in both of the forms Windows uses.
                Assert.DoesNotContain(System.Environment.UserName, copied, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(identity.Name, copied, StringComparison.OrdinalIgnoreCase);
            }

            Assert.DoesNotContain(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                copied,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A settings root whose every segment is something a log must never repeat: an account
        /// name, a SID, a window title and a pointer position.
        /// </summary>
        private static string PoisonedRoot(TemporaryDirectory temp)
        {
            return Path.Combine(temp.Path, FakeAccountName, FakeSid, FakeWindowTitle, FakePointerPosition);
        }

        /// <summary>
        /// Drives three real storage faults through the store and records each one the way the
        /// coordinator does, returning the codes so the caller can prove they arrived.
        /// </summary>
        private static async Task<IReadOnlyList<string>> RecordRealStorageFailuresAsync(string rootDirectory, IDiagnosticSink sink)
        {
            var paths = new SettingsPaths(rootDirectory);
            var files = new FaultInjectingFileOperations(new PhysicalFileOperations());
            var codes = new List<string>();

            Directory.CreateDirectory(rootDirectory);

            using (var store = new JsonSettingsStore(paths, files))
            {
                // A file that is nothing but the user's own text, damaged enough that the
                // parser gives up on it.
                File.WriteAllText(
                    paths.SettingsFile,
                    "{\"scheduleStart\":\"" + FakeWindowTitle + "\", " + FakeSid + " " + FakePointerPosition);
                codes.Add(RecordFailure(sink, store.Load().Outcome));

                // A well-formed document carrying a full path where a time belongs, so the
                // value reaches the validator: the deepest a user string ever travels.
                OperationResult<byte[]> poisoned = SettingsDocument.Serialize(
                    WithScheduleStart(SettingsV1.CreateDefault(), FakeDocumentPath));
                Assert.True(poisoned.Succeeded);
                File.WriteAllBytes(paths.SettingsFile, poisoned.Value!);
                codes.Add(RecordFailure(sink, store.Load().Outcome));

                // And a save that cannot reach the disk at all.
                files.FailWrite = true;
                OperationResult<SettingsV1> save = await store.UpdateAsync(
                    current => SettingsIntent.WithStopped(current, true), CancellationToken.None);
                codes.Add(RecordFailure(sink, save.Outcome));
            }

            return codes;
        }

        private static string RecordFailure(IDiagnosticSink sink, OperationResult outcome)
        {
            Assert.False(outcome.Succeeded);
            Assert.NotNull(outcome.Code);

            sink.Record(outcome.Subsystem, outcome.Code!, outcome.NativeErrorCode);
            return outcome.Code!;
        }
    }
}
