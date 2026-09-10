using System;

namespace MouseJiggler.Core.Settings
{
    /// <summary>How the app decides when it is eligible to run.</summary>
    public enum RunMode
    {
        /// <summary>Runs until explicitly stopped, ignoring the saved schedule.</summary>
        Manual = 0,

        /// <summary>Runs inside the saved schedule, or continuously when scheduling is off.</summary>
        Scheduled = 1,
    }

    /// <summary>
    /// The saved settings document, version 1. Immutable: every change produces a new
    /// instance with a higher revision. <see cref="Stopped"/> is deliberately separate from
    /// <see cref="RunMode"/> so that editing preferences can never start a stopped app.
    /// </summary>
    public sealed class SettingsV1
    {
        public const int CurrentSchemaVersion = 1;

        public const int MinIntervalSeconds = 5;
        public const int MaxIntervalSeconds = 300;
        public const int DefaultIntervalSeconds = 30;

        /// <summary>All seven days selected.</summary>
        public const int AllDaysMask = 127;

        public SettingsV1(
            int schemaVersion,
            long revision,
            bool stopped,
            RunMode runMode,
            bool scheduleEnabled,
            string scheduleStart,
            string scheduleEnd,
            int dayMask,
            bool pauseOnBattery,
            bool keepDisplayOn,
            bool jiggleMouse,
            int intervalSeconds,
            bool diagnosticLogging,
            bool startupInitialized,
            bool firstRunCompleted)
        {
            SchemaVersion = schemaVersion;
            Revision = revision;
            Stopped = stopped;
            RunMode = runMode;
            ScheduleEnabled = scheduleEnabled;
            ScheduleStart = scheduleStart ?? throw new ArgumentNullException(nameof(scheduleStart));
            ScheduleEnd = scheduleEnd ?? throw new ArgumentNullException(nameof(scheduleEnd));
            DayMask = dayMask;
            PauseOnBattery = pauseOnBattery;
            KeepDisplayOn = keepDisplayOn;
            JiggleMouse = jiggleMouse;
            IntervalSeconds = intervalSeconds;
            DiagnosticLogging = diagnosticLogging;
            StartupInitialized = startupInitialized;
            FirstRunCompleted = firstRunCompleted;
        }

        public int SchemaVersion { get; }

        /// <summary>Monotonically increasing; one increment per committed update.</summary>
        public long Revision { get; }

        /// <summary>
        /// Explicit user Stop. Only a Start command clears this; never a schedule boundary,
        /// power change, unlock, settings save, restart or sign-in.
        /// </summary>
        public bool Stopped { get; }

        public RunMode RunMode { get; }

        public bool ScheduleEnabled { get; }

        /// <summary>Invariant HH:mm. Inclusive start of the daily window.</summary>
        public string ScheduleStart { get; }

        /// <summary>Invariant HH:mm. Exclusive end; earlier than start means it crosses midnight.</summary>
        public string ScheduleEnd { get; }

        /// <summary>Monday=1, Tuesday=2, Wednesday=4, Thursday=8, Friday=16, Saturday=32, Sunday=64.</summary>
        public int DayMask { get; }

        public bool PauseOnBattery { get; }

        public bool KeepDisplayOn { get; }

        public bool JiggleMouse { get; }

        /// <summary>Both the idle threshold and the minimum gap between jiggles.</summary>
        public int IntervalSeconds { get; }

        public bool DiagnosticLogging { get; }

        /// <summary>Informational record that installer initialization ran; never permission to register startup.</summary>
        public bool StartupInitialized { get; }

        /// <summary>False until the one-time Settings window and tray explanation have been shown.</summary>
        public bool FirstRunCompleted { get; }

        /// <summary>
        /// First-launch defaults: stopped, Scheduled mode with scheduling switched off, and a
        /// prepared 08:00-17:00 window on all seven days.
        /// </summary>
        public static SettingsV1 CreateDefault()
        {
            return new SettingsV1(
                schemaVersion: CurrentSchemaVersion,
                revision: 1,
                stopped: true,
                runMode: RunMode.Scheduled,
                scheduleEnabled: false,
                scheduleStart: "08:00",
                scheduleEnd: "17:00",
                dayMask: AllDaysMask,
                pauseOnBattery: true,
                keepDisplayOn: true,
                jiggleMouse: true,
                intervalSeconds: DefaultIntervalSeconds,
                diagnosticLogging: false,
                startupInitialized: false,
                firstRunCompleted: false);
        }
    }
}
