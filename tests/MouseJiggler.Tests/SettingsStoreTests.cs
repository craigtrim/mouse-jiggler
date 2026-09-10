using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Settings;
using MouseJiggler.Tests.Fakes;
using MouseJiggler.Windows.Settings;
using MouseJiggler.Windows.Storage;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The persistence contract from issue #4. These run against a temporary directory with
    /// injectable file operations, so no test reads or writes the real user settings.
    /// </summary>
    public sealed class SettingsStoreTests
    {
        [Fact]
        public async Task AMissingFileYieldsStoppedDefaultsAndTheFirstSaveCreatesIt()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);
                using (var store = new JsonSettingsStore(paths, new PhysicalFileOperations()))
                {
                    OperationResult<SettingsV1> loaded = store.Load();

                    Assert.True(loaded.Succeeded);
                    Assert.True(loaded.Value!.Stopped);
                    Assert.False(File.Exists(paths.SettingsFile));

                    OperationResult<SettingsV1> saved = await store.UpdateAsync(
                        current => new SettingsPatch { IntervalSeconds = 45 }.ApplyTo(current),
                        CancellationToken.None);

                    Assert.True(saved.Succeeded);
                    Assert.True(File.Exists(paths.SettingsFile));
                    Assert.Equal(45, saved.Value!.IntervalSeconds);
                    Assert.Equal(2, saved.Value.Revision);
                }
            }
        }

        [Fact]
        public async Task SavingPreferencesWhileStoppedLeavesTheAppStopped()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);
                using (var store = new JsonSettingsStore(paths, new PhysicalFileOperations()))
                {
                    // Every preference the Settings form can reach, changed at once.
                    var patch = new SettingsPatch
                    {
                        ScheduleEnabled = true,
                        ScheduleStart = "09:00",
                        ScheduleEnd = "18:00",
                        DayMask = 31,
                        PauseOnBattery = false,
                        KeepDisplayOn = false,
                        JiggleMouse = false,
                        IntervalSeconds = 120,
                        DiagnosticLogging = true,
                    };

                    OperationResult<SettingsV1> saved = await store.UpdateAsync(patch.ApplyTo, CancellationToken.None);

                    Assert.True(saved.Succeeded);
                    Assert.True(saved.Value!.Stopped);
                    Assert.Equal(RunMode.Scheduled, saved.Value.RunMode);
                }
            }
        }

        [Fact]
        public async Task RevisionIncreasesOncePerCommitAndConcurrentPatchesDoNotLoseEachOther()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);
                var files = new PhysicalFileOperations();

                // Two separate store instances, standing in for two Windows sessions.
                using (var first = new JsonSettingsStore(paths, files))
                using (var second = new JsonSettingsStore(paths, files))
                {
                    await first.UpdateAsync(c => new SettingsPatch { IntervalSeconds = 60 }.ApplyTo(c), CancellationToken.None);
                    await second.UpdateAsync(c => new SettingsPatch { KeepDisplayOn = false }.ApplyTo(c), CancellationToken.None);

                    OperationResult<SettingsV1> final = first.Load();

                    Assert.True(final.Succeeded);

                    // Neither write clobbered the other's field.
                    Assert.Equal(60, final.Value!.IntervalSeconds);
                    Assert.False(final.Value.KeepDisplayOn);
                    Assert.Equal(3, final.Value.Revision);
                }
            }
        }

        [Fact]
        public async Task AStaleFormSaveCannotClearAStopThatCameAfterIt()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);
                using (var store = new JsonSettingsStore(paths, new PhysicalFileOperations()))
                {
                    // Start, then Stop.
                    await store.UpdateAsync(c => SettingsIntent.WithStopped(c, false), CancellationToken.None);
                    store.MarkStopIssued();
                    await store.UpdateAsync(c => SettingsIntent.WithStopped(c, true), CancellationToken.None);

                    // A form that was opened before the Stop now saves its preference edits.
                    await store.UpdateAsync(c => new SettingsPatch { IntervalSeconds = 90 }.ApplyTo(c), CancellationToken.None);

                    OperationResult<SettingsV1> final = store.Load();

                    Assert.True(final.Value!.Stopped);
                    Assert.Equal(90, final.Value.IntervalSeconds);
                    Assert.False(store.HasUncommittedStop);
                }
            }
        }

        [Fact]
        public void ARemoteStartIsRefusedWhileThisSessionHoldsAnUncommittedStop()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);
                using (var store = new JsonSettingsStore(paths, new PhysicalFileOperations()))
                {
                    SettingsV1 remoteStart = SettingsIntent.WithStopped(SettingsV1.CreateDefault(), false);

                    // With nothing pending, another session's Start is honoured.
                    Assert.True(store.ShouldApplyObservedRevision(remoteStart));

                    // Once this session has issued a Stop, it is not.
                    store.MarkStopIssued();
                    Assert.False(store.ShouldApplyObservedRevision(remoteStart));

                    // A remote Stop always applies.
                    Assert.True(store.ShouldApplyObservedRevision(SettingsV1.CreateDefault()));
                }
            }
        }

        [Fact]
        public async Task AFailedWriteLeavesThePreviousDocumentIntact()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);
                var files = new FaultInjectingFileOperations(new PhysicalFileOperations());

                using (var store = new JsonSettingsStore(paths, files))
                {
                    await store.UpdateAsync(c => new SettingsPatch { IntervalSeconds = 30 }.ApplyTo(c), CancellationToken.None);
                    byte[] before = File.ReadAllBytes(paths.SettingsFile);

                    files.FailWrite = true;
                    OperationResult<SettingsV1> failed = await store.UpdateAsync(
                        c => new SettingsPatch { IntervalSeconds = 300 }.ApplyTo(c),
                        CancellationToken.None);

                    Assert.False(failed.Succeeded);
                    Assert.Equal("settings.io", failed.Outcome.Code);
                    Assert.Equal(before, File.ReadAllBytes(paths.SettingsFile));
                }
            }
        }

        [Fact]
        public async Task AFailedReplaceLeavesACompleteDocumentAndNoStrayTempFile()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);
                var files = new FaultInjectingFileOperations(new PhysicalFileOperations());

                using (var store = new JsonSettingsStore(paths, files))
                {
                    await store.UpdateAsync(c => new SettingsPatch { IntervalSeconds = 30 }.ApplyTo(c), CancellationToken.None);

                    files.FailReplace = true;
                    OperationResult<SettingsV1> failed = await store.UpdateAsync(
                        c => new SettingsPatch { IntervalSeconds = 300 }.ApplyTo(c),
                        CancellationToken.None);

                    Assert.False(failed.Succeeded);

                    // The document still parses, and the scratch file was cleaned up.
                    OperationResult<SettingsV1> reloaded = store.Load();
                    Assert.True(reloaded.Succeeded);
                    Assert.Equal(30, reloaded.Value!.IntervalSeconds);
                    Assert.Empty(Directory.GetFiles(temp.Path, SettingsPaths.TempFilePrefix + "*"));
                }
            }
        }

        [Fact]
        public async Task LockContentionTimesOutInsteadOfHanging()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);
                var files = new FaultInjectingFileOperations(new PhysicalFileOperations()) { DenyLock = true };

                using (var store = new JsonSettingsStore(paths, files))
                {
                    OperationResult<SettingsV1> failed = await store.UpdateAsync(
                        c => new SettingsPatch { IntervalSeconds = 60 }.ApplyTo(c),
                        CancellationToken.None);

                    Assert.False(failed.Succeeded);
                    Assert.Equal("settings.lockTimeout", failed.Outcome.Code);
                    Assert.True(failed.Outcome.Retryable);
                }
            }
        }

        [Fact]
        public async Task AnInvalidCandidateIsNeverCommitted()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);
                using (var store = new JsonSettingsStore(paths, new PhysicalFileOperations()))
                {
                    // Enabling a schedule with no days selected is meaningless, so it is refused
                    // rather than quietly treated as every day.
                    OperationResult<SettingsV1> failed = await store.UpdateAsync(
                        c => new SettingsPatch { ScheduleEnabled = true, DayMask = 0 }.ApplyTo(c),
                        CancellationToken.None);

                    Assert.False(failed.Succeeded);
                    Assert.Equal("settings.schedule.noDays", failed.Outcome.Code);
                    Assert.False(File.Exists(paths.SettingsFile));
                }
            }
        }

        [Fact]
        public void ResettingQuarantinesTheOldFileAndReturnsStoppedDefaults()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);
                Directory.CreateDirectory(temp.Path);
                File.WriteAllText(paths.SettingsFile, "{ this is not a settings document");

                using (var store = new JsonSettingsStore(paths, new PhysicalFileOperations()))
                {
                    Assert.False(store.Load().Succeeded);

                    OperationResult<SettingsV1> reset = store.ResetToDefaults();

                    Assert.True(reset.Succeeded);
                    Assert.True(reset.Value!.Stopped);
                    Assert.Single(Directory.GetFiles(temp.Path, SettingsPaths.InvalidBackupPrefix + "*.json"));
                    Assert.True(store.Load().Succeeded);
                }
            }
        }
    }
}
