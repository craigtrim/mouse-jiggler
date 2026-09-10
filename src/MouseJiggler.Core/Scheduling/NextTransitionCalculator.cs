using System;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Settings;

namespace MouseJiggler.Core.Scheduling
{
    /// <summary>
    /// Finds the next instant at which schedule membership actually changes.
    /// </summary>
    /// <remarks>
    /// The search walks real UTC minute boundaries and asks the schedule whether it is inside
    /// the window at each one. Working forwards from UTC rather than constructing a local
    /// deadline is what makes daylight saving correct: during the spring jump the skipped local
    /// times simply never occur, and during the autumn repeat both passes through the repeated
    /// hour are evaluated on their own merits. Constructing "today at 08:00" in local time and
    /// converting it back would invent an instant that does not exist, or pick the wrong one of
    /// two that do.
    /// </remarks>
    public static class NextTransitionCalculator
    {
        /// <summary>The horizon. A weekly schedule always transitions inside eight days, if it transitions at all.</summary>
        public const int SearchDays = 8;

        private const int MinutesPerDay = 24 * 60;

        public static DateTime? FindNextTransitionUtc(
            SettingsV1 settings,
            DateTime utcNow,
            TimeZoneInfo timeZone,
            out TransitionKind kind)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (timeZone == null)
            {
                throw new ArgumentNullException(nameof(timeZone));
            }

            kind = TransitionKind.None;

            // Nothing to preview when the schedule is not what decides eligibility.
            if (!settings.ScheduleEnabled || settings.RunMode != RunMode.Scheduled)
            {
                return null;
            }

            if (settings.DayMask == 0)
            {
                return null;
            }

            bool currentlyInside = DailySchedule.IsWithinWindow(settings, utcNow, timeZone);

            // Configuration is minute-granular, so only minute boundaries can change the answer.
            DateTime cursor = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, utcNow.Minute, 0, DateTimeKind.Utc);
            int limit = SearchDays * MinutesPerDay;

            for (int step = 1; step <= limit; step++)
            {
                DateTime candidate = cursor.AddMinutes(step);
                bool inside = DailySchedule.IsWithinWindow(settings, candidate, timeZone);

                if (inside != currentlyInside)
                {
                    kind = inside ? TransitionKind.Start : TransitionKind.End;
                    return candidate;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Caches the transition so the one-second heartbeat does not repeat an eleven-thousand step
    /// search. The result is invalidated by anything that could change it: a new settings
    /// revision, a different time zone or zone rule, or the arrival of the transition itself.
    /// </summary>
    public sealed class CachedTransitionCalculator
    {
        private long _cachedRevision = -1;
        private string _cachedZoneId = string.Empty;
        private TimeSpan _cachedBaseOffset;
        private bool _cachedSupportsDst;
        private DateTime? _cachedTransition;
        private TransitionKind _cachedKind;
        private bool _hasCachedValue;

        public DateTime? Get(SettingsV1 settings, DateTime utcNow, TimeZoneInfo timeZone, out TransitionKind kind)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (timeZone == null)
            {
                throw new ArgumentNullException(nameof(timeZone));
            }

            bool zoneChanged =
                !string.Equals(_cachedZoneId, timeZone.Id, StringComparison.Ordinal) ||
                _cachedBaseOffset != timeZone.BaseUtcOffset ||
                _cachedSupportsDst != timeZone.SupportsDaylightSavingTime;

            bool expired = _hasCachedValue && _cachedTransition.HasValue && utcNow >= _cachedTransition.Value;

            if (!_hasCachedValue || zoneChanged || expired || _cachedRevision != settings.Revision)
            {
                _cachedTransition = NextTransitionCalculator.FindNextTransitionUtc(settings, utcNow, timeZone, out _cachedKind);
                _cachedRevision = settings.Revision;
                _cachedZoneId = timeZone.Id;
                _cachedBaseOffset = timeZone.BaseUtcOffset;
                _cachedSupportsDst = timeZone.SupportsDaylightSavingTime;
                _hasCachedValue = true;
            }

            kind = _cachedKind;
            return _cachedTransition;
        }

        /// <summary>Drops the cached value after a clock or time zone change.</summary>
        public void Invalidate()
        {
            _hasCachedValue = false;
            _cachedTransition = null;
            _cachedKind = TransitionKind.None;
            _cachedRevision = -1;
            _cachedZoneId = string.Empty;
        }
    }
}
