using System;

namespace MouseJiggler.Core.Environment
{
    /// <summary>External power as Windows reports it. Absence of a battery is not evidence of AC.</summary>
    public enum PowerSource
    {
        Unknown = 0,
        External = 1,
        Battery = 2,
    }

    /// <summary>Console/session state. Unknown suppresses effects until a query succeeds.</summary>
    public enum SessionState
    {
        Unknown = 0,
        ActiveUnlocked = 1,
        Locked = 2,
        Disconnected = 3,
    }

    /// <summary>Whether a requested capability is working, and if not, why not.</summary>
    public enum CapabilityState
    {
        Ok = 0,

        /// <summary>Failing but retried automatically while eligible.</summary>
        Transient = 1,

        /// <summary>Latched until explicit Retry, a mechanism change, or session recovery.</summary>
        Latched = 2,
    }

    /// <summary>
    /// One immutable observation of the world, passed to the pure evaluator. Nothing in Core
    /// reads global state directly; everything it needs arrives here.
    /// </summary>
    public sealed class EnvironmentSnapshot
    {
        public EnvironmentSnapshot(
            DateTime utcNow,
            TimeZoneInfo localTimeZone,
            long monotonicMilliseconds,
            PowerSource power,
            SessionState session,
            bool suspended,
            bool exiting,
            bool inputDesktopAvailable,
            CapabilityState wakeCapability,
            CapabilityState inputCapability,
            bool settingsAvailable)
        {
            if (utcNow.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("utcNow must be UTC.", nameof(utcNow));
            }

            UtcNow = utcNow;
            LocalTimeZone = localTimeZone ?? throw new ArgumentNullException(nameof(localTimeZone));
            MonotonicMilliseconds = monotonicMilliseconds;
            Power = power;
            Session = session;
            Suspended = suspended;
            Exiting = exiting;
            InputDesktopAvailable = inputDesktopAvailable;
            WakeCapability = wakeCapability;
            InputCapability = inputCapability;
            SettingsAvailable = settingsAvailable;
        }

        public DateTime UtcNow { get; }

        /// <summary>The current Windows local zone; never stored in settings.</summary>
        public TimeZoneInfo LocalTimeZone { get; }

        /// <summary>Uptime-based, never wall clock. Used for every duration comparison.</summary>
        public long MonotonicMilliseconds { get; }

        public PowerSource Power { get; }

        public SessionState Session { get; }

        public bool Suspended { get; }

        public bool Exiting { get; }

        public bool InputDesktopAvailable { get; }

        public CapabilityState WakeCapability { get; }

        public CapabilityState InputCapability { get; }

        /// <summary>False when the settings file is missing, corrupt, or of an unsupported schema.</summary>
        public bool SettingsAvailable { get; }
    }
}
