using System;
using System.Collections.Generic;
using System.Globalization;

using MouseJiggler.App;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Commands;
using MouseJiggler.Core.Settings;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The words the user actually reads, from issue #10. The culture is injected on every call,
    /// so a machine in Berlin and a machine in Boston have to agree about what these tests expect.
    /// Nothing here sleeps, moves a pointer or touches the registry: the formatter is pure text.
    /// </summary>
    public sealed class StatusFormatterTests
    {
        private static readonly CultureInfo UnitedStates = CultureInfo.GetCultureInfo("en-US");
        private static readonly CultureInfo UnitedKingdom = CultureInfo.GetCultureInfo("en-GB");

        /// <summary>
        /// Hard-coded rather than read back from <see cref="DateTimeFormatInfo"/>, so a test that
        /// expects German cannot pass by quietly rendering English.
        /// </summary>
        private static readonly string[] EnglishDayNames =
        {
            "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday",
        };

        private static readonly string[] GermanDayNames =
        {
            "Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag",
        };

        private static SettingsV1 Settings(
            bool jiggleMouse = true,
            bool scheduleEnabled = true,
            string scheduleStart = "08:00",
            string scheduleEnd = "17:00")
        {
            SettingsV1 d = SettingsV1.CreateDefault();
            return new SettingsV1(
                d.SchemaVersion, d.Revision, stopped: false, d.RunMode,
                scheduleEnabled, scheduleStart, scheduleEnd, d.DayMask,
                d.PauseOnBattery, d.KeepDisplayOn, jiggleMouse,
                d.IntervalSeconds, d.DiagnosticLogging, d.StartupInitialized, d.FirstRunCompleted);
        }

        private static DesiredEffects Effects(
            StatusCode status,
            DateTime? nextTransitionUtc = null,
            TransitionKind kind = TransitionKind.None,
            bool systemAwake = true,
            bool wakeRequestFailed = false,
            bool inputRequestFailed = false)
        {
            return new DesiredEffects(
                systemAwake: systemAwake,
                displayAwake: false,
                mayJiggle: true,
                status: status,
                nextTransitionUtc: nextTransitionUtc,
                nextTransitionKind: kind,
                wakeRequestFailed: wakeRequestFailed,
                inputRequestFailed: inputRequestFailed);
        }

        /// <summary>
        /// The formatter compares the transition against "today" taken from the ambient clock,
        /// which is the one input it does not accept by injection. Anchoring two days out keeps
        /// the branch under test stable even if the run happens to cross local midnight.
        /// </summary>
        private static DateTime LocalDaysAheadAsUtc(int days, int hour, int minute)
        {
            DateTime localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.Local);
            var local = new DateTime(
                localNow.Year, localNow.Month, localNow.Day, hour, minute, 0, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(local.AddDays(days), TimeZoneInfo.Local);
        }

        private static DayOfWeek LocalDayOf(DateTime utc)
        {
            return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local).DayOfWeek;
        }

        private static string ExpectedDayName(string cultureName, DayOfWeek day)
        {
            return cultureName == "de-DE" ? GermanDayNames[(int)day] : EnglishDayNames[(int)day];
        }

        [Fact]
        public void AManualRunPromisesOnlyToRunUntilStoppedAndNeverQuotesTheScheduleEndTime()
        {
            // A manual run ignores the saved window, so naming 5:00 PM would be a promise the app
            // is not making. The evaluator may still carry a transition instant; it must not leak.
            DateTime transition = LocalDaysAheadAsUtc(2, 17, 0);
            DesiredEffects effects = Effects(StatusCode.RunningManual, transition, TransitionKind.End);

            string text = StatusTextFormatter.Primary(effects, Settings(), UnitedStates);

            Assert.Equal("Running manually, until stopped.", text);
            Assert.DoesNotContain("5:00", text);
            Assert.DoesNotContain("17:00", text);
            Assert.DoesNotContain("until 5", text);
            Assert.DoesNotContain(EnglishDayNames[(int)LocalDayOf(transition)], text);
        }

        [Fact]
        public void AContinuousRunAlsoPromisesOnlyToRunUntilStopped()
        {
            string text = StatusTextFormatter.Primary(
                Effects(StatusCode.RunningContinuous, LocalDaysAheadAsUtc(2, 17, 0), TransitionKind.End),
                Settings(),
                UnitedStates);

            Assert.Equal("Running continuously, until stopped.", text);
        }

        [Theory]
        [InlineData("en-US", "6:30 PM")]
        [InlineData("en-GB", "18:30")]
        [InlineData("de-DE", "18:30")]
        public void AScheduledEndIsWrittenOnTheReaderOwnClockAndInTheirOwnLanguage(
            string cultureName, string expectedTime)
        {
            // en-US reads a 12 hour clock; en-GB and de-DE read a 24 hour clock. One instant has
            // to come out as 6:30 PM for one reader and 18:30 for the next, with the day name
            // translated rather than left in English.
            CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
            DateTime transition = LocalDaysAheadAsUtc(2, 18, 30);
            string expectedDay = ExpectedDayName(cultureName, LocalDayOf(transition));

            string text = StatusTextFormatter.Primary(
                Effects(StatusCode.RunningScheduled, transition, TransitionKind.End),
                Settings(),
                culture);

            Assert.Equal("Running, scheduled until " + expectedDay + " at " + expectedTime + ".", text);
        }

        [Fact]
        public void A24HourReaderIsNeverGivenAMorningTimeForAnEveningTransition()
        {
            // The failure this guards against is the formatter reaching for an invariant "h:mm tt"
            // and a British reader seeing 6:30 for a transition that is really at 18:30.
            DateTime transition = LocalDaysAheadAsUtc(2, 18, 30);

            string british = StatusTextFormatter.Primary(
                Effects(StatusCode.RunningScheduled, transition, TransitionKind.End),
                Settings(),
                UnitedKingdom);

            Assert.Contains("18:30", british);
            Assert.DoesNotContain(UnitedStates.DateTimeFormat.PMDesignator, british);
        }

        [Fact]
        public void AnOvernightWindowEndingAfterMidnightNamesTheDayItEndsOn()
        {
            // A 22:00 to 06:00 window ends on a later date. A bare "6:00 AM" reads as this
            // morning, which has already gone; the day name is the only thing separating them.
            DateTime transition = LocalDaysAheadAsUtc(2, 6, 0);
            string expectedDay = EnglishDayNames[(int)LocalDayOf(transition)];

            string text = StatusTextFormatter.Primary(
                Effects(StatusCode.RunningScheduled, transition, TransitionKind.End),
                Settings(scheduleStart: "22:00", scheduleEnd: "06:00"),
                UnitedStates);

            Assert.Equal("Running, scheduled until " + expectedDay + " at 6:00 AM.", text);
        }

        [Fact]
        public void ATransitionLaterTodayIsGivenAsAPlainTimeWithNoDayName()
        {
            // Naming today is noise, and it is how a reader talks themselves into believing the
            // change is a week away.
            DateTime transition = DateTime.UtcNow;
            string todayName = EnglishDayNames[(int)LocalDayOf(transition)];

            string text = StatusTextFormatter.Primary(
                Effects(StatusCode.RunningScheduled, transition, TransitionKind.End),
                Settings(),
                UnitedStates);

            Assert.StartsWith("Running, scheduled until ", text);
            Assert.DoesNotContain(todayName, text);
            Assert.DoesNotContain(" at ", text);
        }

        [Fact]
        public void AWaitingStatusNamesTheNextStartRatherThanTheNextEnd()
        {
            DateTime transition = LocalDaysAheadAsUtc(2, 8, 0);
            string expectedDay = EnglishDayNames[(int)LocalDayOf(transition)];

            string text = StatusTextFormatter.Primary(
                Effects(StatusCode.WaitingForSchedule, transition, TransitionKind.Start),
                Settings(),
                UnitedStates);

            Assert.Equal("Waiting. Next start " + expectedDay + " at 8:00 AM.", text);
        }

        [Theory]
        [InlineData(StatusCode.RunningScheduled, "Running on schedule.")]
        [InlineData(StatusCode.WaitingForSchedule, "Waiting for the schedule.")]
        public void AScheduleWithNoKnownTransitionSaysSoInsteadOfInventingATime(
            StatusCode status, string expected)
        {
            // No selected days, or a window that never opens again, leaves the evaluator with
            // nothing to quote. The sentence has to stand on its own rather than trail off.
            string text = StatusTextFormatter.Primary(Effects(status), Settings(), UnitedStates);

            Assert.Equal(expected, text);
        }

        [Theory]
        [InlineData(StatusCode.Stopped, "Stopped. Choose Start when ready.")]
        [InlineData(StatusCode.PausedOnBattery, "Paused, on battery.")]
        [InlineData(StatusCode.PowerStatusUnavailable, "Paused. Power status unavailable.")]
        [InlineData(StatusCode.SessionUnavailable, "Waiting. This session is not available.")]
        [InlineData(StatusCode.LockedKeepingAwake, "Locked, keeping the computer awake.")]
        public void EachHaltedStateNamesItsOwnReasonForNotRunning(StatusCode status, string expected)
        {
            // Paused on battery, no power reading, and no usable session are three different
            // problems with three different fixes, so one shared "not running" is not enough.
            Assert.Equal(expected, StatusTextFormatter.Primary(Effects(status), Settings(), UnitedStates));
        }

        [Fact]
        public void AnErrorSaysWhetherWindowsRefusedTheKeepAwakeRequestOrSomethingElseFailed()
        {
            // A refused wake request is the one error the user can act on from the tray, so it
            // must not be flattened into the generic "see details" sentence.
            string refused = StatusTextFormatter.Primary(
                Effects(StatusCode.Error, wakeRequestFailed: true), Settings(), UnitedStates);
            string other = StatusTextFormatter.Primary(
                Effects(StatusCode.Error), Settings(), UnitedStates);

            Assert.Equal("Error. Windows refused the request to stay awake.", refused);
            Assert.Equal("Error. See details in Settings.", other);
        }

        [Fact]
        public void EveryStatusCodeProducesADistinctFinishedSentence()
        {
            var seen = new Dictionary<string, StatusCode>(StringComparer.Ordinal);

            foreach (StatusCode status in Enum.GetValues(typeof(StatusCode)))
            {
                string text = StatusTextFormatter.Primary(Effects(status), Settings(), UnitedStates);

                Assert.False(string.IsNullOrWhiteSpace(text), status + " has no status sentence.");
                Assert.EndsWith(".", text);

                if (seen.TryGetValue(text, out StatusCode earlier))
                {
                    Assert.Fail(status + " reads exactly like " + earlier + ": " + text);
                }

                seen.Add(text, status);
            }

            Assert.Equal(Enum.GetValues(typeof(StatusCode)).Length, seen.Count);
        }

        [Fact]
        public void AStatusCodeThisBuildDoesNotKnowFallsBackToStoppedRatherThanEmptyText()
        {
            // A code added to Core and not yet mapped here has to degrade to something honest,
            // never to a blank label in the tray menu.
            var unknown = (StatusCode)9999;

            Assert.Equal("Stopped.", StatusTextFormatter.Primary(Effects(unknown), Settings(), UnitedStates));
            Assert.Equal("Mouse Jiggler: stopped", StatusTextFormatter.Tooltip(Effects(unknown)));
        }

        [Theory]
        [InlineData(StatusCode.Stopped, "Mouse Jiggler: stopped")]
        [InlineData(StatusCode.WaitingForSchedule, "Mouse Jiggler: waiting for schedule")]
        [InlineData(StatusCode.PausedOnBattery, "Mouse Jiggler: paused on battery")]
        [InlineData(StatusCode.PowerStatusUnavailable, "Mouse Jiggler: power unavailable")]
        [InlineData(StatusCode.SessionUnavailable, "Mouse Jiggler: session unavailable")]
        [InlineData(StatusCode.RunningScheduled, "Mouse Jiggler: running (scheduled)")]
        [InlineData(StatusCode.RunningContinuous, "Mouse Jiggler: running")]
        [InlineData(StatusCode.RunningManual, "Mouse Jiggler: running (manual)")]
        [InlineData(StatusCode.LockedKeepingAwake, "Mouse Jiggler: locked, keeping awake")]
        [InlineData(StatusCode.Error, "Mouse Jiggler: error")]
        public void TheTooltipArrivesWholeRatherThanCutOffMidWord(StatusCode status, string expected)
        {
            // Every mapping is pinned in full because the failure mode is silent: a tip that grows
            // past the limit is chopped by the formatter, and "running (scheduled)" becomes
            // "running (schedul" with nothing to say it happened.
            Assert.Equal(expected, StatusTextFormatter.Tooltip(Effects(status)));
        }

        [Fact]
        public void EveryTooltipFitsInsideTheSixtyThreeCharacterNotifyIconLimit()
        {
            // Windows truncates a longer tip itself, so anything over 63 is text the user simply
            // never sees. The check runs over the enum so a new status cannot slip past it.
            foreach (StatusCode status in Enum.GetValues(typeof(StatusCode)))
            {
                string tooltip = StatusTextFormatter.Tooltip(Effects(status));

                Assert.True(
                    tooltip.Length <= 63,
                    status + " produced a " + tooltip.Length + " character tooltip: " + tooltip);
                Assert.StartsWith("Mouse Jiggler: ", tooltip);
                Assert.True(tooltip.Length > "Mouse Jiggler: ".Length, status + " produced a tooltip with no status.");
            }
        }

        [Fact]
        public void EveryStatusCodeGetsItsOwnTooltipSoTheTrayIsReadableWithoutOpeningTheApp()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (StatusCode status in Enum.GetValues(typeof(StatusCode)))
            {
                Assert.True(seen.Add(StatusTextFormatter.Tooltip(Effects(status))), status + " shares a tooltip.");
            }

            Assert.Equal(Enum.GetValues(typeof(StatusCode)).Length, seen.Count);
        }

        [Fact]
        public void TheDetailLineWarnsThatKeepingAwakeAloneMayNotStopAnIdleLock()
        {
            // With jiggling switched off the app holds the machine awake but generates no input,
            // and a policy-driven idle lock can still fire. Silence here would read as a promise.
            string detail = StatusTextFormatter.Detail(
                Effects(StatusCode.RunningContinuous, systemAwake: true), Settings(jiggleMouse: false));

            Assert.Equal("Keeping the computer awake alone may not prevent idle locking.", detail);
        }

        [Fact]
        public void TheKeepAwakeCaveatIsSilentWhenNothingIsBeingKeptAwake()
        {
            // Jiggling off and no wake request is not a half-working state; there is nothing to
            // qualify, and a warning on a stopped app is just noise.
            string detail = StatusTextFormatter.Detail(
                Effects(StatusCode.Stopped, systemAwake: false), Settings(jiggleMouse: false));

            Assert.Equal(string.Empty, detail);
        }

        [Fact]
        public void AFailedPointerMoveIsReportedEvenThoughTheComputerIsStillHeldAwake()
        {
            // Partial success is the dangerous case: the machine stays awake so the app looks like
            // it is working, while the movement the user asked for is not happening at all.
            string detail = StatusTextFormatter.Detail(
                Effects(StatusCode.RunningContinuous, inputRequestFailed: true), Settings());

            Assert.Equal("Pointer movement has failed. The computer is still being kept awake.", detail);
        }

        [Fact]
        public void AFailedPointerMoveOutranksTheStandingKeepAwakeCaveat()
        {
            // Both conditions hold at once here. A live failure is news; the standing caveat is
            // not, and only one line is on screen.
            string detail = StatusTextFormatter.Detail(
                Effects(StatusCode.RunningContinuous, systemAwake: true, inputRequestFailed: true),
                Settings(jiggleMouse: false));

            Assert.Equal("Pointer movement has failed. The computer is still being kept awake.", detail);
        }

        [Fact]
        public void ALockedSessionExplainsWhichBehavioursStopWhileItIsLocked()
        {
            string detail = StatusTextFormatter.Detail(Effects(StatusCode.LockedKeepingAwake), Settings());

            Assert.Equal("Pointer movement and the display request stop while the session is locked.", detail);
        }

        [Fact]
        public void TheDetailLineStaysEmptyWhenThereIsNothingToQualify()
        {
            // A healthy run should not carry a permanent warning: a caveat that is always on
            // screen is a caveat nobody reads on the day it matters.
            Assert.Equal(string.Empty, StatusTextFormatter.Detail(Effects(StatusCode.RunningManual), Settings()));
            Assert.Equal(string.Empty, StatusTextFormatter.Detail(Effects(StatusCode.RunningScheduled), Settings()));
        }

        [Theory]
        [InlineData(CommandKind.Start, "could not start,")]
        [InlineData(CommandKind.StartNow, "could not start now,")]
        [InlineData(CommandKind.UseSchedule, "could not switch to the schedule,")]
        public void ARefusedCommandNamesWhatTheUserAskedForAndWhyItDidNotHappen(CommandKind kind, string expected)
        {
            OperationResult outcome = OperationResult.Failure(FaultSubsystem.ConfigSave, "settings.lockUnavailable", 32);

            string message = StatusTextFormatter.CommandFailure(kind, outcome);

            Assert.Contains(expected, message, StringComparison.Ordinal);

            // The fault code is what a bug report needs, and the native number with it.
            Assert.Contains("settings.lockUnavailable", message, StringComparison.Ordinal);
            Assert.Contains("(32)", message, StringComparison.Ordinal);
        }

        [Fact]
        public void ARefusedCommandExplainsThatNothingChangedRatherThanOnlyThatASaveFailed()
        {
            OperationResult outcome = OperationResult.Failure(FaultSubsystem.ConfigSave, "settings.writeFailed");

            string message = StatusTextFormatter.CommandFailure(CommandKind.Start, outcome);

            // "Could not save" on its own does not answer "so why is it still stopped".
            Assert.Contains("stayed as it was", message, StringComparison.Ordinal);
            Assert.Contains("try again", message, StringComparison.Ordinal);

            // No native error number was reported, so none is invented.
            Assert.DoesNotContain("()", message, StringComparison.Ordinal);
        }

        [Fact]
        public void ARefusedCommandNeverPrintsAPathThatNamesTheUser()
        {
            OperationResult outcome = OperationResult.Failure(FaultSubsystem.ConfigSave, "settings.writeFailed");

            string message = StatusTextFormatter.CommandFailure(CommandKind.Start, outcome);

            // The same privacy boundary the diagnostics keep. Describing the folder is enough
            // to act on; printing it would put the account name into a screenshot.
            Assert.DoesNotContain(":\\", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Users", message, StringComparison.Ordinal);
            Assert.DoesNotContain(Environment.UserName, message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void FormattingRefusesMissingArgumentsRatherThanShowingAHalfBuiltSentence()
        {
            DesiredEffects effects = Effects(StatusCode.Stopped);

            Assert.Throws<ArgumentNullException>(() => StatusTextFormatter.Primary(null!, Settings(), UnitedStates));
            Assert.Throws<ArgumentNullException>(() => StatusTextFormatter.Primary(effects, null!, UnitedStates));
            Assert.Throws<ArgumentNullException>(() => StatusTextFormatter.Primary(effects, Settings(), null!));
        }
    }
}
