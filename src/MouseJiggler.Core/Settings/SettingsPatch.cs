using System;

namespace MouseJiggler.Core.Settings
{
    /// <summary>
    /// A change to preference fields only. The Settings form can express nothing else: it holds
    /// no way to set <see cref="SettingsV1.Stopped"/> or <see cref="SettingsV1.RunMode"/>, so a
    /// stale form cannot restart a stopped app by saving an old copy of the whole document.
    /// Start, Stop and mode changes travel as commands instead.
    /// </summary>
    public sealed class SettingsPatch
    {
        public bool? ScheduleEnabled { get; set; }

        public string? ScheduleStart { get; set; }

        public string? ScheduleEnd { get; set; }

        public int? DayMask { get; set; }

        public bool? PauseOnBattery { get; set; }

        public bool? KeepDisplayOn { get; set; }

        public bool? JiggleMouse { get; set; }

        public int? IntervalSeconds { get; set; }

        public bool? DiagnosticLogging { get; set; }

        /// <summary>True when this patch would change nothing.</summary>
        public bool IsEmpty =>
            !ScheduleEnabled.HasValue &&
            ScheduleStart == null &&
            ScheduleEnd == null &&
            !DayMask.HasValue &&
            !PauseOnBattery.HasValue &&
            !KeepDisplayOn.HasValue &&
            !JiggleMouse.HasValue &&
            !IntervalSeconds.HasValue &&
            !DiagnosticLogging.HasValue;

        /// <summary>
        /// Produces the candidate document. The revision is assigned by the store under its
        /// lock, so it is carried through unchanged here.
        /// </summary>
        public SettingsV1 ApplyTo(SettingsV1 current)
        {
            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            return new SettingsV1(
                current.SchemaVersion,
                current.Revision,
                current.Stopped,
                current.RunMode,
                ScheduleEnabled ?? current.ScheduleEnabled,
                ScheduleStart ?? current.ScheduleStart,
                ScheduleEnd ?? current.ScheduleEnd,
                DayMask ?? current.DayMask,
                PauseOnBattery ?? current.PauseOnBattery,
                KeepDisplayOn ?? current.KeepDisplayOn,
                JiggleMouse ?? current.JiggleMouse,
                IntervalSeconds ?? current.IntervalSeconds,
                DiagnosticLogging ?? current.DiagnosticLogging,
                current.StartupInitialized,
                current.FirstRunCompleted);
        }
    }

    /// <summary>
    /// Intent changes, which only commands may make. These are separate from
    /// <see cref="SettingsPatch"/> so that no code path can express both at once by accident.
    /// </summary>
    public static class SettingsIntent
    {
        public static SettingsV1 WithStopped(SettingsV1 current, bool stopped)
        {
            return Rebuild(current, stopped, current.RunMode, current.StartupInitialized, current.FirstRunCompleted);
        }

        public static SettingsV1 WithMode(SettingsV1 current, RunMode mode, bool stopped)
        {
            return Rebuild(current, stopped, mode, current.StartupInitialized, current.FirstRunCompleted);
        }

        public static SettingsV1 WithStartupInitialized(SettingsV1 current, bool startupInitialized)
        {
            return Rebuild(current, current.Stopped, current.RunMode, startupInitialized, current.FirstRunCompleted);
        }

        public static SettingsV1 WithFirstRunCompleted(SettingsV1 current, bool firstRunCompleted)
        {
            return Rebuild(current, current.Stopped, current.RunMode, current.StartupInitialized, firstRunCompleted);
        }

        /// <summary>Assigns the next revision. Only the store calls this, and only under its lock.</summary>
        public static SettingsV1 WithRevision(SettingsV1 current, long revision)
        {
            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            return new SettingsV1(
                current.SchemaVersion,
                revision,
                current.Stopped,
                current.RunMode,
                current.ScheduleEnabled,
                current.ScheduleStart,
                current.ScheduleEnd,
                current.DayMask,
                current.PauseOnBattery,
                current.KeepDisplayOn,
                current.JiggleMouse,
                current.IntervalSeconds,
                current.DiagnosticLogging,
                current.StartupInitialized,
                current.FirstRunCompleted);
        }

        private static SettingsV1 Rebuild(SettingsV1 current, bool stopped, RunMode mode, bool startupInitialized, bool firstRunCompleted)
        {
            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            return new SettingsV1(
                current.SchemaVersion,
                current.Revision,
                stopped,
                mode,
                current.ScheduleEnabled,
                current.ScheduleStart,
                current.ScheduleEnd,
                current.DayMask,
                current.PauseOnBattery,
                current.KeepDisplayOn,
                current.JiggleMouse,
                current.IntervalSeconds,
                current.DiagnosticLogging,
                startupInitialized,
                firstRunCompleted);
        }
    }
}
