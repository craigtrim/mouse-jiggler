using System;
using System.IO;
using System.Threading;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Settings;
using MouseJiggler.Windows.Settings;
using MouseJiggler.Windows.Storage;

namespace MouseJiggler.Windows.Startup
{
    /// <summary>The exit codes the installer reads from --install-initialize.</summary>
    public static class InitializationExitCodes
    {
        public const int Success = 0;
        public const int InvalidArguments = 2;

        /// <summary>A copy at a different path owns the running instance or the startup entry.</summary>
        public const int OwnershipConflict = 3;

        public const int SettingsFailed = 5;
        public const int RegistrationFailed = 6;
    }

    /// <summary>
    /// The installer-only initialization step.
    /// </summary>
    /// <remarks>
    /// This runs no engine and shows no window. It exists because the installer needs startup
    /// configured exactly once, at first install, by the executable that was just placed on
    /// disk, and it needs to know whether that worked.
    ///
    /// It is idempotent, and it never clears an existing Stop or replaces existing settings. A
    /// user who installs over a portable copy they had already stopped must not find the app
    /// running afterwards.
    /// </remarks>
    public sealed class StartupInitializationService
    {
        private readonly SettingsPaths _paths;
        private readonly IFileOperations _files;
        private readonly RunKeyStartupRegistration _registration;

        public StartupInitializationService(SettingsPaths paths, IFileOperations files, RunKeyStartupRegistration registration)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _files = files ?? throw new ArgumentNullException(nameof(files));
            _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        }

        /// <summary>
        /// Creates stopped defaults if there are no settings yet, applies the requested startup
        /// registration, and records that initialization ran.
        /// </summary>
        /// <param name="executablePath">The executable being installed, which is what gets registered.</param>
        /// <param name="startupEnabled">The installer's Start with Windows choice.</param>
        public int Run(string executablePath, bool startupEnabled)
        {
            if (string.IsNullOrEmpty(executablePath))
            {
                return InitializationExitCodes.InvalidArguments;
            }

            // Only an executable that carries a valid marker may configure installed startup.
            // Without this, running --install-initialize against a portable copy would give it
            // a startup entry it is never supposed to get on its own.
            OperationResult<InstalledMarker> marker = InstalledMarker.TryRead(
                Path.GetDirectoryName(executablePath) ?? string.Empty,
                _files.FileExists,
                path => System.Text.Encoding.UTF8.GetString(_files.ReadAllBytes(path)));

            if (!marker.Succeeded)
            {
                return InitializationExitCodes.OwnershipConflict;
            }

            using (var store = new JsonSettingsStore(_paths, _files))
            {
                OperationResult<SettingsV1> loaded = store.Load();

                bool settingsExist = _files.FileExists(_paths.SettingsFile);

                if (!loaded.Succeeded && settingsExist)
                {
                    // Damaged settings are left exactly as they are. Overwriting them here
                    // would destroy a Stop the user had set, on a path where nobody is watching.
                    return InitializationExitCodes.SettingsFailed;
                }

                // Registration first, so a failure there is reported before anything claims the
                // installation was fully initialized.
                OperationResult registrationResult = startupEnabled
                    ? _registration.Register(executablePath)
                    : _registration.UnregisterIfOwnedByThisProduct(executablePath);

                if (!registrationResult.Succeeded)
                {
                    return registrationResult.Code == "startup.foreignValue" || registrationResult.Code == "startup.otherInstallation"
                        ? InitializationExitCodes.OwnershipConflict
                        : InitializationExitCodes.RegistrationFailed;
                }

                // Record that initialization happened, creating stopped defaults if this is a
                // clean machine. An existing document keeps its stopped flag and its mode.
                OperationResult<SettingsV1> saved = store
                    .UpdateAsync(current => SettingsIntent.WithStartupInitialized(current, true), CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                if (!saved.Succeeded)
                {
                    // The registry change stands but the record of it did not. Reporting a
                    // failure is right: a half success marked complete is how a broken install
                    // never gets retried.
                    return InitializationExitCodes.SettingsFailed;
                }

                return InitializationExitCodes.Success;
            }
        }
    }
}
