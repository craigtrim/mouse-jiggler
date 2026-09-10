using System;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Scheduling;
using MouseJiggler.Core.Settings;

namespace MouseJiggler.App
{
    /// <summary>
    /// The values on screen, independent of the controls holding them.
    /// </summary>
    /// <remarks>
    /// Times are minutes since midnight rather than a DateTime, because a date picker carries a
    /// date it has no business having: only the time of day is ever saved, and comparing two of
    /// them as DateTimes invites a comparison that happens to work today.
    /// </remarks>
    public sealed class SettingsDraft
    {
        public bool ScheduleEnabled { get; set; }

        public int StartMinutes { get; set; }

        public int EndMinutes { get; set; }

        /// <summary>Seven flags in the order the checkboxes appear, Monday first.</summary>
        public bool[] Days { get; set; } = new bool[7];

        public bool PauseOnBattery { get; set; }

        public bool KeepDisplayOn { get; set; }

        public bool JiggleMouse { get; set; }

        public int IntervalSeconds { get; set; }

        public bool DiagnosticLogging { get; set; }
    }

    /// <summary>
    /// Moves values between the saved document and the window, and answers the questions the
    /// window asks about them.
    /// </summary>
    /// <remarks>
    /// None of this touches Windows Forms, which is the point: the day mask, the overnight case
    /// and the shape of the patch are the parts that are easy to get subtly wrong and impossible
    /// to check by looking at a window. The patch it builds deliberately carries preferences
    /// only. Stopped and RunMode are intent, and a Save must never be able to start a stopped
    /// app because a checkbox happened to be ticked.
    /// </remarks>
    public static class SettingsPresenter
    {
        /// <summary>The days in the order the checkboxes appear. Monday first, as a week reads.</summary>
        public static readonly DayOfWeek[] DayOrder =
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
        };

        public static SettingsDraft ToDraft(SettingsV1 settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            var draft = new SettingsDraft
            {
                ScheduleEnabled = settings.ScheduleEnabled,
                StartMinutes = ParseMinutes(settings.ScheduleStart),
                EndMinutes = ParseMinutes(settings.ScheduleEnd),
                PauseOnBattery = settings.PauseOnBattery,
                KeepDisplayOn = settings.KeepDisplayOn,
                JiggleMouse = settings.JiggleMouse,
                IntervalSeconds = settings.IntervalSeconds,
                DiagnosticLogging = settings.DiagnosticLogging,
            };

            for (int i = 0; i < DayOrder.Length; i++)
            {
                draft.Days[i] = DailySchedule.IsDaySelected(settings.DayMask, DayOrder[i]);
            }

            return draft;
        }

        public static SettingsPatch ToPatch(SettingsDraft draft)
        {
            if (draft == null)
            {
                throw new ArgumentNullException(nameof(draft));
            }

            return new SettingsPatch
            {
                ScheduleEnabled = draft.ScheduleEnabled,
                ScheduleStart = SettingsValidator.FormatTimeOfDay(draft.StartMinutes),
                ScheduleEnd = SettingsValidator.FormatTimeOfDay(draft.EndMinutes),
                DayMask = BuildDayMask(draft.Days),
                PauseOnBattery = draft.PauseOnBattery,
                KeepDisplayOn = draft.KeepDisplayOn,
                JiggleMouse = draft.JiggleMouse,
                IntervalSeconds = draft.IntervalSeconds,
                DiagnosticLogging = draft.DiagnosticLogging,
            };
        }

        /// <summary>
        /// Validates the draft as though it had been saved, using the same rules the store uses.
        /// </summary>
        /// <remarks>
        /// The draft is applied to the current document and the real validator is run over the
        /// result, rather than reimplementing the rules for the window. A second copy of "a
        /// schedule that is on needs at least one day" would eventually disagree with the first,
        /// and the disagreement would show up as a Save button that is enabled for a document
        /// the store then rejects.
        /// </remarks>
        public static OperationResult ValidateDraft(SettingsDraft draft, SettingsV1 current)
        {
            if (draft == null)
            {
                throw new ArgumentNullException(nameof(draft));
            }

            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            return SettingsValidator.Validate(ToPatch(draft).ApplyTo(current));
        }

        public static int BuildDayMask(bool[] days)
        {
            if (days == null)
            {
                throw new ArgumentNullException(nameof(days));
            }

            int mask = 0;

            for (int i = 0; i < days.Length && i < DayOrder.Length; i++)
            {
                if (days[i])
                {
                    mask |= DailySchedule.DayBit(DayOrder[i]);
                }
            }

            return mask;
        }

        /// <summary>
        /// Whether the window being edited crosses midnight. An end earlier than the start is
        /// not an error: it is how a night shift is expressed.
        /// </summary>
        public static bool IsOvernight(int startMinutes, int endMinutes)
        {
            return endMinutes < startMinutes;
        }

        private static int ParseMinutes(string hhmm)
        {
            SettingsValidator.TryParseTimeOfDay(hhmm, out int minutes);
            return minutes;
        }
    }
}
