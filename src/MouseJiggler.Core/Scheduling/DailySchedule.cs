using System;
using MouseJiggler.Core.Settings;

namespace MouseJiggler.Core.Scheduling
{
    /// <summary>
    /// One daily window, evaluated in the current Windows local time zone. The start is
    /// inclusive and the end exclusive, so 08:00 to 17:00 covers 08:00:00 through 16:59:59.
    /// An end earlier than the start crosses midnight and belongs to the day it started on.
    /// </summary>
    public static class DailySchedule
    {
        private const int MinutesPerDay = 24 * 60;

        /// <summary>Monday is bit 0, through Sunday at bit 6.</summary>
        public static int DayBit(DayOfWeek day)
        {
            return 1 << (((int)day + 6) % 7);
        }

        public static bool IsDaySelected(int dayMask, DayOfWeek day)
        {
            return (dayMask & DayBit(day)) != 0;
        }

        /// <summary>
        /// Whether the given instant falls inside the window. Only meaningful when scheduling
        /// is switched on; a schedule switched off means continuous, which the policy handles.
        /// </summary>
        public static bool IsWithinWindow(SettingsV1 settings, DateTime utcInstant, TimeZoneInfo timeZone)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (timeZone == null)
            {
                throw new ArgumentNullException(nameof(timeZone));
            }

            if (!SettingsValidator.TryParseTimeOfDay(settings.ScheduleStart, out int start) ||
                !SettingsValidator.TryParseTimeOfDay(settings.ScheduleEnd, out int end))
            {
                return false;
            }

            // Equal times are not twenty-four hours. Switching the schedule off is how the user
            // asks for all day.
            if (start == end)
            {
                return false;
            }

            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utcInstant, timeZone);
            int minutes = (local.Hour * 60) + local.Minute;
            DayOfWeek today = local.DayOfWeek;

            if (start < end)
            {
                return IsDaySelected(settings.DayMask, today) && minutes >= start && minutes < end;
            }

            // Overnight. The tail after midnight belongs to the previous day's selection, so a
            // Friday-only 22:00 to 06:00 window covers Friday night into Saturday morning.
            if (IsDaySelected(settings.DayMask, today) && minutes >= start)
            {
                return true;
            }

            DayOfWeek yesterday = (DayOfWeek)(((int)today + 6) % 7);
            return IsDaySelected(settings.DayMask, yesterday) && minutes < end;
        }

        /// <summary>True when the window ends on the day after it starts.</summary>
        public static bool IsOvernight(SettingsV1 settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            return SettingsValidator.TryParseTimeOfDay(settings.ScheduleStart, out int start) &&
                   SettingsValidator.TryParseTimeOfDay(settings.ScheduleEnd, out int end) &&
                   start > end;
        }

        internal static int MinutesInDay => MinutesPerDay;
    }
}
