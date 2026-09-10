using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Commands;
using MouseJiggler.Core.Settings;
using MouseJiggler.Tests.Fakes;
using MouseJiggler.Windows.Settings;
using MouseJiggler.Windows.Storage;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The failure matrix from issue #4. A save can be interrupted at any step, and the file on
    /// disk can be anything at all; neither may ever produce a half-written document or an app
    /// that runs after the user stopped it. Every case here runs against a temporary directory
    /// with injected file operations and an injected clock, so nothing sleeps and the real user
    /// settings are never read or written.
    /// </summary>
    public sealed class SettingsFailureMatrixTests
    {
        /// <summary>ERROR_DISK_FULL as the framework surfaces it on an IOException.</summary>
        private const int DiskFullHResult = unchecked((int)0x80070070);

        [Theory]
        [InlineData("beforeTheWrite")]
        [InlineData("afterTheFlush")]
        [InlineData("duringTheReplace")]
        public async Task AnInterruptedSaveLeavesThePreviousDocumentCompleteAtEveryFailurePoint(string failurePoint)
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);
                var files = new FaultInjectingFileOperations(new PhysicalFileOperations());

                using (var store = new JsonSettingsStore(paths, files))
                {
                    byte[] before = await SeedCommittedDocumentAsync(store, paths, 30);

                    switch (failurePoint)
                    {
                        case "beforeTheWrite":
                            files.FailWrite = true;
                            break;

                        case "afterTheFlush":
                            // The bytes reached the disk and only the swap was lost, which is the
                            // case that a naive write-in-place would corrupt.
                            files.FailAfterWrite = true;
                            break;

                        case "duringTheReplace":
                            files.FailReplace = true;
                            break;

                        default:
                            throw new ArgumentOutOfRangeException(nameof(failurePoint));
                    }

                    OperationResult<SettingsV1> failed = await store.UpdateAsync(
                        current => new SettingsPatch { IntervalSeconds = 300 }.ApplyTo(current),
                        CancellationToken.None);

                    Assert.False(failed.Succeeded);
                    Assert.Equal(FaultSubsystem.ConfigSave, failed.Outcome.Subsystem);
                    Assert.Equal("settings.io", failed.Outcome.Code);

                    // Byte for byte, not merely "still parses": a partly applied save that happened
                    // to remain valid JSON would still be a document the user never asked for.
                    Assert.Equal(before, File.ReadAllBytes(paths.SettingsFile));

                    // The abandoned candidate is not left lying beside the real document, where a
                    // later reader or a support bundle could mistake it for settings.
                    Assert.Empty(Directory.GetFiles(temp.Path, SettingsPaths.TempFilePrefix + "*"));

                    OperationResult<SettingsV1> reloaded = store.Load();
                    Assert.True(reloaded.Succeeded);
                    Assert.Equal(30, reloaded.Value!.IntervalSeconds);
                    Assert.Equal(2L, reloaded.Value.Revision);

                    // No failure path may quietly clear the stop the user is relying on.
                    Assert.True(reloaded.Value.Stopped);
                }
            }
        }

        [Fact]
        public async Task AFailureWhileCreatingTheFirstDocumentLeavesNoSettingsFileAtAll()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);

                // Nothing exists yet, so the store creates the file by moving its scratch copy
                // into place rather than by replacing anything.
                var files = new FaultInjectingFileOperations(new PhysicalFileOperations()) { FailMove = true };

                using (var store = new JsonSettingsStore(paths, files))
                {
                    OperationResult<SettingsV1> failed = await store.UpdateAsync(
                        current => new SettingsPatch { IntervalSeconds = 45 }.ApplyTo(current),
                        CancellationToken.None);

                    Assert.False(failed.Succeeded);
                    Assert.Equal("settings.io", failed.Outcome.Code);

                    // An absent file reads as stopped defaults. A zero-length file or a leftover
                    // scratch copy here would instead be reported as damage on the next launch.
                    Assert.False(File.Exists(paths.SettingsFile));
                    Assert.Empty(Directory.GetFiles(temp.Path, SettingsPaths.TempFilePrefix + "*"));

                    OperationResult<SettingsV1> reloaded = store.Load();
                    Assert.True(reloaded.Succeeded);
                    Assert.True(reloaded.Value!.Stopped);
                    Assert.Equal(1L, reloaded.Value.Revision);
                }
            }
        }

        [Fact]
        public async Task AWriterThatCannotTakeTheLockGivesUpWithoutWritingAnything()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);

                // The clock jumps forward on every reading, so the two-second wait expires on the
                // first retry: the timeout is proven without the test ever sleeping.
                Func<DateTime> clock = CreateJumpingClock();

                using (var store = new JsonSettingsStore(paths, new PhysicalFileOperations(), clock))
                {
                    byte[] before = await SeedCommittedDocumentAsync(store, paths, 30);

                    // A real exclusive handle on the lock file, the way a second Windows session
                    // of the same user would hold it.
                    using (var heldByAnotherSession = new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        Assert.True(heldByAnotherSession.CanWrite);

                        OperationResult<SettingsV1> failed = await store.UpdateAsync(
                            current => new SettingsPatch { IntervalSeconds = 300 }.ApplyTo(current),
                            CancellationToken.None);

                        Assert.False(failed.Succeeded);
                        Assert.Equal(FaultSubsystem.ConfigSave, failed.Outcome.Subsystem);
                        Assert.Equal("settings.lockTimeout", failed.Outcome.Code);

                        // Contention passes, so the caller is told this one is worth trying again.
                        Assert.True(failed.Outcome.Retryable);

                        // Losing the race costs nothing: no scratch file, and the document the
                        // other session is working on is untouched.
                        Assert.Equal(before, File.ReadAllBytes(paths.SettingsFile));
                        Assert.Empty(Directory.GetFiles(temp.Path, SettingsPaths.TempFilePrefix + "*"));
                    }
                }
            }
        }

        [Fact]
        public async Task AReadOnlyDirectoryIsReportedAsDeniedAndKeepsTheExistingDocument()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);
                var files = new ExceptionInjectingFileOperations();

                using (var store = new JsonSettingsStore(paths, files))
                {
                    byte[] before = await SeedCommittedDocumentAsync(store, paths, 30);

                    // What Windows raises when the folder refuses the creation of the scratch file.
                    var denied = new UnauthorizedAccessException("Access to the path is denied.");
                    files.WriteFailure = denied;

                    OperationResult<SettingsV1> failed = await store.UpdateAsync(
                        current => new SettingsPatch { IntervalSeconds = 300 }.ApplyTo(current),
                        CancellationToken.None);

                    Assert.False(failed.Succeeded);

                    // Denial is kept distinct from a generic IO fault, because the two need
                    // different advice: one is a permissions problem, the other is the disk.
                    Assert.Equal("settings.accessDenied", failed.Outcome.Code);
                    Assert.Equal(FaultSubsystem.ConfigSave, failed.Outcome.Subsystem);
                    Assert.Equal(denied.HResult, failed.Outcome.NativeErrorCode);

                    Assert.Equal(before, File.ReadAllBytes(paths.SettingsFile));
                    Assert.Empty(Directory.GetFiles(temp.Path, SettingsPaths.TempFilePrefix + "*"));
                }
            }
        }

        [Fact]
        public async Task ADeniedRootDirectoryFailsBeforeTouchingTheLockOrTheDocument()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);
                var denied = new UnauthorizedAccessException("Access to the path is denied.");
                var files = new ExceptionInjectingFileOperations { CreateDirectoryFailure = denied };

                using (var store = new JsonSettingsStore(paths, files))
                {
                    OperationResult<SettingsV1> failed = await store.UpdateAsync(
                        current => new SettingsPatch { IntervalSeconds = 45 }.ApplyTo(current),
                        CancellationToken.None);

                    Assert.False(failed.Succeeded);
                    Assert.Equal("settings.accessDenied", failed.Outcome.Code);
                    Assert.Equal(FaultSubsystem.ConfigSave, failed.Outcome.Subsystem);

                    // Giving up at the first refused step means there is no lock file left
                    // stranded and nothing for the next writer to clean up.
                    Assert.False(File.Exists(paths.LockFile));
                    Assert.False(File.Exists(paths.SettingsFile));
                }
            }
        }

        [Fact]
        public async Task AFullDiskCarriesItsNativeErrorOutWithoutLosingTheSavedDocument()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);
                var files = new ExceptionInjectingFileOperations();

                using (var store = new JsonSettingsStore(paths, files))
                {
                    byte[] before = await SeedCommittedDocumentAsync(store, paths, 30);

                    var diskFull = new IOException("There is not enough space on the disk.", DiskFullHResult);
                    files.WriteFailure = diskFull;

                    OperationResult<SettingsV1> failed = await store.UpdateAsync(
                        current => new SettingsPatch { IntervalSeconds = 300 }.ApplyTo(current),
                        CancellationToken.None);

                    Assert.False(failed.Succeeded);
                    Assert.Equal("settings.io", failed.Outcome.Code);

                    // The number is what makes a full disk diagnosable from a log that is
                    // deliberately stripped of paths and messages.
                    Assert.Equal(DiskFullHResult, failed.Outcome.NativeErrorCode);

                    // Freeing space and saving again is a sensible thing to ask the user to do.
                    Assert.True(failed.Outcome.Retryable);

                    Assert.Equal(before, File.ReadAllBytes(paths.SettingsFile));
                    Assert.Empty(Directory.GetFiles(temp.Path, SettingsPaths.TempFilePrefix + "*"));
                }
            }
        }

        [Theory]
        [InlineData("truncated", "settings.empty")]
        [InlineData("malformed", "settings.malformed")]
        [InlineData("oversized", "settings.oversized")]
        [InlineData("futureSchema", "settings.unsupportedSchema")]
        public void ADocumentThatCannotBeTrustedIsRefusedWithItsBytesLeftOnDiskForRecovery(string kind, string expectedCode)
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);
                File.WriteAllText(paths.SettingsFile, CreateUntrustedDocument(kind));
                byte[] before = File.ReadAllBytes(paths.SettingsFile);

                using (var store = new JsonSettingsStore(paths, new PhysicalFileOperations()))
                {
                    OperationResult<SettingsV1> loaded = store.Load();

                    // A file we cannot vouch for is a fault, never defaults: reading it as defaults
                    // would silently discard the settings the user still has on disk.
                    Assert.False(loaded.Succeeded);
                    Assert.Equal(expectedCode, loaded.Outcome.Code);
                    Assert.Equal(FaultSubsystem.ConfigRead, loaded.Outcome.Subsystem);

                    // Re-reading the same damaged bytes cannot help, so no retry is offered.
                    Assert.False(loaded.Outcome.Retryable);

                    // A revision read out of a refused document must not become the baseline that
                    // the next save counts from.
                    Assert.Equal(0L, store.LastKnownRevision);

                    // Those bytes are the user's only copy until they ask for a reset, so nothing
                    // is rewritten, quarantined or repaired behind their back.
                    Assert.Equal(before, File.ReadAllBytes(paths.SettingsFile));
                    Assert.Empty(Directory.GetFiles(temp.Path, SettingsPaths.InvalidBackupPrefix + "*.json"));
                    Assert.Empty(Directory.GetFiles(temp.Path, SettingsPaths.TempFilePrefix + "*"));

                    // The second read agrees with the first: refusal is not a one-shot state that
                    // a retrying caller could wear down into a success.
                    Assert.False(store.Load().Succeeded);
                }
            }
        }

        [Fact]
        public async Task AStartWhoseWriteFailsLeavesTheAppStoppedAndReportsTheStorageFault()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);
                var files = new FaultInjectingFileOperations(new PhysicalFileOperations());

                using (var store = new JsonSettingsStore(paths, files))
                {
                    await SeedCommittedDocumentAsync(store, paths, 30);

                    SettingsV1 onDisk = store.Load().Value!;
                    CommandOutcome start = ActivityCommandReducer.Reduce(CommandKind.Start, onDisk);

                    // Start is an enabling command, so it may not act before its change is
                    // committed. That rule is what makes the failure below safe.
                    Assert.True(start.RequiresPersistenceBeforeEffects);
                    Assert.False(start.SettingsToPersist!.Stopped);

                    files.FailReplace = true;
                    OperationResult<SettingsV1> committed = await store.UpdateAsync(
                        current => SettingsIntent.WithStopped(current, false),
                        CancellationToken.None);

                    // The caller gets a fault it can show, rather than a silent no-op that would
                    // leave the tray claiming to be running.
                    Assert.False(committed.Succeeded);
                    Assert.Equal(FaultSubsystem.ConfigSave, committed.Outcome.Subsystem);
                    Assert.Equal("settings.io", committed.Outcome.Code);
                    Assert.Null(committed.Value);

                    OperationResult<SettingsV1> reloaded = store.Load();
                    Assert.True(reloaded.Succeeded);
                    Assert.True(reloaded.Value!.Stopped);
                    Assert.Equal(2L, reloaded.Value.Revision);
                    Assert.Empty(Directory.GetFiles(temp.Path, SettingsPaths.TempFilePrefix + "*"));
                }
            }
        }

        [Fact]
        public async Task AStopThatCouldNotBeWrittenKeepsRefusingARemoteStart()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);
                var files = new FaultInjectingFileOperations(new PhysicalFileOperations());

                using (var store = new JsonSettingsStore(paths, files))
                {
                    OperationResult<SettingsV1> running = await store.UpdateAsync(
                        current => SettingsIntent.WithStopped(current, false),
                        CancellationToken.None);
                    Assert.True(running.Succeeded);

                    store.MarkStopIssued();
                    files.FailWrite = true;

                    OperationResult<SettingsV1> stopped = await store.UpdateAsync(
                        current => SettingsIntent.WithStopped(current, true),
                        CancellationToken.None);

                    Assert.False(stopped.Succeeded);

                    // The stop exists only in memory, so the warning has to stand and another
                    // session's start must still be refused: applying it would resurrect exactly
                    // the state this user just cancelled.
                    Assert.True(store.HasUncommittedStop);
                    Assert.False(store.ShouldApplyObservedRevision(SettingsIntent.WithStopped(SettingsV1.CreateDefault(), false)));

                    // What is on disk is the previous complete document, not a hybrid of the two.
                    OperationResult<SettingsV1> reloaded = store.Load();
                    Assert.True(reloaded.Succeeded);
                    Assert.False(reloaded.Value!.Stopped);
                    Assert.Equal(2L, reloaded.Value.Revision);
                }
            }
        }

        [Fact]
        public async Task AFailedSaveReleasesTheLockSoTheNextSaveCommitsTheVeryNextRevision()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);
                var files = new FaultInjectingFileOperations(new PhysicalFileOperations());

                using (var store = new JsonSettingsStore(paths, files))
                {
                    await SeedCommittedDocumentAsync(store, paths, 30);

                    files.FailReplace = true;
                    OperationResult<SettingsV1> failed = await store.UpdateAsync(
                        current => new SettingsPatch { IntervalSeconds = 300 }.ApplyTo(current),
                        CancellationToken.None);
                    Assert.False(failed.Succeeded);

                    files.FailReplace = false;
                    OperationResult<SettingsV1> retried = await store.UpdateAsync(
                        current => new SettingsPatch { IntervalSeconds = 120 }.ApplyTo(current),
                        CancellationToken.None);

                    // Succeeding at all proves the failed attempt released both the lock file and
                    // the in-process queue; otherwise this would have timed out instead.
                    Assert.True(retried.Succeeded);
                    Assert.Equal(120, retried.Value!.IntervalSeconds);

                    // The failed attempt also consumed no revision, so a watcher in another session
                    // sees a continuous sequence rather than a gap it would read as a lost write.
                    Assert.Equal(3L, retried.Value.Revision);
                    Assert.Equal(3L, store.Load().Value!.Revision);
                }
            }
        }

        /// <summary>Commits one known-good document and returns exactly what landed on disk.</summary>
        private static async Task<byte[]> SeedCommittedDocumentAsync(JsonSettingsStore store, SettingsPaths paths, int intervalSeconds)
        {
            OperationResult<SettingsV1> seeded = await store.UpdateAsync(
                current => new SettingsPatch { IntervalSeconds = intervalSeconds }.ApplyTo(current),
                CancellationToken.None);

            Assert.True(seeded.Succeeded);
            return File.ReadAllBytes(paths.SettingsFile);
        }

        /// <summary>
        /// A clock that jumps ten seconds on every reading. Any wait bounded by the store's
        /// two-second lock timeout therefore expires on its first retry, which is what keeps the
        /// contention test instant and identical on every machine.
        /// </summary>
        private static Func<DateTime> CreateJumpingClock()
        {
            var origin = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            int readings = 0;
            return () => origin.AddSeconds(10 * readings++);
        }

        private static string CreateUntrustedDocument(string kind)
        {
            switch (kind)
            {
                case "truncated":
                    // Zero bytes: what a crash between creating a file and flushing it leaves.
                    return string.Empty;

                case "malformed":
                    return "{\"schemaVersion\": 1, \"revision\": 2, \"stopped\": tr";

                case "oversized":
                    // Valid at the front and then padded past the cap, so it is the size that is
                    // refused rather than the syntax.
                    return SettingsDocument.ToJson(SettingsV1.CreateDefault()) + new string(' ', SettingsDocument.MaxBytes);

                case "futureSchema":
                    // Written by a newer build. Guessing at fields we do not know is worse than
                    // refusing, so the version alone decides it, whatever else the file says.
                    return SettingsDocument.ToJson(new SettingsV1(
                        schemaVersion: SettingsV1.CurrentSchemaVersion + 1,
                        revision: 99,
                        stopped: false,
                        runMode: RunMode.Manual,
                        scheduleEnabled: false,
                        scheduleStart: "08:00",
                        scheduleEnd: "17:00",
                        dayMask: SettingsV1.AllDaysMask,
                        pauseOnBattery: true,
                        keepDisplayOn: true,
                        jiggleMouse: true,
                        intervalSeconds: SettingsV1.DefaultIntervalSeconds,
                        diagnosticLogging: false,
                        startupInitialized: false,
                        firstRunCompleted: false));

                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        /// <summary>
        /// Fails one step with an exception the test chooses. That is what this adds beside
        /// <see cref="FaultInjectingFileOperations"/>: the store maps the exception type to its
        /// own fault code and carries the native error number out, so a refused folder and a full
        /// disk are told apart by type and HRESULT rather than by which step failed.
        /// </summary>
        private sealed class ExceptionInjectingFileOperations : IFileOperations
        {
            private readonly IFileOperations _inner = new PhysicalFileOperations();

            public Exception? CreateDirectoryFailure { get; set; }

            public Exception? WriteFailure { get; set; }

            public bool FileExists(string path) => _inner.FileExists(path);

            public void CreateDirectory(string path)
            {
                if (CreateDirectoryFailure != null)
                {
                    throw CreateDirectoryFailure;
                }

                _inner.CreateDirectory(path);
            }

            public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);

            public void WriteAllBytesDurable(string path, byte[] contents)
            {
                if (WriteFailure != null)
                {
                    throw WriteFailure;
                }

                _inner.WriteAllBytesDurable(path, contents);
            }

            public void Replace(string source, string destination, string backup) => _inner.Replace(source, destination, backup);

            public void Move(string source, string destination) => _inner.Move(source, destination);

            public void Delete(string path) => _inner.Delete(path);

            public void Copy(string source, string destination, bool overwrite) => _inner.Copy(source, destination, overwrite);

            public string[] GetFiles(string directory, string searchPattern) => _inner.GetFiles(directory, searchPattern);

            public DateTime GetLastWriteTimeUtc(string path) => _inner.GetLastWriteTimeUtc(path);

            public long GetFileLength(string path) => _inner.GetFileLength(path);

            public IDisposable? TryAcquireExclusive(string path) => _inner.TryAcquireExclusive(path);
        }
    }
}
