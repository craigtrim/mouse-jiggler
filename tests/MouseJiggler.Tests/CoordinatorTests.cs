using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MouseJiggler.App.Runtime;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Commands;
using MouseJiggler.Core.Environment;
using MouseJiggler.Core.Settings;
using MouseJiggler.Tests.Fakes;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The coordinator driven directly, with a clock the test moves by hand.
    /// </summary>
    /// <remarks>
    /// These are the tests the concrete Windows dependencies used to make impossible. The
    /// retry cadence is the reason they matter: a backoff that has quietly become a per-tick
    /// loop still recovers, still reports the same status, and differs from a correct one only
    /// in how many times it calls the native API. Counting those calls is the only way to see it.
    /// </remarks>
    public sealed class CoordinatorTests
    {
        private const int IntervalSeconds = 30;

        /// <summary>Everything the coordinator needs, wired to fakes and disposed together.</summary>
        private sealed class Harness : IDisposable
        {
            public Harness(bool stopped = false, bool jiggleMouse = true)
            {
                Settings = new SettingsV1(
                    schemaVersion: 1,
                    revision: 1,
                    stopped: stopped,
                    runMode: RunMode.Manual,
                    scheduleEnabled: false,
                    scheduleStart: "08:00",
                    scheduleEnd: "17:00",
                    dayMask: SettingsV1.AllDaysMask,
                    pauseOnBattery: true,
                    keepDisplayOn: true,
                    jiggleMouse: jiggleMouse,
                    intervalSeconds: IntervalSeconds,
                    diagnosticLogging: false,
                    startupInitialized: true,
                    firstRunCompleted: true);

                Store = new FakeSettingsStore(Settings);

                Coordinator = new ActivityCoordinator(
                    Store,
                    Settings,
                    Environment,
                    ExecutionState,
                    Idle,
                    Jiggler,
                    Clock,
                    Desktop,
                    new ImmediateInvoker(),
                    Diagnostics);

                Coordinator.StopPersistenceWarningChanged += (_, warn) => Warnings.Add(warn);
            }

            public SettingsV1 Settings { get; }

            public FakeSettingsStore Store { get; }

            public FakePowerSessionSource Environment { get; } = new FakePowerSessionSource();

            public FakeInputDesktopProbe Desktop { get; } = new FakeInputDesktopProbe();

            public FakeExecutionStateController ExecutionState { get; } = new FakeExecutionStateController();

            public FakeIdleInputSource Idle { get; } = new FakeIdleInputSource();

            public FakeMouseJiggler Jiggler { get; } = new FakeMouseJiggler();

            public FakeClock Clock { get; } = new FakeClock();

            public RecordingDiagnosticSink Diagnostics { get; } = new RecordingDiagnosticSink();

            public ActivityCoordinator Coordinator { get; }

            public List<bool> Warnings { get; } = new List<bool>();

            /// <summary>Moves the clock on and evaluates, which is what a heartbeat tick does.</summary>
            public void Tick(TimeSpan amount)
            {
                Clock.Advance(amount);
                Coordinator.Evaluate();
            }

            public void Dispose() => Coordinator.Dispose();
        }

        [Fact]
        public void AFailedWakeRequestRetriesOnTheDocumentedBackoffAndNotOnEveryTick()
        {
            using (var harness = new Harness())
            {
                harness.ExecutionState.FailApply = true;
                harness.Coordinator.Start();

                Assert.Equal(1, harness.ExecutionState.ApplyCalls);

                // Nine hundred milliseconds is not one second. Nothing may be retried yet, and
                // evaluating repeatedly must not turn the backoff into a per-tick loop.
                for (int i = 0; i < 5; i++)
                {
                    harness.Tick(TimeSpan.FromMilliseconds(180));
                }

                Assert.Equal(1, harness.ExecutionState.ApplyCalls);

                harness.Tick(TimeSpan.FromMilliseconds(100));
                Assert.Equal(2, harness.ExecutionState.ApplyCalls);

                harness.Tick(TimeSpan.FromMilliseconds(4900));
                Assert.Equal(2, harness.ExecutionState.ApplyCalls);

                harness.Tick(TimeSpan.FromMilliseconds(100));
                Assert.Equal(3, harness.ExecutionState.ApplyCalls);

                harness.Tick(TimeSpan.FromSeconds(29));
                Assert.Equal(3, harness.ExecutionState.ApplyCalls);

                harness.Tick(TimeSpan.FromSeconds(1));
                Assert.Equal(4, harness.ExecutionState.ApplyCalls);

                // The schedule repeats at thirty seconds rather than escalating or giving up.
                harness.Tick(TimeSpan.FromSeconds(30));
                Assert.Equal(5, harness.ExecutionState.ApplyCalls);

                Assert.Equal(StatusCode.Error, harness.Coordinator.LastEffects.Status);
                Assert.True(harness.Coordinator.LastEffects.WakeRequestFailed);
            }
        }

        [Fact]
        public void ASucceedingWakeRequestClearsTheBackoffSoTheNextFailureStartsAtOneSecondAgain()
        {
            using (var harness = new Harness())
            {
                harness.ExecutionState.FailApply = true;
                harness.Coordinator.Start();

                harness.Tick(TimeSpan.FromSeconds(1));
                harness.Tick(TimeSpan.FromSeconds(5));
                Assert.Equal(3, harness.ExecutionState.ApplyCalls);

                harness.ExecutionState.FailApply = false;
                harness.Tick(TimeSpan.FromSeconds(30));
                Assert.Equal(4, harness.ExecutionState.ApplyCalls);
                Assert.True(harness.ExecutionState.SystemAwakeApplied);

                // Back to the start of the schedule, not resuming at thirty seconds.
                harness.ExecutionState.FailApply = true;
                harness.Tick(TimeSpan.FromSeconds(1));
                Assert.Equal(5, harness.ExecutionState.ApplyCalls);

                harness.Tick(TimeSpan.FromMilliseconds(900));
                Assert.Equal(5, harness.ExecutionState.ApplyCalls);

                harness.Tick(TimeSpan.FromMilliseconds(100));
                Assert.Equal(6, harness.ExecutionState.ApplyCalls);
            }
        }

        [Fact]
        public void AFailedReleaseHonoursTheSameBackoffRatherThanSpinningEverySecond()
        {
            using (var harness = new Harness(stopped: true))
            {
                harness.ExecutionState.FailRelease = true;
                harness.Coordinator.Start();

                int afterStart = harness.ExecutionState.ReleaseCalls;
                Assert.True(afterStart >= 1);

                for (int i = 0; i < 5; i++)
                {
                    harness.Coordinator.Evaluate();
                }

                Assert.Equal(afterStart, harness.ExecutionState.ReleaseCalls);

                harness.Tick(TimeSpan.FromSeconds(1));
                Assert.Equal(afterStart + 1, harness.ExecutionState.ReleaseCalls);

                // A release that starts working clears the fault: the request is genuinely gone.
                harness.ExecutionState.FailRelease = false;
                harness.Tick(TimeSpan.FromSeconds(5));
                Assert.Equal(afterStart + 2, harness.ExecutionState.ReleaseCalls);

                harness.Coordinator.Evaluate();
                Assert.False(harness.Coordinator.LastEffects.WakeRequestFailed);
            }
        }

        [Fact]
        public async Task ExitReleasesEverythingAndNeverRequestsTheWakeStateAgain()
        {
            using (var harness = new Harness())
            {
                harness.ExecutionState.FailApply = true;
                harness.Coordinator.Start();
                Assert.Equal(1, harness.ExecutionState.ApplyCalls);

                await harness.Coordinator.ExecuteAsync(CommandKind.Exit);

                Assert.Equal(StatusCode.Stopped, harness.Coordinator.LastEffects.Status);

                // Well past every retry the backoff would ever schedule.
                for (int i = 0; i < 10; i++)
                {
                    harness.Tick(TimeSpan.FromSeconds(30));
                }

                Assert.Equal(1, harness.ExecutionState.ApplyCalls);
            }
        }

        [Fact]
        public async Task AStopThatCannotBeWrittenStillTakesEffectAndSaysSo()
        {
            using (var harness = new Harness())
            {
                harness.Coordinator.Start();
                harness.Store.FailWrites = true;

                OperationResult outcome = await harness.Coordinator.ExecuteAsync(CommandKind.Stop);

                // Stop is reported as done, because it was: it took effect in memory the moment
                // it was asked for. The unwritten file is a standing warning, not a refusal.
                Assert.True(outcome.Succeeded);

                // In memory first: a disk failure must never leave the app running after the
                // user has asked it to stop.
                Assert.True(harness.Coordinator.Settings.Stopped);
                Assert.Equal(StatusCode.Stopped, harness.Coordinator.LastEffects.Status);
                Assert.False(harness.Coordinator.LastEffects.SystemAwake);

                Assert.True(harness.Coordinator.HasUncommittedStop);
                Assert.Contains(true, harness.Warnings);
                Assert.Contains("settings.writeFailed", harness.Diagnostics.Codes);
            }
        }

        [Fact]
        public async Task AnUncommittedStopIsRetriedOnTheBackoffAndTheWarningClearsWhenItLands()
        {
            using (var harness = new Harness())
            {
                harness.Coordinator.Start();
                harness.Store.FailWrites = true;

                await harness.Coordinator.ExecuteAsync(CommandKind.Stop);
                int afterStop = harness.Store.WriteAttempts;

                // Not due yet, however many times the heartbeat fires.
                for (int i = 0; i < 5; i++)
                {
                    harness.Coordinator.Evaluate();
                }

                Assert.Equal(afterStop, harness.Store.WriteAttempts);

                harness.Tick(TimeSpan.FromSeconds(1));
                Assert.Equal(afterStop + 1, harness.Store.WriteAttempts);

                harness.Tick(TimeSpan.FromSeconds(4));
                Assert.Equal(afterStop + 1, harness.Store.WriteAttempts);

                harness.Store.FailWrites = false;
                harness.Tick(TimeSpan.FromSeconds(1));

                Assert.Equal(afterStop + 2, harness.Store.WriteAttempts);
                Assert.False(harness.Coordinator.HasUncommittedStop);
                Assert.True(harness.Store.Current.Stopped);
                Assert.False(harness.Warnings[harness.Warnings.Count - 1]);
            }
        }

        [Fact]
        public void AnInputFailureThatArrivesAfterTheStateMovedOnDoesNotLatch()
        {
            using (var harness = new Harness())
            {
                harness.Coordinator.Start();

                // A non-retryable send failure is the kind that latches.
                harness.Jiggler.Result = OperationResult.Failure(FaultSubsystem.InputSend, "input.sendFailed", 1, retryable: false);

                bool interrupted = false;
                harness.Jiggler.BeforeReturn = () =>
                {
                    if (interrupted)
                    {
                        return;
                    }

                    // The session locks while the batch is in flight. Locking supersedes the
                    // command, so the failure now describes a world that no longer exists.
                    interrupted = true;
                    harness.Environment.Session = SessionState.Locked;
                    harness.Environment.RaiseSessionChanged();
                };

                harness.Tick(TimeSpan.FromSeconds(IntervalSeconds));
                Assert.Equal(1, harness.Jiggler.SendCalls);

                // Come back to an unlocked session by polling rather than by a notification,
                // because an unlock notification clears the fault by itself and would hide
                // whether one had been latched at all.
                harness.Environment.Session = SessionState.ActiveUnlocked;
                harness.Jiggler.BeforeReturn = null;
                harness.Jiggler.Result = OperationResult.Success();

                harness.Tick(TimeSpan.FromSeconds(6));

                Assert.False(harness.Coordinator.LastEffects.InputRequestFailed);
                Assert.True(harness.Coordinator.LastEffects.MayJiggle);
            }
        }

        [Fact]
        public void AnUninterruptedNonRetryableInputFailureLatchesUntilTheSessionRecovers()
        {
            using (var harness = new Harness())
            {
                harness.Coordinator.Start();
                harness.Jiggler.Result = OperationResult.Failure(FaultSubsystem.InputSend, "input.sendFailed", 1, retryable: false);

                harness.Tick(TimeSpan.FromSeconds(IntervalSeconds));
                Assert.Equal(1, harness.Jiggler.SendCalls);

                // The status is computed before the batch is sent, so the fault becomes visible
                // on the following tick rather than the one that failed.
                harness.Tick(TimeSpan.FromSeconds(1));
                Assert.Equal(1, harness.Jiggler.SendCalls);
                Assert.True(harness.Coordinator.LastEffects.InputRequestFailed);
                Assert.False(harness.Coordinator.LastEffects.MayJiggle);

                // A latched fault does not retry on its own, whatever the interval says.
                harness.Tick(TimeSpan.FromSeconds(IntervalSeconds));
                Assert.Equal(1, harness.Jiggler.SendCalls);

                // Unlocking is a recovery: the desktop the send failed against has gone.
                harness.Jiggler.Result = OperationResult.Success();
                harness.Environment.Session = SessionState.Locked;
                harness.Environment.RaiseSessionChanged();
                harness.Environment.Session = SessionState.ActiveUnlocked;
                harness.Environment.RaiseSessionChanged();

                Assert.False(harness.Coordinator.LastEffects.InputRequestFailed);

                // Recovery starts a fresh grace period rather than firing at once.
                Assert.Equal(1, harness.Jiggler.SendCalls);

                harness.Tick(TimeSpan.FromSeconds(IntervalSeconds));
                Assert.Equal(2, harness.Jiggler.SendCalls);
            }
        }

        [Fact]
        public void ARetryableInputFailureIsAnOrdinarySkipRatherThanAFault()
        {
            using (var harness = new Harness())
            {
                harness.Coordinator.Start();
                harness.Jiggler.Result = OperationResult.Failure(FaultSubsystem.InputSend, "input.blocked", null, retryable: true);

                harness.Tick(TimeSpan.FromSeconds(IntervalSeconds));

                Assert.Equal(1, harness.Jiggler.SendCalls);
                Assert.False(harness.Coordinator.LastEffects.InputRequestFailed);

                // Due again on the next interval, because nothing latched.
                harness.Tick(TimeSpan.FromSeconds(IntervalSeconds));
                Assert.Equal(2, harness.Jiggler.SendCalls);
            }
        }

        [Fact]
        public void AnUnreadableIdleTimeIsNotALicenceToMoveThePointer()
        {
            using (var harness = new Harness())
            {
                harness.Coordinator.Start();
                harness.Idle.Readable = false;

                harness.Tick(TimeSpan.FromSeconds(IntervalSeconds * 3));

                Assert.Equal(0, harness.Jiggler.SendCalls);
                Assert.False(harness.Coordinator.LastEffects.InputRequestFailed);
            }
        }

        [Fact]
        public void TheSecureDesktopSuppressesJigglingWithoutRecordingAFault()
        {
            using (var harness = new Harness())
            {
                harness.Desktop.Available = false;
                harness.Coordinator.Start();

                harness.Tick(TimeSpan.FromSeconds(IntervalSeconds));

                Assert.False(harness.Coordinator.LastEffects.MayJiggle);
                Assert.Equal(0, harness.Jiggler.SendCalls);
                Assert.False(harness.Coordinator.LastEffects.InputRequestFailed);

                // Still keeping the computer awake, which is what the user asked for.
                Assert.True(harness.Coordinator.LastEffects.SystemAwake);
            }
        }

        [Fact]
        public void AFailedPowerQueryIsRecordedAndReadsAsUnknownRatherThanPluggedIn()
        {
            using (var harness = new Harness())
            {
                harness.Environment.FailPowerQuery = true;
                harness.Coordinator.Start();

                Assert.Equal(StatusCode.PowerStatusUnavailable, harness.Coordinator.LastEffects.Status);
                Assert.False(harness.Coordinator.LastEffects.SystemAwake);
                Assert.Contains("power.queryFailed", harness.Diagnostics.Codes);
            }
        }

        [Fact]
        public void AFailedSessionQueryFallsBackToTheStateTheLastNotificationCarried()
        {
            using (var harness = new Harness())
            {
                harness.Environment.FailSessionQuery = true;
                harness.Environment.LastNotifiedSession = SessionState.Locked;
                harness.Coordinator.Start();

                // A lock still keeps the computer awake for work already running, but must not
                // move the pointer or hold the display on.
                Assert.Equal(StatusCode.LockedKeepingAwake, harness.Coordinator.LastEffects.Status);
                Assert.True(harness.Coordinator.LastEffects.SystemAwake);
                Assert.False(harness.Coordinator.LastEffects.DisplayAwake);
                Assert.False(harness.Coordinator.LastEffects.MayJiggle);
                Assert.Contains("session.queryFailed", harness.Diagnostics.Codes);
            }
        }

        [Fact]
        public void PowerAndSessionAreRePolledOnTheFiveSecondFallbackAndNotOnEveryTick()
        {
            using (var harness = new Harness())
            {
                harness.Coordinator.Start();

                int afterStart = harness.Environment.PowerQueries;
                Assert.True(afterStart >= 1);

                for (int i = 0; i < 4; i++)
                {
                    harness.Tick(TimeSpan.FromSeconds(1));
                }

                Assert.Equal(afterStart, harness.Environment.PowerQueries);
                Assert.Equal(afterStart, harness.Environment.SessionQueries);

                // Five seconds is well inside the six-second detection bound the epic sets, so
                // unplugging a laptop is still noticed when notifications never arrive.
                harness.Tick(TimeSpan.FromSeconds(1));
                Assert.Equal(afterStart + 1, harness.Environment.PowerQueries);
                Assert.Equal(afterStart + 1, harness.Environment.SessionQueries);
            }
        }

        [Fact]
        public void UnpluggingIsNoticedThroughThePollAloneWhenNoNotificationArrives()
        {
            using (var harness = new Harness())
            {
                harness.Coordinator.Start();
                Assert.Equal(StatusCode.RunningManual, harness.Coordinator.LastEffects.Status);

                harness.Environment.Power = PowerSource.Battery;

                harness.Tick(TimeSpan.FromSeconds(6));

                Assert.Equal(StatusCode.PausedOnBattery, harness.Coordinator.LastEffects.Status);
                Assert.False(harness.Coordinator.LastEffects.SystemAwake);
            }
        }

        [Fact]
        public void SuspendClearsEveryEffectAndResumeStartsAFreshGracePeriod()
        {
            using (var harness = new Harness())
            {
                harness.Coordinator.Start();

                harness.Coordinator.OnSystemMessage(0x0218, new IntPtr(0x0004));
                Assert.Equal(StatusCode.Stopped, harness.Coordinator.LastEffects.Status);
                Assert.False(harness.Coordinator.LastEffects.SystemAwake);

                // Nothing is replayed for the time asleep, and the first jiggle waits a full
                // interval after waking rather than firing the moment the lid opens.
                harness.Clock.Advance(TimeSpan.FromHours(8));
                harness.Coordinator.OnSystemMessage(0x0218, new IntPtr(0x0007));

                Assert.Equal(StatusCode.RunningManual, harness.Coordinator.LastEffects.Status);
                Assert.Equal(0, harness.Jiggler.SendCalls);

                harness.Tick(TimeSpan.FromSeconds(IntervalSeconds - 1));
                Assert.Equal(0, harness.Jiggler.SendCalls);

                harness.Tick(TimeSpan.FromSeconds(1));
                Assert.Equal(1, harness.Jiggler.SendCalls);
            }
        }

        [Fact]
        public void UnreadableSettingsReportErrorAndHoldNoWakeRequest()
        {
            using (var harness = new Harness())
            {
                harness.Coordinator.NoteUnreadableSettings("settings.malformed");
                harness.Coordinator.Start();

                Assert.True(harness.Coordinator.SettingsUnreadable);
                Assert.Equal("settings.malformed", harness.Coordinator.SettingsFaultCode);
                Assert.Equal(StatusCode.Error, harness.Coordinator.LastEffects.Status);
                Assert.False(harness.Coordinator.LastEffects.SystemAwake);
                Assert.False(harness.ExecutionState.SystemAwakeApplied);
            }
        }

        [Fact]
        public async Task AnEnablingCommandThatCannotBeSavedLeavesTheAppStopped()
        {
            using (var harness = new Harness(stopped: true))
            {
                harness.Coordinator.Start();
                harness.Store.FailWrites = true;

                OperationResult outcome = await harness.Coordinator.ExecuteAsync(CommandKind.Start);

                // The write is what authorizes the change, so a failed write means nothing
                // starts: the alternative is an app that runs now and is stopped again on the
                // next launch. The caller is told, because a tray icon that silently stays
                // stopped is indistinguishable from a click that never registered.
                Assert.False(outcome.Succeeded);
                Assert.Equal("settings.writeFailed", outcome.Code);
                Assert.Contains("settings.writeFailed", harness.Diagnostics.Codes);
                Assert.True(harness.Coordinator.Settings.Stopped);
                Assert.Equal(StatusCode.Stopped, harness.Coordinator.LastEffects.Status);
                Assert.False(harness.ExecutionState.SystemAwakeApplied);
            }
        }

        [Fact]
        public async Task RetryClearsALatchedFaultWithoutStartingAStoppedApp()
        {
            using (var harness = new Harness(stopped: true))
            {
                harness.Coordinator.Start();

                await harness.Coordinator.ExecuteAsync(CommandKind.Retry);

                Assert.True(harness.Coordinator.Settings.Stopped);
                Assert.Equal(StatusCode.Stopped, harness.Coordinator.LastEffects.Status);
                Assert.Equal(0, harness.Jiggler.SendCalls);
            }
        }

        [Fact]
        public async Task ChangingTheIntervalRestartsTheWaitInsteadOfFiringImmediately()
        {
            using (var harness = new Harness())
            {
                harness.Coordinator.Start();
                harness.Tick(TimeSpan.FromSeconds(IntervalSeconds));
                Assert.Equal(1, harness.Jiggler.SendCalls);

                var patch = new SettingsPatch { IntervalSeconds = 60 };
                await harness.Coordinator.ApplySettingsAsync(patch);

                harness.Tick(TimeSpan.FromSeconds(59));
                Assert.Equal(1, harness.Jiggler.SendCalls);

                harness.Tick(TimeSpan.FromSeconds(1));
                Assert.Equal(2, harness.Jiggler.SendCalls);
            }
        }

        [Fact]
        public void DisposeDetachesFromTheSourcesTheCoordinatorDoesNotOwn()
        {
            var harness = new Harness();
            harness.Coordinator.Start();

            Assert.True(harness.Environment.HasSubscribers);
            Assert.True(harness.Store.HasSubscribers);

            harness.Coordinator.Dispose();

            // The coordinator does not own the source, so a handler left behind would keep a
            // disposed coordinator alive and evaluating on someone else's notification.
            Assert.False(harness.Environment.HasSubscribers);
            Assert.False(harness.Store.HasSubscribers);

            // It does release the wake request, which is the one thing that must not outlive it.
            Assert.False(harness.ExecutionState.SystemAwakeApplied);
        }

        [Fact]
        public void EvaluatingAfterDisposeDoesNothingRatherThanThrowing()
        {
            var harness = new Harness();
            harness.Coordinator.Start();
            harness.Coordinator.Dispose();

            int applies = harness.ExecutionState.ApplyCalls;
            harness.Coordinator.Evaluate();

            Assert.Equal(applies, harness.ExecutionState.ApplyCalls);
        }
    }
}
