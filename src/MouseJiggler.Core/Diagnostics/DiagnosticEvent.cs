using System;
using System.Collections.Generic;
using MouseJiggler.Core.Abstractions;

namespace MouseJiggler.Core.Diagnostics
{
    /// <summary>
    /// One sanitized record. It carries a subsystem, a stable code and an optional Win32 error
    /// number, and nothing else. There is deliberately no field for a message, a path or an
    /// exception, because a free-text field is how user data ends up in a log.
    /// </summary>
    public sealed class DiagnosticEvent
    {
        public DiagnosticEvent(DateTime utcTimestamp, FaultSubsystem subsystem, string code, int? nativeErrorCode)
            : this(utcTimestamp, subsystem, code, nativeErrorCode, 0)
        {
        }

        private DiagnosticEvent(
            DateTime utcTimestamp, FaultSubsystem subsystem, string code, int? nativeErrorCode, int suppressedRepeats)
        {
            UtcTimestamp = utcTimestamp;
            Subsystem = subsystem;
            Code = code ?? throw new ArgumentNullException(nameof(code));
            NativeErrorCode = nativeErrorCode;
            SuppressedRepeats = suppressedRepeats;
        }

        public DateTime UtcTimestamp { get; }

        public FaultSubsystem Subsystem { get; }

        public string Code { get; }

        public int? NativeErrorCode { get; }

        /// <summary>
        /// How many identical records were dropped since the last one of this code was kept.
        /// </summary>
        /// <remarks>
        /// Without this a summary is indistinguishable from a single event, and one failure a
        /// minute reads exactly like sixty. That difference is most of what the ring is for when
        /// someone is deciding whether a subsystem is struggling or has stopped entirely.
        /// </remarks>
        public int SuppressedRepeats { get; }

        /// <summary>The same event, standing in for the repeats that were dropped before it.</summary>
        public DiagnosticEvent Summarising(int suppressedRepeats)
        {
            if (suppressedRepeats < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(suppressedRepeats));
            }

            return suppressedRepeats == 0
                ? this
                : new DiagnosticEvent(UtcTimestamp, Subsystem, Code, NativeErrorCode, suppressedRepeats);
        }
    }

    /// <summary>
    /// The most recent events, kept in memory whether or not logging to disk is switched on, so
    /// Copy diagnostics has something to offer without asking the user to reproduce the problem
    /// with logging enabled.
    /// </summary>
    public sealed class DiagnosticRing
    {
        public const int Capacity = 100;

        /// <summary>Repeats of one code are summarised no more often than this.</summary>
        public static readonly TimeSpan RepeatSummaryInterval = TimeSpan.FromMinutes(1);

        private readonly Queue<DiagnosticEvent> _events = new Queue<DiagnosticEvent>();
        private readonly Dictionary<string, RepeatState> _repeats = new Dictionary<string, RepeatState>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        /// <summary>
        /// Records an event unless it is a repeat inside the summary window. A failing subsystem
        /// can produce an event every second, and without this the ring would hold one second of
        /// history instead of the useful minutes around the failure.
        /// </summary>
        public bool Record(DiagnosticEvent entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            lock (_gate)
            {
                string key = entry.Subsystem + "|" + entry.Code;

                if (_repeats.TryGetValue(key, out RepeatState? state))
                {
                    if (entry.UtcTimestamp - state!.LastRecordedUtc < RepeatSummaryInterval)
                    {
                        state.Suppressed++;
                        return false;
                    }

                    // This one stands in for the ones that were dropped, and says how many.
                    entry = entry.Summarising(state.Suppressed);

                    state.LastRecordedUtc = entry.UtcTimestamp;
                    state.Suppressed = 0;
                }
                else
                {
                    _repeats[key] = new RepeatState { LastRecordedUtc = entry.UtcTimestamp };
                }

                _events.Enqueue(entry);

                while (_events.Count > Capacity)
                {
                    _events.Dequeue();
                }

                return true;
            }
        }

        /// <summary>Clears the repeat suppression for a code, so a recovery is always recorded.</summary>
        public void NoteRecovery(FaultSubsystem subsystem, string code)
        {
            lock (_gate)
            {
                _repeats.Remove(subsystem + "|" + code);
            }
        }

        public IReadOnlyList<DiagnosticEvent> Snapshot()
        {
            lock (_gate)
            {
                return new List<DiagnosticEvent>(_events);
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _events.Count;
                }
            }
        }

        private sealed class RepeatState
        {
            public DateTime LastRecordedUtc { get; set; }

            public int Suppressed { get; set; }
        }
    }
}
