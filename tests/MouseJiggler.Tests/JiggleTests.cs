using System;
using System.Runtime.InteropServices;
using MouseJiggler.Core.Input;
using MouseJiggler.Windows.Interop;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The pure half of issue #7: idle arithmetic, target selection and the coordinate mapping
    /// that has to survive a round trip exactly, or the pointer drifts a pixel per jiggle.
    /// </summary>
    public sealed class JiggleTests
    {
        [Fact]
        public void IdleAgeIsComputedInUnsignedArithmeticAcrossTheTickWrap()
        {
            // Ordinary case.
            Assert.True(JiggleEligibility.TryComputeIdleMilliseconds(10_000, 4_000, out uint idle));
            Assert.Equal(6_000u, idle);

            // The 32-bit counter wrapped: now is small, last input was just before the wrap.
            // Subtracting these as signed numbers would produce a negative age.
            Assert.True(JiggleEligibility.TryComputeIdleMilliseconds(1_000, uint.MaxValue - 999, out uint wrapped));
            Assert.Equal(2_000u, wrapped);
        }

        [Fact]
        public void ASuspiciousDeltaIsTreatedAsUnknownRatherThanAsAnIdleMachine()
        {
            // A reading that claims the session has been idle for more than 24 days is far more
            // likely to be stale than true, so the app refuses to move rather than jiggling.
            Assert.False(JiggleEligibility.TryComputeIdleMilliseconds(0, 1, out uint idle));
            Assert.Equal(0u, idle);

            Assert.False(JiggleEligibility.TryComputeIdleMilliseconds(5_000, 5_001, out _));
        }

        [Fact]
        public void AFreshStartWaitsAFullIntervalEvenOnAnAlreadyIdleSession()
        {
            const int Interval = 30;
            long now = 100_000;
            long graceDeadline = now + (Interval * 1000);

            // The session has been idle for an hour, but the app has only just started.
            Assert.False(JiggleEligibility.IsDue(3_600_000, now, graceDeadline, 0, Interval));

            // Once the full interval has passed, it is due.
            Assert.True(JiggleEligibility.IsDue(3_600_000, graceDeadline, graceDeadline, 0, Interval));
        }

        [Fact]
        public void OngoingInputKeepsItFromBecomingDue()
        {
            const int Interval = 30;

            // Someone typed two seconds ago.
            Assert.False(JiggleEligibility.IsDue(2_000, 500_000, 0, 0, Interval));

            // Nothing for thirty seconds.
            Assert.True(JiggleEligibility.IsDue(30_000, 500_000, 0, 0, Interval));
        }

        [Fact]
        public void TwoJigglesAreNeverCloserTogetherThanTheInterval()
        {
            const int Interval = 30;
            long lastJiggle = 500_000;

            Assert.False(JiggleEligibility.IsDue(60_000, lastJiggle + 29_999, 0, lastJiggle, Interval));
            Assert.True(JiggleEligibility.IsDue(60_000, lastJiggle + 30_000, 0, lastJiggle, Interval));
        }

        [Fact]
        public void TheTargetIsOnePixelRightUnlessThatLeavesTheMonitor()
        {
            var monitor = new MonitorBounds(0, 0, 1920, 1080);

            Assert.True(JiggleEligibility.TryChooseTarget(100, 100, monitor, out JiggleTarget? middle));
            Assert.Equal(101, middle!.ToX);
            Assert.Equal(100, middle.ToY);

            // At the right edge it goes left instead of off the monitor.
            Assert.True(JiggleEligibility.TryChooseTarget(1919, 500, monitor, out JiggleTarget? edge));
            Assert.Equal(1918, edge!.ToX);
        }

        [Fact]
        public void EveryCornerOfAMonitorHasAValidNeighbour()
        {
            var monitor = new MonitorBounds(0, 0, 1920, 1080);

            (int X, int Y)[] corners = { (0, 0), (1919, 0), (0, 1079), (1919, 1079) };

            foreach ((int x, int y) in corners)
            {
                Assert.True(JiggleEligibility.TryChooseTarget(x, y, monitor, out JiggleTarget? target));
                Assert.True(monitor.Contains(target!.ToX, target.ToY));
                Assert.Equal(1, Math.Abs(target.ToX - x) + Math.Abs(target.ToY - y));
            }
        }

        [Fact]
        public void AMonitorLeftOfOrAboveThePrimaryWorksWithNegativeCoordinates()
        {
            // A second monitor placed to the left of and above the primary.
            var monitor = new MonitorBounds(-1920, -300, 0, 780);

            Assert.True(JiggleEligibility.TryChooseTarget(-1920, -300, monitor, out JiggleTarget? topLeft));
            Assert.Equal(-1919, topLeft!.ToX);

            Assert.True(JiggleEligibility.TryChooseTarget(-1, 779, monitor, out JiggleTarget? bottomRight));
            Assert.Equal(-2, bottomRight!.ToX);
        }

        [Fact]
        public void AOnePixelWideMonitorMovesVerticallyInstead()
        {
            var column = new MonitorBounds(0, 0, 1, 100);

            Assert.True(JiggleEligibility.TryChooseTarget(0, 50, column, out JiggleTarget? target));
            Assert.Equal(0, target!.ToX);
            Assert.Equal(51, target.ToY);
        }

        [Fact]
        public void ASinglePixelMonitorIsSkippedRatherThanMovedFurther()
        {
            var degenerate = new MonitorBounds(0, 0, 1, 1);

            Assert.False(JiggleEligibility.TryChooseTarget(0, 0, degenerate, out JiggleTarget? target));
            Assert.Null(target);
        }

        [Fact]
        public void APointOutsideTheMonitorIsRejected()
        {
            var monitor = new MonitorBounds(0, 0, 1920, 1080);

            Assert.False(JiggleEligibility.TryChooseTarget(5000, 5000, monitor, out _));
        }

        [Theory]
        [InlineData(0, 0, 1920)]
        [InlineData(0, 0, 3840)]
        [InlineData(-1920, 0, 3840)]
        [InlineData(-2560, 0, 5120)]
        [InlineData(0, 0, 1)]
        [InlineData(-32768, 0, 65536)]
        public void EveryPixelSurvivesTheRoundTripWithoutDrift(int origin, int _, int extent)
        {
            // Sample the whole range for small extents, and the interesting parts of large ones.
            int step = extent > 4096 ? extent / 1024 : 1;

            for (int offset = 0; offset < extent; offset += step)
            {
                int coordinate = origin + offset;

                Assert.True(
                    VirtualDesktopCoordinates.TryNormalize(coordinate, origin, extent, out int normalized),
                    "Could not represent offset " + offset + " of extent " + extent);

                long recovered = VirtualDesktopCoordinates.Denormalize(normalized, extent);

                // This is the property the no-drift promise rests on: Windows must map the
                // normalized value back to exactly the pixel that was asked for.
                Assert.Equal(offset, recovered);
            }

            // The last pixel matters most, because endpoint scaling gets it wrong.
            int lastOffset = extent - 1;
            Assert.True(VirtualDesktopCoordinates.TryNormalize(origin + lastOffset, origin, extent, out int lastNormalized));
            Assert.Equal(lastOffset, VirtualDesktopCoordinates.Denormalize(lastNormalized, extent));
            Assert.InRange(lastNormalized, 0, VirtualDesktopCoordinates.NormalizedMax);
        }

        [Fact]
        public void AnExtentBeyondTheNormalizedRangeReportsUnsupportedGeometryRatherThanGuessing()
        {
            // More pixels than the 0 to 65535 space can address individually. Rather than move
            // the pointer approximately, the app declines.
            // Twice as many pixels as the 0 to 65535 space has values, so each normalized value
            // covers two pixels and only the even ones can be addressed exactly.
            const int Extent = 131072;

            Assert.True(VirtualDesktopCoordinates.TryNormalize(1024, 0, Extent, out _));
            Assert.False(VirtualDesktopCoordinates.TryNormalize(1025, 0, Extent, out _));

            int unrepresentable = 0;
            for (int offset = 0; offset < 4096; offset++)
            {
                if (!VirtualDesktopCoordinates.TryNormalize(offset, 0, Extent, out _))
                {
                    unrepresentable++;
                }
            }

            // Rather than move the pointer approximately, the app declines these.
            Assert.Equal(2048, unrepresentable);
        }

        [Fact]
        public void DegenerateBoundsAreRejected()
        {
            Assert.False(VirtualDesktopCoordinates.TryNormalize(0, 0, 0, out _));
            Assert.False(VirtualDesktopCoordinates.TryNormalize(0, 0, -1920, out _));

            var empty = new VirtualBounds(0, 0, 0, 0);
            Assert.False(empty.IsUsable);
            Assert.False(VirtualDesktopCoordinates.TryNormalizePoint(0, 0, empty, out _, out _));
        }

        [Fact]
        public void APointOutsideTheVirtualDesktopIsRejected()
        {
            var bounds = new VirtualBounds(0, 0, 1920, 1080);

            Assert.False(VirtualDesktopCoordinates.TryNormalizePoint(-1, 0, bounds, out _, out _));
            Assert.False(VirtualDesktopCoordinates.TryNormalizePoint(1920, 0, bounds, out _, out _));
            Assert.True(VirtualDesktopCoordinates.TryNormalizePoint(1919, 1079, bounds, out _, out _));
        }


        [LiveInputFact]
        public void AThousandBatchesLeaveThePointerExactlyWhereItStarted()
        {
            // This is the promise the coordinate mapping exists to keep. It runs only on a
            // dedicated desktop, because it really does move the pointer.
            var device = new MouseJiggler.Windows.Input.MouseJigglerDevice();

            Assert.True(NativeMethods.GetCursorPos(out NativeMethods.POINT start));

            int sent = 0;
            for (int i = 0; i < 1000; i++)
            {
                MouseJiggler.Core.Abstractions.OperationResult result = device.SendJiggle();
                if (result.Succeeded)
                {
                    sent++;
                }
            }

            // SendInput queues the events rather than applying them before it returns, so the
            // final position has to be read after the queue drains. Without this wait the test
            // can catch the pointer between the two halves of the last batch and report a
            // one-pixel drift that is not there.
            NativeMethods.POINT end = WaitForCursorToSettle();

            Assert.True(sent > 0, "No batch was sent; the desktop may not be interactive.");
            Assert.Equal(start.X, end.X);
            Assert.Equal(start.Y, end.Y);
        }

        private static NativeMethods.POINT WaitForCursorToSettle()
        {
            NativeMethods.GetCursorPos(out NativeMethods.POINT previous);

            for (int attempt = 0; attempt < 100; attempt++)
            {
                System.Threading.Thread.Sleep(10);
                NativeMethods.GetCursorPos(out NativeMethods.POINT current);

                if (current.X == previous.X && current.Y == previous.Y)
                {
                    return current;
                }

                previous = current;
            }

            return previous;
        }

        [Fact]
        public void TheNativeInputStructuresAreTheSizeWindowsExpectsOnX64()
        {
            Assert.Equal(8, IntPtr.Size);

            // INPUT is 40 bytes on x64. A wrong size makes SendInput fail rather than
            // misbehave, but it fails every single time, so it is worth asserting here.
            Assert.Equal(40, Marshal.SizeOf(typeof(MouseJiggler.Windows.Interop.NativeMethods.INPUT)));
            Assert.Equal(32, Marshal.SizeOf(typeof(MouseJiggler.Windows.Interop.NativeMethods.MOUSEINPUT)));
            Assert.Equal(8, Marshal.SizeOf(typeof(MouseJiggler.Windows.Interop.NativeMethods.LASTINPUTINFO)));
        }
    }
}
