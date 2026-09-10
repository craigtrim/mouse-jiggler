using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Tests.Fakes;
using MouseJiggler.Windows.Startup;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// Startup registration from issue #9. These use a temporary registry key under
    /// HKCU\Software\MouseJigglerTests, never the real Run key and never StartupApproved.
    /// </summary>
    public sealed class StartupRegistrationTests : IDisposable
    {
        private const string InstalledPath = @"C:\Users\Example\AppData\Local\Programs\MouseJiggler\MouseJiggler.exe";
        private const string PortablePath = @"D:\Portable\MouseJiggler\MouseJiggler.exe";

        private readonly string _keyPath;

        public StartupRegistrationTests()
        {
            _keyPath = @"Software\MouseJigglerTests\" + Guid.NewGuid().ToString("N");
            Registry.CurrentUser.CreateSubKey(_keyPath)?.Dispose();
        }

        private RunKeyStartupRegistration Create()
        {
            return new RunKeyStartupRegistration(
                () => Registry.CurrentUser.OpenSubKey(_keyPath, writable: true),
                () => Registry.CurrentUser.OpenSubKey(_keyPath, writable: false));
        }

        private string? ReadRawValue()
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false))
            {
                return key?.GetValue(RunKeyStartupRegistration.ValueName) as string;
            }
        }

        [Fact]
        public void RegisteringWritesExactlyOneQuotedCommand()
        {
            RunKeyStartupRegistration registration = Create();

            Assert.True(registration.Register(InstalledPath).Succeeded);

            Assert.Equal("\"" + InstalledPath + "\" --startup", ReadRawValue());
            Assert.True(registration.IsRegistered().Value);
        }

        [Fact]
        public void PathsWithSpacesAndNonAsciiCharactersRoundTrip()
        {
            const string Awkward = @"C:\Users\Zoë Smith\Program Files\Mouse Jiggler\MouseJiggler.exe";
            RunKeyStartupRegistration registration = Create();

            Assert.True(registration.Register(Awkward).Succeeded);

            Assert.Equal("\"" + Awkward + "\" --startup", ReadRawValue());
            Assert.Equal(Awkward, registration.GetRegisteredPath().Value);
        }

        [Fact]
        public void AnOverlongPathIsRefusedRatherThanTruncated()
        {
            string deep = @"C:\" + new string('d', 300) + @"\MouseJiggler.exe";
            RunKeyStartupRegistration registration = Create();

            OperationResult result = registration.Register(deep);

            // A truncated command would fail silently at every sign-in.
            Assert.False(result.Succeeded);
            Assert.Equal("startup.commandTooLong", result.Code);
            Assert.Null(ReadRawValue());
        }

        [Fact]
        public void AnotherProductsValueIsNeverOverwrittenOrDeleted()
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true))
            {
                key!.SetValue(RunKeyStartupRegistration.ValueName, @"C:\Other\Thing.exe /background");
            }

            RunKeyStartupRegistration registration = Create();

            Assert.False(registration.Register(InstalledPath).Succeeded);
            Assert.False(registration.Unregister().Succeeded);

            // Still exactly what the other product wrote.
            Assert.Equal(@"C:\Other\Thing.exe /background", ReadRawValue());
        }

        [Fact]
        public void AnUninstallerRemovesOnlyItsOwnInstallationsValue()
        {
            RunKeyStartupRegistration registration = Create();
            Assert.True(registration.Register(PortablePath).Succeeded);

            // The installed uninstaller must not remove a portable copy's registration.
            OperationResult wrongOwner = registration.UnregisterIfPathMatches(InstalledPath);

            Assert.False(wrongOwner.Succeeded);
            Assert.Equal("startup.otherInstallation", wrongOwner.Code);
            Assert.NotNull(ReadRawValue());

            // Its own path does remove it.
            Assert.True(registration.UnregisterIfPathMatches(PortablePath).Succeeded);
            Assert.Null(ReadRawValue());
        }

        [Fact]
        public void UnregisteringWhenNothingIsRegisteredSucceeds()
        {
            Assert.True(Create().Unregister().Succeeded);
        }

        [Theory]
        [InlineData("\"C:\\App\\MouseJiggler.exe\" --startup", true)]
        [InlineData("C:\\App\\MouseJiggler.exe --startup", false)]
        [InlineData("\"C:\\App\\MouseJiggler.exe\"", false)]
        [InlineData("\"C:\\App\\Something.exe\" --startup", false)]
        [InlineData("\"C:\\App\\MouseJiggler.exe\" --startup --extra", false)]
        [InlineData("", false)]
        public void OnlyAWellFormedOwnedCommandIsRecognised(string command, bool expected)
        {
            Assert.Equal(expected, RunKeyStartupRegistration.IsOwnedCommand(command, out _));
        }

        [Fact]
        public void AValidMarkerIdentifiesTheInstalledEdition()
        {
            using (var temp = new TemporaryDirectory())
            {
                File.WriteAllText(InstalledMarker.PathFor(temp.Path), InstalledMarker.Serialize(startupDefault: true));

                OperationResult<InstalledMarker> marker = InstalledMarker.TryRead(temp.Path, File.Exists, File.ReadAllText);

                Assert.True(marker.Succeeded);
                Assert.Equal(1, marker.Value!.SchemaVersion);
                Assert.True(marker.Value.StartupDefault);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("schemaVersion=2\nstartupDefault=true\n")]
        [InlineData("schemaVersion=1\n")]
        [InlineData("garbage without an equals sign")]
        [InlineData("schemaVersion=1\nstartupDefault=maybe\n")]
        public void AMissingOrDamagedMarkerMeansPortable(string contents)
        {
            using (var temp = new TemporaryDirectory())
            {
                File.WriteAllText(InstalledMarker.PathFor(temp.Path), contents);

                OperationResult<InstalledMarker> marker = InstalledMarker.TryRead(temp.Path, File.Exists, File.ReadAllText);

                // Portable never registers startup on its own, so treating a damaged marker as
                // portable cannot produce an unexpected startup entry.
                Assert.False(marker.Succeeded);
            }
        }

        [Fact]
        public void AnAbsentMarkerMeansPortable()
        {
            using (var temp = new TemporaryDirectory())
            {
                OperationResult<InstalledMarker> marker = InstalledMarker.TryRead(temp.Path, File.Exists, File.ReadAllText);

                Assert.False(marker.Succeeded);
                Assert.Equal("marker.absent", marker.Outcome.Code);
            }
        }

        [Fact]
        public void ASubkeyOutlivesTheBaseKeyItWasOpenedThrough()
        {
            // The product opens HKCU in the 64-bit view, takes the Run key from it and lets the
            // base key go. That is only safe because a subkey holds its own handle. If it did
            // not, every startup read would come back empty and report the app as unregistered,
            // which looks exactly like a user who turned the setting off. Asserted against the
            // temporary key rather than the real Run key.
            using (RegistryKey? seed = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true))
            {
                Assert.NotNull(seed);
                seed!.SetValue("Probe", "value");
            }

            RegistryKey? subKey;

            using (RegistryKey user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            {
                subKey = user.OpenSubKey(_keyPath, writable: false);
            }

            using (subKey)
            {
                Assert.NotNull(subKey);
                Assert.Equal("value", subKey!.GetValue("Probe"));
            }
        }

        [Fact]
        public void TheSixtyFourBitViewReachesTheSameCurrentUserData()
        {
            // HKCU\Software is not a redirected hive, so the two views must agree. Stating the
            // view is about not depending on PlatformTarget, and this is what says the stated
            // view is the same place the rest of the tests write to.
            using (RegistryKey? seed = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true))
            {
                Assert.NotNull(seed);
                seed!.SetValue("Probe", "shared");
            }

            using (RegistryKey user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            using (RegistryKey? viewed = user.OpenSubKey(_keyPath, writable: false))
            {
                Assert.NotNull(viewed);
                Assert.Equal("shared", viewed!.GetValue("Probe"));
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
