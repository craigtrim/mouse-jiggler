using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MouseJiggler.App;
using MouseJiggler.App.Runtime;
using MouseJiggler.Core.Diagnostics;
using MouseJiggler.Core.Settings;
using MouseJiggler.Tests.Fakes;
using MouseJiggler.Windows.Diagnostics;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The behaviour of the Settings window, driven through the controls a screen reader would
    /// find rather than through private fields. Anything these tests cannot reach by accessible
    /// name is something a keyboard user cannot reach either.
    /// </summary>
    public sealed class SettingsWindowTests
    {
        private static void OnFormThread(SettingsV1 settings, Action<SettingsForm, FakeSettingsStore> body)
        {
            Exception? failure = null;

            var thread = new System.Threading.Thread(() =>
            {
                using (var directory = new TemporaryDirectory())
                {
                    var ring = new DiagnosticRing();
                    var sink = new LocalDiagnosticSink(Path.Combine(directory.Path, "Logs"), ring);
                    var store = new FakeSettingsStore(settings);
                    var environment = new FakePowerSessionSource();
                    var executionState = new FakeExecutionStateController();

                    var coordinator = new ActivityCoordinator(
                        store,
                        settings,
                        environment,
                        executionState,
                        new FakeIdleInputSource(),
                        new FakeMouseJiggler(),
                        new FakeClock(),
                        new FakeInputDesktopProbe(),
                        new ImmediateInvoker(),
                        new RecordingDiagnosticSink());

                    try
                    {
                        using (var form = new SettingsForm(coordinator, sink, ring))
                        {
                            body(form, store);
                        }
                    }
                    catch (Exception error)
                    {
                        failure = error;
                    }
                    finally
                    {
                        coordinator.Dispose();
                        environment.Dispose();
                        executionState.Dispose();
                        store.Dispose();
                        sink.Dispose();
                    }
                }
            });

            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                throw new Xunit.Sdk.XunitException(failure.ToString());
            }
        }

        private static SettingsV1 Settings(bool scheduleEnabled = false, int dayMask = SettingsV1.AllDaysMask)
        {
            return new SettingsV1(
                schemaVersion: 1,
                revision: 1,
                stopped: true,
                runMode: RunMode.Scheduled,
                scheduleEnabled: scheduleEnabled,
                scheduleStart: "08:00",
                scheduleEnd: "17:00",
                dayMask: dayMask,
                pauseOnBattery: true,
                keepDisplayOn: true,
                jiggleMouse: true,
                intervalSeconds: 30,
                diagnosticLogging: false,
                startupInitialized: true,
                firstRunCompleted: true);
        }

        private static T Find<T>(Control parent, string accessibleName) where T : Control
        {
            T? found = Search<T>(parent, accessibleName);

            Assert.True(found != null, "No control named \"" + accessibleName + "\" is reachable in the window.");
            return found!;
        }

        private static T? Search<T>(Control parent, string accessibleName) where T : Control
        {
            foreach (Control child in parent.Controls)
            {
                if (child is T match && string.Equals(child.AccessibleName, accessibleName, StringComparison.Ordinal))
                {
                    return match;
                }

                T? nested = Search<T>(child, accessibleName);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        [Fact]
        public void TheAboutSectionStartsClosed()
        {
            OnFormThread(Settings(), (form, _) =>
            {
                CheckBox expander = Find<CheckBox>(form, "Show about and diagnostics details");

                // Closed on every launch. This section is for the day something is wrong; on
                // every other day it is noise between the settings and the Save button.
                Assert.False(expander.Checked);

                Label version = Find<Label>(form, "Version and licence");
                Assert.False(IsEffectivelyVisible(version));
            });
        }

        [Fact]
        public void OpeningTheAboutSectionRevealsItsContentsAndGrowsTheWindow()
        {
            OnFormThread(Settings(), (form, _) =>
            {
                ShowOffScreen(form);

                try
                {
                    int closedHeight = form.ClientSize.Height;

                    CheckBox expander = Find<CheckBox>(form, "Show about and diagnostics details");
                    expander.Checked = true;
                    LayoutSettler.Settle(form);

                    Assert.True(IsEffectivelyVisible(Find<Label>(form, "Version and licence")));
                    Assert.True(
                        form.ClientSize.Height > closedHeight,
                        "The window did not grow, so the details are behind a scrollbar or off the bottom.");

                    // And closing it again gives the space back rather than leaving a gap.
                    expander.Checked = false;
                    LayoutSettler.Settle(form);

                    Assert.Equal(closedHeight, form.ClientSize.Height);
                }
                finally
                {
                    form.Hide();
                }
            });
        }

        [Fact]
        public void SaveIsHeldClosedWhileTheScheduleIsOnWithNoDaysChosen()
        {
            OnFormThread(Settings(scheduleEnabled: true), (form, _) =>
            {
                Button save = Find<Button>(form, "Save settings");
                Assert.True(save.Enabled);

                // Clear every day. A schedule that runs on no days is meaningless, and the
                // store rejects it, so the button must not invite the attempt.
                foreach (DayOfWeek day in SettingsPresenter.DayOrder)
                {
                    Find<CheckBox>(form, day.ToString()).Checked = false;
                }

                Assert.False(save.Enabled);

                Find<CheckBox>(form, DayOfWeek.Wednesday.ToString()).Checked = true;
                Assert.True(save.Enabled);
            });
        }

        [Fact]
        public void SaveIsHeldClosedWhenTheScheduleStartsAndEndsAtTheSameMoment()
        {
            OnFormThread(Settings(scheduleEnabled: true), (form, _) =>
            {
                Button save = Find<Button>(form, "Save settings");
                DateTimePicker start = Find<DateTimePicker>(form, "Schedule start time");
                DateTimePicker end = Find<DateTimePicker>(form, "Schedule end time");

                end.Value = start.Value;

                // Equal times are rejected rather than quietly meaning all day. Switching the
                // schedule off is the explicit way to say that.
                Assert.False(save.Enabled);

                end.Value = start.Value.AddHours(1);
                Assert.True(save.Enabled);
            });
        }

        [Fact]
        public void AMeaninglessScheduleDoesNotBlockSavingWhileTheScheduleIsOff()
        {
            OnFormThread(Settings(scheduleEnabled: false), (form, _) =>
            {
                Button save = Find<Button>(form, "Save settings");

                foreach (DayOfWeek day in SettingsPresenter.DayOrder)
                {
                    Find<CheckBox>(form, day.ToString()).Checked = false;
                }

                // The days are kept as a draft for whenever the schedule is switched back on.
                // Refusing to save the rest of the preferences over them would be absurd.
                Assert.True(save.Enabled);
            });
        }

        [Fact]
        public void TheTwoMovementNotesAreAlternativesRatherThanBoth()
        {
            OnFormThread(Settings(), (form, _) =>
            {
                ShowOffScreen(form);

                Label movement = Find<Label>(form, "Movement explanation");
                Label absent = Find<Label>(form, "Movement disabled note");

                Assert.True(movement.Visible);
                Assert.False(absent.Visible);

                // With movement off, the useful thing to say is what keeping the computer awake
                // cannot do on its own.
                Find<CheckBox>(form, "Jiggle mouse").Checked = false;

                Assert.False(movement.Visible);
                Assert.True(absent.Visible);
                Assert.Contains("idle locking", absent.Text, StringComparison.Ordinal);
            });
        }

        [Fact]
        public void TheIntervalIsDisabledWithMovementOffAndStepsInFives()
        {
            OnFormThread(Settings(), (form, _) =>
            {
                var interval = Find<NumericUpDown>(form, "Inactivity interval in seconds");

                Assert.True(interval.Enabled);
                Assert.Equal(5, interval.Increment);
                Assert.Equal(SettingsV1.MinIntervalSeconds, interval.Minimum);
                Assert.Equal(SettingsV1.MaxIntervalSeconds, interval.Maximum);

                // Stepping in fives is a convenience, not a constraint: any whole number in
                // range is still a legal interval.
                interval.Value = 7;
                Assert.Equal(7, interval.Value);

                Find<CheckBox>(form, "Jiggle mouse").Checked = false;
                Assert.False(interval.Enabled);
            });
        }

        [Fact]
        public void ADraftIsNotOverwrittenWhenSomebodyElseSavesAndTheUserIsToldWhy()
        {
            OnFormThread(Settings(), (form, store) =>
            {
                ShowOffScreen(form);

                var interval = Find<NumericUpDown>(form, "Inactivity interval in seconds");
                Label notice = Find<Label>(form, "Settings changed elsewhere");

                Assert.False(notice.Visible);

                interval.Value = 120;

                // Another instance commits something different while this draft is open.
                store.RaiseExternalChange(new SettingsV1(
                    schemaVersion: 1,
                    revision: 9,
                    stopped: true,
                    runMode: RunMode.Manual,
                    scheduleEnabled: false,
                    scheduleStart: "08:00",
                    scheduleEnd: "17:00",
                    dayMask: SettingsV1.AllDaysMask,
                    pauseOnBattery: false,
                    keepDisplayOn: false,
                    jiggleMouse: true,
                    intervalSeconds: 15,
                    diagnosticLogging: false,
                    startupInitialized: true,
                    firstRunCompleted: true));

                // The edit survives. Having the fields move under someone's hands halfway
                // through a change is worse than showing them slightly stale neighbours.
                Assert.Equal(120, interval.Value);
                Assert.True(notice.Visible);
            });
        }

        [Fact]
        public void ACleanFormFollowsAChangeMadeSomewhereElseInsteadOfShowingStaleValues()
        {
            OnFormThread(Settings(), (form, store) =>
            {
                ShowOffScreen(form);

                var interval = Find<NumericUpDown>(form, "Inactivity interval in seconds");
                Label notice = Find<Label>(form, "Settings changed elsewhere");

                Assert.Equal(30, interval.Value);

                store.RaiseExternalChange(new SettingsV1(
                    schemaVersion: 1,
                    revision: 9,
                    stopped: true,
                    runMode: RunMode.Manual,
                    scheduleEnabled: false,
                    scheduleStart: "08:00",
                    scheduleEnd: "17:00",
                    dayMask: SettingsV1.AllDaysMask,
                    pauseOnBattery: true,
                    keepDisplayOn: true,
                    jiggleMouse: true,
                    intervalSeconds: 15,
                    diagnosticLogging: false,
                    startupInitialized: true,
                    firstRunCompleted: true));

                // Nothing was at risk, so the window shows what is actually saved, and there is
                // nothing to warn about.
                Assert.Equal(15, interval.Value);
                Assert.False(notice.Visible);
            });
        }

        [Fact]
        public void TheWindowOpensInsideAConnectedScreen()
        {
            OnFormThread(Settings(), (form, _) =>
            {
                form.Show();

                try
                {
                    LayoutSettler.Settle(form);

                    Rectangle working = Screen.FromControl(form).WorkingArea;

                    Assert.True(working.Contains(form.Bounds),
                        "The window at " + form.Bounds + " is not inside the working area " + working + ".");
                }
                finally
                {
                    form.Hide();
                }
            });
        }

        [Fact]
        public void EnterSavesAndEscapeCancels()
        {
            OnFormThread(Settings(), (form, _) =>
            {
                Assert.Same(Find<Button>(form, "Save settings"), form.AcceptButton);
                Assert.Same(Find<Button>(form, "Cancel"), form.CancelButton);
            });
        }

        [Fact]
        public void EveryControlThatTakesFocusHasAKeyboardRouteToIt()
        {
            OnFormThread(Settings(), (form, _) =>
            {
                var mnemonics = new List<char>();
                var duplicates = new List<string>();

                CollectMnemonics(form, mnemonics, duplicates);

                Assert.True(duplicates.Count == 0, string.Join(", ", duplicates));
                Assert.True(mnemonics.Count >= 10, "Only " + mnemonics.Count + " controls carry a mnemonic.");
            });
        }

        /// <summary>Gathers Alt-key mnemonics and reports any letter claimed twice.</summary>
        private static void CollectMnemonics(Control parent, List<char> seen, List<string> duplicates)
        {
            foreach (Control child in parent.Controls)
            {
                if (child is ButtonBase || child is Label)
                {
                    int index = child.Text.IndexOf('&');

                    if (index >= 0 && index + 1 < child.Text.Length)
                    {
                        char key = char.ToUpperInvariant(child.Text[index + 1]);

                        if (seen.Contains(key))
                        {
                            // Two controls on one letter means Alt cycles instead of acting,
                            // which is a keyboard user quietly losing a shortcut.
                            duplicates.Add("Alt+" + key + " is claimed twice, most recently by \"" + child.Text + "\"");
                        }
                        else
                        {
                            seen.Add(key);
                        }
                    }
                }

                CollectMnemonics(child, seen, duplicates);
            }
        }

        /// <summary>
        /// Shows the window where nobody can see it, because Control.Visible reports the
        /// effective value and reads false for every child of a form that was never shown.
        /// </summary>
        private static void ShowOffScreen(Form form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.ShowInTaskbar = false;
            form.Show();
            LayoutSettler.Settle(form);
        }

        /// <summary>Visible, and every container above it visible too.</summary>
        private static bool IsEffectivelyVisible(Control control)
        {
            for (Control? current = control; current != null; current = current.Parent)
            {
                if (!current.Visible && !(current is Form))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
