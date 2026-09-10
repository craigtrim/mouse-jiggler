using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Diagnostics;
using MouseJiggler.Core.Environment;
using MouseJiggler.Core.Settings;

namespace MouseJiggler.Windows.Diagnostics
{
    /// <summary>
    /// Builds the text that Copy diagnostics puts on the clipboard.
    /// </summary>
    /// <remarks>
    /// This is the one place where information leaves the app, so it is deliberately a fixed
    /// list of fields rather than a dump. The user data directory appears as the literal token
    /// %LOCALAPPDATA%, never expanded, because the expanded form contains the account name.
    /// Nothing here reads a window title, a process name or a pointer position.
    /// </remarks>
    public static class DiagnosticSnapshotBuilder
    {
        public static string AppVersion =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        public static string Build(
            SettingsV1 settings,
            DesiredEffects effects,
            PowerSource power,
            SessionState session,
            bool installedEdition,
            IReadOnlyList<DiagnosticEvent> recentEvents)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (effects == null)
            {
                throw new ArgumentNullException(nameof(effects));
            }

            if (recentEvents == null)
            {
                throw new ArgumentNullException(nameof(recentEvents));
            }

            var builder = new StringBuilder(2048);

            builder.AppendLine("Mouse Jiggler diagnostics");
            builder.AppendLine("=========================");
            builder.AppendLine();

            builder.AppendLine("App version: " + AppVersion);
            builder.AppendLine("Edition: " + (installedEdition ? "installed" : "portable"));
            builder.AppendLine("Windows: " + DescribeWindows());
            builder.AppendLine("Architecture: " + (IntPtr.Size == 8 ? "x64" : "x86"));
            builder.AppendLine("Framework release: " + DescribeFrameworkRelease());
            builder.AppendLine("Data directory: %LOCALAPPDATA%\\MouseJiggler");
            builder.AppendLine();

            builder.AppendLine("Current state");
            builder.AppendLine("  Status: " + effects.Status);
            builder.AppendLine("  Power: " + power);
            builder.AppendLine("  Session: " + session);
            builder.AppendLine("  System awake requested: " + effects.SystemAwake);
            builder.AppendLine("  Display awake requested: " + effects.DisplayAwake);
            builder.AppendLine("  Pointer movement allowed: " + effects.MayJiggle);
            builder.AppendLine("  Wake request failed: " + effects.WakeRequestFailed);
            builder.AppendLine("  Input request failed: " + effects.InputRequestFailed);
            builder.AppendLine();

            builder.AppendLine("Settings");
            builder.AppendLine("  Stopped: " + settings.Stopped);
            builder.AppendLine("  Mode: " + settings.RunMode);
            builder.AppendLine("  Schedule enabled: " + settings.ScheduleEnabled);
            builder.AppendLine("  Window: " + settings.ScheduleStart + " to " + settings.ScheduleEnd);
            builder.AppendLine("  Day mask: " + settings.DayMask.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("  Pause on battery: " + settings.PauseOnBattery);
            builder.AppendLine("  Keep display on: " + settings.KeepDisplayOn);
            builder.AppendLine("  Jiggle mouse: " + settings.JiggleMouse);
            builder.AppendLine("  Interval seconds: " + settings.IntervalSeconds.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("  Diagnostic logging: " + settings.DiagnosticLogging);
            builder.AppendLine();

            builder.AppendLine("Recent events (" + recentEvents.Count.ToString(CultureInfo.InvariantCulture) + ")");

            foreach (DiagnosticEvent entry in recentEvents)
            {
                builder.Append("  ");
                builder.Append(entry.UtcTimestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                builder.Append("  ");
                builder.Append(entry.Subsystem);
                builder.Append("  ");
                builder.Append(entry.Code);

                if (entry.NativeErrorCode.HasValue)
                {
                    builder.Append("  win32=");
                    builder.Append(entry.NativeErrorCode.Value.ToString(CultureInfo.InvariantCulture));
                }

                if (entry.SuppressedRepeats > 0)
                {
                    // Repeats of one code are kept at most once a minute. Saying how many were
                    // dropped is the difference between one failure a minute and sixty, and that
                    // is usually the question someone reading this is trying to answer.
                    builder.Append("  (+");
                    builder.Append(entry.SuppressedRepeats.ToString(CultureInfo.InvariantCulture));
                    builder.Append(" more suppressed)");
                }

                builder.AppendLine();
            }

            return builder.ToString();
        }

        private static string DescribeWindows()
        {
            OperatingSystem os = System.Environment.OSVersion;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}.{1} build {2}",
                os.Version.Major,
                os.Version.Minor,
                os.Version.Build);
        }

        private static string DescribeFrameworkRelease()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                           @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    object? release = key?.GetValue("Release");
                    return release == null
                        ? "unknown"
                        : Convert.ToInt32(release, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (System.Security.SecurityException)
            {
                return "unknown";
            }
            catch (UnauthorizedAccessException)
            {
                return "unknown";
            }
        }
    }
}
