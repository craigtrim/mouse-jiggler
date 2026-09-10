using System;
using MouseJiggler.Core.Abstractions;

namespace MouseJiggler.Core.Diagnostics
{
    /// <summary>
    /// A fault that is currently affecting the app, with when it started and last recurred.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="DiagnosticEvent"/> because they answer different questions. An
    /// event is a thing that happened; a fault is a condition that is still true. The status
    /// line and the Retry button are driven by the second, and a log is a list of the first.
    ///
    /// It counts occurrences rather than storing one record per repeat. A wake request failing
    /// every second for an hour is one fault that has happened three and a half thousand times,
    /// not three and a half thousand faults, and reporting it the second way is how a diagnostic
    /// payload becomes unreadable at the moment it is most needed.
    /// </remarks>
    public sealed class RuntimeFault
    {
        public RuntimeFault(
            FaultSubsystem subsystem,
            string code,
            DateTime firstUtc,
            DateTime lastUtc,
            bool retryable,
            int occurrences,
            int? nativeErrorCode)
        {
            Subsystem = subsystem;
            Code = code ?? throw new ArgumentNullException(nameof(code));
            FirstUtc = firstUtc;
            LastUtc = lastUtc;
            Retryable = retryable;
            Occurrences = occurrences;
            NativeErrorCode = nativeErrorCode;
        }

        public FaultSubsystem Subsystem { get; }

        /// <summary>A stable short identifier, never a message and never a path.</summary>
        public string Code { get; }

        public DateTime FirstUtc { get; }

        public DateTime LastUtc { get; }

        /// <summary>False when only an explicit user action can clear this.</summary>
        public bool Retryable { get; }

        public int Occurrences { get; }

        /// <summary>An optional sanitized Win32 error number.</summary>
        public int? NativeErrorCode { get; }
    }
}
