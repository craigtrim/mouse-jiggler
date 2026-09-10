using System;

namespace MouseJiggler.App.Runtime
{
    /// <summary>
    /// When to try again, for the two things that can fail and be retried.
    /// </summary>
    /// <remarks>
    /// The keep-awake request and an unwritten Stop both back off on the same schedule, and they
    /// back off independently: a display that will not stay on must not delay a Stop reaching
    /// disk, and a full disk must not stop the wake request being retried.
    ///
    /// This exists as its own type because the cadence is the part that is invisible when it is
    /// wrong. A backoff that has degenerated into a per-tick loop still recovers and still shows
    /// the same status; the only thing that differs is how many times it calls a native API, and
    /// nothing on screen says so. Keeping the schedule in one place, with the deadlines exposed,
    /// means it can be asserted rather than assumed.
    ///
    /// Everything here is monotonic milliseconds. Never wall clock: the user changing the clock,
    /// or a daylight saving jump, must not turn a one second wait into an hour or a negative
    /// number.
    /// </remarks>
    public sealed class RecoveryCoordinator
    {
        /// <summary>The first three waits, in seconds; every later one repeats the last.</summary>
        private static readonly int[] ScheduleSeconds = { 1, 5, 30 };

        private const int RepeatingSeconds = 30;

        private long _wakeNextRetryMs;
        private int _wakeAttempt;

        private bool _stopPending;
        private long _stopNextRetryMs;
        private int _stopAttempt;

        /// <summary>The waits this schedule produces, for tests and for documentation.</summary>
        public static int[] Schedule => new[] { 1, 5, 30, 30 };

        /// <summary>
        /// Whether a failing wake request may be attempted again.
        /// </summary>
        /// <remarks>
        /// True when nothing has failed yet, so the ordinary path is not gated by a backoff that
        /// has never been armed.
        /// </remarks>
        public bool IsWakeRetryDue(long nowMs)
        {
            return _wakeNextRetryMs == 0 || nowMs >= _wakeNextRetryMs;
        }

        /// <summary>Arms the next wait after a failed acquire or release.</summary>
        public void NoteWakeFailure(long nowMs)
        {
            int seconds = _wakeAttempt < ScheduleSeconds.Length
                ? ScheduleSeconds[_wakeAttempt]
                : RepeatingSeconds;

            _wakeAttempt++;
            _wakeNextRetryMs = nowMs + (seconds * 1000L);
        }

        /// <summary>
        /// Clears the backoff. The next failure starts at one second again rather than resuming
        /// at thirty, because a request that worked and then failed is a new problem.
        /// </summary>
        public void NoteWakeSuccess()
        {
            _wakeAttempt = 0;
            _wakeNextRetryMs = 0;
        }

        /// <summary>True while a Stop is held in memory but is not on disk.</summary>
        public bool HasUncommittedStop => _stopPending;

        /// <summary>
        /// Records that a Stop could not be written, and arms the first retry.
        /// </summary>
        /// <remarks>
        /// This is the one failure the user has to be told about, because the old state comes
        /// back on restart. Retrying is what lets the warning clear itself when the disk
        /// recovers, rather than leaving a warning standing over a problem that has gone.
        /// </remarks>
        public void NoteStopWriteFailed(long nowMs)
        {
            _stopPending = true;
            _stopAttempt = 0;
            _stopNextRetryMs = nowMs + (ScheduleSeconds[0] * 1000L);
        }

        /// <summary>Whether an outstanding Stop write is due to be attempted again.</summary>
        public bool IsStopRetryDue(long nowMs)
        {
            return _stopPending && nowMs >= _stopNextRetryMs;
        }

        /// <summary>Arms the next wait, before the attempt rather than after it.</summary>
        /// <remarks>
        /// Before, so that an attempt which blocks or throws still leaves a deadline in the
        /// future. Arming afterwards would mean a failure part way through left the retry due
        /// immediately, which is the busy loop this schedule exists to prevent.
        /// </remarks>
        public void NoteStopRetryStarted(long nowMs)
        {
            _stopAttempt++;

            int seconds = _stopAttempt < ScheduleSeconds.Length
                ? ScheduleSeconds[_stopAttempt]
                : RepeatingSeconds;

            _stopNextRetryMs = nowMs + (seconds * 1000L);
        }

        /// <summary>The Stop reached disk. Nothing is outstanding.</summary>
        public void NoteStopWritten()
        {
            _stopPending = false;
            _stopAttempt = 0;
            _stopNextRetryMs = 0;
        }

        /// <summary>
        /// Forgets everything. Used when the settings are reset, which supersedes any Stop that
        /// was waiting to be written to a file that no longer exists.
        /// </summary>
        public void Reset()
        {
            NoteWakeSuccess();
            NoteStopWritten();
        }
    }
}
