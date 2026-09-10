using System;
using System.Globalization;
using Microsoft.Win32;
using MouseJiggler.Core.Abstractions;

namespace MouseJiggler.Windows.Startup
{
    /// <summary>
    /// The owned HKCU Run value.
    /// </summary>
    /// <remarks>
    /// Windows Startup settings and Task Manager are what actually decide whether a startup
    /// entry runs. This class can report that the app owns a Run value, but the presence of that
    /// value is not proof that Windows has it enabled, and there is no supported way to read the
    /// effective state. The undocumented StartupApproved bytes are deliberately left alone: a
    /// user who switched the app off in Windows must not have that decision quietly undone.
    /// </remarks>
    public sealed class RunKeyStartupRegistration : IStartupRegistration
    {
        public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string ValueName = "MouseJiggler";

        /// <summary>The documented limit on a Run command.</summary>
        public const int MaxCommandLength = 260;

        private readonly Func<RegistryKey?> _openWritable;
        private readonly Func<RegistryKey?> _openReadOnly;

        public RunKeyStartupRegistration()
            : this(
                () => OpenRunKey(writable: true),
                () => OpenRunKey(writable: false))
        {
        }

        /// <summary>
        /// Opens the Run key in the 64-bit view, stated rather than inherited.
        /// </summary>
        /// <remarks>
        /// This application is built x64 with Prefer32Bit off, so the default view is already the
        /// 64-bit one, and HKCU\Software is not a redirected hive in any case. Naming the view
        /// anyway costs one call and removes a dependency between a build setting and where a
        /// startup entry lands. Someone who switches PlatformTarget to AnyCPU for an unrelated
        /// reason should not discover months later that the app writes its Run value somewhere
        /// Windows never reads, with nothing failing at the time to say so.
        /// </remarks>
        private static RegistryKey? OpenRunKey(bool writable)
        {
            using (RegistryKey user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            {
                return user.OpenSubKey(RunKeyPath, writable);
            }
        }

        /// <summary>Used by tests, which never touch the real Run key.</summary>
        public RunKeyStartupRegistration(Func<RegistryKey?> openWritable, Func<RegistryKey?> openReadOnly)
        {
            _openWritable = openWritable ?? throw new ArgumentNullException(nameof(openWritable));
            _openReadOnly = openReadOnly ?? throw new ArgumentNullException(nameof(openReadOnly));
        }

        /// <summary>The exact command written to the registry: a quoted path plus the switch.</summary>
        public static string BuildCommand(string executablePath)
        {
            if (string.IsNullOrEmpty(executablePath))
            {
                throw new ArgumentException("An executable path is required.", nameof(executablePath));
            }

            return string.Format(CultureInfo.InvariantCulture, "\"{0}\" --startup", executablePath);
        }

        /// <summary>
        /// Whether a command looks like this product. Used before replacing or deleting a value,
        /// so another product's entry that happens to share the name is never touched.
        /// </summary>
        public static bool IsOwnedCommand(string? command, out string executablePath)
        {
            executablePath = string.Empty;

            if (string.IsNullOrEmpty(command))
            {
                return false;
            }

            string text = command!.Trim();
            if (text.Length < 3 || text[0] != '"')
            {
                return false;
            }

            int closing = text.IndexOf('"', 1);
            if (closing <= 1)
            {
                return false;
            }

            string path = text.Substring(1, closing - 1);
            string remainder = text.Substring(closing + 1).Trim();

            if (!remainder.Equals("--startup", StringComparison.Ordinal))
            {
                return false;
            }

            if (!path.EndsWith(@"\MouseJiggler.exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            executablePath = path;
            return true;
        }

        public OperationResult<bool> IsRegistered()
        {
            try
            {
                using (RegistryKey? key = _openReadOnly())
                {
                    if (key == null)
                    {
                        return OperationResult<bool>.Success(false);
                    }

                    var command = key.GetValue(ValueName) as string;
                    return OperationResult<bool>.Success(IsOwnedCommand(command, out _));
                }
            }
            catch (System.Security.SecurityException ex)
            {
                return OperationResult<bool>.Failure(FaultSubsystem.StartupRegistration, "startup.accessDenied", ex.HResult);
            }
            catch (UnauthorizedAccessException ex)
            {
                return OperationResult<bool>.Failure(FaultSubsystem.StartupRegistration, "startup.accessDenied", ex.HResult);
            }
        }

        /// <summary>The path currently registered, whether or not it is this copy.</summary>
        public OperationResult<string> GetRegisteredPath()
        {
            try
            {
                using (RegistryKey? key = _openReadOnly())
                {
                    var command = key?.GetValue(ValueName) as string;

                    if (command == null)
                    {
                        return OperationResult<string>.Success(string.Empty);
                    }

                    return IsOwnedCommand(command, out string path)
                        ? OperationResult<string>.Success(path)
                        : OperationResult<string>.Failure(FaultSubsystem.StartupRegistration, "startup.foreignValue", retryable: false);
                }
            }
            catch (System.Security.SecurityException ex)
            {
                return OperationResult<string>.Failure(FaultSubsystem.StartupRegistration, "startup.accessDenied", ex.HResult);
            }
        }

        public OperationResult Register(string executablePath)
        {
            string command = BuildCommand(executablePath);

            if (command.Length > MaxCommandLength)
            {
                // Truncating would produce a command that silently fails at every sign-in.
                return OperationResult.Failure(FaultSubsystem.StartupRegistration, "startup.commandTooLong", retryable: false);
            }

            OperationResult<string> existing = GetRegisteredPath();
            if (!existing.Succeeded && existing.Outcome.Code == "startup.foreignValue")
            {
                // Something else owns this value name. Report it rather than overwrite it.
                return OperationResult.Failure(FaultSubsystem.StartupRegistration, "startup.foreignValue", retryable: false);
            }

            try
            {
                using (RegistryKey? key = _openWritable())
                {
                    if (key == null)
                    {
                        return OperationResult.Failure(FaultSubsystem.StartupRegistration, "startup.keyUnavailable");
                    }

                    key.SetValue(ValueName, command, RegistryValueKind.String);
                    return OperationResult.Success();
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                return OperationResult.Failure(FaultSubsystem.StartupRegistration, "startup.accessDenied", ex.HResult);
            }
            catch (System.Security.SecurityException ex)
            {
                return OperationResult.Failure(FaultSubsystem.StartupRegistration, "startup.accessDenied", ex.HResult);
            }
        }

        public OperationResult Unregister()
        {
            return UnregisterIfPathMatches(null);
        }

        /// <summary>
        /// Removes the value when this product owns it, and treats "nothing registered" as
        /// success. Used when the installer is told not to start the app at sign-in: the goal
        /// is that no entry remains for this executable, not that one had to exist first.
        /// </summary>
        public OperationResult UnregisterIfOwnedByThisProduct(string executablePath)
        {
            OperationResult<string> registered = GetRegisteredPath();

            if (!registered.Succeeded)
            {
                return OperationResult.Failure(registered.Outcome.Subsystem, registered.Outcome.Code!, retryable: false);
            }

            if (string.IsNullOrEmpty(registered.Value))
            {
                return OperationResult.Success();
            }

            return UnregisterIfPathMatches(executablePath);
        }

        /// <summary>
        /// Removes the value only when it points at <paramref name="requiredPath"/>, or at any
        /// owned path when that is null. An installed uninstaller passes its own directory so it
        /// cannot delete a portable copy's registration.
        /// </summary>
        public OperationResult UnregisterIfPathMatches(string? requiredPath)
        {
            try
            {
                using (RegistryKey? key = _openWritable())
                {
                    if (key == null)
                    {
                        return OperationResult.Success();
                    }

                    var command = key.GetValue(ValueName) as string;
                    if (command == null)
                    {
                        return OperationResult.Success();
                    }

                    if (!IsOwnedCommand(command, out string path))
                    {
                        return OperationResult.Failure(FaultSubsystem.StartupRegistration, "startup.foreignValue", retryable: false);
                    }

                    if (requiredPath != null &&
                        !string.Equals(
                            Process.SingleInstanceService.NormalizePath(path),
                            Process.SingleInstanceService.NormalizePath(requiredPath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        // A different copy of this product owns it, so it stays.
                        return OperationResult.Failure(FaultSubsystem.StartupRegistration, "startup.otherInstallation", retryable: false);
                    }

                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                    return OperationResult.Success();
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                return OperationResult.Failure(FaultSubsystem.StartupRegistration, "startup.accessDenied", ex.HResult);
            }
            catch (System.Security.SecurityException ex)
            {
                return OperationResult.Failure(FaultSubsystem.StartupRegistration, "startup.accessDenied", ex.HResult);
            }
        }
    }
}
