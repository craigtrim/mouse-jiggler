using System;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Scheduling;
using MouseJiggler.Core.Settings;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// Schedule membership and the next-transition preview, including the daylight saving cases
    /// named in issue #5. Every test uses explicit instants; none of them sleeps.
    /// </summary>
    public sealed class ScheduleTests
    {
        private static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
        private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

        private static SettingsV1 Window(string start, string end, int dayMask = SettingsV1.AllDaysMask)
        {
            SettingsV1 defaults = SettingsV1.CreateDefault();
            return new SettingsV1(
                defaults.SchemaVersion, defaults.Revision, stopped: false, RunMode.Scheduled,
                scheduleEnabled: true, start, end, dayMask,
                defaults.PauseOnBattery, defaults.KeepDisplayOn, defaults.JiggleMouse,
                defaults.IntervalSeconds, defaults.DiagnosticLogging, defaults.StartupInitialized,
                defaults.FirstRunCompleted);
        }

        private static DateTime UtcAt(int year, int month, int day, int hour, int minute, int second = 0)
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        }

        [Theory]
        [InlineData(7, 59, 59, false)]
        [InlineData(8, 0, 0, true)]
        [InlineData(16, 59, 59, true)]
        [InlineData(17, 0, 0, false)]
        public void TheStartIsInclusiveAndTheEndIsExclusive(int hour, int minute, int second, bool expected)
        {
            SettingsV1 settings = Window("08:00", "17:00");

            // A Wednesday, in UTC, so the local zone does not shift the hour under test.
            DateTime instant = UtcAt(2026, 9, 9, hour, minute, second);

            Assert.Equal(expected, DailySchedule.IsWithinWindow(settings, instant, Utc));
        }

        [Fact]
        public void AnOvernightWindowBelongsToTheDayItStartedOn()
        {
            // Friday only, 22:00 to 06:00.
            SettingsV1 settings = Window("22:00", "06:00", DailySchedule.DayBit(DayOfWeek.Friday));

            // Friday 2026-09-11 night.
            Assert.False(DailySchedule.IsWithinWindow(settings, UtcAt(2026, 9, 11, 21, 59), Utc));
            Assert.True(DailySchedule.IsWithinWindow(settings, UtcAt(2026, 9, 11, 22, 0), Utc));
            Assert.True(DailySchedule.IsWithinWindow(settings, UtcAt(2026, 9, 11, 23, 59), Utc));

            // Saturday morning is still the Friday window.
            Assert.True(DailySchedule.IsWithinWindow(settings, UtcAt(2026, 9, 12, 0, 0), Utc));
            Assert.True(DailySchedule.IsWithinWindow(settings, UtcAt(2026, 9, 12, 5, 59), Utc));

            // Saturday 06:00 ends it, and Saturday night does not start a new one.
            Assert.False(DailySchedule.IsWithinWindow(settings, UtcAt(2026, 9, 12, 6, 0), Utc));
            Assert.False(DailySchedule.IsWithinWindow(settings, UtcAt(2026, 9, 12, 22, 0), Utc));
        }

        [Fact]
        public void SundayToMondayRollsOverCorrectly()
        {
            SettingsV1 settings = Window("23:00", "02:00", DailySchedule.DayBit(DayOfWeek.Sunday));

            // Sunday 2026-09-13 into Monday.
            Assert.True(DailySchedule.IsWithinWindow(settings, UtcAt(2026, 9, 13, 23, 30), Utc));
            Assert.True(DailySchedule.IsWithinWindow(settings, UtcAt(2026, 9, 14, 1, 59), Utc));
            Assert.False(DailySchedule.IsWithinWindow(settings, UtcAt(2026, 9, 14, 2, 0), Utc));
        }

        [Fact]
        public void EqualTimesAreInvalidRatherThanMeaningAllDay()
        {
            SettingsV1 settings = Window("09:00", "09:00");

            Assert.False(DailySchedule.IsWithinWindow(settings, UtcAt(2026, 9, 9, 12, 0), Utc));
        }

        [Fact]
        public void TheSpringForwardGapProducesNoImpossibleStartTime()
        {
            // Central time skips 02:00 to 03:00 local on 2026-03-08. A window starting at 02:30
            // never opens that day, and the preview must not invent an instant for it.
            SettingsV1 settings = Window("02:30", "04:00", DailySchedule.DayBit(DayOfWeek.Sunday));

            // 07:00 UTC on that date is 01:00 local, before the jump.
            DateTime beforeJump = UtcAt(2026, 3, 8, 7, 0);
            Assert.False(DailySchedule.IsWithinWindow(settings, beforeJump, Central));

            DateTime? next = NextTransitionCalculator.FindNextTransitionUtc(settings, beforeJump, Central, out TransitionKind kind);

            Assert.True(next.HasValue);
            Assert.Equal(TransitionKind.Start, kind);

            // The window can only open once the clock has jumped to 03:00 local, which is
            // 08:00 UTC. It is never reported as opening at a local time that did not happen.
            Assert.Equal(UtcAt(2026, 3, 8, 8, 0), next!.Value);

            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(next.Value, Central);
            Assert.Equal(3, local.Hour);
        }

        [Fact]
        public void BothPassesThroughTheRepeatedAutumnHourQualify()
        {
            // Central time repeats 01:00 to 02:00 local on 2026-11-01.
            SettingsV1 settings = Window("01:00", "01:30", DailySchedule.DayBit(DayOfWeek.Sunday));

            // First pass: 06:15 UTC is 01:15 local on daylight time.
            DateTime firstPass = UtcAt(2026, 11, 1, 6, 15);
            Assert.Equal(1, TimeZoneInfo.ConvertTimeFromUtc(firstPass, Central).Hour);
            Assert.True(DailySchedule.IsWithinWindow(settings, firstPass, Central));

            // Second pass: 07:15 UTC is 01:15 local again, now on standard time.
            DateTime secondPass = UtcAt(2026, 11, 1, 7, 15);
            Assert.Equal(1, TimeZoneInfo.ConvertTimeFromUtc(secondPass, Central).Hour);
            Assert.True(DailySchedule.IsWithinWindow(settings, secondPass, Central));

            // The half hour between them is outside the window on both passes.
            Assert.False(DailySchedule.IsWithinWindow(settings, UtcAt(2026, 11, 1, 6, 45), Central));
            Assert.False(DailySchedule.IsWithinWindow(settings, UtcAt(2026, 11, 1, 7, 45), Central));
        }

        [Fact]
        public void ThePreviewReportsTheNextRealTransition()
        {
            SettingsV1 settings = Window("08:00", "17:00");

            DateTime beforeStart = UtcAt(2026, 9, 9, 6, 30);
            DateTime? start = NextTransitionCalculator.FindNextTransitionUtc(settings, beforeStart, Utc, out TransitionKind startKind);
            Assert.Equal(UtcAt(2026, 9, 9, 8, 0), start);
            Assert.Equal(TransitionKind.Start, startKind);

            DateTime inside = UtcAt(2026, 9, 9, 12, 0);
            DateTime? end = NextTransitionCalculator.FindNextTransitionUtc(settings, inside, Utc, out TransitionKind endKind);
            Assert.Equal(UtcAt(2026, 9, 9, 17, 0), end);
            Assert.Equal(TransitionKind.End, endKind);
        }

        [Fact]
        public void AZoneWithoutDaylightSavingBehavesTheSameWay()
        {
            TimeZoneInfo arizona = TimeZoneInfo.FindSystemTimeZoneById("US Mountain Standard Time");
            Assert.False(arizona.SupportsDaylightSavingTime);

            SettingsV1 settings = Window("08:00", "17:00");
            DateTime beforeStart = UtcAt(2026, 3, 8, 14, 0); // 07:00 local

            DateTime? next = NextTransitionCalculator.FindNextTransitionUtc(settings, beforeStart, arizona, out TransitionKind kind);

            Assert.Equal(TransitionKind.Start, kind);
            Assert.Equal(UtcAt(2026, 3, 8, 15, 0), next);
        }

        [Fact]
        public void NoDaysSelectedHasNoTransitionToPreview()
        {
            SettingsV1 settings = Window("08:00", "17:00", dayMask: 0);

            DateTime? next = NextTransitionCalculator.FindNextTransitionUtc(settings, UtcAt(2026, 9, 9, 6, 0), Utc, out TransitionKind kind);

            Assert.Null(next);
            Assert.Equal(TransitionKind.None, kind);
        }

        [Fact]
        public void TheCacheRecomputesWhenTheTransitionPassesOrTheZoneChanges()
        {
            var cache = new CachedTransitionCalculator();
            SettingsV1 settings = Window("08:00", "17:00");

            DateTime? first = cache.Get(settings, UtcAt(2026, 9, 9, 6, 0), Utc, out _);
            Assert.Equal(UtcAt(2026, 9, 9, 8, 0), first);

            // Once that instant has arrived the cached answer is stale, so the next call must
            // return the following transition rather than a time in the past.
            DateTime? second = cache.Get(settings, UtcAt(2026, 9, 9, 8, 0), Utc, out TransitionKind kind);
            Assert.Equal(UtcAt(2026, 9, 9, 17, 0), second);
            Assert.Equal(TransitionKind.End, kind);

            // A different zone changes the answer even at the same instant.
            DateTime? central = cache.Get(settings, UtcAt(2026, 9, 9, 8, 0), Central, out _);
            Assert.NotEqual(second, central);
        }
    }
}
