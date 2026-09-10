using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WinFormsTimer = System.Windows.Forms.Timer;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Commands;
using MouseJiggler.Core.Diagnostics;
using MouseJiggler.Core.Environment;
using MouseJiggler.Core.Input;
using MouseJiggler.Core.Settings;
using MouseJiggler.Windows.Input;
using MouseJiggler.Windows.Interop;

namespace MouseJiggler.App.Runtime
{
    /// <summary>
    /// Applies what the policy decides.
    /// </summary>
    /// <remarks>
    /// Everything here runs on the one owner thread. The keep-awake request is scoped to that
    /// thread, and serializing the rest alongside it means there is no lock to forget: a
    /// command, a timer tick and a native notification cannot interleave halfway through
    /// applying an effect.
    /// </remarks>
    public sealed class ActivityCoordinator : IDisposable
    {
        /// <summary>The fallback re-read cadence, well inside the six-second detection bound.</summary>
        private const long EnvironmentRefreshIntervalMs = 5000;

        private readonly ISettingsStore _store;
        private readonly ActivityPolicy _policy;
        private readonly IPowerSessionSource _environment;
        private readonly IInputDesktopProbe _desktop;
        private readonly IExecutionStateController _executionState;
        private readonly IIdleInputSource _idle;
        private readonly IMouseJiggler _jiggler;
        private readonly IClock _clock;
        private readonly WinFormsTimer _heartbeat;

        // Held as fields so Dispose can detach them. The coordinator does not own the source,
        // so leaving handlers on it would keep a disposed coordinator reachable and evaluating.
        private readonly EventHandler _onPowerChanged;
        private readonly EventHandler _onSessionChanged;

        /// <summary>
        /// Used to get work back onto the owner thread. The settings watcher raises its events
        /// from a thread pool thread, and applying an effect from there would be refused by the
        /// execution-state controller, which is scoped to the thread that acquired the request.
        /// </summary>
        private readonly ISynchronizeInvoke _owner;
        private readonly IDiagnosticSink _diagnostics;

        /// <summary>Owns both retry cadences: the wake request, and an unwritten Stop.</summary>
        private readonly RecoveryCoordinator _recovery = new RecoveryCoordinator();

        private SettingsV1 _settings;
        private long _generation;
        private bool _exiting;
        private bool _suspended;
        private bool _disposed;

        private PowerSource _lastPower = PowerSource.Unknown;
        private SessionState _lastSession = SessionState.Unknown;
        private CapabilityState _wakeCapability = CapabilityState.Ok;
        private CapabilityState _inputCapability = CapabilityState.Ok;

        private long _graceDeadlineMs;
        private long _lastJiggleMs;
        private bool _stopNotPersisted;
        private long _lastEnvironmentRefreshMs;
        private bool _settingsUnreadable;

        public ActivityCoordinator(
            ISettingsStore store,
            SettingsV1 initialSettings,
            IPowerSessionSource environment,
            IExecutionStateController executionState,
            IIdleInputSource idle,
            IMouseJiggler jiggler,
            IClock clock,
            IInputDesktopProbe desktop,
            ISynchronizeInvoke owner,
            IDiagnosticSink diagnostics)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _settings = initialSettings ?? throw new ArgumentNullException(nameof(initialSettings));
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
            _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
            _executionState = executionState ?? throw new ArgumentNullException(nameof(executionState));
            _idle = idle ?? throw new ArgumentNullException(nameof(idle));
            _jiggler = jiggler ?? throw new ArgumentNullException(nameof(jiggler));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _policy = new ActivityPolicy();

            _heartbeat = new WinFormsTimer { Interval = 1000 };
            _heartbeat.Tick += (_, __) => Evaluate();

            _onPowerChanged = (_, __) => OnEnvironmentChanged();
            _onSessionChanged = (_, __) => OnSessionChanged();

            _environment.PowerChanged += _onPowerChanged;
            _environment.SessionChanged += _onSessionChanged;
            _store.ExternalChangeObserved += OnExternalSettingsChange;
        }

