using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Environment;
using MouseJiggler.Core.Settings;

namespace MouseJiggler.Tests.Fakes
{
    /// <summary>
    /// A clock the test moves by hand. Every duration in the coordinator is compared against
    /// <see cref="MonotonicMilliseconds"/>, so advancing that is what makes a retry become due
    /// without the test sleeping for thirty seconds to find out.
    /// </summary>
    public sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc);

        public long MonotonicMilliseconds { get; set; } = 1_000_000;

        public TimeZoneInfo LocalTimeZone { get; set; } = TimeZoneInfo.Utc;

        /// <summary>Moves both clocks forward together, the way real time does.</summary>
        public void Advance(TimeSpan amount)
        {
            MonotonicMilliseconds += (long)amount.TotalMilliseconds;
            UtcNow = UtcNow.Add(amount);
        }
    }

    /// <summary>Power and session under the test's control, including query failure.</summary>
    public sealed class FakePowerSessionSource : IPowerSessionSource
    {
        public PowerSource Power { get; set; } = PowerSource.External;

        public SessionState Session { get; set; } = SessionState.ActiveUnlocked;

        public SessionState LastNotifiedSession { get; set; } = SessionState.Unknown;

        public bool FailPowerQuery { get; set; }

        public bool FailSessionQuery { get; set; }

        public int PowerQueries { get; private set; }

        public int SessionQueries { get; private set; }

        public bool Disposed { get; private set; }

        public event EventHandler? PowerChanged;

        public event EventHandler? SessionChanged;

        public OperationResult<PowerSource> QueryPower()
        {
            PowerQueries++;
            return FailPowerQuery
                ? OperationResult<PowerSource>.Failure(FaultSubsystem.PowerQuery, "power.queryFailed", 5)
                : OperationResult<PowerSource>.Success(Power);
        }

        public OperationResult<SessionState> QuerySession()
        {
            SessionQueries++;
            return FailSessionQuery
                ? OperationResult<SessionState>.Failure(FaultSubsystem.SessionQuery, "session.queryFailed", 5)
                : OperationResult<SessionState>.Success(Session);
        }

        public void RaisePowerChanged() => PowerChanged?.Invoke(this, EventArgs.Empty);

        public void RaiseSessionChanged() => SessionChanged?.Invoke(this, EventArgs.Empty);

        /// <summary>True while at least one handler is attached, which is how detach is proven.</summary>
        public bool HasSubscribers => PowerChanged != null || SessionChanged != null;

        public void Dispose() => Disposed = true;
    }

    /// <summary>The desktop probe, so the secure-desktop path is reachable without a UAC prompt.</summary>
    public sealed class FakeInputDesktopProbe : IInputDesktopProbe
    {
        public bool Available { get; set; } = true;

        public bool IsInputDesktopAvailable() => Available;
    }

    /// <summary>
    /// Records every call to the keep-awake request. The call counts are the point: a retry
    /// policy that has degenerated into a busy loop looks identical from the outside except in
    /// how often it calls this.
    /// </summary>
    public sealed class FakeExecutionStateController : IExecutionStateController
    {
        public bool FailApply { get; set; }

        public bool FailRelease { get; set; }

        public int ApplyCalls { get; private set; }

        public int ReleaseCalls { get; private set; }

        public bool SystemAwakeApplied { get; private set; }

        public bool DisplayAwakeApplied { get; private set; }

        public bool Disposed { get; private set; }

        public OperationResult Apply(bool systemAwake, bool displayAwake)
        {
            ApplyCalls++;

            if (FailApply)
            {
                return OperationResult.Failure(FaultSubsystem.WakeAcquire, "wake.applyFailed", 1);
            }

            SystemAwakeApplied = systemAwake;
            DisplayAwakeApplied = displayAwake;
            return OperationResult.Success();
        }

        public OperationResult Release()
        {
            ReleaseCalls++;

            if (FailRelease)
            {
                return OperationResult.Failure(FaultSubsystem.WakeRelease, "wake.releaseFailed", 1);
            }

            SystemAwakeApplied = false;
            DisplayAwakeApplied = false;
            return OperationResult.Success();
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>Idle time under the test's control, including the unreadable case.</summary>
    public sealed class FakeIdleInputSource : IIdleInputSource
    {
        public uint IdleMilliseconds { get; set; } = 600_000;

        public bool Readable { get; set; } = true;

        public bool TryGetIdleMilliseconds(out uint idleMilliseconds)
        {
            idleMilliseconds = IdleMilliseconds;
            return Readable;
        }
    }

    /// <summary>Counts pointer batches and can fail them either retryably or not.</summary>
    public sealed class FakeMouseJiggler : IMouseJiggler
    {
        /// <summary>Runs before the result is returned, so a test can change state mid-send.</summary>
        public Action? BeforeReturn { get; set; }

        public OperationResult Result { get; set; } = OperationResult.Success();

        public int SendCalls { get; private set; }

        public OperationResult SendJiggle()
        {
            SendCalls++;
            BeforeReturn?.Invoke();
            return Result;
        }
    }

    /// <summary>A settings store in memory, with a switch that fails every write.</summary>
    public sealed class FakeSettingsStore : ISettingsStore
    {
        private SettingsV1 _current;

        public FakeSettingsStore(SettingsV1 initial)
        {
            _current = initial ?? throw new ArgumentNullException(nameof(initial));
        }

        public bool FailWrites { get; set; }

        public int WriteAttempts { get; private set; }

        public SettingsV1 Current => _current;

        public bool Disposed { get; private set; }

        public event EventHandler<SettingsV1>? ExternalChangeObserved;

        public OperationResult<SettingsV1> Load() => OperationResult<SettingsV1>.Success(_current);

        public Task<OperationResult<SettingsV1>> UpdateAsync(Func<SettingsV1, SettingsV1> patch, CancellationToken cancellationToken)
        {
            if (patch == null)
            {
                throw new ArgumentNullException(nameof(patch));
            }

            WriteAttempts++;

            if (FailWrites)
            {
                return Task.FromResult(
                    OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigSave, "settings.writeFailed"));
            }

            _current = patch(_current);
            return Task.FromResult(OperationResult<SettingsV1>.Success(_current));
        }

        public void RaiseExternalChange(SettingsV1 observed)
        {
            _current = observed;
            ExternalChangeObserved?.Invoke(this, observed);
        }

        public bool HasSubscribers => ExternalChangeObserved != null;

        public void Dispose() => Disposed = true;
    }

    /// <summary>Keeps every recorded fault and transition so a test can assert on them.</summary>
    public sealed class RecordingDiagnosticSink : IDiagnosticSink
    {
        public List<string> Codes { get; } = new List<string>();

        public List<string> Transitions { get; } = new List<string>();

        public void Record(FaultSubsystem subsystem, string code, int? nativeErrorCode = null)
        {
            Codes.Add(code);
        }

        public void RecordTransition(StatusCode from, StatusCode to)
        {
            Transitions.Add(from + "->" + to);
        }
    }

    /// <summary>
    /// Stands in for the owner-thread marshaller. The test itself is the owner thread, so
    /// nothing needs marshalling and <see cref="InvokeRequired"/> is false.
    /// </summary>
    public sealed class ImmediateInvoker : ISynchronizeInvoke
    {
        public bool InvokeRequired => false;

        public IAsyncResult BeginInvoke(Delegate method, object[] args)
        {
            object result = method.DynamicInvoke(args);
            return new CompletedAsyncResult(result);
        }

        public object EndInvoke(IAsyncResult result)
        {
            return ((CompletedAsyncResult)result).Value;
        }

        public object Invoke(Delegate method, object[] args)
        {
            return method.DynamicInvoke(args);
        }

        private sealed class CompletedAsyncResult : IAsyncResult
        {
            private readonly ManualResetEvent _handle = new ManualResetEvent(true);

            public CompletedAsyncResult(object value)
            {
                Value = value;
            }

            public object Value { get; }

            public object AsyncState => Value;

            public WaitHandle AsyncWaitHandle => _handle;

            public bool CompletedSynchronously => true;

            public bool IsCompleted => true;
        }
    }
}
