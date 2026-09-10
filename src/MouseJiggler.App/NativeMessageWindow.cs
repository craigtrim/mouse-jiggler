using System;
using System.Windows.Forms;
using MouseJiggler.Windows.Interop;

namespace MouseJiggler.App
{
    /// <summary>
    /// The hidden window that receives system notifications.
    /// </summary>
    /// <remarks>
    /// It is a top-level window rather than a message-only one. Message-only windows do not
    /// receive broadcast messages, and both power-setting notifications and session change
    /// notifications are delivered that way, so a message-only window would silently never hear
    /// about a machine being unplugged. It has no taskbar presence and is never shown.
    /// </remarks>
    public sealed class NativeMessageWindow : NativeWindow, IDisposable
    {
        private bool _disposed;

        public NativeMessageWindow()
        {
            var parameters = new CreateParams
            {
                Caption = "MouseJiggler.MessageWindow",
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,

                // WS_OVERLAPPED, never shown, and marked as a tool window so it cannot appear
                // in the taskbar or the alt-tab list.
                Style = 0,
                ExStyle = 0x00000080, // WS_EX_TOOLWINDOW
            };

            CreateHandle(parameters);
        }

        /// <summary>Raised for every message the coordinator or adapters care about.</summary>
        public event EventHandler<WindowMessageEventArgs>? MessageReceived;

        protected override void WndProc(ref Message m)
        {
            if (!_disposed)
            {
                MessageReceived?.Invoke(this, new WindowMessageEventArgs(m.Msg, m.WParam, m.LParam));
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (Handle != IntPtr.Zero)
            {
                DestroyHandle();
            }
        }
    }

    public sealed class WindowMessageEventArgs : EventArgs
    {
        public WindowMessageEventArgs(int message, IntPtr wParam, IntPtr lParam)
        {
            Message = message;
            WParam = wParam;
            LParam = lParam;
        }

        public int Message { get; }

        public IntPtr WParam { get; }

        public IntPtr LParam { get; }
    }
}
