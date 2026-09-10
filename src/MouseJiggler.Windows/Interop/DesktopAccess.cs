using System;
using System.Runtime.InteropServices;

namespace MouseJiggler.Windows.Interop
{
    /// <summary>
    /// Whether this thread can reach the desktop that is currently receiving input.
    /// </summary>
    /// <remarks>
    /// When the secure desktop is up, for a UAC prompt or the lock screen, the input desktop is
    /// not the one this process is attached to. Injecting then would either fail or land
    /// somewhere unintended, so the app checks first and skips. It never switches desktops and
    /// never tries to reach a secure one.
    /// </remarks>
    public static class DesktopAccess
    {
        public static bool IsInputDesktopAvailable()
        {
            IntPtr inputDesktop = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DESKTOP_READOBJECTS);
            if (inputDesktop == IntPtr.Zero)
            {
                // Access denied here normally means the secure desktop is showing.
                return false;
            }

            try
            {
                IntPtr threadDesktop = NativeMethods.GetThreadDesktop(NativeMethods.GetCurrentThreadId());
                if (threadDesktop == IntPtr.Zero)
                {
                    return false;
                }

                string? inputName = TryGetName(inputDesktop);
                string? threadName = TryGetName(threadDesktop);

                // The thread desktop handle is owned by the system, so only the one opened here
                // is closed below.
                return inputName != null && string.Equals(inputName, threadName, StringComparison.Ordinal);
            }
            finally
            {
                NativeMethods.CloseDesktop(inputDesktop);
            }
        }

        private static string? TryGetName(IntPtr desktop)
        {
            NativeMethods.GetUserObjectInformation(desktop, NativeMethods.UOI_NAME, IntPtr.Zero, 0, out uint needed);

            if (needed == 0 || needed > 1024)
            {
                return null;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!NativeMethods.GetUserObjectInformation(desktop, NativeMethods.UOI_NAME, buffer, needed, out _))
                {
                    return null;
                }

                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
