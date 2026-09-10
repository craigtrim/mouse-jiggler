namespace MouseJiggler.Windows.Interop
{
    /// <summary>
    /// The window messages the application layer routes to the adapters. These are public
    /// because the hidden message window lives in the app, while the code that interprets the
    /// payloads lives here; the rest of the native surface stays internal.
    /// </summary>
    public static class WindowMessages
    {
        public const int PowerBroadcast = NativeMethods.WM_POWERBROADCAST;
        public const int SessionChange = NativeMethods.WM_WTSSESSION_CHANGE;
        public const int TimeChange = NativeMethods.WM_TIMECHANGE;
        public const int EndSession = NativeMethods.WM_ENDSESSION;
        public const int QueryEndSession = NativeMethods.WM_QUERYENDSESSION;

        /// <summary>The machine is going to sleep.</summary>
        public const int PowerSuspend = NativeMethods.PBT_APMSUSPEND;

        public const int PowerResumeAutomatic = NativeMethods.PBT_APMRESUMEAUTOMATIC;

        public const int PowerResumeSuspend = NativeMethods.PBT_APMRESUMESUSPEND;

        public static bool IsResume(int eventType)
        {
            return eventType == PowerResumeAutomatic || eventType == PowerResumeSuspend;
        }
    }
}
