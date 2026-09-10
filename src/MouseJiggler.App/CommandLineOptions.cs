using System;

namespace MouseJiggler.App
{
    /// <summary>
    /// The accepted command line. Only fixed switch names are recognised; anything else is an
    /// error rather than a guess, and no argument is ever treated as a path to execute.
    /// </summary>
    internal sealed class CommandLineOptions
    {
        private CommandLineOptions(bool quietStartup, bool showSettings, bool shutdownForUpdate, bool installInitialize, bool? startupEnabled, bool hasError)
        {
            QuietStartup = quietStartup;
            ShowSettings = showSettings;
            ShutdownForUpdate = shutdownForUpdate;
            InstallInitialize = installInitialize;
            StartupEnabled = startupEnabled;
            HasError = hasError;
        }

        /// <summary>Launched at sign-in: create the tray without opening Settings.</summary>
        internal bool QuietStartup { get; }

        internal bool ShowSettings { get; }

        internal bool ShutdownForUpdate { get; }

        internal bool InstallInitialize { get; }

        /// <summary>Only meaningful with <see cref="InstallInitialize"/>.</summary>
        internal bool? StartupEnabled { get; }

        internal bool HasError { get; }

        internal static CommandLineOptions Parse(string[] args)
        {
            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            bool quietStartup = false;
            bool showSettings = false;
            bool shutdownForUpdate = false;
            bool installInitialize = false;
            bool? startupEnabled = null;

            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];

                if (string.Equals(argument, Program.StartupSwitch, StringComparison.Ordinal))
                {
                    quietStartup = true;
                }
                else if (string.Equals(argument, Program.ShowSettingsSwitch, StringComparison.Ordinal))
                {
                    showSettings = true;
                }
                else if (string.Equals(argument, Program.ShutdownForUpdateSwitch, StringComparison.Ordinal))
                {
                    shutdownForUpdate = true;
                }
                else if (string.Equals(argument, Program.InstallInitializeSwitch, StringComparison.Ordinal))
                {
                    installInitialize = true;
                }
                else if (string.Equals(argument, "--startup-enabled", StringComparison.Ordinal))
                {
                    if (i + 1 >= args.Length)
                    {
                        return Invalid();
                    }

                    string value = args[++i];
                    if (string.Equals(value, "true", StringComparison.Ordinal))
                    {
                        startupEnabled = true;
                    }
                    else if (string.Equals(value, "false", StringComparison.Ordinal))
                    {
                        startupEnabled = false;
                    }
                    else
                    {
                        return Invalid();
                    }
                }
                else
                {
                    return Invalid();
                }
            }

            // --startup-enabled only means something to the installer workflow, and that
            // workflow cannot run without it.
            if (installInitialize != startupEnabled.HasValue)
            {
                return Invalid();
            }

            return new CommandLineOptions(quietStartup, showSettings, shutdownForUpdate, installInitialize, startupEnabled, hasError: false);
        }

        private static CommandLineOptions Invalid()
        {
            return new CommandLineOptions(false, false, false, false, null, hasError: true);
        }
    }
}