        /// <summary>Raised whenever the visible state changes, so the tray and Settings can follow.</summary>
        public event EventHandler<DesiredEffects>? StateChanged;

        /// <summary>Raised when a Stop is in memory but could not be written.</summary>
        public event EventHandler<bool>? StopPersistenceWarningChanged;

        /// <summary>Raised whenever the saved settings change, from any source.</summary>
        public event EventHandler<SettingsV1>? SettingsChanged;

        /// <summary>
        /// Clears every effect and records a stopped state, for use when the process is about
        /// to die from an unhandled failure. Carrying on with unknown invariants would be worse
        /// than stopping: the wake request is the thing that must not outlive the app.
        /// </summary>
        public void EmergencyShutdown(string reasonCode)
        {
            try
            {
                _exiting = true;
                Interlocked.Increment(ref _generation);
                _heartbeat.Stop();
                _diagnostics.Record(FaultSubsystem.ShellOrIpc, reasonCode);
                _executionState.Release();

                _store.UpdateAsync(current => SettingsIntent.WithStopped(current, true), CancellationToken.None)
                      .Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public SettingsV1 Settings => _settings;

        public DesiredEffects LastEffects { get; private set; } = DesiredEffects.None(StatusCode.Stopped);

        public void Start()
        {
            RefreshEnvironment();
            ResetGrace();
            _heartbeat.Start();
            Evaluate();
        }

        /// <summary>
        /// Runs a command. Enabling commands persist before they take effect; Stop takes effect
        /// at once and persists afterwards.
        /// </summary>
        /// <returns>
        /// The outcome of the command itself. An enabling command that could not be saved fails
        /// here, because the app has deliberately stayed stopped and the user is owed the reason.
        /// Stop always succeeds: it takes effect in memory whatever the disk does, and a write
        /// that failed is reported through <see cref="StopPersistenceWarningChanged"/> instead,
        /// which is a standing condition rather than a one-off answer to a click.
        /// </returns>
        public async Task<OperationResult> ExecuteAsync(CommandKind kind)
        {
            if (_disposed)
            {
                return OperationResult.Success();
            }

            CommandOutcome outcome = ActivityCommandReducer.Reduce(kind, _settings);
            long generation = Interlocked.Increment(ref _generation);

            if (kind == CommandKind.Stop)
            {
                // In memory first. A disk failure must not leave the app running after the user
                // has asked it to stop.
                _settings = outcome.SettingsToPersist!;
                ClearAllEffects();
                Evaluate();

                (_store as MouseJiggler.Windows.Settings.JsonSettingsStore)?.MarkStopIssued();
                await PersistAsync(SettingsIntent.WithStopped, true, generation).ConfigureAwait(true);
                return OperationResult.Success();
            }

            if (kind == CommandKind.Exit)
            {
                _exiting = true;
                ClearAllEffects();
                Evaluate();
                return OperationResult.Success();
            }

            if (kind == CommandKind.Retry)
            {
                // Retry clears latched faults and starts a fresh grace period. It never starts
                // an app that is stopped.
                _wakeCapability = CapabilityState.Ok;
                _inputCapability = CapabilityState.Ok;
                _recovery.NoteWakeSuccess();
                ResetGrace();
                RefreshEnvironment();
                Evaluate();
                return OperationResult.Success();
            }

            if (outcome.SettingsToPersist == null)
            {
                Evaluate();
                return OperationResult.Success();
            }

            // An enabling command may not act until its change is committed.
            SettingsV1 target = outcome.SettingsToPersist;
            OperationResult<SettingsV1> saved = await _store
                .UpdateAsync(current => Rebuild(current, target), CancellationToken.None)
                .ConfigureAwait(true);

            if (!saved.Succeeded)
            {
                // Stay stopped and hand the reason back. The write is what authorizes the
                // change: starting anyway would run now and be stopped again on the next
                // launch, with nothing on screen to explain either.
                _diagnostics.Record(saved.Outcome.Subsystem, saved.Outcome.Code!, saved.Outcome.NativeErrorCode);
                Evaluate();
                return saved.Outcome;
            }

            if (generation != Interlocked.Read(ref _generation))
            {
                // Something superseded this command while the write was in flight. Nothing
                // failed, so there is nothing to report.
                return OperationResult.Success();
            }

            _settings = saved.Value!;
            ResetGrace();
            RefreshEnvironment();
            Evaluate();
            return OperationResult.Success();
        }

        /// <summary>
        /// Records that the one-time Settings window and tray explanation have been shown. This
        /// is intent rather than preference, so it cannot travel as a settings patch: a patch
        /// carries the existing flag through unchanged and would leave it false forever.
        /// </summary>
        public async Task MarkFirstRunCompleteAsync()
        {
            if (_settings.FirstRunCompleted)
            {
                return;
            }

            OperationResult<SettingsV1> saved = await _store
                .UpdateAsync(current => SettingsIntent.WithFirstRunCompleted(current, true), CancellationToken.None)
                .ConfigureAwait(true);

            if (saved.Succeeded)
            {
                _settings = saved.Value!;
                SettingsChanged?.Invoke(this, _settings);
            }
        }

        /// <summary>Applies a preference patch. Never touches the stopped flag or the mode.</summary>
        public async Task<OperationResult<SettingsV1>> ApplySettingsAsync(SettingsPatch patch)
        {
            if (patch == null)
            {
                throw new ArgumentNullException(nameof(patch));
            }

            OperationResult<SettingsV1> saved = await _store
                .UpdateAsync(patch.ApplyTo, CancellationToken.None)
                .ConfigureAwait(true);

            if (saved.Succeeded)
            {
                bool inputMechanismChanged = _settings.JiggleMouse != saved.Value!.JiggleMouse;

                _settings = saved.Value!;
                Interlocked.Increment(ref _generation);
                SettingsChanged?.Invoke(this, _settings);

                // Turning jiggling off and on again is the user changing the selected
                // mechanism, which clears a latched fault just as an explicit Retry does.
                if (inputMechanismChanged)
                {
                    _inputCapability = CapabilityState.Ok;
                }

                // A changed interval restarts the wait rather than firing immediately.
                ResetGrace();
                Evaluate();
            }

            return saved;
        }

        /// <summary>Called by the message window for power, session, suspend and clock messages.</summary>
        public void OnSystemMessage(int message, IntPtr wParam)
        {
            switch (message)
            {
                case WindowMessages.TimeChange:
                    _policy.InvalidateScheduleCache();
                    Evaluate();
                    break;

                case WindowMessages.PowerBroadcast:
                    if (wParam.ToInt32() == WindowMessages.PowerSuspend)
                    {
                        _suspended = true;
                        ClearAllEffects();
                        Evaluate();
                    }
                    else if (WindowMessages.IsResume(wParam.ToInt32()))
                    {
                        // Evaluate the present moment. Nothing is replayed for the time asleep.
                        _suspended = false;
                        _policy.InvalidateScheduleCache();
                        ResetGrace();
                        OnEnvironmentChanged();
                    }

                    break;
            }
        }

        private void OnSessionChanged()
        {
            // Lock has to take effect at once: invalidate first so queued input cannot land.
            Interlocked.Increment(ref _generation);
            RefreshEnvironment();

            if (_lastSession == SessionState.ActiveUnlocked)
            {
                // Coming back to an unlocked session is a recovery: the desktop the injection
                // failed against is gone, so holding the old fault would be reporting a problem
                // that no longer exists. A full interval passes before the first jiggle.
                _inputCapability = CapabilityState.Ok;
                ResetGrace();
            }

            Evaluate();
        }

        private void OnEnvironmentChanged()
        {
            RefreshEnvironment();
            Evaluate();
        }

        private void OnExternalSettingsChange(object? sender, SettingsV1 observed)
        {
            if (_owner.InvokeRequired)
            {
                // Arrived on the watcher's thread; hand it to the owner thread before acting.
                _owner.BeginInvoke(new Action(() => OnExternalSettingsChange(sender, observed)), null);
                return;
            }

            // Superseded while it was queued. The hop above is the gap: the file is read on the
            // watcher's thread, and by the time this runs a reset or this session's own commit
            // may have replaced what was read. Applying it anyway puts the app back on a
            // document that no longer exists, and the next save writes it over whatever did
            // replace it. See issue #20.
            if (observed.Revision != _store.LastKnownRevision)
            {
                return;
            }

            _settings = observed;
            Interlocked.Increment(ref _generation);
            SettingsChanged?.Invoke(this, _settings);
            Evaluate();
        }

        /// <summary>
        /// Re-reads power and session on a slow cadence regardless of notifications.
        /// </summary>
        /// <remarks>
        /// Notifications can be missed, and registering for them can fail outright, in which
        /// case nothing else would ever refresh these. Unplugging a laptop has to be noticed
        /// even then, so the heartbeat re-reads at least every five seconds. That is cheap
        /// enough to run always rather than only when registration failed, which also means the
        /// fallback path is exercised on every machine instead of only broken ones.
        /// </remarks>
        private void RefreshEnvironmentIfDue()
        {
            long now = _clock.MonotonicMilliseconds;

            if (_lastEnvironmentRefreshMs != 0 && (now - _lastEnvironmentRefreshMs) < EnvironmentRefreshIntervalMs)
            {
                return;
            }

            _lastEnvironmentRefreshMs = now;
            RefreshEnvironment();
        }

        private void RefreshEnvironment()
        {
            _lastEnvironmentRefreshMs = _clock.MonotonicMilliseconds;

            OperationResult<PowerSource> power = _environment.QueryPower();
            if (power.Succeeded)
            {
                _lastPower = power.Value;
            }
            else
            {
                _lastPower = PowerSource.Unknown;
                _diagnostics.Record(power.Outcome.Subsystem, power.Outcome.Code!, power.Outcome.NativeErrorCode);
            }

            OperationResult<SessionState> session = _environment.QuerySession();
            if (session.Succeeded)
            {
                _lastSession = session.Value;
            }
            else
            {
                _lastSession = _environment.LastNotifiedSession;
                _diagnostics.Record(session.Outcome.Subsystem, session.Outcome.Code!, session.Outcome.NativeErrorCode);
            }
        }

        private EnvironmentSnapshot CreateSnapshot()
        {
            return new EnvironmentSnapshot(
                _clock.UtcNow,
                _clock.LocalTimeZone,
                _clock.MonotonicMilliseconds,
                _lastPower,
                _lastSession,
                _suspended,
                _exiting,
                _desktop.IsInputDesktopAvailable(),
                _wakeCapability,
                _inputCapability,
                settingsAvailable: !_settingsUnreadable);
        }

        /// <summary>The heartbeat: evaluate, apply, and jiggle if one is due.</summary>
        public void Evaluate()
        {
            if (_disposed)
            {
                return;
            }

            RefreshEnvironmentIfDue();

            EnvironmentSnapshot snapshot = CreateSnapshot();
            RetryStopPersistenceIfDue(snapshot.MonotonicMilliseconds);
            DesiredEffects effects = _policy.Evaluate(_settings, snapshot);

            ApplyExecutionState(effects, snapshot);

            if (effects.MayJiggle)
            {
                TryJiggle(snapshot);
            }

            if (effects.Status != LastEffects.Status)
            {
                _diagnostics.RecordTransition(LastEffects.Status, effects.Status);
            }

            LastEffects = effects;
            StateChanged?.Invoke(this, effects);
        }

        private void ApplyExecutionState(DesiredEffects effects, EnvironmentSnapshot snapshot)
        {
            bool retryDue = _recovery.IsWakeRetryDue(snapshot.MonotonicMilliseconds);

            if (!effects.SystemAwake)
            {
                // The release path honours the backoff too. Without this guard a release that
                // keeps failing is re-attempted every single second forever, which is a busy
                // loop wearing the costume of a retry policy.
                if (_wakeCapability != CapabilityState.Ok && !retryDue)
                {
                    return;
                }

                OperationResult released = _executionState.Release();
                if (!released.Succeeded)
                {
                    _wakeCapability = CapabilityState.Transient;
                    _diagnostics.Record(released.Subsystem, released.Code!, released.NativeErrorCode);
                }

                if (!released.Succeeded && retryDue)
                {
                    // A failed release keeps retrying whatever the current eligibility is: the
                    // request may still be standing, and it must not outlive the process.
                    _recovery.NoteWakeFailure(snapshot.MonotonicMilliseconds);
                }
                else if (released.Succeeded)
                {
                    _wakeCapability = CapabilityState.Ok;
                    _recovery.NoteWakeSuccess();
                }

                return;
            }

            if (_wakeCapability != CapabilityState.Ok && !retryDue)
            {
                return;
            }

            OperationResult applied = _executionState.Apply(effects.SystemAwake, effects.DisplayAwake);
            if (applied.Succeeded)
            {
                _wakeCapability = CapabilityState.Ok;
                _recovery.NoteWakeSuccess();
            }
            else
            {
                _wakeCapability = CapabilityState.Transient;
                _diagnostics.Record(applied.Subsystem, applied.Code!, applied.NativeErrorCode);
                _recovery.NoteWakeFailure(snapshot.MonotonicMilliseconds);
            }
        }

        private void TryJiggle(EnvironmentSnapshot snapshot)
        {
            if (!_idle.TryGetIdleMilliseconds(out uint idleMs))
            {
                // An unreadable idle time is not a licence to move the pointer.
                return;
            }

            if (!JiggleEligibility.IsDue(idleMs, snapshot.MonotonicMilliseconds, _graceDeadlineMs, _lastJiggleMs, _settings.IntervalSeconds))
            {
                return;
            }

            long generation = Interlocked.Read(ref _generation);

            OperationResult result = _jiggler.SendJiggle();
            if (result.Succeeded)
            {
                _lastJiggleMs = snapshot.MonotonicMilliseconds;
                _inputCapability = CapabilityState.Ok;
                return;
            }

            _diagnostics.Record(result.Subsystem, result.Code!, result.NativeErrorCode);

            if (generation != Interlocked.Read(ref _generation))
            {
                // Superseded while sending; the outcome is irrelevant now.
                return;
            }

            if (!result.Retryable)
            {
                // Only a genuine send failure latches. A held button or a busy desktop is an
                // ordinary skip that will be due again on the next tick.
                _inputCapability = CapabilityState.Latched;
            }
        }

        private void ResetGrace()
        {
            _graceDeadlineMs = _clock.MonotonicMilliseconds + (_settings.IntervalSeconds * 1000L);
            _lastJiggleMs = 0;
        }

        private void ClearAllEffects()
        {
            Interlocked.Increment(ref _generation);
            _executionState.Release();
        }

        private async Task PersistAsync(Func<SettingsV1, bool, SettingsV1> change, bool value, long generation)
        {
            OperationResult<SettingsV1> saved = await _store
                .UpdateAsync(current => change(current, value), CancellationToken.None)
                .ConfigureAwait(true);

            bool warn = !saved.Succeeded;
            if (saved.Succeeded)
            {
                _settings = saved.Value!;
            }
            else
            {
                _diagnostics.Record(saved.Outcome.Subsystem, saved.Outcome.Code!, saved.Outcome.NativeErrorCode);

                // A Stop that only exists in memory is the one failure the user must be told
                // about, because the old state comes back on restart. Retrying is what lets the
                // warning clear itself when the disk recovers.
                _recovery.NoteStopWriteFailed(_clock.MonotonicMilliseconds);
            }

            if (_stopNotPersisted != warn)
            {
                _stopNotPersisted = warn;
                StopPersistenceWarningChanged?.Invoke(this, warn);
            }
        }

        /// <summary>
        /// Retries an outstanding stopped=true write on the 1, 5, 30 second schedule, then on
        /// every later command. Stop cancels capability retries but never this one.
        /// </summary>
        private void RetryStopPersistenceIfDue(long nowMs)
        {
            if (!_recovery.IsStopRetryDue(nowMs))
            {
                return;
            }

            _recovery.NoteStopRetryStarted(nowMs);

            OperationResult<SettingsV1> saved = _store
                .UpdateAsync(current => SettingsIntent.WithStopped(current, true), CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (!saved.Succeeded)
            {
                return;
            }

            _settings = saved.Value!;
            _recovery.NoteStopWritten();
            _stopNotPersisted = false;
            StopPersistenceWarningChanged?.Invoke(this, false);
        }

        /// <summary>
        /// One final bounded attempt to commit an outstanding Stop before the process ends.
        /// Returns false when it could not be committed, which the caller reports rather than
        /// exiting as though everything had been saved.
        /// </summary>
        public bool TryFlushPendingStop()
        {
            if (!_recovery.HasUncommittedStop)
            {
                return true;
            }

            try
            {
                Task<OperationResult<SettingsV1>> write = _store.UpdateAsync(
                    current => SettingsIntent.WithStopped(current, true), CancellationToken.None);

                if (!write.Wait(TimeSpan.FromSeconds(2)))
                {
                    return false;
                }

                if (write.Result.Succeeded)
                {
                    _recovery.NoteStopWritten();
                    return true;
                }
            }
            catch (AggregateException)
            {
            }

            return false;
        }

        /// <summary>True while a Stop is in memory but not yet on disk.</summary>
        public bool HasUncommittedStop => _recovery.HasUncommittedStop;

        /// <summary>
        /// True when the settings file exists but could not be read. The app runs on defaults
        /// so the user is not locked out, but it must say so rather than presenting a healthy
        /// stopped app while their real settings sit damaged on disk.
        /// </summary>
        public bool SettingsUnreadable => _settingsUnreadable;

        /// <summary>The fault that made the settings unreadable, for the recovery message.</summary>
        public string? SettingsFaultCode { get; private set; }

        /// <summary>Records that startup could not read the settings file.</summary>
        public void NoteUnreadableSettings(string faultCode)
        {
            _settingsUnreadable = true;
            SettingsFaultCode = faultCode;
            _diagnostics.Record(FaultSubsystem.ConfigRead, faultCode);
        }

        /// <summary>
        /// Quarantines the current settings file and starts again from stopped defaults. Only
        /// an explicit user action reaches here: recovering automatically could restart an app
        /// the user had deliberately stopped.
        /// </summary>
        public OperationResult<SettingsV1> ResetSettings()
        {
            var store = _store as MouseJiggler.Windows.Settings.JsonSettingsStore;
            if (store == null)
            {
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigSave, "settings.resetUnsupported", retryable: false);
            }

            OperationResult<SettingsV1> reset = store.ResetToDefaults();

            if (reset.Succeeded)
            {
                _settings = reset.Value!;
                _recovery.Reset();
                _stopNotPersisted = false;
                _settingsUnreadable = false;
                SettingsFaultCode = null;
                Interlocked.Increment(ref _generation);
                SettingsChanged?.Invoke(this, _settings);
                StopPersistenceWarningChanged?.Invoke(this, false);
                Evaluate();
            }
            else
            {
                _diagnostics.Record(reset.Outcome.Subsystem, reset.Outcome.Code!, reset.Outcome.NativeErrorCode);
            }

            return reset;
        }

        private static SettingsV1 Rebuild(SettingsV1 current, SettingsV1 target)
        {
            // Carry the intent from the command onto whatever the newest committed document is,
            // so a concurrent preference save is not lost.
            SettingsV1 withMode = SettingsIntent.WithMode(current, target.RunMode, target.Stopped);
            return withMode;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _exiting = true;

            _heartbeat.Stop();
            _heartbeat.Dispose();

            _store.ExternalChangeObserved -= OnExternalSettingsChange;
            _environment.PowerChanged -= _onPowerChanged;
            _environment.SessionChanged -= _onSessionChanged;

            // Release the wake request before anything else goes away.
            _executionState.Release();
        }
    }
}
