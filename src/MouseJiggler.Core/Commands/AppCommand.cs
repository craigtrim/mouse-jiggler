using System;

namespace MouseJiggler.Core.Commands
{
    /// <summary>The complete set of intent-changing commands. Timers and hotkeys are excluded from v1.</summary>
    public enum CommandKind
    {
        /// <summary>Clear Stop and resume the saved mode.</summary>
        Start = 0,

        /// <summary>Clear Stop and select Manual: eligible immediately, subject to power and session gates.</summary>
        StartNow = 1,

        /// <summary>Clear Stop and select Scheduled: the saved window, or continuous when scheduling is off.</summary>
        UseSchedule = 2,

        /// <summary>Clear effects immediately and persist stopped=true. Never auto-cleared.</summary>
        Stop = 3,

        /// <summary>Patch preference fields only. Never touches stopped or runMode.</summary>
        ApplySettings = 4,

        /// <summary>Retry failed capability or storage work. Never an implicit Start.</summary>
        Retry = 5,

        /// <summary>Release everything and end the process, preserving saved intent.</summary>
        Exit = 6,
    }

    /// <summary>
    /// A command plus the generation it was issued against. A queued callback may only act when
    /// its generation still matches, so work scheduled before a Stop, mode change, lock, suspend
    /// or disposal can never activate anything afterwards.
    /// </summary>
    public sealed class AppCommand
    {
        public AppCommand(CommandKind kind, long generation, Guid commandId)
        {
            if (generation < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(generation));
            }

            Kind = kind;
            Generation = generation;
            CommandId = commandId;
        }

        public CommandKind Kind { get; }

        public long Generation { get; }

        public Guid CommandId { get; }

        public static AppCommand Create(CommandKind kind, long generation)
        {
            return new AppCommand(kind, generation, Guid.NewGuid());
        }

        /// <summary>True when this command still speaks for the current state.</summary>
        public bool IsCurrent(long currentGeneration)
        {
            return Generation == currentGeneration;
        }
    }
}
