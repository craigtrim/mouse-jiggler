using System;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Environment;
using MouseJiggler.Core.Scheduling;
using MouseJiggler.Core.Settings;

namespace MouseJiggler.Core.Activity
{
    /// <summary>
    /// The one place that decides what the app should be doing. Pure: it reads only its two
    /// arguments and returns a description of the desired state. Applying that description is
    /// the coordinator's job, which is what keeps this testable without a real desktop.
    /// </summary>
    public sealed class ActivityPolicy : IActivityPolicy
    {
        private readonly CachedTransitionCalculator _transitions = new CachedTransitionCalculator();

        public DesiredEffects Evaluate(SettingsV1 settings, EnvironmentSnapshot environment)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (environment == null)
            {
                throw new ArgumentNullException(nameof(environment));
            }

            // The gates are ordered, and the first one that applies becomes the visible status.
            // Anything that stops the app must be checked before anything that would start it.

            if (environment.Exiting || environment.Suspended)
            {
                return DesiredEffects.None(StatusCode.Stopped);
            }

            if (!environment.SettingsAvailable)
            {
                return DesiredEffects.None(StatusCode.Error);
            }

            if (settings.Stopped)
            {
                return DesiredEffects.None(StatusCode.Stopped);
            }

            if (environment.Session == SessionState.Disconnected || environment.Session == SessionState.Unknown)
            {
                return DesiredEffects.None(StatusCode.SessionUnavailable);
            }

            if (settings.PauseOnBattery && environment.Power != PowerSource.External)
            {
                // Unknown power blocks only because the user asked to run on external power
                // alone. With that box cleared, an unreadable power status does not gate.
                StatusCode reason = environment.Power == PowerSource.Unknown
                    ? StatusCode.PowerStatusUnavailable
                    : StatusCode.PausedOnBattery;

                return DesiredEffects.None(reason);
            }

            DateTime? nextTransition = _transitions.Get(settings, environment.UtcNow, environment.LocalTimeZone, out TransitionKind kind);
            bool scheduleGates = settings.RunMode == RunMode.Scheduled && settings.ScheduleEnabled;

            if (scheduleGates && !DailySchedule.IsWithinWindow(settings, environment.UtcNow, environment.LocalTimeZone))
            {
                return new DesiredEffects(
                    systemAwake: false,
                    displayAwake: false,
                    mayJiggle: false,
                    status: StatusCode.WaitingForSchedule,
                    nextTransitionUtc: nextTransition,
                    nextTransitionKind: kind,
                    wakeRequestFailed: false,
                    inputRequestFailed: false);
            }

            return EligibleEffects(settings, environment, nextTransition, kind);
        }

        private static DesiredEffects EligibleEffects(
            SettingsV1 settings,
            EnvironmentSnapshot environment,
            DateTime? nextTransition,
            TransitionKind kind)
        {
            bool locked = environment.Session == SessionState.Locked;

            // A locked session keeps the machine awake for work already running, but must not
            // move the pointer or hold the display on. That holds even with both user-facing
            // switches off, because the system request is what "keep the computer awake" means.
            bool systemAwake = true;
            bool displayAwake = !locked && settings.KeepDisplayOn;
            bool mayJiggle = !locked
                             && settings.JiggleMouse
                             && environment.InputDesktopAvailable
                             && environment.InputCapability != CapabilityState.Latched;

            bool wakeFailed = environment.WakeCapability != CapabilityState.Ok;
            bool inputFailed = settings.JiggleMouse && environment.InputCapability == CapabilityState.Latched;

            StatusCode status;
            if (wakeFailed)
            {
                // Keeping the computer awake is the mechanism everything else rests on, so its
                // failure is an error rather than a footnote on a running app.
                status = StatusCode.Error;
            }
            else if (locked)
            {
                status = StatusCode.LockedKeepingAwake;
            }
            else if (settings.RunMode == RunMode.Manual)
            {
                status = StatusCode.RunningManual;
            }
            else if (settings.ScheduleEnabled)
            {
                status = StatusCode.RunningScheduled;
            }
            else
            {
                status = StatusCode.RunningContinuous;
            }

            return new DesiredEffects(
                systemAwake,
                displayAwake,
                mayJiggle,
                status,
                nextTransition,
                kind,
                wakeFailed,
                inputFailed);
        }

        /// <summary>Drops cached schedule data after a clock or time zone change.</summary>
        public void InvalidateScheduleCache() => _transitions.Invalidate();
    }
}
