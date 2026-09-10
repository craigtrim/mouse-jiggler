using System;

namespace MouseJiggler.App
{
    /// <summary>Which control a validation failure belongs beside.</summary>
    public enum SettingsField
    {
        /// <summary>No single control is at fault; the message belongs in a dialog.</summary>
        None = 0,
        Days = 1,
        EndTime = 2,
        Interval = 3,
    }

    /// <summary>A validation failure, in the words the user reads and beside the control at fault.</summary>
    public sealed class ValidationMessage
    {
        public ValidationMessage(SettingsField field, string text)
        {
            Field = field;
            Text = text ?? throw new ArgumentNullException(nameof(text));
        }

        public SettingsField Field { get; }

        public string Text { get; }

        /// <summary>True when this belongs on an error provider rather than in a dialog.</summary>
        public bool BelongsToAControl => Field != SettingsField.None;
    }

    /// <summary>
    /// Turns a fault code into something a person can act on.
    /// </summary>
    /// <remarks>
    /// Separate from the window because it is the part worth testing: a code that gains a new
    /// meaning, or one the validator starts returning that nothing here recognises, would
    /// otherwise show the generic dialog and quietly lose the reason. Pure text in, pure text
    /// out, no Windows Forms.
    ///
    /// Each message says what to do rather than only what is wrong. "Select at least one day" is
    /// an instruction; "invalid day mask" is a description of the code's feelings.
    /// </remarks>
    public static class SettingsValidationPresenter
    {
        public static ValidationMessage Describe(string? code)
        {
            switch (code)
            {
                case "settings.schedule.noDays":
                    return new ValidationMessage(
                        SettingsField.Days,
                        "Select at least one day, or switch the schedule off.");

                case "settings.schedule.equalTimes":
                    return new ValidationMessage(
                        SettingsField.EndTime,
                        "The start and end times must differ. Switch the schedule off to run all day.");

                case "settings.schedule.invalidTime":
                    return new ValidationMessage(
                        SettingsField.EndTime,
                        "Enter both times as a time of day.");

                case "settings.intervalSeconds.invalid":
                    return new ValidationMessage(
                        SettingsField.Interval,
                        "Choose between 5 and 300 seconds.");

                case "settings.lockTimeout":
                    return new ValidationMessage(
                        SettingsField.None,
                        "Another copy of Mouse Jiggler is saving at the same moment. Try again.");

                case "settings.accessDenied":
                    return new ValidationMessage(
                        SettingsField.None,
                        "Your settings could not be saved because the file could not be written. "
                        + "The previous settings are unchanged.");

                default:
                    return new ValidationMessage(
                        SettingsField.None,
                        "Your settings could not be saved. The previous settings are unchanged.");
            }
        }
    }
}
