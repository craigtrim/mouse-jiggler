using System;
using System.Runtime.InteropServices;
using System.Threading;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Windows.Interop;

namespace MouseJiggler.Windows.Power
{
    /// <summary>
    /// Owns the keep-awake request.
    /// </summary>
    /// <remarks>
    /// SetThreadExecutionState is scoped to the calling thread, so every call has to happen on
    /// the one owner thread. Releasing from a different thread would leave the original request
    /// in place, which is exactly the failure that outlives the app and keeps a machine awake
    /// after the user stopped it. The controller therefore records its owner thread on first use
    /// and refuses to be driven from anywhere else.
    ///
    /// It asks for nothing more than it needs: no power-plan edits, no away mode, no
    /// ES_USER_PRESENT, and no attempt to wake a sleeping machine or to prevent an explicit
    /// sleep or lid close.
    /// </remarks>
    public sealed class ExecutionStateController : IExecutionStateController
    {
        private int _ownerThreadId;
        private bool _hasRequest;
        private bool _disposed;

        public bool SystemAwakeApplied { get; private set; }

        public bool DisplayAwakeApplied { get; private set; }

        /// <summary>What was last asked for, which may differ from what Windows accepted.</summary>
        public bool SystemAwakeRequested { get; private set; }

        public bool DisplayAwakeRequested { get; private set; }

        public OperationResult Apply(bool systemAwake, bool displayAwake)
        {
            ThrowIfDisposed();
            OperationResult owner = ClaimOrVerifyOwnerThread();
            if (!owner.Succeeded)
            {
                return owner;
            }

            if (!systemAwake)
            {
                return Release();
            }

            SystemAwakeRequested = true;
            DisplayAwakeRequested = displayAwake;

            // Skip the call when nothing changed, so a one-second heartbeat does not hammer the
            // API with an identical mask.
            if (_hasRequest && SystemAwakeApplied && DisplayAwakeApplied == displayAwake)
            {
                return OperationResult.Success();
            }

            NativeMethods.ExecutionState flags =
                NativeMethods.ExecutionState.Continuous | NativeMethods.ExecutionState.SystemRequired;

            if (displayAwake)
            {
                flags |= NativeMethods.ExecutionState.DisplayRequired;
            }

            NativeMethods.ExecutionState previous = NativeMethods.SetThreadExecutionState(flags);
            if (previous == 0)
            {
                int error = Marshal.GetLastWin32Error();

                // The request did not take. Nothing is marked as applied, and the coordinator's
                // retry schedule decides when to try again.
                SystemAwakeApplied = false;
                DisplayAwakeApplied = false;
                return OperationResult.Failure(FaultSubsystem.WakeAcquire, "wake.acquireFailed", error);
            }

            _hasRequest = true;
            SystemAwakeApplied = true;
            DisplayAwakeApplied = displayAwake;
            return OperationResult.Success();
        }

        public OperationResult Release()
        {
            ThrowIfDisposed();

            SystemAwakeRequested = false;
            DisplayAwakeRequested = false;

            if (!_hasRequest)
            {
                SystemAwakeApplied = false;
                DisplayAwakeApplied = false;
                return OperationResult.Success();
            }

            OperationResult owner = ClaimOrVerifyOwnerThread();
            if (!owner.Succeeded)
            {
                return owner;
            }

            NativeMethods.ExecutionState previous =
                NativeMethods.SetThreadExecutionState(NativeMethods.ExecutionState.Continuous);

            if (previous == 0)
            {
                int error = Marshal.GetLastWin32Error();

                // A failed release is the dangerous direction: the request may still stand.
                // It is reported and retried while the process lives rather than forgotten.
                return OperationResult.Failure(FaultSubsystem.WakeRelease, "wake.releaseFailed", error);
            }

            _hasRequest = false;
            SystemAwakeApplied = false;
            DisplayAwakeApplied = false;
            return OperationResult.Success();
        }

        private OperationResult ClaimOrVerifyOwnerThread()
        {
            int current = Thread.CurrentThread.ManagedThreadId;

            if (_ownerThreadId == 0)
            {
                _ownerThreadId = current;
                return OperationResult.Success();
            }

            if (_ownerThreadId != current)
            {
                return OperationResult.Failure(FaultSubsystem.WakeAcquire, "wake.wrongThread", retryable: false);
            }

            return OperationResult.Success();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ExecutionStateController));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            // Best effort: if the request cannot be released here, process exit is the final
            // boundary that clears it.
            if (_hasRequest && _ownerThreadId == Thread.CurrentThread.ManagedThreadId)
            {
                NativeMethods.SetThreadExecutionState(NativeMethods.ExecutionState.Continuous);
                _hasRequest = false;
            }

            SystemAwakeApplied = false;
            DisplayAwakeApplied = false;
            _disposed = true;
        }
    }
}
