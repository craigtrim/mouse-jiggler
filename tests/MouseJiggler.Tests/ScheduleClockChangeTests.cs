using System;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Commands;
using MouseJiggler.Core.Environment;
using MouseJiggler.Core.Scheduling;
using MouseJiggler.Core.Settings;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// What happens to the schedule and its cached preview when the clock, the time zone or the
    /// saved window moves underneath them, from issue #5. The wall clock is the one input the
    /// user can rewrite at will, so each test pins a way that could leave the app running when
    /// it should not, replaying a window it slept through, or showing a transition that has
    /// already been overtaken. Every instant here is explicit and nothing sleeps.
    /// </summary>
    public sealed class ScheduleClockChangeTests
    {
        private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
        private static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");

        private static SettingsV1 Window(string start, string end, int dayMask = SettingsV1.AllDaysMask)
        {
            SettingsV1 d = SettingsV1.CreateDefault();
            return new SettingsV1(
                d.SchemaVersion, d.Revision, stopped: false, RunMode.Scheduled,
                scheduleEnabled: true, start, end, dayMask,
                d.PauseOnBattery, d.KeepDisplayOn, d.JiggleMouse,
                d.IntervalSeconds, d.DiagnosticLogging, d.StartupInitialized, d.FirstRunCompleted);
        }

        /// <summary>
        /// A preference save as the store commits it. The patch itself carries the old revision
        /// through, because only the store assigns the next one, and the cached preview keys on
        /// that revision to notice the edit.
        /// </summary>
        private static SettingsV1 Saved(SettingsV1 current, SettingsPatch patch)
        {
            return SettingsIntent.WithRevision(patch.ApplyTo(current), current.Revision + 1);
        }

        private static EnvironmentSnapshot Snapshot(DateTime utcNow, TimeZoneInfo timeZone)
        {
            // Everything except the clock and the zone is deliberately benign, so a status of
            // anything other than the schedule's own answer means the schedule produced it.
            return new EnvironmentSnapshot(
                utcNow, timeZone, monotonicMilliseconds: 1_000,
                PowerSource.External, SessionState.ActiveUnlocked,
                suspended: false, exiting: false, inputDesktopAvailable: true,
                CapabilityState.Ok, CapabilityState.Ok, settingsAvailable: true);
        }

        private static DateTime UtcAt(int year, int month, int day, int hour, int minute)
        {
            return new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);
        }

        /// <summary>
        /// A synthetic zone whose daylight step is half an hour rather than the usual one, which
        /// is the case no real test machine can be relied on to have installed. Spring forward
        /// is the second Sunday in March and the step back is the first Sunday in November, so
        /// the skipped and the repeated stretch are each thirty minutes long.
        /// </summary>
        private static TimeZoneInfo HalfHourDaylightZone()
        {
            TimeZoneInfo.TransitionTime springForward =
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
            TimeZoneInfo.TransitionTime fallBack =
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);

            TimeZoneInfo.AdjustmentRule rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2000, 1, 1),
                new DateTime(2100, 1, 1),
                TimeSpan.FromMinutes(30),
                springForward,
                fallBack);

            // A zero base offset keeps the arithmetic readable: standard local time is UTC, and
            // daylight local time is UTC plus thirty minutes.
            return TimeZoneInfo.CreateCustomTimeZone(
                "Test Half Hour Daylight",
                TimeSpan.Zero,
                "Half hour daylight",
                "Half hour standard",
                "Half hour daylight",
                new[] { rule });
        }

        /// <summary>A zone with a non-hour offset and no daylight rule at all.</summary>
        private static TimeZoneInfo FixedOffsetZone()
        {
            return TimeZoneInfo.CreateCustomTimeZone(
                "Test Fixed Offset",
                new TimeSpan(5, 45, 0),
                "Fixed offset",
                "Fixed offset");
        }

        /// <summary>
        /// Zones built by this differ only in the months their daylight rule covers, so at a
        /// given instant two of them share a base offset and daylight support while reporting
        /// local times an hour apart.
        /// </summary>
        private static TimeZoneInfo PairedZone(string id, int daylightStartMonth, int daylightEndMonth)
        {
            TimeZoneInfo.TransitionTime start =
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), daylightStartMonth, 1);
            TimeZoneInfo.TransitionTime end =
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), daylightEndMonth, 1);

            TimeZoneInfo.AdjustmentRule rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2000, 1, 1),
                new DateTime(2100, 1, 1),
                TimeSpan.FromHours(1),
                start,
                end);

            return TimeZoneInfo.CreateCustomTimeZone(
                id, TimeSpan.FromHours(2), id, id + " standard", id + " daylight", new[] { rule });
        }

        [Fact]
        public void AdvancingTheClockPastAWholeWindowPreviewsTheNextStartRatherThanTheEndThatElapsed()
        {
            var cache = new CachedTransitionCalculator();
            SettingsV1 settings = Window("08:00", "17:00");

            DateTime? start = cache.Get(settings, UtcAt(2026, 9, 9, 6, 0), Utc, out TransitionKind startKind);
            Assert.Equal(UtcAt(2026, 9, 9, 8, 0), start);
            Assert.Equal(TransitionKind.Start, startKind);

            // The user sets the clock forward to late evening. The whole 08:00 to 17:00 window
            // went by while the clock was elsewhere, so the 17:00 end the cache was holding is
            // now history: reporting it would put a past instant on screen as the next change.
            DateTime afterJump = UtcAt(2026, 9, 9, 23, 0);
            DateTime? next = cache.Get(settings, afterJump, Utc, out TransitionKind nextKind);

            Assert.True(next.HasValue);
            Assert.Equal(UtcAt(2026, 9, 10, 8, 0), next!.Value);
            Assert.Equal(TransitionKind.Start, nextKind);
            Assert.True(next.Value > afterJump);
        }

        [Fact]
        public void AClockWoundBackwardsEndsEligibilityAtOnceAndTheInvalidateRefreshesThePreview()
        {
            var policy = new ActivityPolicy();
            SettingsV1 settings = Window("08:00", "17:00");

            DesiredEffects inside = policy.Evaluate(settings, Snapshot(UtcAt(2026, 9, 9, 12, 0), Utc));
            Assert.Equal(StatusCode.RunningScheduled, inside.Status);
            Assert.Equal(UtcAt(2026, 9, 9, 17, 0), inside.NextTransitionUtc);

            // Wound back to before the window opened. Membership is asked of the instant every
            // time and is never cached, so the app must stop now rather than carry on until the
            // end it had already computed comes round again.
            DateTime rewound = UtcAt(2026, 9, 9, 6, 0);
            DesiredEffects outside = policy.Evaluate(settings, Snapshot(rewound, Utc));

            Assert.Equal(StatusCode.WaitingForSchedule, outside.Status);
            Assert.False(outside.MayJiggle);
            Assert.False(outside.SystemAwake);
            Assert.False(outside.DisplayAwake);

            // A backwards jump cannot expire the cached preview on its own, because the instant
            // it holds is still in the future. WM_TIMECHANGE dropping the cache is what makes
            // the preview catch up, so that hook is the guarantee worth pinning.
            policy.InvalidateScheduleCache();
            DesiredEffects refreshed = policy.Evaluate(settings, Snapshot(rewound, Utc));

            Assert.Equal(UtcAt(2026, 9, 9, 8, 0), refreshed.NextTransitionUtc);
            Assert.Equal(TransitionKind.Start, refreshed.NextTransitionKind);
        }

        [Fact]
        public void ResumingAfterTheWindowHasEntirelyPassedReplaysNothing()
        {
            var policy = new ActivityPolicy();
            SettingsV1 settings = Window("08:00", "17:00");

            DesiredEffects beforeSleep = policy.Evaluate(settings, Snapshot(UtcAt(2026, 9, 9, 7, 0), Utc));
            Assert.Equal(StatusCode.WaitingForSchedule, beforeSleep.Status);
            Assert.Equal(UtcAt(2026, 9, 9, 8, 0), beforeSleep.NextTransitionUtc);

            // The machine slept through the whole window and woke at 22:00. Nothing is owed for
            // the hours it missed: no jiggle is replayed, no keep-awake request is made to make
            // up for them, and the preview points at tomorrow rather than at what was skipped.
            DateTime wake = UtcAt(2026, 9, 9, 22, 0);
            DesiredEffects afterWake = policy.Evaluate(settings, Snapshot(wake, Utc));

            Assert.Equal(StatusCode.WaitingForSchedule, afterWake.Status);
            Assert.False(afterWake.MayJiggle);
            Assert.False(afterWake.SystemAwake);
            Assert.False(afterWake.DisplayAwake);
            Assert.Equal(TransitionKind.Start, afterWake.NextTransitionKind);
            Assert.True(afterWake.NextTransitionUtc.HasValue);
            Assert.Equal(UtcAt(2026, 9, 10, 8, 0), afterWake.NextTransitionUtc!.Value);
            Assert.True(afterWake.NextTransitionUtc.Value > wake);
        }

        [Fact]
        public void SwitchingTimeZoneWhileTheWindowIsActiveRecomputesTheCachedPreview()
        {
            var cache = new CachedTransitionCalculator();
            SettingsV1 settings = Window("08:00", "17:00");
            DateTime noon = UtcAt(2026, 9, 9, 12, 0);

            Assert.True(DailySchedule.IsWithinWindow(settings, noon, Utc));
            DateTime? end = cache.Get(settings, noon, Utc, out TransitionKind endKind);
            Assert.Equal(UtcAt(2026, 9, 9, 17, 0), end);
            Assert.Equal(TransitionKind.End, endKind);

            // The user picks Central without the clock moving. The same instant now reads 07:00
            // locally, an hour before the window opens, so the app must go from running with an
            // end pending to waiting with a start pending.
            Assert.False(DailySchedule.IsWithinWindow(settings, noon, Central));
            DateTime? start = cache.Get(settings, noon, Central, out TransitionKind startKind);

            Assert.Equal(UtcAt(2026, 9, 9, 13, 0), start);
            Assert.Equal(TransitionKind.Start, startKind);
        }

        [Fact]
        public void TwoZonesSharingABaseOffsetAndDaylightSupportAreNotTreatedAsInterchangeable()
        {
            // Both sit at UTC+02:00 and both observe daylight saving; they differ only in
            // whether their rule is in season on the date under test. A cache that compared
            // offsets and daylight support alone would answer the second zone with the first
            // zone's transition, which is an hour wrong.
            TimeZoneInfo outOfSeason = PairedZone("Test Zone Alpha", daylightStartMonth: 6, daylightEndMonth: 7);
            TimeZoneInfo inSeason = PairedZone("Test Zone Beta", daylightStartMonth: 3, daylightEndMonth: 11);
            DateTime noon = UtcAt(2026, 9, 9, 12, 0);

            Assert.Equal(outOfSeason.BaseUtcOffset, inSeason.BaseUtcOffset);
            Assert.True(outOfSeason.SupportsDaylightSavingTime);
            Assert.True(inSeason.SupportsDaylightSavingTime);
            Assert.NotEqual(outOfSeason.GetUtcOffset(noon), inSeason.GetUtcOffset(noon));

            var cache = new CachedTransitionCalculator();
            SettingsV1 settings = Window("08:00", "17:00");

            DateTime? underAlpha = cache.Get(settings, noon, outOfSeason, out _);
            Assert.Equal(UtcAt(2026, 9, 9, 15, 0), underAlpha);

            DateTime? underBeta = cache.Get(settings, noon, inSeason, out _);
            Assert.Equal(UtcAt(2026, 9, 9, 14, 0), underBeta);
        }

        [Fact]
        public void AZoneDataUpdateUnderAnUnchangedIdIsCoveredByTheExplicitInvalidate()
        {
            // Windows can change a zone's rules without changing its id, and that leaves the
            // base offset and the daylight support identical, so nothing about the zone object
            // announces the change. Dropping the cache outright is the only thing that covers
            // it, which is why the clock-change hook calls Invalidate rather than trusting the
            // comparison.
            const string SharedId = "Test Zone Under Update";
            TimeZoneInfo beforeUpdate = PairedZone(SharedId, daylightStartMonth: 6, daylightEndMonth: 7);
            TimeZoneInfo afterUpdate = PairedZone(SharedId, daylightStartMonth: 3, daylightEndMonth: 11);
            DateTime noon = UtcAt(2026, 9, 9, 12, 0);

            var cache = new CachedTransitionCalculator();
            SettingsV1 settings = Window("08:00", "17:00");

            Assert.Equal(UtcAt(2026, 9, 9, 15, 0), cache.Get(settings, noon, beforeUpdate, out _));

            cache.Invalidate();

            Assert.Equal(UtcAt(2026, 9, 9, 14, 0), cache.Get(settings, noon, afterUpdate, out _));
        }

        [Theory]
        [InlineData("02:00")]
        [InlineData("02:29")]
        public void AHalfHourSpringForwardOpensTheWindowAtTheJumpAndNotAtALocalTimeThatNeverOccurs(string configuredStart)
        {
            // On 2026-03-08 this zone's local clock goes straight from 01:59 to 02:30, so every
            // local time in that half hour is configurable but unreachable. A window asking for
            // one of them must open at the jump itself and never at an invented instant.
            TimeZoneInfo zone = HalfHourDaylightZone();
            SettingsV1 settings = Window(configuredStart, "04:00", DailySchedule.DayBit(DayOfWeek.Sunday));
            DateTime beforeJump = UtcAt(2026, 3, 8, 1, 30);

            Assert.False(DailySchedule.IsWithinWindow(settings, beforeJump, zone));

            DateTime? start = NextTransitionCalculator.FindNextTransitionUtc(settings, beforeJump, zone, out TransitionKind kind);

            Assert.True(start.HasValue);
            Assert.Equal(TransitionKind.Start, kind);
            Assert.Equal(UtcAt(2026, 3, 8, 2, 0), start!.Value);

            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(start.Value, zone);
            Assert.Equal(2, local.Hour);
            Assert.Equal(30, local.Minute);
        }

        [Fact]
        public void AHalfHourSpringForwardShortensTheWindowByExactlyThatHalfHour()
        {
            // The window reads two hours on the settings page. On the day the clock jumps by
            // thirty minutes it is worth ninety minutes of real time, and the preview has to
            // say so rather than promising the nominal length.
            TimeZoneInfo zone = HalfHourDaylightZone();
            SettingsV1 settings = Window("02:00", "04:00", DailySchedule.DayBit(DayOfWeek.Sunday));

            DateTime? start = NextTransitionCalculator.FindNextTransitionUtc(
                settings, UtcAt(2026, 3, 8, 1, 30), zone, out TransitionKind startKind);
            Assert.True(start.HasValue);
            Assert.Equal(TransitionKind.Start, startKind);

            DateTime? end = NextTransitionCalculator.FindNextTransitionUtc(
                settings, start!.Value, zone, out TransitionKind endKind);

            Assert.True(end.HasValue);
            Assert.Equal(TransitionKind.End, endKind);
            Assert.Equal(UtcAt(2026, 3, 8, 3, 30), end!.Value);
            Assert.Equal(TimeSpan.FromMinutes(90), end.Value - start.Value);
        }

        [Fact]
        public void AHalfHourFallBackRunsTheWindowTwiceAndTheCacheFollowsEveryBoundary()
        {
            // On 2026-11-01 local 01:30 to 01:59 happens twice in this zone. A window inside
            // that stretch is genuinely due twice, and each of the four boundaries has to
            // expire the cached preview in turn; a cache that held the first end would sit out
            // the second pass entirely.
            TimeZoneInfo zone = HalfHourDaylightZone();
            SettingsV1 settings = Window("01:30", "01:45", DailySchedule.DayBit(DayOfWeek.Sunday));
            var cache = new CachedTransitionCalculator();

            DateTime? firstStart = cache.Get(settings, UtcAt(2026, 11, 1, 0, 50), zone, out TransitionKind firstStartKind);
            Assert.Equal(UtcAt(2026, 11, 1, 1, 0), firstStart);
            Assert.Equal(TransitionKind.Start, firstStartKind);

            DateTime? firstEnd = cache.Get(settings, UtcAt(2026, 11, 1, 1, 0), zone, out TransitionKind firstEndKind);
            Assert.Equal(UtcAt(2026, 11, 1, 1, 15), firstEnd);
            Assert.Equal(TransitionKind.End, firstEndKind);

            DateTime? secondStart = cache.Get(settings, UtcAt(2026, 11, 1, 1, 15), zone, out TransitionKind secondStartKind);
            Assert.Equal(UtcAt(2026, 11, 1, 1, 30), secondStart);
            Assert.Equal(TransitionKind.Start, secondStartKind);

            DateTime? secondEnd = cache.Get(settings, UtcAt(2026, 11, 1, 1, 30), zone, out TransitionKind secondEndKind);
            Assert.Equal(UtcAt(2026, 11, 1, 1, 45), secondEnd);
            Assert.Equal(TransitionKind.End, secondEndKind);

            // Both openings are the same reading on the wall clock, half an hour apart in real
            // time: that is what makes this a repeat rather than two ordinary windows.
            Assert.Equal(
                TimeZoneInfo.ConvertTimeFromUtc(UtcAt(2026, 11, 1, 1, 0), zone),
                TimeZoneInfo.ConvertTimeFromUtc(UtcAt(2026, 11, 1, 1, 30), zone));
        }

        [Fact]
        public void AZoneWithNoDaylightSavingKeepsTheWindowAtItsConfiguredLocalTimes()
        {
            // A fixed offset, and one that is not a whole hour. The date the rest of the world
            // jumps on is an ordinary day here, so the window opens at exactly 08:00 local and
            // lasts its full nine hours, and the next day repeats it unshifted.
            TimeZoneInfo zone = FixedOffsetZone();
            Assert.False(zone.SupportsDaylightSavingTime);

            SettingsV1 settings = Window("08:00", "17:00");
            DateTime beforeStart = UtcAt(2026, 3, 8, 1, 0);
            Assert.False(DailySchedule.IsWithinWindow(settings, beforeStart, zone));

            DateTime? start = NextTransitionCalculator.FindNextTransitionUtc(settings, beforeStart, zone, out TransitionKind startKind);
            Assert.True(start.HasValue);
            Assert.Equal(TransitionKind.Start, startKind);
            Assert.Equal(UtcAt(2026, 3, 8, 2, 15), start!.Value);

            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(start.Value, zone);
            Assert.Equal(8, local.Hour);
            Assert.Equal(0, local.Minute);

            DateTime? end = NextTransitionCalculator.FindNextTransitionUtc(settings, start.Value, zone, out TransitionKind endKind);
            Assert.True(end.HasValue);
            Assert.Equal(TransitionKind.End, endKind);
            Assert.Equal(TimeSpan.FromHours(9), end!.Value - start.Value);

            DateTime? nextDay = NextTransitionCalculator.FindNextTransitionUtc(settings, end.Value, zone, out TransitionKind nextDayKind);
            Assert.Equal(UtcAt(2026, 3, 9, 2, 15), nextDay);
            Assert.Equal(TransitionKind.Start, nextDayKind);
        }

        [Fact]
        public void UntickingTheCurrentDayWhileTheWindowIsRunningEndsItImmediately()
        {
            var policy = new ActivityPolicy();
            SettingsV1 active = Window("08:00", "17:00");
            DateTime noon = UtcAt(2026, 9, 9, 12, 0); // A Wednesday.

            DesiredEffects running = policy.Evaluate(active, Snapshot(noon, Utc));
            Assert.Equal(StatusCode.RunningScheduled, running.Status);
            Assert.True(running.MayJiggle);
            Assert.Equal(UtcAt(2026, 9, 9, 17, 0), running.NextTransitionUtc);

            // Wednesday is unticked mid-window. The saved revision changes with it, which is
            // what tells the cached preview that the end it is holding no longer exists.
            SettingsV1 withoutWednesday = Saved(
                active,
                new SettingsPatch { DayMask = SettingsV1.AllDaysMask & ~DailySchedule.DayBit(DayOfWeek.Wednesday) });

            DesiredEffects afterEdit = policy.Evaluate(withoutWednesday, Snapshot(noon, Utc));

            Assert.Equal(StatusCode.WaitingForSchedule, afterEdit.Status);
            Assert.False(afterEdit.MayJiggle);
            Assert.Equal(TransitionKind.Start, afterEdit.NextTransitionKind);
            Assert.Equal(UtcAt(2026, 9, 10, 8, 0), afterEdit.NextTransitionUtc);

            // The edit travelled as a preference patch, so it moved no intent on the way past.
            Assert.False(withoutWednesday.Stopped);
            Assert.Equal(RunMode.Scheduled, withoutWednesday.RunMode);
        }

        [Fact]
        public void DisablingSchedulingClearsThePreviewRatherThanLeavingAStaleTransitionOnDisplay()
        {
            var policy = new ActivityPolicy();
            SettingsV1 active = Window("08:00", "17:00");
            DateTime noon = UtcAt(2026, 9, 9, 12, 0);

            DesiredEffects running = policy.Evaluate(active, Snapshot(noon, Utc));
            Assert.Equal(UtcAt(2026, 9, 9, 17, 0), running.NextTransitionUtc);

            // Scheduling off means continuous. Nothing is going to happen at 17:00 any more, so
            // holding that instant would promise the user a stop that never comes.
            SettingsV1 continuous = Saved(active, new SettingsPatch { ScheduleEnabled = false });
            DesiredEffects afterDisable = policy.Evaluate(continuous, Snapshot(noon, Utc));

            Assert.Equal(StatusCode.RunningContinuous, afterDisable.Status);
            Assert.True(afterDisable.MayJiggle);
            Assert.Null(afterDisable.NextTransitionUtc);
            Assert.Equal(TransitionKind.None, afterDisable.NextTransitionKind);
        }

        [Fact]
        public void SavingAWindowThatIsNotCurrentlyActiveDoesNotStartTheApp()
        {
            var policy = new ActivityPolicy();
            SettingsV1 morning = Window("08:00", "17:00");
            DateTime early = UtcAt(2026, 9, 9, 6, 0);

            DesiredEffects waiting = policy.Evaluate(morning, Snapshot(early, Utc));
            Assert.Equal(StatusCode.WaitingForSchedule, waiting.Status);
            Assert.Equal(UtcAt(2026, 9, 9, 8, 0), waiting.NextTransitionUtc);

            // Saving a window that does not contain the present moment is the ordinary case for
            // editing the schedule at all. It must move the preview and nothing else.
            SettingsV1 later = Saved(morning, new SettingsPatch { ScheduleStart = "10:00", ScheduleEnd = "12:00" });
            DesiredEffects afterSave = policy.Evaluate(later, Snapshot(early, Utc));

            Assert.Equal(StatusCode.WaitingForSchedule, afterSave.Status);
            Assert.False(afterSave.MayJiggle);
            Assert.False(afterSave.SystemAwake);
            Assert.Equal(TransitionKind.Start, afterSave.NextTransitionKind);
            Assert.Equal(UtcAt(2026, 9, 9, 10, 0), afterSave.NextTransitionUtc);
        }

        [Fact]
        public void NoClockChangeZoneChangeOrScheduleEditEverClearsAnExplicitStop()
        {
            var policy = new ActivityPolicy();
            SettingsV1 stopped = SettingsIntent.WithStopped(Window("08:00", "17:00"), true);

            // Inside the window, before it, days later, in another zone, and at the instant a
            // half-hour daylight jump lands. A Stop outranks every one of them.
            EnvironmentSnapshot[] snapshots =
            {
                Snapshot(UtcAt(2026, 9, 9, 12, 0), Utc),
                Snapshot(UtcAt(2026, 9, 9, 6, 0), Utc),
                Snapshot(UtcAt(2026, 9, 12, 23, 0), Utc),
                Snapshot(UtcAt(2026, 9, 9, 12, 0), Central),
                Snapshot(UtcAt(2026, 3, 8, 2, 0), HalfHourDaylightZone()),
            };

            foreach (EnvironmentSnapshot snapshot in snapshots)
            {
                policy.InvalidateScheduleCache();
                DesiredEffects effects = policy.Evaluate(stopped, snapshot);

                Assert.Equal(StatusCode.Stopped, effects.Status);
                Assert.False(effects.MayJiggle);
                Assert.False(effects.SystemAwake);
                Assert.False(effects.DisplayAwake);
                Assert.Null(effects.NextTransitionUtc);
            }

            // Every schedule edit in this file travels as a preference patch, and a patch
            // carries the stopped flag through untouched, so none of them can start the app.
            Assert.True(Saved(stopped, new SettingsPatch { ScheduleEnabled = false }).Stopped);
            Assert.True(Saved(stopped, new SettingsPatch { DayMask = DailySchedule.DayBit(DayOfWeek.Sunday) }).Stopped);
            Assert.True(Saved(stopped, new SettingsPatch { ScheduleStart = "10:00", ScheduleEnd = "12:00" }).Stopped);

            // And the command that carries such a save persists no intent change of its own.
            Assert.Null(ActivityCommandReducer.Reduce(CommandKind.ApplySettings, stopped).SettingsToPersist);
        }
    }
}
