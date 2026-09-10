using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Settings storage under paths that are perfectly legal and rarely tested.
    /// </summary>
    /// <remarks>
    /// A Windows account name can be almost anything, and %LOCALAPPDATA% is built from it. A
    /// developer whose account is called "Craig" never sees what happens to someone whose
    /// account is called "Zoë", "日本語" or "Ωμέγα", and the failure would look like the app
    /// simply not saving. Every path here is one a real person could have.
    /// </remarks>
    public sealed class AwkwardPathTests
    {
        public static IEnumerable<object[]> AwkwardNames()
        {
            yield return new object[] { "Zoë Ashworth" };
            yield return new object[] { "日本語ユーザー" };
            yield return new object[] { "Ωμέγα" };
            yield return new object[] { "Пользователь" };
            yield return new object[] { "user with spaces" };
            yield return new object[] { "user'with'quotes" };
            yield return new object[] { "user.with.dots" };
            yield return new object[] { "café-naïve" };
        }

        private static SettingsPaths PathsUnder(string root)
        {
            Directory.CreateDirectory(root);
            return new SettingsPaths(root);
        }

        [Theory]
        [MemberData(nameof(AwkwardNames))]
        public async Task SettingsSaveAndReloadUnderAnAwkwardAccountName(string accountName)
        {
            using (var directory = new TemporaryDirectory())
            {
                string root = Path.Combine(directory.Path, accountName, "MouseJiggler");
                SettingsPaths paths = PathsUnder(root);

                using (var store = new JsonSettingsStore(paths, new PhysicalFileOperations()))
                {
                    OperationResult<SettingsV1> saved = await store
                        .UpdateAsync(current => SettingsIntent.WithStopped(current, false), CancellationToken.None)
                        .ConfigureAwait(true);

                    Assert.True(saved.Succeeded, "Saving failed under " + accountName + ": " + saved.Outcome.Code);
                    Assert.False(saved.Value!.Stopped);
                }

                // A second store, as a restart would be. The bytes on disk have to survive the
                // round trip through a path the writer and the reader both had to encode.
                using (var reopened = new JsonSettingsStore(paths, new PhysicalFileOperations()))
                {
                    OperationResult<SettingsV1> loaded = reopened.Load();

                    Assert.True(loaded.Succeeded, "Reloading failed under " + accountName + ": " + loaded.Outcome.Code);
                    Assert.False(loaded.Value!.Stopped);
                }
            }
        }

        [Theory]
        [MemberData(nameof(AwkwardNames))]
        public void TheLogAndLockPathsAreBuiltFromTheSameRoot(string accountName)
        {
            using (var directory = new TemporaryDirectory())
            {
                string root = Path.Combine(directory.Path, accountName, "MouseJiggler");
                SettingsPaths paths = PathsUnder(root);

                // Every owned path has to sit inside the one directory, or an uninstall that
                // removes the directory leaves something behind.
                Assert.StartsWith(root, paths.SettingsFile, StringComparison.Ordinal);
                Assert.StartsWith(root, paths.LockFile, StringComparison.Ordinal);
                Assert.StartsWith(root, paths.LogsDirectory, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task ADiskWithNoSpaceLeftIsReportedRatherThanLosingTheDocument()
        {
            using (var directory = new TemporaryDirectory())
            {
                SettingsPaths paths = PathsUnder(Path.Combine(directory.Path, "MouseJiggler"));
                var files = new FaultInjectingFileOperations(new PhysicalFileOperations());

                using (var store = new JsonSettingsStore(paths, files))
                {
                    OperationResult<SettingsV1> first = await store
                        .UpdateAsync(current => SettingsIntent.WithStopped(current, false), CancellationToken.None)
                        .ConfigureAwait(true);

                    Assert.True(first.Succeeded);

                    files.FailWrite = true;

                    OperationResult<SettingsV1> second = await store
                        .UpdateAsync(current => SettingsIntent.WithStopped(current, true), CancellationToken.None)
                        .ConfigureAwait(true);

                    Assert.False(second.Succeeded);

                    // A failure code, not an exception, and not a truncated file. The previous
                    // document has to still be readable: a full disk must cost the change, never
                    // the settings that were already saved.
                    Assert.NotNull(second.Outcome.Code);
                    Assert.Equal(FaultSubsystem.ConfigSave, second.Outcome.Subsystem);

                    files.FailWrite = false;

                    OperationResult<SettingsV1> reloaded = store.Load();
                    Assert.True(reloaded.Succeeded);
                    Assert.False(reloaded.Value!.Stopped);
                }
            }
        }

        [Fact]
        public async Task ALockedSettingsFileIsReportedWithoutDiscardingTheIntent()
        {
            using (var directory = new TemporaryDirectory())
            {
                SettingsPaths paths = PathsUnder(Path.Combine(directory.Path, "MouseJiggler"));
                var files = new FaultInjectingFileOperations(new PhysicalFileOperations());

                using (var store = new JsonSettingsStore(paths, files))
                {
                    await store.UpdateAsync(current => current, CancellationToken.None).ConfigureAwait(true);

                    // Another session is holding the lock and will not let go.
                    files.DenyLock = true;

                    OperationResult<SettingsV1> blocked = await store
                        .UpdateAsync(current => SettingsIntent.WithStopped(current, true), CancellationToken.None)
                        .ConfigureAwait(true);

                    Assert.False(blocked.Succeeded);
                    Assert.Equal("settings.lockTimeout", blocked.Outcome.Code);

                    // Retryable, because the other session will finish. A fault that says
                    // otherwise would turn a moment of contention into a permanent failure.
                    Assert.True(blocked.Outcome.Retryable);

                    files.DenyLock = false;

                    OperationResult<SettingsV1> retried = await store
                        .UpdateAsync(current => SettingsIntent.WithStopped(current, true), CancellationToken.None)
                        .ConfigureAwait(true);

                    Assert.True(retried.Succeeded);
                    Assert.True(retried.Value!.Stopped);
                }
            }
        }

        [Fact]
        public void AVeryLongPathIsRejectedCleanlyRatherThanThrowing()
        {
            using (var directory = new TemporaryDirectory())
            {
                // Beyond MAX_PATH without long-path support enabled. The app declares
                // longPathAware, but the answer must be a fault either way rather than an
                // exception escaping into the message loop.
                string deep = directory.Path;

                for (int i = 0; i < 30; i++)
                {
                    deep = Path.Combine(deep, new string('d', 20));
                }

                var paths = new SettingsPaths(deep);

                using (var store = new JsonSettingsStore(paths, new PhysicalFileOperations()))
                {
                    OperationResult<SettingsV1> loaded = store.Load();

                    // Either it works, because long paths are enabled, or it reports a fault.
                    // What it must never do is throw.
                    Assert.True(loaded.Succeeded || loaded.Outcome.Code != null);
                }
            }
        }
    }
}
