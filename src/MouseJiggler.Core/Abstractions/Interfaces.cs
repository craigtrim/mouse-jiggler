using System;
using System.Threading;
using System.Threading.Tasks;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Environment;
using MouseJiggler.Core.Settings;

namespace MouseJiggler.Core.Abstractions
{
    /// <summary>
    /// Time, in the two forms the app needs: UTC for schedule membership, and a monotonic
    /// counter for every duration comparison so that clock changes cannot distort timing.
    /// </summary>
    public interface IClock
    {
        DateTime UtcNow { get; }

        /// <summary>Uptime-based milliseconds. Never derived from local time.</summary>
        long MonotonicMilliseconds { get; }

        TimeZoneInfo LocalTimeZone { get; }
    }

    /// <summary>Daily-window membership and the next real transition, including DST jumps.</summary>
    public interface IScheduleEvaluator
    {
        bool IsWithinWindow(SettingsV1 settings, DateTime utcNow, TimeZoneInfo timeZone);

        /// <summary>The next actual eligibility change, or null when the schedule never transitions.</summary>
        DateTime? FindNextTransitionUtc(SettingsV1 settings, DateTime utcNow, TimeZoneInfo timeZone, out TransitionKind kind);
    }

    /// <summary>
    /// The single place that decides what should happen. Pure: same inputs, same outputs,
    /// no side effects, no ambient state.
    /// </summary>
    public interface IActivityPolicy
    {
        DesiredEffects Evaluate(SettingsV1 settings, EnvironmentSnapshot environment);
    }

    /// <summary>
    /// Loads and atomically updates the settings file. Updates are serialized through one queue
    /// and a per-user lock file; a patch never carries stopped or runMode from a stale form.
    /// </summary>
    public interface ISettingsStore : IDisposable
    {
        OperationResult<SettingsV1> Load();

        /// <summary>
        /// Applies <paramref name="patch"/> to the latest committed revision under the lock.
        /// Runs off the UI thread and completes with the committed settings.
        /// </summary>
        Task<OperationResult<SettingsV1>> UpdateAsync(Func<SettingsV1, SettingsV1> patch, CancellationToken cancellationToken);

        /// <summary>
        /// The revision this store last read or wrote.
        /// </summary>
        /// <remarks>
        /// An observation is delivered across a thread hop, so it can arrive after something
        /// newer has replaced it. Comparing its revision against this says whether it still
        /// describes the settings that exist, or a document that has already been superseded.
        /// See issue #20.
        /// </remarks>
        long LastKnownRevision { get; }

        /// <summary>Raised when another writer commits a new revision. Marshalled by the App dispatcher.</summary>
        event EventHandler<SettingsV1>? ExternalChangeObserved;
    }

    /// <summary>Windows-reported power and session state. There is never a user-entered value.</summary>
    /// <remarks>
    /// A query reports its failure rather than flattening it to Unknown, because the two mean
    /// different things: Unknown is an answer that suppresses effects, and a failed call is a
    /// fault that has to reach diagnostics. The notifications are only a hint that reading again
    /// is worthwhile; the query stays authoritative.
    /// </remarks>
    public interface IPowerSessionSource : IDisposable
    {
        OperationResult<PowerSource> QueryPower();

        OperationResult<SessionState> QuerySession();

        /// <summary>
        /// The state the last notification carried, used when a query fails. A lock has to take
        /// effect even if WTSQuerySessionInformation is refusing to answer.
        /// </summary>
        SessionState LastNotifiedSession { get; }

        /// <summary>Raised when the power source may have changed.</summary>
        event EventHandler? PowerChanged;

        /// <summary>
        /// Raised on lock, unlock, connect and disconnect. Kept separate from
        /// <see cref="PowerChanged"/> because a session change is also a recovery point: the
        /// desktop that an injection failed against is gone once the session comes back.
        /// </summary>
        event EventHandler? SessionChanged;
    }

    /// <summary>
    /// Whether this thread can reach the desktop that is currently receiving input. Injected
    /// rather than called statically so that the secure-desktop path is reachable in a test and
    /// so that a headless build agent, where the probe legitimately fails, does not decide it.
    /// </summary>
    public interface IInputDesktopProbe
    {
        bool IsInputDesktopAvailable();
    }

    /// <summary>
    /// Owns the SetThreadExecutionState request. Every call happens on the coordinator's
    /// owner thread, and the applied mask is tracked separately from the requested one.
    /// </summary>
    public interface IExecutionStateController : IDisposable
    {
        OperationResult Apply(bool systemAwake, bool displayAwake);

        OperationResult Release();

        /// <summary>What Windows actually accepted, which may differ from what was asked.</summary>
        bool SystemAwakeApplied { get; }

        bool DisplayAwakeApplied { get; }
    }

    /// <summary>Session-local last-input observation. Includes synthetic input; not a record of human activity.</summary>
    public interface IIdleInputSource
    {
        /// <summary>False when the reading is unavailable or implausible, which is treated as "not idle".</summary>
        bool TryGetIdleMilliseconds(out uint idleMilliseconds);
    }

    /// <summary>Moves the pointer one pixel and back in a single atomic batch.</summary>
    public interface IMouseJiggler
    {
        OperationResult SendJiggle();
    }

    /// <summary>The owned HKCU Run value. Windows may still override effective startup.</summary>
    public interface IStartupRegistration
    {
        /// <summary>Whether this product owns a Run value; not proof that Windows has it enabled.</summary>
        OperationResult<bool> IsRegistered();

        OperationResult Register(string executablePath);

        OperationResult Unregister();
    }

    /// <summary>Bounded, sanitized diagnostics. Never raw input, paths, titles or account names.</summary>
    public interface IDiagnosticSink
    {
        void Record(FaultSubsystem subsystem, string code, int? nativeErrorCode = null);

        void RecordTransition(StatusCode from, StatusCode to);
    }
}
