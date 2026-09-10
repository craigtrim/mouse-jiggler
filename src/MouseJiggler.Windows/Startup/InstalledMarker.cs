using System;
using System.Globalization;
using System.IO;
using MouseJiggler.Core.Abstractions;

namespace MouseJiggler.Windows.Startup
{
    /// <summary>
    /// Tells the installed edition apart from the portable one.
    /// </summary>
    /// <remarks>
    /// The marker ships only inside the installer payload; the portable ZIP omits it. The edition
    /// is never inferred from the directory name, because a user is free to extract the portable
    /// copy into a folder called anything at all, including one that looks installed.
    ///
    /// A marker that is missing, unreadable or invalid means portable. That is the safe
    /// direction: portable never registers startup on its own, so a damaged marker cannot cause
    /// an unexpected startup entry.
    /// </remarks>
    public sealed class InstalledMarker
    {
        public const string FileName = "installed.marker";

        private InstalledMarker(int schemaVersion, bool startupDefault)
        {
            SchemaVersion = schemaVersion;
            StartupDefault = startupDefault;
        }

        public int SchemaVersion { get; }

        /// <summary>What the installer's Start with Windows checkbox was set to, kept for diagnosis.</summary>
        public bool StartupDefault { get; }

        public static string PathFor(string executableDirectory)
        {
            return Path.Combine(executableDirectory, FileName);
        }

        /// <summary>Reads and validates the marker. Anything unexpected means portable.</summary>
        public static OperationResult<InstalledMarker> TryRead(string executableDirectory, Func<string, bool> exists, Func<string, string> readText)
        {
            if (exists == null)
            {
                throw new ArgumentNullException(nameof(exists));
            }

            if (readText == null)
            {
                throw new ArgumentNullException(nameof(readText));
            }

            string path = PathFor(executableDirectory);

            if (!exists(path))
            {
                return OperationResult<InstalledMarker>.Failure(FaultSubsystem.StartupRegistration, "marker.absent", retryable: false);
            }

            string text;
            try
            {
                text = readText(path);
            }
            catch (IOException)
            {
                return OperationResult<InstalledMarker>.Failure(FaultSubsystem.StartupRegistration, "marker.unreadable", retryable: false);
            }
            catch (UnauthorizedAccessException)
            {
                return OperationResult<InstalledMarker>.Failure(FaultSubsystem.StartupRegistration, "marker.unreadable", retryable: false);
            }

            if (text == null || text.Length > 4096)
            {
                return OperationResult<InstalledMarker>.Failure(FaultSubsystem.StartupRegistration, "marker.invalid", retryable: false);
            }

            int schemaVersion = 0;
            bool? startupDefault = null;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    return OperationResult<InstalledMarker>.Failure(FaultSubsystem.StartupRegistration, "marker.invalid", retryable: false);
                }

                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();

                if (string.Equals(key, "schemaVersion", StringComparison.Ordinal))
                {
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out schemaVersion))
                    {
                        return OperationResult<InstalledMarker>.Failure(FaultSubsystem.StartupRegistration, "marker.invalid", retryable: false);
                    }
                }
                else if (string.Equals(key, "startupDefault", StringComparison.Ordinal))
                {
                    if (string.Equals(value, "true", StringComparison.Ordinal))
                    {
                        startupDefault = true;
                    }
                    else if (string.Equals(value, "false", StringComparison.Ordinal))
                    {
                        startupDefault = false;
                    }
                    else
                    {
                        return OperationResult<InstalledMarker>.Failure(FaultSubsystem.StartupRegistration, "marker.invalid", retryable: false);
                    }
                }
            }

            if (schemaVersion != 1 || !startupDefault.HasValue)
            {
                return OperationResult<InstalledMarker>.Failure(FaultSubsystem.StartupRegistration, "marker.invalid", retryable: false);
            }

            return OperationResult<InstalledMarker>.Success(new InstalledMarker(schemaVersion, startupDefault.Value));
        }

        public static string Serialize(bool startupDefault)
        {
            return "schemaVersion=1\nstartupDefault=" + (startupDefault ? "true" : "false") + "\n";
        }
    }
}
