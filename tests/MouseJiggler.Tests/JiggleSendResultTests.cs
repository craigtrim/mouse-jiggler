using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Commands;
using MouseJiggler.Core.Environment;
using MouseJiggler.Core.Input;
using MouseJiggler.Core.Settings;
using MouseJiggler.Windows.Input;
using MouseJiggler.Windows.Interop;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The send half of issue #7: what the app does with the count SendInput returns, what the
    /// two records it builds actually contain, and what a generation change does to a batch that
    /// is already under way.
    /// </summary>
    /// <remarks>
    /// Nothing here touches the real device. <c>FakeMouseJiggler</c> runs the same decision
    /// sequence against injected geometry and a scripted insertion count, so an ordinary test
    /// run moves no pointer. The records it hands to the fake native call are built by the
    /// product's own record builder, which is what makes the flag, tag and round-trip
    /// assertions statements about the shipping code rather than about the fake.
    /// </remarks>
    public sealed class JiggleSendResultTests
    {
        private const int DesktopWidth = 1920;
        private const int DesktopHeight = 1080;
        private const int DefaultCursorX = 400;
        private const int DefaultCursorY = 300;

        private const uint MovementFlags =
            NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK;

        /// <summary>
        /// Every documented mouse flag that is not movement: the button, X-button and wheel
        /// events the product promises never to generate.
        /// </summary>
        private const uint ClickWheelAndButtonFlags =
            0x0002 | 0x0004 | 0x0008 | 0x0010 | 0x0020 | 0x0040 | 0x0080 | 0x0100 | 0x0800 | 0x1000;

        [Fact]
        public void ASuccessfulJiggleIsExactlyOneNativeCallCarryingTwoRecords()
        {
            var harness = new JiggleHarness();

            OperationResult? result = harness.Tick();

            Assert.NotNull(result);
            Assert.True(result!.Succeeded);

            // Both halves go in one call. Two calls, or a restore on a timer, would leave a
            // window in which the pointer sits on the wrong pixel.
            SentBatch batch = Assert.Single(harness.Jiggler.Batches);
            Assert.Equal(2u, batch.Count);
            Assert.Equal(2, batch.Records.Length);

            // The count argument has to agree with the array, and the size argument has to be
            // the real structure size, or SendInput rejects the call every single time.
            Assert.Equal(batch.Records.Length, (int)batch.Count);
            Assert.Equal(Marshal.SizeOf(typeof(NativeMethods.INPUT)), batch.StructureSize);
            Assert.Equal(1, harness.Jiggler.Attempts);
        }

        [Fact]
        public void TheGeneratedRecordsCarryOnlyMovementFlagsAndNoClickWheelOrKeystrokeBits()
        {
            // A mistyped constant is how a jiggle would quietly become a click, so the composed
            // mask is checked against the button and wheel flags directly.
            Assert.Equal(0xC001u, MovementFlags);
            Assert.Equal(0u, MovementFlags & ClickWheelAndButtonFlags);

            var harness = new JiggleHarness();
            harness.Tick();

            foreach (NativeMethods.INPUT record in Assert.Single(harness.Jiggler.Batches).Records)
            {
                // INPUT_MOUSE is 0; a keyboard record would be 1, and the union is deliberately
                // never populated through its keyboard member.
                Assert.Equal(0, record.type);
                Assert.Equal(MovementFlags, record.union.mi.dwFlags);
                Assert.Equal(0u, record.union.mi.dwFlags & ClickWheelAndButtonFlags);
            }
        }

        [Fact]
        public void EveryRecordHasMouseDataZeroTimeZeroAndAPointerSizedOwnedTag()
        {
            var harness = new JiggleHarness();
            harness.Tick();

            // A zero tag would be indistinguishable from input the app did not generate, which
            // is the whole point of tagging.
            Assert.NotEqual(IntPtr.Zero, MouseJigglerDevice.OwnedTag);

            foreach (NativeMethods.INPUT record in Assert.Single(harness.Jiggler.Batches).Records)
            {
                // mouseData is meaningful only for wheel and X-button events, and time = 0 lets
                // Windows stamp the record rather than the app backdating it.
                Assert.Equal(0u, record.union.mi.mouseData);
                Assert.Equal(0u, record.union.mi.time);
                Assert.Equal(MouseJigglerDevice.OwnedTag, record.union.mi.dwExtraInfo);
            }

            // dwExtraInfo is the last field of MOUSEINPUT and has to be a whole pointer wide.
            // Declared as an int it would look right on x86 and corrupt the layout on x64.
            int extraInfoWidth = Marshal.SizeOf(typeof(NativeMethods.MOUSEINPUT))
                                 - Marshal.OffsetOf(typeof(NativeMethods.MOUSEINPUT), "dwExtraInfo").ToInt32();

            Assert.Equal(IntPtr.Size, extraInfoWidth);
        }

        [Fact]
        public void TheSecondRecordReturnsThePointerToTheExactPixelItStartedOn()
        {
            var harness = new JiggleHarness();
            harness.Tick();

            SentBatch batch = Assert.Single(harness.Jiggler.Batches);
            NativeMethods.MOUSEINPUT away = batch.Records[0].union.mi;
            NativeMethods.MOUSEINPUT back = batch.Records[1].union.mi;

            // Windows floors a normalized value back to a pixel. The no-drift promise holds only
            // if these two map back to the neighbour and to the original pixel exactly.
            Assert.Equal(DefaultCursorX + 1L, VirtualDesktopCoordinates.Denormalize(away.dx, DesktopWidth));
            Assert.Equal((long)DefaultCursorY, VirtualDesktopCoordinates.Denormalize(away.dy, DesktopHeight));
            Assert.Equal((long)DefaultCursorX, VirtualDesktopCoordinates.Denormalize(back.dx, DesktopWidth));
            Assert.Equal((long)DefaultCursorY, VirtualDesktopCoordinates.Denormalize(back.dy, DesktopHeight));

            // And the batch genuinely moves: two identical records would reset no idle timer.
            Assert.NotEqual(away.dx, back.dx);
            Assert.InRange(away.dx, 0, VirtualDesktopCoordinates.NormalizedMax);
            Assert.InRange(back.dx, 0, VirtualDesktopCoordinates.NormalizedMax);
        }

        [Theory]
        [InlineData(0u)]
        [InlineData(1u)]
        public void ASendThatInsertsFewerThanTwoEventsLatchesTheInputCapability(uint inserted)
        {
            var harness = new JiggleHarness();
            harness.Jiggler.InsertedEvents = inserted;

            OperationResult? result = harness.Tick();

            Assert.NotNull(result);
            Assert.False(result!.Succeeded);
            Assert.Equal(FaultSubsystem.InputSend, result.Subsystem);
            Assert.Equal("input.sendFailed", result.Code);

            // Retryable is the only thing the coordinator reads before latching, so a partial
            // insertion has to arrive as non-retryable or the failure would be invisible.
            Assert.False(result.Retryable);

            // UIPI blocking cannot always be identified from the Win32 error, but discarding it
            // would leave nothing at all to look at in the diagnostics.
            Assert.Equal(harness.Jiggler.NativeErrorCode, result.NativeErrorCode);
            Assert.Equal(CapabilityState.Latched, harness.InputCapability);
        }

        [Fact]
        public void APartialSendIsNeverFollowedByACompensatingMoveOrADelayedRestore()
        {
            var harness = new JiggleHarness();
            harness.Jiggler.InsertedEvents = 1;

            harness.Tick();

            // One event went in, so the pointer is a pixel from where it started. A second blind
            // write is as likely to make that worse, so nothing is sent to correct it.
            Assert.Single(harness.Jiggler.Batches);
            Assert.Equal(1, harness.Jiggler.Attempts);

            // Nothing is queued for later either: the next heartbeat prepares no batch at all.
            harness.Tick();
            Assert.Single(harness.Jiggler.Batches);
            Assert.Equal(1, harness.Jiggler.Attempts);

            // Keeping the computer awake still works; only jiggling is reported as failed.
            Assert.True(harness.Effects.SystemAwake);
            Assert.False(harness.Effects.MayJiggle);
            Assert.True(harness.Effects.InputRequestFailed);
        }

        [Fact]
        public void ALatchedFaultSurvivesEveryLaterTickUntilAnExplicitRetry()
        {
            var harness = new JiggleHarness();
            harness.Jiggler.InsertedEvents = 0;
            harness.Tick();

            Assert.Equal(CapabilityState.Latched, harness.InputCapability);

            // Whatever blocked the send has gone away, but the fault does not clear itself: an
            // app that silently resumed would hide a real failure from the user.
            harness.Jiggler.InsertedEvents = 2;
            Assert.Null(harness.Tick());
            Assert.Single(harness.Jiggler.Batches);

            harness.Retry();
            OperationResult? afterRetry = harness.Tick();

            Assert.NotNull(afterRetry);
            Assert.True(afterRetry!.Succeeded);
            Assert.Equal(2, harness.Jiggler.Batches.Count);
            Assert.Equal(CapabilityState.Ok, harness.InputCapability);
            Assert.False(harness.Effects.InputRequestFailed);
        }

        [Fact]
        public void AHeldButtonOrModifierSkipsTheBatchWithoutSendingAnythingAndNeverLatches()
        {
            var harness = new JiggleHarness();
            harness.Jiggler.ButtonOrModifierHeld = true;

            OperationResult? skipped = harness.Tick();

            Assert.NotNull(skipped);
            Assert.Equal("input.skipped.userBusy", skipped!.Code);
            Assert.True(skipped.Retryable);

            // A drag or a keyboard shortcut is in progress, so not one record reaches Windows.
            Assert.Empty(harness.Jiggler.Batches);
            Assert.Equal(CapabilityState.Ok, harness.InputCapability);

            // The drag ends, and the very next tick jiggles as usual.
            harness.Jiggler.ButtonOrModifierHeld = false;
            OperationResult? next = harness.Tick();

            Assert.NotNull(next);
            Assert.True(next!.Succeeded);
            Assert.Single(harness.Jiggler.Batches);
        }

        [Fact]
        public void AnUnreadableCursorOrAMissingMonitorIsAnOrdinarySkipRatherThanALatchedFault()
        {
            var unreadable = new JiggleHarness();
            unreadable.Jiggler.CursorReadable = false;

            OperationResult? noCursor = unreadable.Tick();

            Assert.NotNull(noCursor);
            Assert.Equal(FaultSubsystem.InputRead, noCursor!.Subsystem);
            Assert.Equal("input.cursorPosFailed", noCursor.Code);
            Assert.True(noCursor.Retryable);
            Assert.Empty(unreadable.Jiggler.Batches);
            Assert.Equal(CapabilityState.Ok, unreadable.InputCapability);

            // The display the pointer was on has just been undocked.
            var undocked = new JiggleHarness();
            undocked.Jiggler.Monitor = null;

            OperationResult? noMonitor = undocked.Tick();

            Assert.NotNull(noMonitor);
            Assert.Equal("input.monitorUnavailable", noMonitor!.Code);
            Assert.True(noMonitor.Retryable);
            Assert.Empty(undocked.Jiggler.Batches);
            Assert.Equal(CapabilityState.Ok, undocked.InputCapability);
        }

        [Fact]
        public void AMonitorWithNoNeighbouringPixelSkipsWithoutLatching()
        {
            var harness = new JiggleHarness();
            harness.Jiggler.Monitor = new MonitorBounds(0, 0, 1, 1);
            harness.Jiggler.CursorX = 0;
            harness.Jiggler.CursorY = 0;

            OperationResult? skipped = harness.Tick();

            Assert.NotNull(skipped);
            Assert.Equal("input.skipped.noNeighbour", skipped!.Code);
            Assert.True(skipped.Retryable);
            Assert.Empty(harness.Jiggler.Batches);
            Assert.Equal(CapabilityState.Ok, harness.InputCapability);
        }

        [Fact]
        public void AnUnavailableInputDesktopStopsTheBatchBeforeItIsPreparedAndNeverLatches()
        {
            var harness = new JiggleHarness();
            harness.InputDesktopAvailable = false;

            Assert.Null(harness.Tick());

            // The secure desktop being up is a reason, not a fault: nothing is prepared, nothing
            // is sent, and the user is never asked to retry anything.
            Assert.Equal(0, harness.Jiggler.Attempts);
            Assert.Empty(harness.Jiggler.Batches);
            Assert.Equal(CapabilityState.Ok, harness.InputCapability);
            Assert.False(harness.Effects.InputRequestFailed);

            harness.InputDesktopAvailable = true;

            Assert.True(harness.Tick()!.Succeeded);
        }

        [Fact]
        public void UnrepresentableGeometryIsRefusedBeforeAnythingIsSent()
        {
            var harness = new JiggleHarness();
            ConfigureUnrepresentableDesktop(harness);

            OperationResult? skipped = harness.Tick();

            Assert.NotNull(skipped);
            Assert.False(skipped!.Succeeded);
            Assert.Equal("input.unsupportedGeometry", skipped.Code);

            // Rather than move the pointer approximately, or enlarge the movement until it can
            // be expressed, nothing is sent at all.
            Assert.Empty(harness.Jiggler.Batches);
        }

        [Fact]
        public void UnrepresentableGeometryIsAnOrdinarySkipRatherThanALatchedFault()
        {
            var harness = new JiggleHarness();
            ConfigureUnrepresentableDesktop(harness);

            OperationResult? skipped = harness.Tick();

            // Issue #7: "A failed GetLastInputInfo, an inaccessible or mismatched input desktop,
            // a held button or modifier, and unrepresentable geometry are ordinary skips:
            // retried on the next tick, surfaced as a reason, never latched. Only a SendInput
            // call returning a count other than 2 latches the input capability fault."
            //
            // Retryable is the only thing the coordinator reads before latching, so a layout
            // the mapping cannot express must report itself as a skip. Docking or a resolution
            // change can make it representable again moments later, and latching would leave the
            // user pressing Retry for something that fixed itself.
            Assert.NotNull(skipped);
            Assert.True(skipped!.Retryable);
            Assert.Equal(CapabilityState.Ok, harness.InputCapability);

            // A resolution change makes the desktop representable again, and the next tick has
            // to jiggle without anyone intervening.
            harness.Jiggler.VirtualDesktop = new VirtualBounds(0, 0, DesktopWidth, DesktopHeight);
            harness.Jiggler.Monitor = new MonitorBounds(0, 0, DesktopWidth, DesktopHeight);

            Assert.True(harness.Tick()!.Succeeded);
        }

        [Fact]
        public void AGenerationChangeBetweenPreparingAndSendingCancelsTheBatch()
        {
            var harness = new JiggleHarness();

            // A Stop, a lock or a settings change lands after the geometry has been read and
            // before the call goes out.
            harness.Jiggler.WhilePreparing = harness.Supersede;

            OperationResult? cancelled = harness.Tick();

            Assert.NotNull(cancelled);
            Assert.False(cancelled!.Succeeded);
            Assert.Equal("input.skipped.cancelled", cancelled.Code);
            Assert.True(cancelled.Retryable);

            // Nothing reached Windows, and a cancelled batch is not a fault.
            Assert.Empty(harness.Jiggler.Batches);
            Assert.Equal(CapabilityState.Ok, harness.InputCapability);

            // Cancellation is not sticky: the next tick runs against the new generation.
            harness.Jiggler.WhilePreparing = null;

            Assert.True(harness.Tick()!.Succeeded);
            Assert.Single(harness.Jiggler.Batches);
        }

        [Fact]
        public void AGenerationChangeWhileTheSendIsInFlightDiscardsTheOutcomeInsteadOfLatching()
        {
            var harness = new JiggleHarness();
            harness.Jiggler.InsertedEvents = 0;

            // Stop waits for the synchronous call to return, so this failure is real. It
            // describes a batch that no longer speaks for the app's state, though, and latching
            // on it would leave behind a fault that only a Retry could clear.
            harness.Jiggler.DuringSend = harness.Supersede;

            OperationResult? superseded = harness.Tick();

            Assert.NotNull(superseded);
            Assert.False(superseded!.Succeeded);
            Assert.False(superseded.Retryable);
            Assert.Equal(CapabilityState.Ok, harness.InputCapability);
            Assert.Single(harness.Jiggler.Batches);
        }

        /// <summary>
        /// Twice as many pixels as the normalized space has values, so only even offsets can be
        /// addressed exactly and the neighbour of an even pixel cannot be expressed at all.
        /// </summary>
        private static void ConfigureUnrepresentableDesktop(JiggleHarness harness)
        {
            const int Extent = 131072;

            harness.Jiggler.VirtualDesktop = new VirtualBounds(0, 0, Extent, DesktopHeight);
            harness.Jiggler.Monitor = new MonitorBounds(0, 0, Extent, DesktopHeight);
            harness.Jiggler.CursorX = 100;
            harness.Jiggler.CursorY = DefaultCursorY;
        }

        /// <summary>
        /// Builds one record with the device's own record builder.
        /// </summary>
        /// <remarks>
        /// The builder is private, and the only public way into it reads the real cursor and
        /// then really moves it, so reflection is what lets the records the product would hand
        /// to Windows be inspected in a run that must move nothing.
        /// </remarks>
        private static NativeMethods.INPUT BuildRecord(int normalizedX, int normalizedY)
        {
            MethodInfo? builder = typeof(MouseJigglerDevice).GetMethod(
                "CreateMove",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(int), typeof(int) },
                null);

            if (builder == null)
            {
                throw new InvalidOperationException(
                    "MouseJigglerDevice.CreateMove(int, int) is gone; these tests assert on the records it builds.");
            }

            object? record = builder.Invoke(null, new object[] { normalizedX, normalizedY });

            if (record == null)
            {
                throw new InvalidOperationException("MouseJigglerDevice.CreateMove returned nothing.");
            }

            return (NativeMethods.INPUT)record;
        }

        /// <summary>One batch as it would have been handed to SendInput.</summary>
        private sealed class SentBatch
        {
            public SentBatch(uint count, NativeMethods.INPUT[] records, int structureSize)
            {
                Count = count;
                Records = records;
                StructureSize = structureSize;
            }

            /// <summary>The count argument, which has to match the array length.</summary>
            public uint Count { get; }

            public NativeMethods.INPUT[] Records { get; }

            public int StructureSize { get; }
        }

        /// <summary>
        /// Runs the device's decision sequence against injected geometry and a scripted
        /// insertion count.
        /// </summary>
        /// <remarks>
        /// The real device reads the cursor, the monitor and the virtual desktop from Windows
        /// and then moves the pointer, so it cannot appear in an ordinary test run. The order of
        /// the checks, the fault codes and the retryable flags here are copied from
        /// src/MouseJiggler.Windows/Input/MouseJigglerDevice.cs and have to be kept in step
        /// with it.
        /// </remarks>
        private sealed class FakeMouseJiggler : IMouseJiggler
        {
            public int CursorX { get; set; } = DefaultCursorX;

            public int CursorY { get; set; } = DefaultCursorY;

            /// <summary>False stands in for a failed GetCursorPos.</summary>
            public bool CursorReadable { get; set; } = true;

            public bool ButtonOrModifierHeld { get; set; }

            /// <summary>Null stands in for a monitor Windows will not describe.</summary>
            public MonitorBounds? Monitor { get; set; } = new MonitorBounds(0, 0, DesktopWidth, DesktopHeight);

            public VirtualBounds VirtualDesktop { get; set; } = new VirtualBounds(0, 0, DesktopWidth, DesktopHeight);

            /// <summary>What the scripted SendInput reports having inserted.</summary>
            public uint InsertedEvents { get; set; } = 2;

            /// <summary>ERROR_ACCESS_DENIED, which is what UIPI blocking tends to look like.</summary>
            public int NativeErrorCode { get; set; } = 5;

            /// <summary>Whether the batch has been superseded, evaluated where the device evaluates it.</summary>
            public Func<bool> IsCancelled { get; set; } = () => false;

            /// <summary>Runs immediately before the cancellation check, for a Stop landing mid-preparation.</summary>
            public Action? WhilePreparing { get; set; }

            /// <summary>Runs inside the scripted native call, for a Stop landing mid-send.</summary>
            public Action? DuringSend { get; set; }

            public List<SentBatch> Batches { get; } = new List<SentBatch>();

            /// <summary>How many times a batch was attempted, whether or not anything was sent.</summary>
            public int Attempts { get; private set; }

            public OperationResult SendJiggle()
            {
                Attempts++;

                if (!CursorReadable)
                {
                    return OperationResult.Failure(FaultSubsystem.InputRead, "input.cursorPosFailed", NativeErrorCode);
                }

                if (ButtonOrModifierHeld)
                {
                    return OperationResult.Failure(FaultSubsystem.InputSend, "input.skipped.userBusy", retryable: true);
                }

                MonitorBounds? monitor = Monitor;
                if (monitor == null)
                {
                    return OperationResult.Failure(FaultSubsystem.InputRead, "input.monitorUnavailable");
                }

                if (!JiggleEligibility.TryChooseTarget(CursorX, CursorY, monitor, out JiggleTarget? target))
                {
                    return OperationResult.Failure(FaultSubsystem.InputSend, "input.skipped.noNeighbour", retryable: true);
                }

                if (!VirtualDesktopCoordinates.TryNormalizePoint(target.ToX, target.ToY, VirtualDesktop, out int toX, out int toY) ||
                    !VirtualDesktopCoordinates.TryNormalizePoint(target.FromX, target.FromY, VirtualDesktop, out int backX, out int backY))
                {
                    return OperationResult.Failure(FaultSubsystem.InputSend, "input.unsupportedGeometry", retryable: true);
                }

                WhilePreparing?.Invoke();

                if (IsCancelled())
                {
                    return OperationResult.Failure(FaultSubsystem.InputSend, "input.skipped.cancelled", retryable: true);
                }

                var inputs = new NativeMethods.INPUT[2];
                inputs[0] = BuildRecord(toX, toY);
                inputs[1] = BuildRecord(backX, backY);

                uint inserted = Send(2, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT)));

                if (inserted != 2)
                {
                    return OperationResult.Failure(FaultSubsystem.InputSend, "input.sendFailed", NativeErrorCode, retryable: false);
                }

                return OperationResult.Success();
            }

            private uint Send(uint count, NativeMethods.INPUT[] inputs, int structureSize)
            {
                DuringSend?.Invoke();
                Batches.Add(new SentBatch(count, inputs, structureSize));
                return InsertedEvents;
            }
        }

        /// <summary>
        /// One coordinator heartbeat: the real policy decides whether a batch may be prepared,
        /// the fake device attempts it, and the result is interpreted the way the coordinator
        /// interprets it.
        /// </summary>
        /// <remarks>
        /// The latch rule and the generation check are the two lines of
        /// ActivityCoordinator.TryJiggle, restated here because the test assembly references
        /// Core and Windows but not App. Everything they act on - the policy, the eligibility
        /// ladder, the fault values - is the product's own.
        /// </remarks>
        private sealed class JiggleHarness
        {
            private static readonly DateTime Noon = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);

            private readonly ActivityPolicy _policy = new ActivityPolicy();
            private readonly SettingsV1 _settings;
            private long _generation;

            public JiggleHarness()
            {
                SettingsV1 defaults = SettingsV1.CreateDefault();

                _settings = new SettingsV1(
                    defaults.SchemaVersion,
                    defaults.Revision,
                    stopped: false,
                    runMode: RunMode.Manual,
                    scheduleEnabled: false,
                    defaults.ScheduleStart,
                    defaults.ScheduleEnd,
                    defaults.DayMask,
                    pauseOnBattery: false,
                    keepDisplayOn: true,
                    jiggleMouse: true,
                    defaults.IntervalSeconds,
                    defaults.DiagnosticLogging,
                    defaults.StartupInitialized,
                    defaults.FirstRunCompleted);
            }

            public FakeMouseJiggler Jiggler { get; } = new FakeMouseJiggler();

            public CapabilityState InputCapability { get; private set; } = CapabilityState.Ok;

            public bool InputDesktopAvailable { get; set; } = true;

            public DesiredEffects Effects { get; private set; } = DesiredEffects.None(StatusCode.Stopped);

            /// <summary>Null when the policy left no room to jiggle, so no batch was attempted.</summary>
            public OperationResult? Tick()
            {
                var environment = new EnvironmentSnapshot(
                    Noon,
                    TimeZoneInfo.Utc,
                    monotonicMilliseconds: 1_000,
                    power: PowerSource.External,
                    session: SessionState.ActiveUnlocked,
                    suspended: false,
                    exiting: false,
                    inputDesktopAvailable: InputDesktopAvailable,
                    wakeCapability: CapabilityState.Ok,
                    inputCapability: InputCapability,
                    settingsAvailable: true);

                Effects = _policy.Evaluate(_settings, environment);

                if (!Effects.MayJiggle)
                {
                    return null;
                }

                // The batch carries the generation it was prepared under, and AppCommand.IsCurrent
                // is the product's own rule for whether queued work still speaks for the app.
                AppCommand batch = AppCommand.Create(CommandKind.StartNow, _generation);
                Jiggler.IsCancelled = () => !batch.IsCurrent(_generation);

                OperationResult result = Jiggler.SendJiggle();

                if (result.Succeeded)
                {
                    InputCapability = CapabilityState.Ok;
                    return result;
                }

                if (!batch.IsCurrent(_generation))
                {
                    // Superseded while sending; the outcome describes a batch nobody wants now.
                    return result;
                }

                if (!result.Retryable)
                {
                    InputCapability = CapabilityState.Latched;
                }

                return result;
            }

            /// <summary>A Stop, lock, suspend or settings change: everything prepared before it is void.</summary>
            public void Supersede()
            {
                _generation++;
            }

            /// <summary>The explicit Retry command, the only thing that clears a latched input fault.</summary>
            public void Retry()
            {
                InputCapability = CapabilityState.Ok;
            }
        }
    }
}
