using System;
using System.IO;
using System.Text;
using Microsoft.Win32;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Settings;
using MouseJiggler.Tests.Fakes;
using MouseJiggler.Windows.Settings;
using MouseJiggler.Windows.Startup;
using MouseJiggler.Windows.Storage;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The installer-only initialization workflow from issue #9. It must be idempotent, must
    /// never clear an existing Stop, and must refuse to configure startup for an executable it
    /// cannot prove is the installed one.
    /// </summary>
    public sealed class StartupInitializationTests : IDisposable
    {
        private readonly string _keyPath;

        public StartupInitializationTests()
        {
            _keyPath = @"Software\MouseJigglerTests\" + Guid.NewGuid().ToString("N");
            Registry.CurrentUser.CreateSubKey(_keyPath)?.Dispose();
        }

        private RunKeyStartupRegistration Registration()
        {
            return new RunKeyStartupRegistration(
                () => Registry.CurrentUser.OpenSubKey(_keyPath, writable: true),
                () => Registry.CurrentUser.OpenSubKey(_keyPath, writable: false));
        }

        private string? RegisteredCommand()
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false))
            {
                return key?.GetValue(RunKeyStartupRegistration.ValueName) as string;
            }
        }

        /// <summary>Creates an install directory carrying a valid marker plus the executable.</summary>
        private static string CreateInstalledLayout(string root)
        {
            string appDirectory = Path.Combine(root, "Programs", "MouseJiggler");
            Directory.CreateDirectory(appDirectory);

            string exe = Path.Combine(appDirectory, "MouseJiggler.exe");
            File.WriteAllText(exe, "stand-in for the executable");
            File.WriteAllText(InstalledMarker.PathFor(appDirectory), InstalledMarker.Serialize(startupDefault: true));

            return exe;
        }

        [Fact]
        public void AFreshInstallRegistersStartupAndLeavesTheAppStopped()
        {
            using (var temp = new TemporaryDirectory())
            {
                string exe = CreateInstalledLayout(temp.Path);
                var paths = new SettingsPaths(Path.Combine(temp.Path, "data"));
                var service = new StartupInitializationService(paths, new PhysicalFileOperations(), Registration());

                int code = service.Run(exe, startupEnabled: true);

                Assert.Equal(InitializationExitCodes.Success, code);
                Assert.Equal("\"" + exe + "\" --startup", RegisteredCommand());

                using (var store = new JsonSettingsStore(paths, new PhysicalFileOperations()))
                {
                    OperationResult<SettingsV1> saved = store.Load();

                    Assert.True(saved.Succeeded);
                    Assert.True(saved.Value!.Stopped);
                    Assert.True(saved.Value.StartupInitialized);
                }
            }
        }

        [Fact]
        public void TheStartupCheckboxOffLeavesNoRunValue()
        {
            using (var temp = new TemporaryDirectory())
            {
                string exe = CreateInstalledLayout(temp.Path);
                var paths = new SettingsPaths(Path.Combine(temp.Path, "data"));
                var service = new StartupInitializationService(paths, new PhysicalFileOperations(), Registration());

                int code = service.Run(exe, startupEnabled: false);

                Assert.Equal(InitializationExitCodes.Success, code);
                Assert.Null(RegisteredCommand());
            }
        }

        [Fact]
        public void RunningInitializationTwiceIsHarmless()
        {
            using (var temp = new TemporaryDirectory())
            {
                string exe = CreateInstalledLayout(temp.Path);
                var paths = new SettingsPaths(Path.Combine(temp.Path, "data"));
                var service = new StartupInitializationService(paths, new PhysicalFileOperations(), Registration());

                Assert.Equal(InitializationExitCodes.Success, service.Run(exe, startupEnabled: true));
                Assert.Equal(InitializationExitCodes.Success, service.Run(exe, startupEnabled: true));

                Assert.Equal("\"" + exe + "\" --startup", RegisteredCommand());
            }
        }

        [Fact]
        public void AnExistingStopSurvivesInstallation()
        {
            using (var temp = new TemporaryDirectory())
            {
                string exe = CreateInstalledLayout(temp.Path);
                var paths = new SettingsPaths(Path.Combine(temp.Path, "data"));
                var files = new PhysicalFileOperations();

                // A portable copy the user had already configured and stopped.
                Directory.CreateDirectory(paths.RootDirectory);
                SettingsV1 existing = SettingsIntent.WithRevision(
                    new SettingsPatch { IntervalSeconds = 120 }.ApplyTo(SettingsV1.CreateDefault()), 7);
                File.WriteAllBytes(paths.SettingsFile, SettingsDocument.Serialize(existing).Value!);

                var service = new StartupInitializationService(paths, files, Registration());
                Assert.Equal(InitializationExitCodes.Success, service.Run(exe, startupEnabled: true));

                using (var store = new JsonSettingsStore(paths, files))
                {
                    SettingsV1 after = store.Load().Value!;

                    // Installing over an existing configuration must not start the app or
                    // discard what the user had chosen.
                    Assert.True(after.Stopped);
                    Assert.Equal(120, after.IntervalSeconds);
                    Assert.True(after.StartupInitialized);
                }
            }
        }

        [Fact]
        public void AnExecutableWithoutAValidMarkerIsRefused()
        {
            using (var temp = new TemporaryDirectory())
            {
                // A portable layout: the executable is there, the marker is not.
                string appDirectory = Path.Combine(temp.Path, "Portable");
                Directory.CreateDirectory(appDirectory);
                string exe = Path.Combine(appDirectory, "MouseJiggler.exe");
                File.WriteAllText(exe, "stand-in");

                var paths = new SettingsPaths(Path.Combine(temp.Path, "data"));
                var service = new StartupInitializationService(paths, new PhysicalFileOperations(), Registration());

                int code = service.Run(exe, startupEnabled: true);

                // Portable copies never get an installed startup entry this way.
                Assert.Equal(InitializationExitCodes.OwnershipConflict, code);
                Assert.Null(RegisteredCommand());
            }
        }

        [Fact]
        public void ADamagedMarkerIsRefusedRatherThanTrusted()
        {
            using (var temp = new TemporaryDirectory())
            {
                string appDirectory = Path.Combine(temp.Path, "Programs", "MouseJiggler");
                Directory.CreateDirectory(appDirectory);
                string exe = Path.Combine(appDirectory, "MouseJiggler.exe");
                File.WriteAllText(exe, "stand-in");
                File.WriteAllText(InstalledMarker.PathFor(appDirectory), "schemaVersion=99\nstartupDefault=yes\n");

                var paths = new SettingsPaths(Path.Combine(temp.Path, "data"));
                var service = new StartupInitializationService(paths, new PhysicalFileOperations(), Registration());

                Assert.Equal(InitializationExitCodes.OwnershipConflict, service.Run(exe, startupEnabled: true));
            }
        }

        [Fact]
        public void AnotherProductsRunValueIsReportedAsAConflict()
        {
            using (var temp = new TemporaryDirectory())
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true))
                {
                    key!.SetValue(RunKeyStartupRegistration.ValueName, @"C:\Other\Thing.exe /background");
                }

                string exe = CreateInstalledLayout(temp.Path);
                var paths = new SettingsPaths(Path.Combine(temp.Path, "data"));
                var service = new StartupInitializationService(paths, new PhysicalFileOperations(), Registration());

                Assert.Equal(InitializationExitCodes.OwnershipConflict, service.Run(exe, startupEnabled: true));
                Assert.Equal(@"C:\Other\Thing.exe /background", RegisteredCommand());
            }
        }

        [Fact]
        public void DamagedSettingsAreLeftAloneRatherThanOverwritten()
        {
            using (var temp = new TemporaryDirectory())
            {
                string exe = CreateInstalledLayout(temp.Path);
                var paths = new SettingsPaths(Path.Combine(temp.Path, "data"));

                Directory.CreateDirectory(paths.RootDirectory);
                const string Damaged = "{ this is not a settings document";
                File.WriteAllText(paths.SettingsFile, Damaged);

                var service = new StartupInitializationService(paths, new PhysicalFileOperations(), Registration());
                int code = service.Run(exe, startupEnabled: true);

                Assert.Equal(InitializationExitCodes.SettingsFailed, code);

                // Overwriting here would destroy a Stop the user had set, on a path where
                // nobody is watching the result.
                Assert.Equal(Damaged, File.ReadAllText(paths.SettingsFile));
            }
        }

        public void Dispose()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
            }
            catch (ArgumentException)
            {
            }
        }
    }
}
