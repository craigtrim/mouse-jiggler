using System;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Commands;
using MouseJiggler.Core.Environment;
using MouseJiggler.Core.Settings;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The eligibility ladder and the capability reporting rules from issue #5. Each test pins
    /// one collision between competing conditions, because the order they resolve in is the
    /// part that is easy to get subtly wrong.
    /// </summary>
    public sealed class ActivityPolicyTests
    {
        private static readonly DateTime Noon = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);

        private static SettingsV1 Running(
            RunMode mode = RunMode.Scheduled,
            bool scheduleEnabled = false,
            bool pauseOnBattery = true,
            bool keepDisplayOn = true,
            bool jiggleMouse = true)
        {
            SettingsV1 d = SettingsV1.CreateDefault();
            return new SettingsV1(
                d.SchemaVersion, d.Revision, stopped: false, mode,
                scheduleEnabled, d.ScheduleStart, d.ScheduleEnd, d.DayMask,
                pauseOnBattery, keepDisplayOn, jiggleMouse,
                d.IntervalSeconds, d.DiagnosticLogging, d.StartupInitialized, d.FirstRunCompleted);
        }

        private static EnvironmentSnapshot Environment(
            PowerSource power = PowerSource.External,
            SessionState session = SessionState.ActiveUnlocked,
            bool suspended = false,
            bool exiting = false,
            bool inputDesktopAvailable = true,
            CapabilityState wake = CapabilityState.Ok,
            CapabilityState input = CapabilityState.Ok,
            bool settingsAvailable = true,
            DateTime? utcNow = null)
        {
            return new EnvironmentSnapshot(
                utcNow ?? Noon, TimeZoneInfo.Utc, 1_000, power, session,
                suspended, exiting, inputDesktopAvailable, wake, input, settingsAvailable);
        }

        [Fact]
        public void AStoppedAppRequestsNothingEvenWhenEverythingElseIsFine()
        {
            var policy = new ActivityPolicy();

            DesiredEffects effects = policy.Evaluate(SettingsV1.CreateDefault(), Environment());

            Assert.Equal(StatusCode.Stopped, effects.Status);
            Assert.False(effects.SystemAwake);
            Assert.False(effects.DisplayAwake);
            Assert.False(effects.MayJiggle);
        }

        [Fact]
        public void ScheduledModeWithSchedulingOffRunsContinuously()
        {
            var policy = new ActivityPolicy();

            DesiredEffects effects = policy.Evaluate(Running(), Environment());

            Assert.Equal(StatusCode.RunningContinuous, effects.Status);
            Assert.True(effects.SystemAwake);
            Assert.True(effects.DisplayAwake);
            Assert.True(effects.MayJiggle);
        }

        [Fact]
        public void StopOutranksAnEligibleSchedule()
        {
            var policy = new ActivityPolicy();
            SettingsV1 stoppedButScheduled = SettingsIntent.WithStopped(Running(scheduleEnabled: true), true);

            DesiredEffects effects = policy.Evaluate(stoppedButScheduled, Environment());

            Assert.Equal(StatusCode.Stopped, effects.Status);
        }

        [Fact]
        public void ExitingAndSuspendingOutrankEverythingIncludingAnEnabledApp()
        {
            var policy = new ActivityPolicy();

            Assert.Equal(StatusCode.Stopped, policy.Evaluate(Running(), Environment(exiting: true)).Status);
            Assert.Equal(StatusCode.Stopped, policy.Evaluate(Running(), Environment(suspended: true)).Status);
        }

        [Fact]
        public void ADisconnectedSessionOutranksThePowerGate()
        {
            var policy = new ActivityPolicy();

            DesiredEffects effects = policy.Evaluate(Running(), Environment(power: PowerSource.Battery, session: SessionState.Disconnected));

            Assert.Equal(StatusCode.SessionUnavailable, effects.Status);
        }

        [Fact]
        public void BatteryAndUnknownPowerAreReportedDifferentlyButBothPause()
        {
            var policy = new ActivityPolicy();

            Assert.Equal(StatusCode.PausedOnBattery, policy.Evaluate(Running(), Environment(power: PowerSource.Battery)).Status);
            Assert.Equal(StatusCode.PowerStatusUnavailable, policy.Evaluate(Running(), Environment(power: PowerSource.Unknown)).Status);
        }

        [Fact]
        public void UnknownPowerDoesNotGateWhenTheUserDidNotAskForExternalPowerOnly()
        {
            var policy = new ActivityPolicy();

            DesiredEffects effects = policy.Evaluate(Running(pauseOnBattery: false), Environment(power: PowerSource.Unknown));

            Assert.Equal(StatusCode.RunningContinuous, effects.Status);
            Assert.True(effects.SystemAwake);
        }

        [Fact]
        public void ALockedSessionKeepsTheComputerAwakeAndNothingElse()
        {
            var policy = new ActivityPolicy();

            DesiredEffects effects = policy.Evaluate(Running(), Environment(session: SessionState.Locked));

            Assert.Equal(StatusCode.LockedKeepingAwake, effects.Status);
            Assert.True(effects.SystemAwake);
            Assert.False(effects.DisplayAwake);
            Assert.False(effects.MayJiggle);
        }

        [Fact]
        public void TheSystemRequestIsHeldWhileLockedEvenWithBothUserSwitchesOff()
        {
            // This is the case issue #14's A10 row previously called optional. The system
            // request is what "keep the computer awake" means, so it does not depend on the
            // display or pointer switches.
            var policy = new ActivityPolicy();
            SettingsV1 settings = Running(keepDisplayOn: false, jiggleMouse: false);

            DesiredEffects effects = policy.Evaluate(settings, Environment(session: SessionState.Locked));

            Assert.True(effects.SystemAwake);
            Assert.False(effects.DisplayAwake);
            Assert.False(effects.MayJiggle);
            Assert.Equal(StatusCode.LockedKeepingAwake, effects.Status);
        }

        [Fact]
        public void ManualModeIgnoresTheScheduleEntirely()
        {
            var policy = new ActivityPolicy();

            // A schedule that is saved and enabled, evaluated at midnight, well outside 08:00 to 17:00.
            SettingsV1 manual = Running(RunMode.Manual, scheduleEnabled: true);
            DateTime midnight = new DateTime(2026, 9, 9, 0, 30, 0, DateTimeKind.Utc);

            DesiredEffects effects = policy.Evaluate(manual, Environment(utcNow: midnight));

            Assert.Equal(StatusCode.RunningManual, effects.Status);
            Assert.True(effects.SystemAwake);
        }

        [Fact]
        public void OutsideTheWindowScheduledModeWaitsAndReportsTheNextStart()
        {
            var policy = new ActivityPolicy();
            DateTime midnight = new DateTime(2026, 9, 9, 0, 30, 0, DateTimeKind.Utc);

            DesiredEffects effects = policy.Evaluate(Running(scheduleEnabled: true), Environment(utcNow: midnight));

            Assert.Equal(StatusCode.WaitingForSchedule, effects.Status);
            Assert.False(effects.SystemAwake);
            Assert.Equal(new DateTime(2026, 9, 9, 8, 0, 0, DateTimeKind.Utc), effects.NextTransitionUtc);
            Assert.Equal(TransitionKind.Start, effects.NextTransitionKind);
        }

        [Fact]
        public void ALatchedInputFaultStopsJigglingWithoutClaimingTheAppIsFullyRunning()
        {
            var policy = new ActivityPolicy();

            DesiredEffects effects = policy.Evaluate(Running(), Environment(input: CapabilityState.Latched));

            Assert.True(effects.SystemAwake);
            Assert.True(effects.DisplayAwake);
            Assert.False(effects.MayJiggle);
            Assert.True(effects.InputRequestFailed);
        }

        [Fact]
        public void AFailedWakeRequestIsAnErrorRatherThanASuccessfulRun()
        {
            var policy = new ActivityPolicy();

            DesiredEffects effects = policy.Evaluate(Running(), Environment(wake: CapabilityState.Latched));

            Assert.Equal(StatusCode.Error, effects.Status);
            Assert.True(effects.WakeRequestFailed);
        }

        [Fact]
        public void AnInaccessibleInputDesktopSuppressesJigglingWithoutLatchingAFault()
        {
            var policy = new ActivityPolicy();

            DesiredEffects effects = policy.Evaluate(Running(), Environment(inputDesktopAvailable: false));

            Assert.False(effects.MayJiggle);
            Assert.False(effects.InputRequestFailed);
            Assert.Equal(StatusCode.RunningContinuous, effects.Status);
        }

        [Fact]
        public void UnreadableConfigurationLeavesTheAppInErrorRatherThanRunning()
        {
            var policy = new ActivityPolicy();

            DesiredEffects effects = policy.Evaluate(Running(), Environment(settingsAvailable: false));

            Assert.Equal(StatusCode.Error, effects.Status);
            Assert.False(effects.SystemAwake);
        }

        [Fact]
        public void StartNowSelectsManualWhileStartOnScheduleReturnsToScheduled()
        {
            SettingsV1 stopped = SettingsV1.CreateDefault();

            CommandOutcome startNow = ActivityCommandReducer.Reduce(CommandKind.StartNow, stopped);
            Assert.False(startNow.SettingsToPersist!.Stopped);
            Assert.Equal(RunMode.Manual, startNow.SettingsToPersist.RunMode);
            Assert.True(startNow.RequiresPersistenceBeforeEffects);

            CommandOutcome useSchedule = ActivityCommandReducer.Reduce(CommandKind.UseSchedule, startNow.SettingsToPersist);
            Assert.False(useSchedule.SettingsToPersist!.Stopped);
            Assert.Equal(RunMode.Scheduled, useSchedule.SettingsToPersist.RunMode);
        }

        [Fact]
        public void StopClearsEffectsBeforeItIsPersistedAndKeepsTheSavedMode()
        {
            SettingsV1 manual = ActivityCommandReducer.Reduce(CommandKind.StartNow, SettingsV1.CreateDefault()).SettingsToPersist!;

            CommandOutcome stop = ActivityCommandReducer.Reduce(CommandKind.Stop, manual);

            Assert.True(stop.ClearsEffectsImmediately);
            Assert.False(stop.RequiresPersistenceBeforeEffects);
            Assert.True(stop.SettingsToPersist!.Stopped);

            // The mode survives, so a later plain Start resumes Manual rather than guessing.
            Assert.Equal(RunMode.Manual, stop.SettingsToPersist.RunMode);
        }

        [Fact]
        public void ApplySettingsAndRetryChangeNoSavedIntent()
        {
            SettingsV1 stopped = SettingsV1.CreateDefault();

            Assert.Null(ActivityCommandReducer.Reduce(CommandKind.ApplySettings, stopped).SettingsToPersist);
            Assert.Null(ActivityCommandReducer.Reduce(CommandKind.Retry, stopped).SettingsToPersist);
            Assert.False(ActivityCommandReducer.Reduce(CommandKind.Retry, stopped).RequiresPersistenceBeforeEffects);
        }

        [Fact]
        public void ExitPreservesIntentAndEndsTheProcess()
        {
            SettingsV1 running = ActivityCommandReducer.Reduce(CommandKind.Start, SettingsV1.CreateDefault()).SettingsToPersist!;

            CommandOutcome exit = ActivityCommandReducer.Reduce(CommandKind.Exit, running);

            Assert.Null(exit.SettingsToPersist);
            Assert.True(exit.EndsProcess);
            Assert.True(exit.ClearsEffectsImmediately);
        }

        [Fact]
        public void AStaleGenerationCannotActivateAnything()
        {
            AppCommand queued = AppCommand.Create(CommandKind.Start, generation: 4);

            Assert.True(queued.IsCurrent(4));

            // A Stop, a mode change, a lock or a suspend increments the generation, and the
            // queued callback is then powerless.
            Assert.False(queued.IsCurrent(5));
        }
    }
}
