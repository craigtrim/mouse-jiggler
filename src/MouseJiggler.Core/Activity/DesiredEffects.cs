using System;

namespace MouseJiggler.Core.Activity
{
    /// <summary>
    /// The visible status. Computed by the evaluator, never inferred from a checkbox colour.
    /// App maps these to localized text; Core emits codes and data only.
    /// </summary>
    public enum StatusCode
    {
        Stopped = 0,
        WaitingForSchedule = 1,
        PausedOnBattery = 2,
        PowerStatusUnavailable = 3,
        SessionUnavailable = 4,
        RunningScheduled = 5,
        RunningContinuous = 6,
        RunningManual = 7,
        LockedKeepingAwake = 8,
        Error = 9,
    }

    /// <summary>What the next schedule transition does.</summary>
    public enum TransitionKind
    {
        None = 0,
        Start = 1,
        End = 2,
    }

    /// <summary>
    /// The evaluator's output: what the coordinator should request, and what to tell the user.
    /// Only the coordinator applies these; the evaluator itself has no side effects.
    /// </summary>
    public sealed class DesiredEffects
    {
        public DesiredEffects(
            bool systemAwake,
            bool displayAwake,
            bool mayJiggle,
            StatusCode status,
            DateTime? nextTransitionUtc,
            TransitionKind nextTransitionKind,
            bool wakeRequestFailed,
            bool inputRequestFailed)
        {
            if (nextTransitionUtc.HasValue && nextTransitionUtc.Value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("nextTransitionUtc must be UTC.", nameof(nextTransitionUtc));
            }

            SystemAwake = systemAwake;
            DisplayAwake = displayAwake;
            MayJiggle = mayJiggle;
            Status = status;
            NextTransitionUtc = nextTransitionUtc;
            NextTransitionKind = nextTransitionKind;
            WakeRequestFailed = wakeRequestFailed;
            InputRequestFailed = inputRequestFailed;
        }

        public bool SystemAwake { get; }

        public bool DisplayAwake { get; }

        public bool MayJiggle { get; }

        public StatusCode Status { get; }

        /// <summary>The next actual eligibility change, including DST jumps.</summary>
        public DateTime? NextTransitionUtc { get; }

        public TransitionKind NextTransitionKind { get; }

        /// <summary>A selected keep-awake request that Windows refused. Never reported as success.</summary>
        public bool WakeRequestFailed { get; }

        /// <summary>Selected jiggling has failed while other behaviour may still work.</summary>
        public bool InputRequestFailed { get; }

        /// <summary>Nothing requested; used for Stop, suspend and exit.</summary>
        public static DesiredEffects None(StatusCode status)
        {
            return new DesiredEffects(
                systemAwake: false,
                displayAwake: false,
                mayJiggle: false,
                status: status,
                nextTransitionUtc: null,
                nextTransitionKind: TransitionKind.None,
                wakeRequestFailed: false,
                inputRequestFailed: false);
        }
    }
}
