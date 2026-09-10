using System;
using System.Runtime.InteropServices;
using System.Threading;

using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Environment;
using MouseJiggler.Core.Settings;
using MouseJiggler.Windows.Interop;
using MouseJiggler.Windows.Power;
using MouseJiggler.Windows.Sessions;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The power and session adapters driven by fabricated Windows values rather than by this
    /// machine, from issue #6.
    /// </summary>
    /// <remarks>
    /// Nothing here reaches the operating system. Power classification is fed a hand-built
    /// SYSTEM_POWER_STATUS, the broadcast handlers are fed hand-built message payloads, and the
    /// keep-awake request is counted through a fake so that SetThreadExecutionState is never
    /// called: a test that took out a real wake request would hold the machine running the
    /// suite awake, which is the exact defect this product exists to avoid inflicting by
    /// accident.
    ///
    /// The two real <see cref="ExecutionStateController"/> tests below stay on the code paths
    /// that provably return before the native call, so they leave the machine untouched too.
    /// </remarks>
    public sealed class PowerSessionAdapterTests
    {
        /// <summary>BATTERY_FLAG_HIGH: more than 66 percent remaining.</summary>
        private const byte BatteryFlagHigh = 1;

        /// <summary>BATTERY_FLAG_CHARGING.</summary>
        private const byte BatteryFlagCharging = 8;

        /// <summary>BATTERY_FLAG_NO_SYSTEM_BATTERY: a desktop, or a laptop with the pack removed.</summary>
        private const byte BatteryFlagNoBattery = 128;

        /// <summary>BATTERY_FLAG_UNKNOWN, which Windows also uses for an unknown percentage.</summary>
        private const byte BatteryUnknown = 255;

        private static readonly DateTime Noon = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);

        private static readonly DateTime HalfPastMidnight = new DateTime(2026, 9, 9, 0, 30, 0, DateTimeKind.Utc);

        // ---- Power classification -------------------------------------------------

        [Theory]
        [InlineData(1, BatteryFlagHigh, (byte)80, PowerSource.External)]
        [InlineData(0, BatteryFlagHigh, (byte)80, PowerSource.Battery)]
        [InlineData(255, BatteryUnknown, BatteryUnknown, PowerSource.Unknown)]
        public void TheReportedLineStatusIsTheOnlyThingThatDecidesThePowerSource(
            byte acLineStatus,
            byte batteryFlag,
            byte batteryLifePercent,
            PowerSource expected)
        {
            Assert.Equal(expected, ClassifyReportedStatus(acLineStatus, batteryFlag, batteryLifePercent));
        }

        [Fact]
        public void EveryUndocumentedLineStatusIsUnknownRatherThanAssumedToBeExternalPower()
        {
            // Windows documents 0, 1 and 255, but the field is a byte and a driver may report
            // anything. Guessing "plugged in" for an unrecognised value would silently defeat
            // the pause-on-battery setting on exactly the machines that report oddly, so the
            // whole range outside 0 and 1 has to land on Unknown.
            for (int value = 0; value <= byte.MaxValue; value++)
            {
                PowerSource classified = PowerStatusSource.Classify((byte)value);

                PowerSource expected = value == 1
                    ? PowerSource.External
                    : value == 0 ? PowerSource.Battery : PowerSource.Unknown;

                Assert.Equal(expected, classified);
            }
        }

        [Fact]
        public void ABatteryReportingItselfAsAbsentIsNotEvidenceEitherWay()
        {
            // BatteryFlag 128 says no battery is installed. On a desktop that usually arrives
            // alongside ACLineStatus 1, but a dock or a UPS driver can report the same flag
            // while the line is offline, and that case must still count as running on battery.
            Assert.Equal(PowerSource.External, ClassifyReportedStatus(1, BatteryFlagNoBattery, BatteryUnknown));
            Assert.Equal(PowerSource.Battery, ClassifyReportedStatus(0, BatteryFlagNoBattery, BatteryUnknown));
            Assert.Equal(PowerSource.Unknown, ClassifyReportedStatus(255, BatteryFlagNoBattery, BatteryUnknown));
        }

        [Fact]
        public void AFullBatteryThatHasStoppedChargingIsStillExternalPower()
        {
            // A laptop left plugged in settles at 100 percent with the charging bit clear.
            // Reading "not charging" as "unplugged" would pause the app on a machine sitting on
            // mains power, which is the complaint that made this rule explicit.
            Assert.Equal(0, BatteryFlagHigh & BatteryFlagCharging);

            Assert.Equal(PowerSource.External, ClassifyReportedStatus(1, BatteryFlagHigh, 100));

            // The same battery reading with the line offline is genuinely on battery.
            Assert.Equal(PowerSource.Battery, ClassifyReportedStatus(0, BatteryFlagHigh, 100));
        }

        [Fact]
        public void AChargingBatteryOnAnOfflineLineIsStillReportedAsBattery()
        {
            // The charging bit describes the battery, not the machine's power source, and a
            // driver can leave it set briefly after the cable is pulled.
            Assert.Equal(
                PowerSource.Battery,
                ClassifyReportedStatus(0, (byte)(BatteryFlagHigh | BatteryFlagCharging), 55));
        }

        [Fact]
        public void AFailedPowerQueryCarriesNoValueAndNamesItsFault()
        {
            // This is the shape the adapter returns when GetSystemPowerStatus fails. The value
            // side has to fall back to Unknown rather than to whatever the enum's zero happens
            // to be, because a caller that reads Value without checking Succeeded would
            // otherwise be told the machine is on mains power.
            OperationResult<PowerSource> failure =
                OperationResult<PowerSource>.Failure(FaultSubsystem.PowerQuery, "power.queryFailed", 6);

            Assert.False(failure.Succeeded);
            Assert.Equal(PowerSource.Unknown, failure.Value);
            Assert.Equal(FaultSubsystem.PowerQuery, failure.Outcome.Subsystem);
            Assert.Equal("power.queryFailed", failure.Outcome.Code);
            Assert.Equal(6, failure.Outcome.NativeErrorCode);
            Assert.True(failure.Outcome.Retryable);
        }

        [Fact]
        public void AnUnreadablePowerStatusPausesTheAppAndHoldsNoWakeRequest()
        {
            // An outright API failure reaches the policy as Unknown. With the user's
            // external-power-only setting on, that has to pause and release rather than carry
            // on from the last good reading.
            var policy = new ActivityPolicy();
            var controller = new CountingExecutionStateController();

            DesiredEffects onPower = ApplyOnce(policy, Running(), Snapshot(power: PowerSource.External), controller);
            Assert.Equal(StatusCode.RunningContinuous, onPower.Status);
            Assert.Equal(1, controller.WakeAcquisitionCount);

            DesiredEffects unreadable = ApplyOnce(policy, Running(), Snapshot(power: PowerSource.Unknown, tickSeconds: 1), controller);

            Assert.Equal(StatusCode.PowerStatusUnavailable, unreadable.Status);
            Assert.False(unreadable.SystemAwake);
            Assert.False(controller.SystemAwakeApplied);
            Assert.False(controller.HasOutstandingRequest);
            Assert.Equal(1, controller.WakeReleaseCount);
        }

        // ---- Power broadcasts -----------------------------------------------------

        [Fact]
        public void APowerStatusBroadcastAsksForARereadRatherThanCarryingTheVerdict()
        {
            using (var source = new PowerStatusSource())
            {
                int changes = 0;
                source.Changed += (_, __) => changes++;

                bool handled = source.HandleMessage(
                    NativeMethods.WM_POWERBROADCAST,
                    new IntPtr(NativeMethods.PBT_APMPOWERSTATUSCHANGE),
                    IntPtr.Zero);

                Assert.True(handled);
                Assert.Equal(1, changes);
            }
        }

        [Theory]
        [InlineData(NativeMethods.PBT_APMRESUMEAUTOMATIC)]
        [InlineData(NativeMethods.PBT_APMRESUMESUSPEND)]
        public void ResumingFromSleepReRaisesTheChangeSoThePowerSourceIsReexamined(int eventType)
        {
            // The cable can be pulled while the machine is asleep, so a resume has to count as
            // a power event and not only as a scheduling one.
            using (var source = new PowerStatusSource())
            {
                int changes = 0;
                source.Changed += (_, __) => changes++;

                Assert.True(source.HandleMessage(NativeMethods.WM_POWERBROADCAST, new IntPtr(eventType), IntPtr.Zero));
                Assert.Equal(1, changes);
            }
        }

        [Fact]
        public void ASuspendBroadcastIsLeftForTheOwnerToHandle()
        {
            // Suspend clears every effect, which is the coordinator's job. If this adapter
            // claimed the message the owner would never see it, and the wake request would
            // survive into sleep.
            using (var source = new PowerStatusSource())
            {
                int changes = 0;
                source.Changed += (_, __) => changes++;

                bool handled = source.HandleMessage(
                    NativeMethods.WM_POWERBROADCAST,
                    new IntPtr(NativeMethods.PBT_APMSUSPEND),
                    IntPtr.Zero);

                Assert.False(handled);
                Assert.Equal(0, changes);
            }
        }

        [Fact]
        public void AnUnrelatedWindowMessageIsIgnoredByThePowerAdapter()
        {
            using (var source = new PowerStatusSource())
            {
                int changes = 0;
                source.Changed += (_, __) => changes++;

                Assert.False(source.HandleMessage(NativeMethods.WM_TIMECHANGE, IntPtr.Zero, IntPtr.Zero));
                Assert.Equal(0, changes);
            }
        }

        [Fact]
        public void OnlyTheExternalPowerSettingRaisesAChange()
        {
            using (var source = new PowerStatusSource())
            {
                int changes = 0;
                source.Changed += (_, __) => changes++;

                // A fabricated GUID_ACDC_POWER_SOURCE payload: the one setting subscribed to.
                Assert.True(RaisePowerSettingChange(source, NativeMethods.GuidAcdcPowerSource, dataLength: 4, data: 1));
                Assert.Equal(1, changes);

                // Another power setting arriving on the same window is consumed, but it must
                // not be mistaken for a power-source change.
                Assert.True(RaisePowerSettingChange(source, Guid.Empty, dataLength: 4, data: 1));
                Assert.Equal(1, changes);
            }
        }

        [Fact]
        public void AShortPowerSettingPayloadIsRejectedRatherThanReadPastItsLength()
        {
            // The declared length is the only thing standing between this handler and a read
            // beyond the message buffer, so a truncated payload is dropped even when the
            // setting GUID is the one being watched.
            using (var source = new PowerStatusSource())
            {
                int changes = 0;
                source.Changed += (_, __) => changes++;

                Assert.True(RaisePowerSettingChange(source, NativeMethods.GuidAcdcPowerSource, dataLength: 1, data: 1));
                Assert.Equal(0, changes);
            }
        }

        [Fact]
        public void APowerSettingChangeWithNoPayloadIsIgnoredRatherThanDereferenced()
        {
            using (var source = new PowerStatusSource())
            {
                int changes = 0;
                source.Changed += (_, __) => changes++;

                // A null lParam must not be marshalled. Declining it is what keeps this from
                // faulting the message loop that owns the tray icon.
                Assert.False(source.HandleMessage(
                    NativeMethods.WM_POWERBROADCAST,
                    new IntPtr(NativeMethods.PBT_POWERSETTINGCHANGE),
                    IntPtr.Zero));

                Assert.Equal(0, changes);
            }
        }

        // ---- Session notifications ------------------------------------------------

        [Fact]
        public void LockingAndUnlockingTakeEffectOnTheNotificationRatherThanAtTheNextPoll()
        {
            using (var source = new SessionStatusSource())
            {
                int changes = 0;
                source.Changed += (_, __) => changes++;

                // Waiting for a poll would let the pointer move on a machine that is already
                // locked, so the notified state changes as the message is handled.
                Assert.True(source.HandleMessage(NativeMethods.WM_WTSSESSION_CHANGE, new IntPtr(NativeMethods.WTS_SESSION_LOCK)));
                Assert.Equal(SessionState.Locked, source.NotifiedState);

                Assert.True(source.HandleMessage(NativeMethods.WM_WTSSESSION_CHANGE, new IntPtr(NativeMethods.WTS_SESSION_UNLOCK)));
                Assert.Equal(SessionState.ActiveUnlocked, source.NotifiedState);

                Assert.Equal(2, changes);
            }
        }

        [Theory]
        [InlineData(NativeMethods.WTS_CONSOLE_DISCONNECT)]
        [InlineData(NativeMethods.WTS_REMOTE_DISCONNECT)]
        [InlineData(NativeMethods.WTS_SESSION_LOGOFF)]
        public void EveryFormOfLeavingTheSessionIsTreatedAsDisconnected(int wParam)
        {
            // Fast user switching, a dropped remote session and a sign-out all mean this
            // session is no longer the one at the keyboard.
            using (var source = new SessionStatusSource())
            {
                Assert.True(source.HandleMessage(NativeMethods.WM_WTSSESSION_CHANGE, new IntPtr(wParam)));
                Assert.Equal(SessionState.Disconnected, source.NotifiedState);
            }
        }

        [Theory]
        [InlineData(NativeMethods.WTS_CONSOLE_CONNECT)]
        [InlineData(NativeMethods.WTS_REMOTE_CONNECT)]
        public void ConnectingReturnsToUnknownBecauseAConnectedSessionMayStillBeLocked(int wParam)
        {
            using (var source = new SessionStatusSource())
            {
                Assert.True(source.HandleMessage(NativeMethods.WM_WTSSESSION_CHANGE, new IntPtr(NativeMethods.WTS_SESSION_LOCK)));

                Assert.True(source.HandleMessage(NativeMethods.WM_WTSSESSION_CHANGE, new IntPtr(wParam)));

                // Not ActiveUnlocked: reconnecting to a locked session is ordinary, and
                // assuming otherwise would move the pointer on a lock screen.
                Assert.Equal(SessionState.Unknown, source.NotifiedState);
            }
        }

        [Fact]
        public void AnUnrecognisedSessionCodeLeavesTheLastKnownStateAlone()
        {
            using (var source = new SessionStatusSource())
            {
                Assert.True(source.HandleMessage(NativeMethods.WM_WTSSESSION_CHANGE, new IntPtr(NativeMethods.WTS_SESSION_LOCK)));

                int changes = 0;
                source.Changed += (_, __) => changes++;

                // WTS_SESSION_REMOTE_CONTROL and its neighbours say nothing about the lock
                // state, so acting on them would throw away a Locked reading for no reason.
                Assert.False(source.HandleMessage(NativeMethods.WM_WTSSESSION_CHANGE, new IntPtr(0x5)));

                Assert.Equal(SessionState.Locked, source.NotifiedState);
                Assert.Equal(0, changes);
            }
        }

        [Fact]
        public void AnUnrelatedWindowMessageIsIgnoredByTheSessionAdapter()
        {
            // The lock code and a power event code collide numerically, so the message id is
            // what has to be checked first.
            using (var source = new SessionStatusSource())
            {
                Assert.False(source.HandleMessage(NativeMethods.WM_POWERBROADCAST, new IntPtr(NativeMethods.WTS_SESSION_LOCK)));
                Assert.Equal(SessionState.Unknown, source.NotifiedState);
            }
        }

        [Fact]
        public void AFreshSessionSourceStartsUnknownRatherThanUnlocked()
        {
            // A failed query falls back to this notified state, so its starting value is what
            // the app believes before anything has been observed. Unknown suppresses effects;
            // ActiveUnlocked would have let a locked machine be jiggled at startup.
            using (var source = new SessionStatusSource())
            {
                Assert.Equal(SessionState.Unknown, source.NotifiedState);
            }

            OperationResult<SessionState> failure =
                OperationResult<SessionState>.Failure(FaultSubsystem.SessionQuery, "session.queryFailed", 5);

            Assert.False(failure.Succeeded);
            Assert.Equal(SessionState.Unknown, failure.Value);
            Assert.Equal(FaultSubsystem.SessionQuery, failure.Outcome.Subsystem);
        }

        // ---- Registration and disposal --------------------------------------------

        [Fact]
        public void NeitherSourceRegistersWithoutAWindowToRegisterAgainst()
        {
            // A zero handle is a wiring mistake. Registering anyway would hand the notification
            // to no one and leave the app quietly relying on polling it did not know it needed.
            using (var power = new PowerStatusSource())
            using (var session = new SessionStatusSource())
            {
                Assert.Throws<ArgumentException>(() => power.Register(IntPtr.Zero));
                Assert.Throws<ArgumentException>(() => session.Register(IntPtr.Zero));

                Assert.True(power.RequiresPolling);
                Assert.True(session.RequiresPolling);
            }
        }

        [Fact]
        public void DisposingEitherSourceTwiceLeavesNothingToUnregisterASecondTime()
        {
            // Disposal unregisters what it holds. Doing that twice would unregister a handle
            // that is no longer ours, and registering after disposal would leak a subscription
            // with nothing left to release it.
            var power = new PowerStatusSource();
            var session = new SessionStatusSource();

            power.Dispose();
            power.Dispose();
            session.Dispose();
            session.Dispose();

            Assert.Throws<ObjectDisposedException>(() => power.Register(new IntPtr(1)));
            Assert.Throws<ObjectDisposedException>(() => session.Register(new IntPtr(1)));
        }

        // ---- The keep-awake request -----------------------------------------------

        [Fact]
        public void AThousandIdenticalSnapshotsAcquireTheWakeRequestExactlyOnce()
        {
            // The heartbeat evaluates every second. If an unchanged mask reached the API each
            // time, a day of running would be tens of thousands of redundant calls, and any
            // flap in the policy's answer would show up here as a second acquisition.
            var policy = new ActivityPolicy();
            var controller = new CountingExecutionStateController();
            SettingsV1 settings = Running();

            for (int tick = 0; tick < 1000; tick++)
            {
                DesiredEffects effects = ApplyOnce(policy, settings, Snapshot(tickSeconds: tick), controller);
                Assert.Equal(StatusCode.RunningContinuous, effects.Status);
            }

            Assert.Equal(1000, controller.ApplyCallCount);
            Assert.Equal(1, controller.WakeAcquisitionCount);
            Assert.Equal(0, controller.WakeReleaseCount);
            Assert.True(controller.SystemAwakeApplied);
            Assert.True(controller.DisplayAwakeApplied);

            // One request outstanding, and releasing it leaves none: the acquisitions and the
            // releases balance, so nothing stays registered behind the app.
            Assert.True(controller.HasOutstandingRequest);
            Assert.True(controller.Release().Succeeded);
            Assert.False(controller.HasOutstandingRequest);
            Assert.Equal(controller.WakeAcquisitionCount, controller.WakeReleaseCount);
        }

        [Fact]
        public void AChangedDisplayMaskIsTheOneThingThatReacquiresOnAnOtherwiseSteadyRun()
        {
            // Turning off "keep the display on" while running must reach the API, or the
            // display stays awake for as long as the app does.
            var policy = new ActivityPolicy();
            var controller = new CountingExecutionStateController();

            for (int tick = 0; tick < 100; tick++)
            {
                ApplyOnce(policy, Running(keepDisplayOn: true), Snapshot(tickSeconds: tick), controller);
            }

            Assert.Equal(1, controller.WakeAcquisitionCount);
            Assert.True(controller.DisplayAwakeApplied);

            ApplyOnce(policy, Running(keepDisplayOn: false), Snapshot(tickSeconds: 100), controller);

            Assert.Equal(2, controller.WakeAcquisitionCount);
            Assert.True(controller.SystemAwakeApplied);
            Assert.False(controller.DisplayAwakeApplied);

            // The system request was never dropped on the way, so the machine got no window in
            // which it could sleep.
            Assert.Equal(0, controller.WakeReleaseCount);
        }

        [Fact]
        public void PowerChangingDuringManualOperationReleasesOnceAndReacquiresOnce()
        {
            // Manual mode ignores the schedule, so this runs at half past midnight with an
            // enabled 08:00 to 17:00 window: only the power change may move it.
            var policy = new ActivityPolicy();
            var controller = new CountingExecutionStateController();
            SettingsV1 settings = Running(RunMode.Manual, scheduleEnabled: true);

            RunTicks(policy, settings, controller, PowerSource.External, HalfPastMidnight, 0, StatusCode.RunningManual);
            Assert.Equal(1, controller.WakeAcquisitionCount);

            RunTicks(policy, settings, controller, PowerSource.Battery, HalfPastMidnight, 1000, StatusCode.PausedOnBattery);
            Assert.Equal(1, controller.WakeReleaseCount);
            Assert.False(controller.SystemAwakeApplied);

            RunTicks(policy, settings, controller, PowerSource.External, HalfPastMidnight, 2000, StatusCode.RunningManual);

            // Three thousand ticks, one unplug and one replug: exactly one release and one
            // fresh acquisition, and the request standing at the end is the new one.
            Assert.Equal(2, controller.WakeAcquisitionCount);
            Assert.Equal(1, controller.WakeReleaseCount);
            Assert.True(controller.HasOutstandingRequest);
            Assert.True(controller.SystemAwakeApplied);
        }

        [Fact]
        public void PowerChangingDuringScheduledOperationFollowsTheSameReleaseAndReacquirePath()
        {
            // Noon sits inside the enabled 08:00 to 17:00 window, and fifty minutes of ticks
            // stay inside it, so the schedule is constant and the power source is the variable.
            var policy = new ActivityPolicy();
            var controller = new CountingExecutionStateController();
            SettingsV1 settings = Running(RunMode.Scheduled, scheduleEnabled: true);

            RunTicks(policy, settings, controller, PowerSource.External, Noon, 0, StatusCode.RunningScheduled);
            Assert.Equal(1, controller.WakeAcquisitionCount);

            RunTicks(policy, settings, controller, PowerSource.Battery, Noon, 1000, StatusCode.PausedOnBattery);
            Assert.False(controller.HasOutstandingRequest);

            RunTicks(policy, settings, controller, PowerSource.External, Noon, 2000, StatusCode.RunningScheduled);

            Assert.Equal(2, controller.WakeAcquisitionCount);
            Assert.Equal(1, controller.WakeReleaseCount);
            Assert.True(controller.SystemAwakeApplied);
        }

        [Fact]
        public void ADroppedPowerReadingPausesJustAsBatteryDoesButSaysSoDifferently()
        {
            // Both pause, and the difference matters to the user: one is a laptop on battery,
            // the other is a machine whose power status could not be read at all.
            var policy = new ActivityPolicy();
            var controller = new CountingExecutionStateController();
            SettingsV1 settings = Running();

            ApplyOnce(policy, settings, Snapshot(power: PowerSource.External), controller);

            DesiredEffects battery = ApplyOnce(policy, settings, Snapshot(power: PowerSource.Battery, tickSeconds: 1), controller);
            Assert.Equal(StatusCode.PausedOnBattery, battery.Status);

            ApplyOnce(policy, settings, Snapshot(power: PowerSource.External, tickSeconds: 2), controller);

            DesiredEffects unknown = ApplyOnce(policy, settings, Snapshot(power: PowerSource.Unknown, tickSeconds: 3), controller);
            Assert.Equal(StatusCode.PowerStatusUnavailable, unknown.Status);

            Assert.Equal(2, controller.WakeAcquisitionCount);
            Assert.Equal(2, controller.WakeReleaseCount);
            Assert.False(controller.HasOutstandingRequest);
        }

        [Fact]
        public void ThePowerGateOutranksTheScheduleSoAnOutOfWindowUnplugStillReportsTheBattery()
        {
            // Both conditions block. The user is told about the one they can act on rather than
            // about a schedule that would not have started the app anyway.
            var policy = new ActivityPolicy();
            SettingsV1 settings = Running(RunMode.Scheduled, scheduleEnabled: true);

            DesiredEffects effects = policy.Evaluate(settings, Snapshot(power: PowerSource.Battery, utcNow: HalfPastMidnight));

            Assert.Equal(StatusCode.PausedOnBattery, effects.Status);
            Assert.False(effects.SystemAwake);
        }

        [Fact]
        public void ClearingPauseOnBatteryKeepsTheRequestAcquiredAcrossAPowerChange()
        {
            // With the setting off, unplugging is not an event at all, so the request must not
            // be dropped and taken out again: that gap is a window in which the machine sleeps.
            var policy = new ActivityPolicy();
            var controller = new CountingExecutionStateController();
            SettingsV1 settings = Running(pauseOnBattery: false);

            RunTicks(policy, settings, controller, PowerSource.External, Noon, 0, StatusCode.RunningContinuous);
            RunTicks(policy, settings, controller, PowerSource.Battery, Noon, 1000, StatusCode.RunningContinuous);
            RunTicks(policy, settings, controller, PowerSource.Unknown, Noon, 2000, StatusCode.RunningContinuous);

            Assert.Equal(1, controller.WakeAcquisitionCount);
            Assert.Equal(0, controller.WakeReleaseCount);
            Assert.True(controller.HasOutstandingRequest);
        }

        [Fact]
        public void ALockedSessionOnBatteryStillPausesRatherThanHoldingTheMachineAwake()
        {
            // Locked normally keeps the computer awake. The power gate is checked first, so an
            // unplugged laptop that is locked does not quietly stay awake on its battery.
            var policy = new ActivityPolicy();
            var controller = new CountingExecutionStateController();

            DesiredEffects effects = ApplyOnce(
                policy,
                Running(),
                Snapshot(power: PowerSource.Battery, session: SessionState.Locked),
                controller);

            Assert.Equal(StatusCode.PausedOnBattery, effects.Status);
            Assert.False(controller.HasOutstandingRequest);
            Assert.Equal(0, controller.WakeAcquisitionCount);
        }

        [Fact]
        public void ARefusedWakeRequestIsNeverRecordedAsApplied()
        {
            // The request is scoped to the thread that made it, so the controller refuses to be
            // driven from anywhere else. Ownership is claimed here on a second thread through
            // the release path, which returns before the native call, and the acquisition is
            // then attempted from the test thread: nothing reaches SetThreadExecutionState.
            using (var controller = new ExecutionStateController())
            {
                OperationResult? claimed = null;

                // A dedicated thread, not Task.Run: waiting on a pool task can inline it onto
                // this very thread, which would defeat the check under test.
                var owner = new Thread(() => claimed = controller.Apply(systemAwake: false, displayAwake: false));
                owner.Start();
                owner.Join();

                Assert.NotNull(claimed);
                Assert.True(claimed!.Succeeded);

                OperationResult refused = controller.Apply(systemAwake: true, displayAwake: true);

                Assert.False(refused.Succeeded);
                Assert.Equal("wake.wrongThread", refused.Code);
                Assert.Equal(FaultSubsystem.WakeAcquire, refused.Subsystem);
                Assert.False(refused.Retryable);

                // A refusal must not leave the app believing a request is standing.
                Assert.False(controller.SystemAwakeApplied);
                Assert.False(controller.DisplayAwakeApplied);
                Assert.False(controller.SystemAwakeRequested);
                Assert.False(controller.DisplayAwakeRequested);
            }
        }

        [Fact]
        public void AControllerThatHoldsNothingReleasesWithoutEverCallingTheApi()
        {
            // A not-awake mask goes down the release path. With no outstanding request that has
            // to stay a no-op, because the heartbeat runs it every second for as long as the
            // app is stopped or paused.
            using (var controller = new ExecutionStateController())
            {
                for (int tick = 0; tick < 1000; tick++)
                {
                    Assert.True(controller.Apply(systemAwake: false, displayAwake: false).Succeeded);
                }

                Assert.False(controller.SystemAwakeApplied);
                Assert.False(controller.DisplayAwakeApplied);
                Assert.False(controller.SystemAwakeRequested);
                Assert.False(controller.DisplayAwakeRequested);
            }
        }

        // ---- Helpers --------------------------------------------------------------

        /// <summary>
        /// Classifies a hand-built SYSTEM_POWER_STATUS, so each case reads as the machine state
        /// it stands for rather than as a bare byte.
        /// </summary>
        private static PowerSource ClassifyReportedStatus(byte acLineStatus, byte batteryFlag, byte batteryLifePercent)
        {
            var status = new NativeMethods.SYSTEM_POWER_STATUS
            {
                ACLineStatus = acLineStatus,
                BatteryFlag = batteryFlag,
                BatteryLifePercent = batteryLifePercent,
                SystemStatusFlag = 0,
                BatteryLifeTime = uint.MaxValue,
                BatteryFullLifeTime = uint.MaxValue,
            };

            return PowerStatusSource.Classify(status.ACLineStatus);
        }

        /// <summary>
        /// Delivers a fabricated WM_POWERBROADCAST power-setting payload. The buffer is owned
        /// here and freed straight afterwards, which is what Windows does to the real one.
        /// </summary>
        private static bool RaisePowerSettingChange(PowerStatusSource source, Guid setting, uint dataLength, byte data)
        {
            var payload = new NativeMethods.POWERBROADCAST_SETTING
            {
                PowerSetting = setting,
                DataLength = dataLength,
                Data = data,
            };

            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeMethods.POWERBROADCAST_SETTING)));

            try
            {
                Marshal.StructureToPtr(payload, buffer, false);

                return source.HandleMessage(
                    NativeMethods.WM_POWERBROADCAST,
                    new IntPtr(NativeMethods.PBT_POWERSETTINGCHANGE),
                    buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>Settings for an app the user has started. Jiggling is off: this file is about power.</summary>
        private static SettingsV1 Running(
            RunMode mode = RunMode.Scheduled,
            bool scheduleEnabled = false,
            bool pauseOnBattery = true,
            bool keepDisplayOn = true)
        {
            SettingsV1 d = SettingsV1.CreateDefault();

            return new SettingsV1(
                d.SchemaVersion, d.Revision, stopped: false, mode,
                scheduleEnabled, d.ScheduleStart, d.ScheduleEnd, d.DayMask,
                pauseOnBattery, keepDisplayOn, jiggleMouse: false,
                d.IntervalSeconds, d.DiagnosticLogging, d.StartupInitialized, d.FirstRunCompleted);
        }

        private static EnvironmentSnapshot Snapshot(
            PowerSource power = PowerSource.External,
            SessionState session = SessionState.ActiveUnlocked,
            DateTime? utcNow = null,
            int tickSeconds = 0)
        {
            // One heartbeat per second, on both clocks, so a long run is a realistic one rather
            // than the same instant repeated.
            DateTime start = utcNow ?? Noon;

            return new EnvironmentSnapshot(
                start.AddSeconds(tickSeconds),
                TimeZoneInfo.Utc,
                1000L + (tickSeconds * 1000L),
                power,
                session,
                suspended: false,
                exiting: false,
                inputDesktopAvailable: true,
                wakeCapability: CapabilityState.Ok,
                inputCapability: CapabilityState.Ok,
                settingsAvailable: true);
        }

        /// <summary>Evaluates once and applies the answer to the fake.</summary>
        /// <remarks>
        /// This is the whole of the coordinator's execution-state rule: ask for the mask when
        /// the policy wants the machine awake, release when it does not. The coordinator itself
        /// lives in the App assembly, so the rule is restated here rather than reached for, and
        /// the fake is what stands in for the API underneath it.
        /// </remarks>
        private static DesiredEffects ApplyOnce(
            ActivityPolicy policy,
            SettingsV1 settings,
            EnvironmentSnapshot snapshot,
            CountingExecutionStateController controller)
        {
            DesiredEffects effects = policy.Evaluate(settings, snapshot);

            if (effects.SystemAwake)
            {
                controller.Apply(effects.SystemAwake, effects.DisplayAwake);
            }
            else
            {
                controller.Release();
            }

            return effects;
        }

        /// <summary>A thousand consecutive heartbeats with one unchanging environment.</summary>
        private static void RunTicks(
            ActivityPolicy policy,
            SettingsV1 settings,
            CountingExecutionStateController controller,
            PowerSource power,
            DateTime start,
            int firstTickSeconds,
            StatusCode expected)
        {
            for (int tick = 0; tick < 1000; tick++)
            {
                DesiredEffects effects = ApplyOnce(
                    policy,
                    settings,
                    Snapshot(power: power, utcNow: start, tickSeconds: firstTickSeconds + tick),
                    controller);

                Assert.Equal(expected, effects.Status);
            }
        }

        /// <summary>
        /// A stand-in for the real controller that counts the calls which would reach
        /// SetThreadExecutionState, and applies the same rule: an unchanged mask is a no-op.
        /// </summary>
        private sealed class CountingExecutionStateController : IExecutionStateController
        {
            private bool _outstanding;

            /// <summary>Every Apply, including the ones the unchanged-mask rule swallows.</summary>
            public int ApplyCallCount { get; private set; }

            /// <summary>Calls that would have reached the API to take out a request.</summary>
            public int WakeAcquisitionCount { get; private set; }

            /// <summary>Calls that would have reached the API to give one back.</summary>
            public int WakeReleaseCount { get; private set; }

            public bool SystemAwakeApplied { get; private set; }

            public bool DisplayAwakeApplied { get; private set; }

            /// <summary>True while a request stands. It must be false once the app is finished.</summary>
            public bool HasOutstandingRequest => _outstanding;

            public OperationResult Apply(bool systemAwake, bool displayAwake)
            {
                ApplyCallCount++;

                if (!systemAwake)
                {
                    return Release();
                }

                if (_outstanding && SystemAwakeApplied && DisplayAwakeApplied == displayAwake)
                {
                    return OperationResult.Success();
                }

                WakeAcquisitionCount++;
                _outstanding = true;
                SystemAwakeApplied = true;
                DisplayAwakeApplied = displayAwake;
                return OperationResult.Success();
            }

            public OperationResult Release()
            {
                if (_outstanding)
                {
                    WakeReleaseCount++;
                    _outstanding = false;
                }

                SystemAwakeApplied = false;
                DisplayAwakeApplied = false;
                return OperationResult.Success();
            }

            public void Dispose()
            {
                SystemAwakeApplied = false;
                DisplayAwakeApplied = false;
            }
        }
    }
}
