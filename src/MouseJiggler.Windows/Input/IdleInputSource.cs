using System;
using System.Runtime.InteropServices;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Input;
using MouseJiggler.Windows.Interop;

namespace MouseJiggler.Windows.Input
{
    /// <summary>
    /// Reads how long the session has been idle.
    /// </summary>
    /// <remarks>
    /// GetLastInputInfo is a session-local observation that counts synthesized input as well as
    /// physical input, so the app's own jiggle resets it. That is accepted rather than worked
    /// around: distinguishing real input would mean installing a global hook and watching what
    /// the user types, which this app will not do. The cost is only that a jiggle is sometimes
    /// delayed by another program's injected input, which is the harmless direction to err in.
    /// </remarks>
    public sealed class IdleInputSource : IIdleInputSource
    {
        public bool TryGetIdleMilliseconds(out uint idleMilliseconds)
        {
            idleMilliseconds = 0;

            var info = new NativeMethods.LASTINPUTINFO
            {
                cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.LASTINPUTINFO)),
            };

            if (!NativeMethods.GetLastInputInfo(ref info))
            {
                return false;
            }

            uint now = NativeMethods.GetTickCount();
            return JiggleEligibility.TryComputeIdleMilliseconds(now, info.dwTime, out idleMilliseconds);
        }
    }

    /// <summary>Uptime-based time, used for every duration the app measures.</summary>
    public sealed class MonotonicClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;

        /// <summary>
        /// GetTickCount64 rather than Environment.TickCount, which is 32-bit and wraps, or
        /// Stopwatch, whose ticks cannot be compared against the input timestamps.
        /// </summary>
        public long MonotonicMilliseconds => unchecked((long)NativeMethods.GetTickCount64());

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
    }
}
