using System;
using System.Globalization;
using MouseJiggler.Core.Abstractions;

namespace MouseJiggler.Core.Settings
{
    /// <summary>
    /// Validates a candidate settings document. Pure and framework-free, so the same rules
    /// apply whether the candidate came off disk, out of the Settings form, or from a test.
    /// </summary>
    public static class SettingsValidator
    {
        /// <summary>Parses an invariant HH:mm value in the range 00:00 to 23:59.</summary>
        public static bool TryParseTimeOfDay(string? value, out int minutesSinceMidnight)
        {
            minutesSinceMidnight = 0;

            if (value == null || value.Length != 5 || value[2] != ':')
            {
                return false;
            }

            if (!int.TryParse(value.Substring(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hours))
            {
                return false;
            }

            if (!int.TryParse(value.Substring(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
            {
                return false;
            }

            if (hours < 0 || hours > 23 || minutes < 0 || minutes > 59)
            {
                return false;
            }

            minutesSinceMidnight = (hours * 60) + minutes;
            return true;
        }

        public static string FormatTimeOfDay(int minutesSinceMidnight)
        {
            if (minutesSinceMidnight < 0 || minutesSinceMidnight >= 24 * 60)
            {
                throw new ArgumentOutOfRangeException(nameof(minutesSinceMidnight));
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:00}:{1:00}",
                minutesSinceMidnight / 60,
                minutesSinceMidnight % 60);
        }

        /// <summary>
        /// Checks a whole document. A schedule that is switched off may keep draft days and
        /// times as long as their syntax is valid; a schedule that is switched on may not be
        /// committed while it is meaningless.
        /// </summary>
        public static OperationResult Validate(SettingsV1? settings)
        {
            if (settings == null)
            {
                return Fail("settings.null");
            }

            if (settings.SchemaVersion != SettingsV1.CurrentSchemaVersion)
            {
                return Fail("settings.unsupportedSchema");
            }

            if (settings.Revision < 1)
            {
                return Fail("settings.revision.invalid");
            }

            if (settings.RunMode != RunMode.Manual && settings.RunMode != RunMode.Scheduled)
            {
                return Fail("settings.runMode.invalid");
            }

            if (settings.DayMask < 0 || settings.DayMask > SettingsV1.AllDaysMask)
            {
                return Fail("settings.dayMask.invalid");
            }

            if (!TryParseTimeOfDay(settings.ScheduleStart, out int start))
            {
                return Fail("settings.scheduleStart.invalid");
            }

            if (!TryParseTimeOfDay(settings.ScheduleEnd, out int end))
            {
                return Fail("settings.scheduleEnd.invalid");
            }

            if (settings.IntervalSeconds < SettingsV1.MinIntervalSeconds ||
                settings.IntervalSeconds > SettingsV1.MaxIntervalSeconds)
            {
                return Fail("settings.intervalSeconds.invalid");
            }

            if (settings.ScheduleEnabled)
            {
                if (settings.DayMask == 0)
                {
                    return Fail("settings.schedule.noDays");
                }

                // Equal times are rejected rather than quietly meaning all day. Switching the
                // schedule off is the explicit way to say all day.
                if (start == end)
                {
                    return Fail("settings.schedule.equalTimes");
                }
            }

            return OperationResult.Success();
        }

        private static OperationResult Fail(string code)
        {
            return OperationResult.Failure(FaultSubsystem.ConfigRead, code, retryable: false);
        }
    }
}
