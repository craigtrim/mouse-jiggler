using System;
using MouseJiggler.Core.Commands;
using MouseJiggler.Core.Settings;

namespace MouseJiggler.Core.Activity
{
    /// <summary>What a command needs the store to persist, and whether effects may be applied yet.</summary>
    public sealed class CommandOutcome
    {
        public CommandOutcome(SettingsV1? settingsToPersist, bool requiresPersistenceBeforeEffects, bool clearsEffectsImmediately, bool endsProcess)
        {
            SettingsToPersist = settingsToPersist;
            RequiresPersistenceBeforeEffects = requiresPersistenceBeforeEffects;
            ClearsEffectsImmediately = clearsEffectsImmediately;
            EndsProcess = endsProcess;
        }

        /// <summary>Null when the command changes no saved state.</summary>
        public SettingsV1? SettingsToPersist { get; }

        /// <summary>An enabling command may not act until its change is committed.</summary>
        public bool RequiresPersistenceBeforeEffects { get; }

        /// <summary>Stop drops everything in memory first and persists afterwards.</summary>
        public bool ClearsEffectsImmediately { get; }

        public bool EndsProcess { get; }
    }

    /// <summary>
    /// Turns a command into the saved-state change it implies. Keeping this separate from the
    /// policy means the rule "an enable action never starts until it has been saved" is stated
    /// once, rather than repeated at every call site that can start the app.
    /// </summary>
    public static class ActivityCommandReducer
    {
        public static CommandOutcome Reduce(CommandKind kind, SettingsV1 current)
        {
            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            switch (kind)
            {
                case CommandKind.Start:
                    // Resume whatever mode was saved. Scheduled with its schedule switched off
                    // means continuous.
                    return Enabling(SettingsIntent.WithStopped(current, false));

                case CommandKind.StartNow:
                    // The explicit override: Manual, eligible at once, and it outlives a
                    // schedule boundary until Stop or an explicit return to the schedule.
                    return Enabling(SettingsIntent.WithMode(current, RunMode.Manual, stopped: false));

                case CommandKind.UseSchedule:
                    return Enabling(SettingsIntent.WithMode(current, RunMode.Scheduled, stopped: false));

                case CommandKind.Stop:
                    return new CommandOutcome(
                        SettingsIntent.WithStopped(current, true),
                        requiresPersistenceBeforeEffects: false,
                        clearsEffectsImmediately: true,
                        endsProcess: false);

                case CommandKind.ApplySettings:
                    // Preference edits alone. The stopped flag and the mode are carried through
                    // untouched, so saving can never start the app.
                    return new CommandOutcome(null, false, false, false);

                case CommandKind.Retry:
                    // Retry never implies a Start; it only re-attempts work that already failed.
                    return new CommandOutcome(null, false, false, false);

                case CommandKind.Exit:
                    return new CommandOutcome(null, false, true, true);

                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private static CommandOutcome Enabling(SettingsV1 settings)
        {
            return new CommandOutcome(settings, requiresPersistenceBeforeEffects: true, clearsEffectsImmediately: false, endsProcess: false);
        }
    }
}
