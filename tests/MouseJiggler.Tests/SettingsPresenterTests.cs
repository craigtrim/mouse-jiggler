using System;
using MouseJiggler.App;
using MouseJiggler.Core.Scheduling;
using MouseJiggler.Core.Settings;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The part of the Settings window that has nothing to do with windows: the day mask, the
    /// overnight case, and the shape of the patch a save produces. All of it was previously
    /// inside the form, where the only way to check it was to open the window and look.
    /// </summary>
    public sealed class SettingsPresenterTests
    {
        private static SettingsV1 Saved(
            bool scheduleEnabled = true,
            string start = "08:00",
            string end = "17:00",
            int dayMask = SettingsV1.AllDaysMask,
            bool stopped = true,
            RunMode mode = RunMode.Scheduled)
        {
            return new SettingsV1(
                schemaVersion: 1,
                revision: 3,
                stopped: stopped,
                runMode: mode,
                scheduleEnabled: scheduleEnabled,
                scheduleStart: start,
                scheduleEnd: end,
                dayMask: dayMask,
                pauseOnBattery: true,
                keepDisplayOn: false,
                jiggleMouse: true,
                intervalSeconds: 45,
                diagnosticLogging: true,
                startupInitialized: true,
                firstRunCompleted: true);
        }

        [Fact]
        public void TheDraftCarriesEveryEditablePreferenceAndNothingElse()
        {
            SettingsDraft draft = SettingsPresenter.ToDraft(Saved());

            Assert.True(draft.ScheduleEnabled);
            Assert.Equal(8 * 60, draft.StartMinutes);
            Assert.Equal(17 * 60, draft.EndMinutes);
            Assert.True(draft.PauseOnBattery);
            Assert.False(draft.KeepDisplayOn);
            Assert.True(draft.JiggleMouse);
            Assert.Equal(45, draft.IntervalSeconds);
            Assert.True(draft.DiagnosticLogging);

            Assert.All(draft.Days, day => Assert.True(day));
        }

        [Fact]
        public void TheDaysAreOrderedMondayFirstAsAWeekReads()
        {
            Assert.Equal(
                new[]
                {
                    DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
                    DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
                },
                SettingsPresenter.DayOrder);
        }

        [Fact]
        public void EveryDayCombinationSurvivesTheRoundTripThroughTheMask()
        {
            // All 128 combinations, because an off-by-one in the bit order shows up only for the
            // days it happens to swap, and a spot check would miss it.
            for (int mask = 0; mask <= SettingsV1.AllDaysMask; mask++)
            {
                SettingsDraft draft = SettingsPresenter.ToDraft(Saved(dayMask: mask));

                Assert.Equal(mask, SettingsPresenter.BuildDayMask(draft.Days));
            }
        }

        [Fact]
        public void TheMaskUsesTheBitTheScheduleItselfUses()
        {
            var mondayOnly = new bool[7];
            mondayOnly[0] = true;

            int mask = SettingsPresenter.BuildDayMask(mondayOnly);

            Assert.Equal(DailySchedule.DayBit(DayOfWeek.Monday), mask);
            Assert.True(DailySchedule.IsDaySelected(mask, DayOfWeek.Monday));
            Assert.False(DailySchedule.IsDaySelected(mask, DayOfWeek.Sunday));
        }

        [Fact]
        public void AShortArrayIsNotAnExceptionAndAnAbsentArrayIs()
        {
            // Fewer flags than days means the missing ones are simply unselected. A null array is
            // a programming error rather than an unusual selection, so it throws.
            Assert.Equal(0, SettingsPresenter.BuildDayMask(new bool[0]));
            Assert.Throws<ArgumentNullException>(() => SettingsPresenter.BuildDayMask(null!));
        }

        [Fact]
        public void APatchCarriesPreferencesOnlyAndNeverIntent()
        {
            SettingsDraft draft = SettingsPresenter.ToDraft(Saved(stopped: true, mode: RunMode.Scheduled));
            draft.IntervalSeconds = 120;
            draft.KeepDisplayOn = true;

            SettingsPatch patch = SettingsPresenter.ToPatch(draft);

            Assert.Equal(120, patch.IntervalSeconds);
            Assert.True(patch.KeepDisplayOn);

            // Saving a preference must never be able to start a stopped app. SettingsPatch has
            // no field for stopped or runMode at all, which is what makes that structural rather
            // than a rule somebody has to remember.
            SettingsV1 stopped = Saved(stopped: true, mode: RunMode.Scheduled);
            SettingsV1 applied = patch.ApplyTo(stopped);

            Assert.True(applied.Stopped);
            Assert.Equal(RunMode.Scheduled, applied.RunMode);
        }

        [Fact]
        public void TimesGoBackOutInTheInvariantFormatTheDocumentUses()
        {
            var draft = new SettingsDraft
            {
                StartMinutes = 22 * 60 + 30,
                EndMinutes = 6 * 60 + 5,
            };

            SettingsPatch patch = SettingsPresenter.ToPatch(draft);

            Assert.Equal("22:30", patch.ScheduleStart);
            Assert.Equal("06:05", patch.ScheduleEnd);
        }

        [Theory]
        [InlineData(8 * 60, 17 * 60, false)]
        [InlineData(22 * 60, 6 * 60, true)]
        [InlineData(0, 0, false)]
        [InlineData(1, 0, true)]
        public void AnEndBeforeTheStartIsANightShiftRatherThanAMistake(int start, int end, bool overnight)
        {
            Assert.Equal(overnight, SettingsPresenter.IsOvernight(start, end));
        }

        [Fact]
        public void AnUnreadableTimeInTheSavedDocumentBecomesMidnightRatherThanThrowing()
        {
            // The document is validated on the way in, so this should not happen. If it does, the
            // window still has to open: refusing to draw itself would leave no way to fix it.
            SettingsDraft draft = SettingsPresenter.ToDraft(Saved(start: "not a time", end: "17:00"));

            Assert.Equal(0, draft.StartMinutes);
            Assert.Equal(17 * 60, draft.EndMinutes);
        }

        [Fact]
        public void MissingArgumentsAreRefusedRatherThanQuietlyProducingAnEmptyDraft()
        {
            Assert.Throws<ArgumentNullException>(() => SettingsPresenter.ToDraft(null!));
            Assert.Throws<ArgumentNullException>(() => SettingsPresenter.ToPatch(null!));
        }
    }

    /// <summary>
    /// What a rejected save actually says. The messages matter as much as the validation: a
    /// code the presenter does not recognise falls back to a dialog, and the reason is lost.
    /// </summary>
    public sealed class SettingsValidationPresenterTests
    {
        [Theory]
        [InlineData("settings.schedule.noDays", SettingsField.Days)]
        [InlineData("settings.schedule.equalTimes", SettingsField.EndTime)]
        [InlineData("settings.schedule.invalidTime", SettingsField.EndTime)]
        [InlineData("settings.intervalSeconds.invalid", SettingsField.Interval)]
        public void AFieldFaultPointsAtTheControlToChange(string code, SettingsField expected)
        {
            ValidationMessage message = SettingsValidationPresenter.Describe(code);

            Assert.Equal(expected, message.Field);
            Assert.True(message.BelongsToAControl);
            Assert.NotEqual(string.Empty, message.Text);
        }

        [Theory]
        [InlineData("settings.lockTimeout")]
        [InlineData("settings.accessDenied")]
        [InlineData("settings.somethingNobodyHasWrittenYet")]
        [InlineData(null)]
        public void AFaultWithNoOneControlToBlameGoesToADialog(string? code)
        {
            ValidationMessage message = SettingsValidationPresenter.Describe(code);

            Assert.Equal(SettingsField.None, message.Field);
            Assert.False(message.BelongsToAControl);
            Assert.NotEqual(string.Empty, message.Text);
        }

        [Fact]
        public void EveryMessageSaysWhatToDoRatherThanOnlyWhatIsWrong()
        {
            string[] codes =
            {
                "settings.schedule.noDays",
                "settings.schedule.equalTimes",
                "settings.schedule.invalidTime",
                "settings.intervalSeconds.invalid",
                "settings.lockTimeout",
                "settings.accessDenied",
                "settings.unknown",
            };

            foreach (string code in codes)
            {
                string text = SettingsValidationPresenter.Describe(code).Text;

                // A message the user can act on ends in a full stop and is a sentence, not a
                // code. Leaking the fault code into the words would be the failure mode here.
                Assert.EndsWith(".", text, StringComparison.Ordinal);
                Assert.DoesNotContain("settings.", text, StringComparison.Ordinal);
                Assert.True(text.Length > 20, code + " has no real message.");
            }
        }

        [Fact]
        public void AFailedSaveAlwaysSaysThePreviousSettingsSurvived()
        {
            // The window stays open on failure so the edits are not lost, and the saved document
            // is untouched. Both halves have to be said, or the user reasonably assumes they
            // have lost something.
            string text = SettingsValidationPresenter.Describe("settings.unknown").Text;

            Assert.Contains("unchanged", text, StringComparison.Ordinal);
        }
    }
}
