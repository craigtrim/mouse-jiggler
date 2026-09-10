using System;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// Marks a test that has a real effect on the machine: injected pointer input, or a wake
    /// request that Windows actually honours.
    /// </summary>
    /// <remarks>
    /// These are skipped unless MOUSEJIGGLER_LIVE_INPUT is set to 1 on an interactive desktop,
    /// so running the suite while you are working cannot move your pointer or hold your display
    /// on. Being interactive is not on its own enough: the variable says the desktop is a
    /// disposable one. Anything native that such a test needs has to be created inside the test
    /// body, because a class fixture would run even when the test is skipped.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class LiveInputFactAttribute : FactAttribute
    {
        public const string EnvironmentVariableName = "MOUSEJIGGLER_LIVE_INPUT";

        public LiveInputFactAttribute()
        {
            if (!IsEnabled)
            {
                Skip = "Set " + EnvironmentVariableName + "=1 on a dedicated interactive desktop to run this.";
            }
        }

        public static bool IsEnabled =>
            string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariableName), "1", StringComparison.Ordinal) &&
            Environment.UserInteractive;
    }
}
