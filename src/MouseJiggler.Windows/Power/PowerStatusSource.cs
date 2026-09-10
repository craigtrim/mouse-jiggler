using System;
using System.Runtime.InteropServices;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Environment;
using MouseJiggler.Windows.Interop;

namespace MouseJiggler.Windows.Power
{
    /// <summary>
    /// External power, as Windows reports it. There is no user-entered plugged-in setting
    /// anywhere in the product; this is the only source.
    /// </summary>
    public sealed class PowerStatusSource : IDisposable
    {
        private IntPtr _notificationHandle = IntPtr.Zero;
        private bool _disposed;

        /// <summary>Raised when Windows says the power source may have changed.</summary>
        public event EventHandler? Changed;

        /// <summary>True when notification registration failed and polling must cover for it.</summary>
        public bool RequiresPolling { get; private set; } = true;

        /// <summary>
        /// Reads the authoritative snapshot. Notifications are only a hint that this is worth
        /// calling again.
        /// </summary>
        public OperationResult<PowerSource> Query()
        {
            if (!NativeMethods.GetSystemPowerStatus(out NativeMethods.SYSTEM_POWER_STATUS status))
            {
                int error = Marshal.GetLastWin32Error();
                return OperationResult<PowerSource>.Failure(FaultSubsystem.PowerQuery, "power.queryFailed", error);
            }

            return OperationResult<PowerSource>.Success(Classify(status.ACLineStatus));
        }

        /// <summary>
        /// Maps the reported line status. Only ACLineStatus decides this. A full battery, a
        /// battery that reports itself as absent, and the charging bit are all irrelevant:
        /// none of them establishes that the machine is on external power.
        /// </summary>
        public static PowerSource Classify(byte acLineStatus)
        {
            switch (acLineStatus)
            {
                case NativeMethods.AcLineStatusOnline:
                    return PowerSource.External;

                case NativeMethods.AcLineStatusOffline:
                    return PowerSource.Battery;

                default:
                    return PowerSource.Unknown;
            }
        }

        /// <summary>
        /// Subscribes to power-source changes on the owner's hidden top-level window. A failure
        /// here is not fatal: it turns on polling and reports a diagnostic rather than
        /// pretending the machine is plugged in.
        /// </summary>
        public OperationResult Register(IntPtr windowHandle)
        {
            ThrowIfDisposed();

            if (windowHandle == IntPtr.Zero)
            {
                throw new ArgumentException("A window handle is required.", nameof(windowHandle));
            }

            if (_notificationHandle != IntPtr.Zero)
            {
                return OperationResult.Success();
            }

            Guid setting = NativeMethods.GuidAcdcPowerSource;
            IntPtr handle = NativeMethods.RegisterPowerSettingNotification(windowHandle, ref setting, NativeMethods.DeviceNotifyWindowHandle);

            if (handle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                RequiresPolling = true;
                return OperationResult.Failure(FaultSubsystem.PowerQuery, "power.registerFailed", error);
            }

            _notificationHandle = handle;
            RequiresPolling = false;
            return OperationResult.Success();
        }

        /// <summary>
        /// Handles a window message. The payload is validated by length and copied before the
        /// message buffer is released; nothing holds a pointer into it.
        /// </summary>
        public bool HandleMessage(int message, IntPtr wParam, IntPtr lParam)
        {
            if (message != NativeMethods.WM_POWERBROADCAST)
            {
                return false;
            }

            int eventType = wParam.ToInt32();

            if (eventType == NativeMethods.PBT_APMPOWERSTATUSCHANGE ||
                eventType == NativeMethods.PBT_APMRESUMEAUTOMATIC ||
                eventType == NativeMethods.PBT_APMRESUMESUSPEND)
            {
                Changed?.Invoke(this, EventArgs.Empty);
                return true;
            }

            if (eventType == NativeMethods.PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
            {
                var setting = (NativeMethods.POWERBROADCAST_SETTING)Marshal.PtrToStructure(
                    lParam, typeof(NativeMethods.POWERBROADCAST_SETTING))!;

                if (setting.PowerSetting == NativeMethods.GuidAcdcPowerSource && setting.DataLength >= sizeof(int))
                {
                    // The payload says which source it is, but the query stays authoritative,
                    // so this is only a signal to re-read.
                    Changed?.Invoke(this, EventArgs.Empty);
                }

                return true;
            }

            return false;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PowerStatusSource));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_notificationHandle != IntPtr.Zero)
            {
                NativeMethods.UnregisterPowerSettingNotification(_notificationHandle);
                _notificationHandle = IntPtr.Zero;
            }
        }
    }
}
