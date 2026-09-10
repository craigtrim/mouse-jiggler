using System;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Environment;
using MouseJiggler.Windows.Sessions;

namespace MouseJiggler.Windows.Power
{
    /// <summary>
    /// The Windows implementation of <see cref="IPowerSessionSource"/>, composing the power and
    /// session adapters behind the one contract the coordinator depends on.
    /// </summary>
    /// <remarks>
    /// The two adapters stay separate types because they register with different APIs and answer
    /// different messages. Joining them here rather than in the coordinator keeps the interop in
    /// one assembly, and means the coordinator can be constructed against a fake.
    /// </remarks>
    public sealed class WindowsPowerSessionSource : IPowerSessionSource
    {
        private readonly PowerStatusSource _power = new PowerStatusSource();
        private readonly SessionStatusSource _session = new SessionStatusSource();
        private bool _disposed;

        public WindowsPowerSessionSource()
        {
            _power.Changed += (_, e) => PowerChanged?.Invoke(this, e);
            _session.Changed += (_, e) => SessionChanged?.Invoke(this, e);
        }

        public event EventHandler? PowerChanged;

        public event EventHandler? SessionChanged;

        public OperationResult<PowerSource> QueryPower()
        {
            return _power.Query();
        }

        public OperationResult<SessionState> QuerySession()
        {
            return _session.Query();
        }

        public SessionState LastNotifiedSession => _session.NotifiedState;

        /// <summary>
        /// True when either registration failed, so the heartbeat poll is the only signal left.
        /// The poll runs regardless, so this is reported rather than acted on.
        /// </summary>
        public bool RequiresPolling => _power.RequiresPolling || _session.RequiresPolling;

        /// <summary>Subscribes both sources to the caller's hidden top-level window.</summary>
        /// <remarks>
        /// The two results are returned separately because either can fail on its own, and the
        /// caller records which one did. A failure is not fatal: detection falls back to polling.
        /// </remarks>
        public OperationResult RegisterPower(IntPtr windowHandle)
        {
            return _power.Register(windowHandle);
        }

        public OperationResult RegisterSession(IntPtr windowHandle)
        {
            return _session.Register(windowHandle);
        }

        /// <summary>
        /// Offers a window message to both sources. The two message ranges do not overlap, so
        /// the order here decides only which is asked first, never which one claims a message.
        /// </summary>
        public bool HandleMessage(int message, IntPtr wParam, IntPtr lParam)
        {
            bool handled = _power.HandleMessage(message, wParam, lParam);
            return _session.HandleMessage(message, wParam) || handled;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session.Dispose();
            _power.Dispose();
        }
    }
}
