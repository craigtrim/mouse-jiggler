using System;
using System.Reflection;
using System.Windows.Forms;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Windows.Process;
using MouseJiggler.Windows.Settings;
using MouseJiggler.Windows.Startup;
using MouseJiggler.Windows.Storage;

namespace MouseJiggler.App
{
    /// <summary>
    /// The STA entry point. It composes the Windows adapters into one ApplicationContext and
    /// hands control to the message loop; no visible form owns the process lifetime.
    /// </summary>
    internal static class Program
    {
        /// <summary>Launched by the HKCU Run value at sign-in. Stays quiet: no Settings window.</summary>
        internal const string StartupSwitch = "--startup";

        /// <summary>A second launch asks the existing instance to show Settings.</summary>
        internal const string ShowSettingsSwitch = "--show-settings";

        /// <summary>The installer asks a running instance of the same executable to exit gracefully.</summary>
        internal const string ShutdownForUpdateSwitch = "--shutdown-for-update";

        /// <summary>Installer-only initialization; runs no engine and shows no Settings.</summary>
        internal const string InstallInitializeSwitch = "--install-initialize";

        private static readonly TimeSpan IpcTimeout = TimeSpan.FromSeconds(5);

        [STAThread]
        private static int Main(string[] args)
        {
            // Must precede the creation of any control.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            CommandLineOptions options = CommandLineOptions.Parse(args);
            if (options.HasError)
            {
                return ExitCodes.InvalidArguments;
            }

            string executablePath = Assembly.GetExecutingAssembly().Location;

            using (var instance = new SingleInstanceService(
                SingleInstanceService.CurrentUserSid(),
                SingleInstanceService.CurrentSessionId(),
                executablePath))
            {
                if (!instance.TryAcquireOwnership())
                {
                    return TalkToRunningInstance(instance, options);
                }

                if (options.InstallInitialize)
                {
                    // Nothing else owns this session, so initialize directly.
                    return RunInstallInitialize(executablePath, options.StartupEnabled!.Value);
                }

                if (options.ShutdownForUpdate)
                {
                    // Nothing was running, so there is nothing to shut down.
                    return ExitCodes.Success;
                }

                var context = new TrayApplicationContext(options.QuietStartup, instance);
                instance.RequestHandler = context.HandleIpcRequest;
                instance.StartListening();

                // A failure anywhere must still release the wake request. Without these, an
                // unhandled exception would kill the process with the request standing, and the
                // machine would stay awake with nothing left to switch it off.
                Application.ThreadException += (_, e) => context.HandleFatalError(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (_, e) => context.HandleFatalError(e.ExceptionObject as Exception);

                Application.Run(context);
                return ExitCodes.Success;
            }
        }

        /// <summary>
        /// The installer-only path: no engine, no window, and an exit code the installer reads.
        /// </summary>
        private static int RunInstallInitialize(string executablePath, bool startupEnabled)
        {
            var service = new StartupInitializationService(
                SettingsPaths.ForCurrentUser(),
                new PhysicalFileOperations(),
                new RunKeyStartupRegistration());

            return service.Run(executablePath, startupEnabled);
        }

        /// <summary>
        /// Hands the request to the instance that already owns this session. A second engine is
        /// never started: two of them would fight over the same settings file and the same wake
        /// request.
        /// </summary>
        private static int TalkToRunningInstance(SingleInstanceService instance, CommandLineOptions options)
        {
            if (options.QuietStartup)
            {
                // A duplicate sign-in launch leaves quietly rather than stealing focus.
                return ExitCodes.Success;
            }

            if (options.InstallInitialize)
            {
                // Something already owns this session. Initialization may only proceed when that
                // owner is the same executable, so an installer for one copy cannot configure
                // startup for a different one.
                int conflict = CheckOwnerIsThisExecutable(instance);
                if (conflict != ExitCodes.Success)
                {
                    return conflict;
                }

                return RunInstallInitialize(instance.ExecutablePath, options.StartupEnabled!.Value);
            }

            if (options.ShutdownForUpdate)
            {
                // Only the same executable may ask the owner to close, so an installer for one
                // copy cannot shut down a different one.
                int conflict = CheckOwnerIsThisExecutable(instance);
                if (conflict != ExitCodes.Success)
                {
                    return conflict;
                }

                string? response = instance.SendRequest(IpcCommands.ShutdownForUpdate, IpcTimeout);

                if (string.Equals(response, IpcCommands.ResponseOk, StringComparison.Ordinal))
                {
                    return ExitCodes.Success;
                }

                // The instance exited but its Stop never reached disk. The installer needs to
                // know, because a restart would otherwise resurrect the previous state.
                return ExitCodes.PersistenceFailed;
            }

            string? shown = instance.SendRequest(IpcCommands.ShowSettings, IpcTimeout);
            if (shown == null)
            {
                MessageBox.Show(
                    "Mouse Jiggler is already running, but it is not responding. Try closing it from the notification area, or sign out and back in.",
                    "Mouse Jiggler",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return ExitCodes.PersistenceFailed;
            }

            return ExitCodes.Success;
        }

        /// <summary>
        /// Success when the process answering this session's pipe is running this same
        /// executable, and an exit code to return when it is not.
        /// </summary>
        /// <remarks>
        /// The owner's path is taken from the operating system rather than from the reply, so a
        /// process that answered the pipe first cannot claim to be this executable and have an
        /// installer act on the claim. An answer that cannot be verified is treated the same as
        /// a conflict: refusing costs the installer a message asking the user to close the app,
        /// and proceeding could close or reconfigure something else entirely.
        /// </remarks>
        private static int CheckOwnerIsThisExecutable(SingleInstanceService instance)
        {
            OperationResult<IpcIdentity> owner = instance.RequestIdentity(IpcTimeout);

            if (!owner.Succeeded)
            {
                // Nothing answered is a communication failure the installer should report as
                // such. Everything else is an ownership question answered no.
                return string.Equals(owner.Outcome.Code, "ipc.noAnswer", StringComparison.Ordinal)
                    ? ExitCodes.PersistenceFailed
                    : ExitCodes.OwnershipConflict;
            }

            return instance.IsSameExecutable(owner.Value!.ExecutablePath)
                ? ExitCodes.Success
                : ExitCodes.OwnershipConflict;
        }
    }

    /// <summary>Process exit codes. The installer reads these, so they are part of the contract.</summary>
    internal static class ExitCodes
    {
        internal const int Success = 0;
        internal const int InvalidArguments = 2;

        /// <summary>Another executable path owns the running instance or the startup entry.</summary>
        internal const int OwnershipConflict = 3;

        /// <summary>An outstanding Stop could not be committed before shutdown.</summary>
        internal const int PersistenceFailed = 4;
    }
}
