using System;
using System.Runtime.InteropServices;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Input;
using MouseJiggler.Windows.Interop;

namespace MouseJiggler.Windows.Input
{
    /// <summary>
    /// Moves the pointer one physical pixel and back.
    /// </summary>
    /// <remarks>
    /// Both movements go in a single SendInput call. Sending them separately, or restoring on a
    /// timer, would leave a window in which the pointer sits on the wrong pixel and a real
    /// movement could be fought. One call does not make the operation perfectly invisible to a
    /// user moving the mouse at that instant, but it is the smallest window available, and
    /// nothing is ever forced back afterwards.
    ///
    /// The batch carries no clicks, no wheel events and no keystrokes, and every record is
    /// tagged so the app can recognise its own input.
    /// </remarks>
    public sealed class MouseJigglerDevice : IMouseJiggler
    {
        /// <summary>Marks input this app generated.</summary>
        public static readonly IntPtr OwnedTag = new IntPtr(0x4D4A4747); // "MJGG"

        private readonly Func<bool> _isCancelled;

        public MouseJigglerDevice(Func<bool>? isCancelled = null)
        {
            _isCancelled = isCancelled ?? (() => false);
        }

        public OperationResult SendJiggle()
        {
            if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
            {
                return OperationResult.Failure(FaultSubsystem.InputRead, "input.cursorPosFailed", Marshal.GetLastWin32Error());
            }

            if (IsAnyButtonOrModifierHeld())
            {
                // A drag or a keyboard shortcut is in progress. This is an ordinary skip, not a
                // fault: it will be due again on the next tick.
                return OperationResult.Failure(FaultSubsystem.InputSend, "input.skipped.userBusy", retryable: true);
            }

            MonitorBounds? monitor = TryGetMonitorBounds(cursor);
            if (monitor == null)
            {
                return OperationResult.Failure(FaultSubsystem.InputRead, "input.monitorUnavailable");
            }

            if (!JiggleEligibility.TryChooseTarget(cursor.X, cursor.Y, monitor, out JiggleTarget? target))
            {
                return OperationResult.Failure(FaultSubsystem.InputSend, "input.skipped.noNeighbour", retryable: true);
            }

            VirtualBounds virtualBounds = GetVirtualBounds();

            if (!VirtualDesktopCoordinates.TryNormalizePoint(target.ToX, target.ToY, virtualBounds, out int toX, out int toY) ||
                !VirtualDesktopCoordinates.TryNormalizePoint(target.FromX, target.FromY, virtualBounds, out int backX, out int backY))
            {
                // Retryable: a display layout that cannot be addressed exactly is an ordinary
                // skip, not a failure of the input mechanism. Docking, undocking or a resolution
                // change can make it representable again a second later, and latching here would
                // leave the user pressing Retry for something that fixed itself.
                return OperationResult.Failure(FaultSubsystem.InputSend, "input.unsupportedGeometry", retryable: true);
            }

            // The last check before committing: anything that changed since the snapshot,
            // including a Stop, cancels the batch outright.
            if (_isCancelled())
            {
                return OperationResult.Failure(FaultSubsystem.InputSend, "input.skipped.cancelled", retryable: true);
            }

            var inputs = new NativeMethods.INPUT[2];
            inputs[0] = CreateMove(toX, toY);
            inputs[1] = CreateMove(backX, backY);

            int size = Marshal.SizeOf(typeof(NativeMethods.INPUT));
            uint inserted = NativeMethods.SendInput(2, inputs, size);

            if (inserted != 2)
            {
                // A partial insertion leaves the pointer displaced by one pixel. No compensating
                // move is sent: another blind write is as likely to make it worse, and the honest
                // response is to latch the fault and say jiggling has failed.
                int error = Marshal.GetLastWin32Error();
                return OperationResult.Failure(FaultSubsystem.InputSend, "input.sendFailed", error, retryable: false);
            }

            return OperationResult.Success();
        }

        private static NativeMethods.INPUT CreateMove(int normalizedX, int normalizedY)
        {
            return new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                union = new NativeMethods.INPUTUNION
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        dx = normalizedX,
                        dy = normalizedY,
                        mouseData = 0,
                        dwFlags = NativeMethods.MOUSEEVENTF_MOVE |
                                  NativeMethods.MOUSEEVENTF_ABSOLUTE |
                                  NativeMethods.MOUSEEVENTF_VIRTUALDESK,
                        time = 0,
                        dwExtraInfo = OwnedTag,
                    },
                },
            };
        }

        /// <summary>
        /// True while any mouse button or modifier is down. Checked immediately before sending,
        /// so a drag that started microseconds ago still suppresses the movement.
        /// </summary>
        public static bool IsAnyButtonOrModifierHeld()
        {
            int[] keys =
            {
                NativeMethods.VK_LBUTTON, NativeMethods.VK_RBUTTON, NativeMethods.VK_MBUTTON,
                NativeMethods.VK_XBUTTON1, NativeMethods.VK_XBUTTON2,
                NativeMethods.VK_SHIFT, NativeMethods.VK_CONTROL, NativeMethods.VK_MENU,
                NativeMethods.VK_LWIN, NativeMethods.VK_RWIN,
            };

            foreach (int key in keys)
            {
                if ((NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static MonitorBounds? TryGetMonitorBounds(NativeMethods.POINT point)
        {
            IntPtr monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return null;
            }

            var info = new NativeMethods.MONITORINFO
            {
                cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.MONITORINFO)),
            };

            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return null;
            }

            // Monitor bounds, not the work area: the pointer may legitimately sit over the taskbar.
            return new MonitorBounds(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
        }

        public static VirtualBounds GetVirtualBounds()
        {
            return new VirtualBounds(
                NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
                NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
                NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
                NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
        }
    }
}
