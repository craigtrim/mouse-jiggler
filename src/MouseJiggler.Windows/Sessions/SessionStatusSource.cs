using System;
using System.Runtime.InteropServices;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Environment;
using MouseJiggler.Windows.Interop;

namespace MouseJiggler.Windows.Sessions
{
    /// <summary>
    /// Whether this session is connected and unlocked. The initial state is queried rather than
    /// assumed, because assuming "unlocked" at startup would allow pointer movement on a locked
    /// machine. An unsuccessful query stays Unknown, which suppresses effects.
    /// </summary>
    public sealed class SessionStatusSource : IDisposable
    {
        private IntPtr _registeredWindow = IntPtr.Zero;
        private bool _disposed;

        /// <summary>The last state derived from a notification, used until the next query.</summary>
        private SessionState _notifiedState = SessionState.Unknown;

        public event EventHandler? Changed;

        /// <summary>True when session notifications are unavailable and polling must cover for it.</summary>
        public bool RequiresPolling { get; private set; } = true;

        public OperationResult<SessionState> Query()
        {
            IntPtr buffer = IntPtr.Zero;

            try
            {
                if (!NativeMethods.WTSQuerySessionInformation(
                        NativeMethods.WTS_CURRENT_SERVER_HANDLE,
                        NativeMethods.WTS_CURRENT_SESSION,
                        NativeMethods.WTSSessionInfoEx,
                        out buffer,
                        out uint bytes))
                {
                    int error = Marshal.GetLastWin32Error();
                    return OperationResult<SessionState>.Failure(FaultSubsystem.SessionQuery, "session.queryFailed", error);
                }

                if (buffer == IntPtr.Zero || bytes < Marshal.SizeOf(typeof(NativeMethods.WTSINFOEX)))
                {
                    return OperationResult<SessionState>.Failure(FaultSubsystem.SessionQuery, "session.shortBuffer", retryable: true);
                }

                var info = (NativeMethods.WTSINFOEX)Marshal.PtrToStructure(buffer, typeof(NativeMethods.WTSINFOEX))!;

                if (info.Level != 1)
                {
                    return OperationResult<SessionState>.Failure(FaultSubsystem.SessionQuery, "session.unexpectedLevel", retryable: false);
                }

                if (info.Data.SessionState == NativeMethods.WTSDisconnected)
                {
                    return OperationResult<SessionState>.Success(SessionState.Disconnected);
                }

                SessionState state = info.Data.SessionFlags == NativeMethods.WTS_SESSIONSTATE_LOCK
                    ? SessionState.Locked
                    : SessionState.ActiveUnlocked;

                return OperationResult<SessionState>.Success(state);
            }
            catch (OverflowException)
            {
                return OperationResult<SessionState>.Failure(FaultSubsystem.SessionQuery, "session.marshalFailed", retryable: false);
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    NativeMethods.WTSFreeMemory(buffer);
                }
            }
        }

        public OperationResult Register(IntPtr windowHandle)
        {
            ThrowIfDisposed();

            if (windowHandle == IntPtr.Zero)
            {
                throw new ArgumentException("A window handle is required.", nameof(windowHandle));
            }

            if (_registeredWindow != IntPtr.Zero)
            {
                return OperationResult.Success();
            }

            if (!NativeMethods.WTSRegisterSessionNotification(windowHandle, NativeMethods.NOTIFY_FOR_THIS_SESSION))
            {
                int error = Marshal.GetLastWin32Error();
                RequiresPolling = true;
                return OperationResult.Failure(FaultSubsystem.SessionQuery, "session.registerFailed", error);
            }

            _registeredWindow = windowHandle;
            RequiresPolling = false;
            return OperationResult.Success();
        }

        /// <summary>
        /// Handles a session message. Locking must take effect at once rather than waiting for
        /// the next poll, so the notified state is recorded immediately.
        /// </summary>
        public bool HandleMessage(int message, IntPtr wParam)
        {
            if (message != NativeMethods.WM_WTSSESSION_CHANGE)
            {
                return false;
            }

            switch (wParam.ToInt32())
            {
                case NativeMethods.WTS_SESSION_LOCK:
                    _notifiedState = SessionState.Locked;
                    break;

                case NativeMethods.WTS_SESSION_UNLOCK:
                    _notifiedState = SessionState.ActiveUnlocked;
                    break;

                case NativeMethods.WTS_CONSOLE_DISCONNECT:
                case NativeMethods.WTS_REMOTE_DISCONNECT:
                case NativeMethods.WTS_SESSION_LOGOFF:
                    _notifiedState = SessionState.Disconnected;
                    break;

                case NativeMethods.WTS_CONSOLE_CONNECT:
                case NativeMethods.WTS_REMOTE_CONNECT:
                    _notifiedState = SessionState.Unknown;
                    break;

                default:
                    return false;
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>The state last reported by a notification, if any.</summary>
        public SessionState NotifiedState => _notifiedState;

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SessionStatusSource));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_registeredWindow != IntPtr.Zero)
            {
                NativeMethods.WTSUnRegisterSessionNotification(_registeredWindow);
                _registeredWindow = IntPtr.Zero;
            }
        }
    }
}
