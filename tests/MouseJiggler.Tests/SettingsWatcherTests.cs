using System;
using System.IO;
using System.Threading;
using MouseJiggler.Core.Settings;
using MouseJiggler.Tests.Fakes;
using MouseJiggler.Windows.Settings;
using MouseJiggler.Windows.Storage;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The cross-session watcher. Another Windows session of the same user commits a revision,
    /// and this session has to notice without being told.
    /// </summary>
    public sealed class SettingsWatcherTests
    {
        [Fact]
        public void ARevisionWrittenByAnotherWriterIsObserved()
        {
            using (var temp = new TemporaryDirectory())
            {
                var paths = new SettingsPaths(temp.Path);

                using (var store = new JsonSettingsStore(paths, new PhysicalFileOperations()))
                using (var watcher = new SettingsFileWatcher(paths, store))
                {
                    SettingsV1? observed = null;
                    using (var seen = new ManualResetEventSlim(false))
                    {
                        store.ExternalChangeObserved += (_, s) => { observed = s; seen.Set(); };

                        // Establishes the baseline revision of 1 from defaults.
                        Assert.True(store.Load().Succeeded);
                        watcher.Start();

                        SettingsV1 started = SettingsIntent.WithRevision(
                            SettingsIntent.WithStopped(SettingsV1.CreateDefault(), false), 2);

                        File.WriteAllBytes(paths.SettingsFile, SettingsDocument.Serialize(started).Value!);

                        Assert.True(seen.Wait(TimeSpan.FromSeconds(10)), "The watcher never reported the change.");
                    }

                    Assert.NotNull(observed);
                    Assert.False(observed!.Stopped);
                    Assert.Equal(2, observed.Revision);
                }
            }
        }
    }
}
