using System;
using System.Runtime.InteropServices;

namespace MouseJiggler.Windows.Interop
{
    /// <summary>
    /// The native surface the app uses. Structures match the documented native widths and
    /// alignment, and every entry point here is read-only or scoped to this process.
    /// </summary>
    internal static class NativeMethods
    {
        // ---- Power ----------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        internal const byte AcLineStatusOffline = 0;
        internal const byte AcLineStatusOnline = 1;
        internal const byte AcLineStatusUnknown = 255;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        /// <summary>GUID_ACDC_POWER_SOURCE: the external power source changed.</summary>
        internal static readonly Guid GuidAcdcPowerSource = new Guid("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548");

        internal const int DeviceNotifyWindowHandle = 0x00000000;

        internal const int WM_POWERBROADCAST = 0x0218;
        internal const int PBT_APMPOWERSTATUSCHANGE = 0x000A;
        internal const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        internal const int PBT_APMRESUMESUSPEND = 0x0007;
        internal const int PBT_APMSUSPEND = 0x0004;
        internal const int PBT_POWERSETTINGCHANGE = 0x8013;

        [StructLayout(LayoutKind.Sequential)]
        internal struct POWERBROADCAST_SETTING
        {
            public Guid PowerSetting;
            public uint DataLength;

            // The payload follows this header. It is copied out by length before the message
            // buffer goes away rather than being held as a pointer.
            public byte Data;
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterPowerSettingNotification(IntPtr handle);

        // ---- Execution state ------------------------------------------------------

        [Flags]
        internal enum ExecutionState : uint
        {
            SystemRequired = 0x00000001,
            DisplayRequired = 0x00000002,
            Continuous = 0x80000000,
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

        // ---- Sessions -------------------------------------------------------------

        internal const int WM_WTSSESSION_CHANGE = 0x02B1;
        internal const int WTS_SESSION_LOCK = 0x7;
        internal const int WTS_SESSION_UNLOCK = 0x8;
        internal const int WTS_CONSOLE_CONNECT = 0x1;
        internal const int WTS_CONSOLE_DISCONNECT = 0x2;
        internal const int WTS_REMOTE_CONNECT = 0x3;
        internal const int WTS_REMOTE_DISCONNECT = 0x4;
        internal const int WTS_SESSION_LOGOFF = 0x6;

        internal const int NOTIFY_FOR_THIS_SESSION = 0;

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSRegisterSessionNotification(IntPtr window, int flags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSUnRegisterSessionNotification(IntPtr window);

        internal const int WTSSessionInfoEx = 25;

        internal const int WTS_SESSIONSTATE_LOCK = 0;
        internal const int WTS_SESSIONSTATE_UNLOCK = 1;

        /// <summary>WTS_CONNECTSTATE_CLASS values that matter here.</summary>
        internal const int WTSActive = 0;
        internal const int WTSDisconnected = 4;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WTSINFOEX_LEVEL1
        {
            public uint SessionId;
            public int SessionState;
            public int SessionFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
            public string WinStationName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)]
            public string UserName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)]
            public string DomainName;

            public long LogonTime;
            public long ConnectTime;
            public long DisconnectTime;
            public long LastInputTime;
            public long CurrentTime;
            public uint IncomingBytes;
            public uint OutgoingBytes;
            public uint IncomingFrames;
            public uint OutgoingFrames;
            public uint IncomingCompressedBytes;
            public uint OutgoingCompressedBytes;
        }

        /// <summary>
        /// The union is aligned to eight bytes because the level-one payload contains 64-bit
        /// fields, so the marshaller places it at offset eight of its own accord.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WTSINFOEX
        {
            public uint Level;
            public WTSINFOEX_LEVEL1 Data;
        }

        internal static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;
        internal const uint WTS_CURRENT_SESSION = unchecked((uint)-1);

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQuerySessionInformation(
            IntPtr server,
            uint sessionId,
            int infoClass,
            out IntPtr buffer,
            out uint bytesReturned);

        [DllImport("wtsapi32.dll")]
        internal static extern void WTSFreeMemory(IntPtr memory);

        [DllImport("kernel32.dll")]
        internal static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

        // ---- Desktops -------------------------------------------------------------

        internal const uint DESKTOP_READOBJECTS = 0x0001;
        internal const int UOI_NAME = 2;

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetThreadDesktop(uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseDesktop(IntPtr desktop);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetUserObjectInformation(
            IntPtr handle,
            int index,
            IntPtr info,
            uint length,
            out uint lengthNeeded);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        // ---- Input ----------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        internal struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        [DllImport("kernel32.dll")]
        internal static extern uint GetTickCount();

        [DllImport("kernel32.dll")]
        internal static extern ulong GetTickCount64();

        internal const int INPUT_MOUSE = 0;
        internal const uint MOUSEEVENTF_MOVE = 0x0001;
        internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        internal const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        /// <summary>
        /// The INPUT union. Only the mouse member is ever populated, but the structure must
        /// still be the full native size or SendInput rejects it.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct INPUT
        {
            public int type;
            public INPUTUNION union;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct INPUTUNION
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;

            // Keyboard and hardware members are larger on some layouts; reserving the space
            // keeps the union the documented size without giving this code a way to send keys.
            [FieldOffset(0)]
            public KEYBDINPUT ki;

            [FieldOffset(0)]
            public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint count, [MarshalAs(UnmanagedType.LPArray)] INPUT[] inputs, int size);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);

        internal const int VK_LBUTTON = 0x01;
        internal const int VK_RBUTTON = 0x02;
        internal const int VK_MBUTTON = 0x04;
        internal const int VK_XBUTTON1 = 0x05;
        internal const int VK_XBUTTON2 = 0x06;
        internal const int VK_SHIFT = 0x10;
        internal const int VK_CONTROL = 0x11;
        internal const int VK_MENU = 0x12;
        internal const int VK_LWIN = 0x5B;
        internal const int VK_RWIN = 0x5C;

        // ---- Monitors -------------------------------------------------------------

        internal const int SM_XVIRTUALSCREEN = 76;
        internal const int SM_YVIRTUALSCREEN = 77;
        internal const int SM_CXVIRTUALSCREEN = 78;
        internal const int SM_CYVIRTUALSCREEN = 79;

        internal const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromPoint(POINT point, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        // ---- Named pipes ----------------------------------------------------------

        /// <summary>
        /// The process that owns the other end of a connected pipe.
        /// </summary>
        /// <remarks>
        /// Asked of the client's own handle, so the answer comes from the kernel rather than
        /// from whatever the server chose to write into its reply. That distinction is the whole
        /// point: a reply is a string an untrusted process can compose, and this is not.
        /// </remarks>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeServerProcessId(SafeHandle pipe, out uint serverProcessId);

        // ---- Messages -------------------------------------------------------------

        internal const int WM_TIMECHANGE = 0x001E;
        internal const int WM_ENDSESSION = 0x0016;
        internal const int WM_QUERYENDSESSION = 0x0011;
    }
}
