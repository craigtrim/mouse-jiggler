using System;
using System.Globalization;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Commands;
using MouseJiggler.Core.Settings;

namespace MouseJiggler.App
{
    /// <summary>
    /// Turns a status code into the words the user reads. Core deals in codes and instants; the
    /// times are formatted here in the user's regional format, and the culture is injected so
    /// the tests are deterministic rather than dependent on the machine running them.
    /// </summary>
    public static class StatusTextFormatter
    {
        public static string Primary(DesiredEffects effects, SettingsV1 settings, CultureInfo culture)
        {
            if (effects == null)
            {
                throw new ArgumentNullException(nameof(effects));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (culture == null)
            {
                throw new ArgumentNullException(nameof(culture));
            }

            switch (effects.Status)
            {
                case StatusCode.Stopped:
                    return "Stopped. Choose Start when ready.";

                case StatusCode.RunningManual:
                    // Deliberately not "until 17:00": a manual run ignores the saved window, and
                    // showing that end time would be a promise the app is not making.
                    return "Running manually, until stopped.";

                case StatusCode.RunningScheduled:
                    return effects.NextTransitionUtc.HasValue
                        ? "Running, scheduled until " + FormatLocal(effects.NextTransitionUtc.Value, culture) + "."
                        : "Running on schedule.";

                case StatusCode.RunningContinuous:
                    return "Running continuously, until stopped.";

                case StatusCode.WaitingForSchedule:
                    return effects.NextTransitionUtc.HasValue
                        ? "Waiting. Next start " + FormatLocal(effects.NextTransitionUtc.Value, culture) + "."
                        : "Waiting for the schedule.";

                case StatusCode.PausedOnBattery:
                    return "Paused, on battery.";

                case StatusCode.PowerStatusUnavailable:
                    return "Paused. Power status unavailable.";

                case StatusCode.SessionUnavailable:
                    return "Waiting. This session is not available.";

                case StatusCode.LockedKeepingAwake:
                    return "Locked, keeping the computer awake.";

                case StatusCode.Error:
                    return effects.WakeRequestFailed
                        ? "Error. Windows refused the request to stay awake."
                        : "Error. See details in Settings.";

                default:
                    return "Stopped.";
            }
        }

        /// <summary>The secondary line, which explains what is and is not happening.</summary>
        public static string Detail(DesiredEffects effects, SettingsV1 settings)
        {
            if (effects.InputRequestFailed)
            {
                return "Pointer movement has failed. The computer is still being kept awake.";
            }

            if (!settings.JiggleMouse && effects.SystemAwake)
            {
                return "Keeping the computer awake alone may not prevent idle locking.";
            }

            if (effects.Status == StatusCode.LockedKeepingAwake)
            {
                return "Pointer movement and the display request stop while the session is locked.";
            }

            return string.Empty;
        }

        /// <summary>
        /// Explains a command that did not happen. Named after the thing the user asked for
        /// rather than the subsystem that refused, because "could not save" is not an answer to
        /// "why is it still stopped".
        /// </summary>
        public static string CommandFailure(CommandKind kind, OperationResult outcome)
        {
            if (outcome == null)
            {
                throw new ArgumentNullException(nameof(outcome));
            }

            string action;
            switch (kind)
            {
                case CommandKind.StartNow:
                    action = "start now";
                    break;

                case CommandKind.UseSchedule:
                    action = "switch to the schedule";
                    break;

                case CommandKind.Stop:
                    action = "stop";
                    break;

                default:
                    action = "start";
                    break;
            }

            return "Mouse Jiggler could not " + action + ", because the change could not be saved."
                + System.Environment.NewLine + System.Environment.NewLine
                + "It has stayed as it was rather than running until the next restart and then "
                + "reverting without explanation. Check that " + SettingsFolderDescription()
                + " is writable, then try again."
                + System.Environment.NewLine + System.Environment.NewLine
                + "Reason: " + (outcome.Code ?? "unknown")
                + (outcome.NativeErrorCode.HasValue
                    ? " (" + outcome.NativeErrorCode.Value.ToString(CultureInfo.InvariantCulture) + ")"
                    : string.Empty);
        }

        /// <summary>Describes the settings folder without printing a path that names the user.</summary>
        private static string SettingsFolderDescription()
        {
            return "the Mouse Jiggler folder in your local application data";
        }

        /// <summary>Short enough for the tray tooltip, which Windows truncates at 63 characters.</summary>
        public static string Tooltip(DesiredEffects effects)
        {
            string text = "Mouse Jiggler: " + ShortStatus(effects.Status);
            return text.Length <= 63 ? text : text.Substring(0, 63);
        }

        private static string ShortStatus(StatusCode status)
        {
            switch (status)
            {
                case StatusCode.RunningManual: return "running (manual)";
                case StatusCode.RunningScheduled: return "running (scheduled)";
                case StatusCode.RunningContinuous: return "running";
                case StatusCode.WaitingForSchedule: return "waiting for schedule";
                case StatusCode.PausedOnBattery: return "paused on battery";
                case StatusCode.PowerStatusUnavailable: return "power unavailable";
                case StatusCode.SessionUnavailable: return "session unavailable";
                case StatusCode.LockedKeepingAwake: return "locked, keeping awake";
                case StatusCode.Error: return "error";
                default: return "stopped";
            }
        }

        private static string FormatLocal(DateTime utc, CultureInfo culture)
        {
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
            DateTime today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.Local).Date;

            // A 12 or 24 hour clock according to the user's regional settings.
            string time = local.ToString("t", culture);

            if (local.Date == today)
            {
                return time;
            }

            return local.ToString("dddd", culture) + " at " + time;
        }
    }
}
