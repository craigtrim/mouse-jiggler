using System;
using System.Diagnostics.CodeAnalysis;

namespace MouseJiggler.Core.Input
{
    /// <summary>A monitor rectangle in physical pixels.</summary>
    public sealed class MonitorBounds
    {
        public MonitorBounds(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Left { get; }

        public int Top { get; }

        /// <summary>Exclusive, as Windows reports it.</summary>
        public int Right { get; }

        /// <summary>Exclusive.</summary>
        public int Bottom { get; }

        public int Width => Right - Left;

        public int Height => Bottom - Top;

        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }

    /// <summary>Where to move the pointer for one jiggle, expressed in physical pixels.</summary>
    public sealed class JiggleTarget
    {
        public JiggleTarget(int fromX, int fromY, int toX, int toY)
        {
            FromX = fromX;
            FromY = fromY;
            ToX = toX;
            ToY = toY;
        }

        public int FromX { get; }

        public int FromY { get; }

        public int ToX { get; }

        public int ToY { get; }
    }

    /// <summary>
    /// The pure half of jiggling: when it is due, and where the pointer should go. Keeping this
    /// free of native calls is what lets the awkward cases be tested without a real desktop.
    /// </summary>
    public static class JiggleEligibility
    {
        /// <summary>
        /// Computes idle age from the raw tick values, in unsigned arithmetic so the 32-bit
        /// counter wrapping to zero after roughly 49 days cannot produce a negative age.
        /// </summary>
        /// <remarks>
        /// A delta larger than <see cref="int.MaxValue"/> is far more likely to be a stale or
        /// inconsistent reading than a genuinely idle machine, so it is treated as unknown.
        /// Refusing to move is the conservative answer: a missed jiggle is a minor annoyance,
        /// while an unexpected one during real work is not.
        /// </remarks>
        public static bool TryComputeIdleMilliseconds(uint nowTicks, uint lastInputTicks, out uint idleMilliseconds)
        {
            unchecked
            {
                uint delta = nowTicks - lastInputTicks;

                if (delta > int.MaxValue)
                {
                    idleMilliseconds = 0;
                    return false;
                }

                idleMilliseconds = delta;
                return true;
            }
        }

        /// <summary>
        /// Whether a jiggle is due. Both thresholds are the configured interval: the session
        /// must have been idle that long, and that long must have passed since the last batch.
        /// </summary>
        public static bool IsDue(uint idleMilliseconds, long monotonicNowMs, long graceDeadlineMs, long lastJiggleMs, int intervalSeconds)
        {
            if (intervalSeconds <= 0)
            {
                return false;
            }

            long intervalMs = intervalSeconds * 1000L;

            // Activation, resume, unlock and settings changes all set a fresh deadline, so the
            // app never moves the pointer the instant it starts merely because the session was
            // already idle beforehand.
            if (monotonicNowMs < graceDeadlineMs)
            {
                return false;
            }

            if (idleMilliseconds < intervalMs)
            {
                return false;
            }

            if (lastJiggleMs > 0 && (monotonicNowMs - lastJiggleMs) < intervalMs)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Picks the adjacent pixel. One to the right when that stays on the same monitor,
        /// otherwise one to the left; a monitor only one pixel wide moves vertically instead.
        /// Monitor bounds are used rather than the work area, so the taskbar does not shrink
        /// the space the pointer may occupy.
        /// </summary>
        public static bool TryChooseTarget(
            int x, int y, MonitorBounds monitor, [NotNullWhen(true)] out JiggleTarget? target)
        {
            if (monitor == null)
            {
                throw new ArgumentNullException(nameof(monitor));
            }

            target = null;

            if (monitor.Width <= 0 || monitor.Height <= 0 || !monitor.Contains(x, y))
            {
                return false;
            }

            if (x + 1 < monitor.Right)
            {
                target = new JiggleTarget(x, y, x + 1, y);
                return true;
            }

            if (x - 1 >= monitor.Left)
            {
                target = new JiggleTarget(x, y, x - 1, y);
                return true;
            }

            if (y + 1 < monitor.Bottom)
            {
                target = new JiggleTarget(x, y, x, y + 1);
                return true;
            }

            if (y - 1 >= monitor.Top)
            {
                target = new JiggleTarget(x, y, x, y - 1);
                return true;
            }

            // A single-pixel monitor has no neighbour. Skipping is correct; enlarging the
            // movement to make it fit would not be.
            return false;
        }
    }
}
