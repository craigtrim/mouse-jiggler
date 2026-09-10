using System;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using MouseJiggler.App.Runtime;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Commands;
using MouseJiggler.Core.Diagnostics;
using MouseJiggler.Core.Settings;
using MouseJiggler.Windows.Diagnostics;
using MouseJiggler.Windows.Input;
using MouseJiggler.Windows.Interop;
using MouseJiggler.Windows.Power;
using MouseJiggler.Windows.Process;
using MouseJiggler.Windows.Sessions;
using MouseJiggler.Windows.Settings;
using MouseJiggler.Windows.Storage;

namespace MouseJiggler.App
{
    /// <summary>
    /// Owns the process. The tray icon, the hidden message window, the adapters and the
    /// coordinator all live and die here, on one STA thread, without a dummy visible form
    /// keeping the message loop alive.
    /// </summary>
    public sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly bool _quietStartup;
        private readonly SingleInstanceService _instance;
        private readonly NativeMessageWindow _messageWindow;
        private readonly JsonSettingsStore _store;
        private readonly SettingsFileWatcher _watcher;
        private readonly WindowsPowerSessionSource _environment;
        private readonly ExecutionStateController _executionState;
        private readonly DiagnosticRing _diagnosticRing;
        private readonly LocalDiagnosticSink _diagnostics;
        private readonly ActivityCoordinator _coordinator;
        private readonly TrayMenuController _tray;
        private readonly Control _marshaller;

        private readonly int _taskbarCreatedMessage;

        private Form? _settingsForm;
        private bool _disposed;

        public TrayApplicationContext(bool quietStartup, SingleInstanceService instance)
        {
            _quietStartup = quietStartup;
            _instance = instance ?? throw new ArgumentNullException(nameof(instance));

            SettingsPaths paths = SettingsPaths.ForCurrentUser();
            _store = new JsonSettingsStore(paths, new PhysicalFileOperations());

            // A settings file that cannot be read leaves the app stopped rather than guessing.
            OperationResult<SettingsV1> loaded = _store.Load();
            SettingsV1 settings = loaded.Succeeded ? loaded.Value! : SettingsV1.CreateDefault();

            // A handle created here, on the owner thread, is what background callbacks marshal
            // through. It is never shown.
            _marshaller = new Control();
            _ = _marshaller.Handle;

            _diagnosticRing = new DiagnosticRing();
            _diagnostics = new LocalDiagnosticSink(paths.LogsDirectory, _diagnosticRing);

            // Writing to disk follows the saved preference; the in-memory ring runs regardless,
            // so Copy diagnostics works without asking the user to reproduce the problem.
            _diagnostics.SetDiskLogging(settings.DiagnosticLogging);

            _watcher = new SettingsFileWatcher(paths, _store);
            _environment = new WindowsPowerSessionSource();
            _executionState = new ExecutionStateController();

            _coordinator = new ActivityCoordinator(
                _store,
                settings,
                _environment,
                _executionState,
                new IdleInputSource(),
                new MouseJigglerDevice(),
                new MonotonicClock(),
                new InputDesktopProbe(),
                _marshaller,
                _diagnostics);

            _messageWindow = new NativeMessageWindow();
            _messageWindow.MessageReceived += OnWindowMessage;

            // A failed registration is survivable, because the heartbeat re-reads power and
            // session every five seconds regardless. It is still recorded: without that, a
            // machine where detection is running six seconds late looks identical to one where
            // it is instant, and there is nothing in the log to tell them apart.
            OperationResult powerRegistration = _environment.RegisterPower(_messageWindow.Handle);
            if (!powerRegistration.Succeeded)
            {
                _diagnostics.Record(powerRegistration.Subsystem, powerRegistration.Code!, powerRegistration.NativeErrorCode);
            }

            OperationResult sessionRegistration = _environment.RegisterSession(_messageWindow.Handle);
            if (!sessionRegistration.Succeeded)
            {
                _diagnostics.Record(sessionRegistration.Subsystem, sessionRegistration.Code!, sessionRegistration.NativeErrorCode);
            }

            _taskbarCreatedMessage = RegisterTaskbarCreatedMessage();

            _tray = new TrayMenuController();
            _tray.CommandRequested += (_, kind) => RunCommand(kind);
            _tray.SettingsRequested += (_, __) => ShowSettings();
            _tray.ReleasesRequested += (_, __) => OpenReleasesPage();

            // A file that exists but cannot be read is not the same as no file at all. The app
            // carries on with defaults so the user is not locked out, and says so instead of
            // presenting a healthy stopped app over damaged settings.
            if (!loaded.Succeeded && System.IO.File.Exists(paths.SettingsFile))
            {
                _coordinator.NoteUnreadableSettings(loaded.Outcome.Code ?? "settings.unreadable");
            }

            _coordinator.StateChanged += OnStateChanged;
            _coordinator.SettingsChanged += (_, updated) => _diagnostics.SetDiskLogging(updated.DiagnosticLogging);
            _coordinator.StopPersistenceWarningChanged += OnStopPersistenceWarningChanged;
            _watcher.Start();
            _coordinator.Start();

            if (!_quietStartup && !settings.FirstRunCompleted)
            {
                ExplainTheTrayIcon();
                ShowSettings();
                MarkFirstRunComplete();
            }
        }

        /// <summary>
        /// Last-resort handling for an unhandled exception. Effects are cleared and the app
        /// exits rather than continuing with invariants nobody can vouch for.
        /// </summary>
        public void HandleFatalError(Exception? error)
        {
            _coordinator.EmergencyShutdown("app.unhandledException");

            if (!_quietStartup)
            {
                MessageBox.Show(
                    "Mouse Jiggler has stopped because of an unexpected error and is closing. It is no longer keeping this computer awake."
                        + (error == null ? string.Empty : System.Environment.NewLine + System.Environment.NewLine + error.GetType().Name),
                    "Mouse Jiggler",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            Shutdown();
        }

        /// <summary>Handles a request that arrived from a second launch.</summary>
        public string HandleIpcRequest(string request)
        {
            // Marshal onto the owner thread through the control whose handle was created there.
            // The context menu is not a safe choice: until it has been shown once it has no
            // handle, and BeginInvoke on it throws.
            if (string.Equals(request, IpcCommands.ShowSettings, StringComparison.Ordinal))
            {
                _marshaller.BeginInvoke(new Action(ShowSettings));
                return IpcCommands.ResponseOk;
            }

            if (string.Equals(request, IpcCommands.ShutdownForUpdate, StringComparison.Ordinal))
            {
                // Answer with the real outcome, not an optimistic OK. Invoke rather than
                // BeginInvoke so the shutdown has actually happened before the caller is told.
                object? outcome = _marshaller.Invoke(new Func<bool>(ShutdownGracefully), null);

                return outcome is bool committed && committed
                    ? IpcCommands.ResponseOk
                    : IpcCommands.ResponseStopNotPersisted;
            }

            return IpcCommands.ResponseUnknownCommand;
        }

        private void OnWindowMessage(object? sender, WindowMessageEventArgs e)
        {
            if (e.Message == _taskbarCreatedMessage)
            {
                RestoreTrayIcon();
                return;
            }

            if (e.Message == WindowMessages.PowerBroadcast)
            {
                _environment.HandleMessage(e.Message, e.WParam, e.LParam);
                _coordinator.OnSystemMessage(e.Message, e.WParam);
                return;
            }

            if (e.Message == WindowMessages.SessionChange)
            {
                _environment.HandleMessage(e.Message, e.WParam, e.LParam);
                return;
            }

            if (e.Message == WindowMessages.TimeChange)
            {
                _coordinator.OnSystemMessage(e.Message, e.WParam);
                return;
            }

            if (e.Message == WindowMessages.EndSession)
            {
                // Signing out or shutting down still goes through the ordinary cleanup, so the
                // wake request is released rather than abandoned.
                Shutdown();
            }
        }

        private void OnStopPersistenceWarningChanged(object? sender, bool unsaved)
        {
            if (!unsaved)
            {
                return;
            }

            // Stop took effect in memory, but it will not survive a restart. Saying so once is
            // better than a tooltip nobody reads or silence that implies it worked.
            _tray.ShowBalloon(
                "Mouse Jiggler",
                "Stopped, but this change could not be saved. It may not survive a restart.",
                ToolTipIcon.Warning);
        }

        private void OnStateChanged(object? sender, DesiredEffects effects)
        {
            _tray.Render(effects, _coordinator.Settings);
        }

        private void RunCommand(CommandKind kind)
        {
            if (kind == CommandKind.Exit)
            {
                ShutdownGracefully();
                return;
            }

            if (kind == CommandKind.Start && !_coordinator.Settings.Stopped)
            {
                kind = CommandKind.Stop;
            }

            RunCommandAsync(kind);
        }

        /// <summary>
        /// Runs the command and reports a refusal. Discarding the outcome here was the whole
        /// defect: a Start that could not be saved left the tray icon stopped and said nothing,
        /// which is indistinguishable from a click that never registered.
        /// </summary>
        private async void RunCommandAsync(CommandKind kind)
        {
            OperationResult outcome;

            try
            {
                outcome = await _coordinator.ExecuteAsync(kind).ConfigureAwait(true);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (outcome.Succeeded || _quietStartup || _disposed)
            {
                return;
            }

            MessageBox.Show(
                StatusTextFormatter.CommandFailure(kind, outcome),
                "Mouse Jiggler",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private void ShowSettings()
        {
            if (_settingsForm != null && !_settingsForm.IsDisposed)
            {
                _settingsForm.Activate();
                return;
            }

            _settingsForm = new SettingsForm(_coordinator, _diagnostics, _diagnosticRing);
            _settingsForm.FormClosed += (_, __) => _settingsForm = null;
            _settingsForm.Show();
        }

        /// <summary>
        /// Says once, on the first launch, where the app lives. A tray-only app that just opens
        /// a window has told the user nothing about what happens when they close it: closing
        /// Settings is not quitting, and the icon is the only way back.
        /// </summary>
        /// <remarks>
        /// A balloon is used rather than a dialog because it points at the icon it is describing.
        /// Windows may suppress it entirely, under focus assist or a notifications policy, so
        /// nothing depends on it having been seen: it explains, and Settings opens regardless.
        /// </remarks>
        private void ExplainTheTrayIcon()
        {
            _tray.ShowBalloon(
                "Mouse Jiggler is running here",
                "It stays in the notification area. Right click the icon to start or stop it, "
                + "and double click to reopen Settings. Closing the Settings window does not quit.",
                ToolTipIcon.Info);
        }

        private void MarkFirstRunComplete()
        {
            Task task = _coordinator.MarkFirstRunCompleteAsync();
            task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private void OpenReleasesPage()
        {
            try
            {
                System.Diagnostics.Process.Start("https://github.com/craigtrim/mouse-jiggler/releases");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No browser association. Nothing useful to do beyond not crashing.
            }
        }

        private void RestoreTrayIcon()
        {
            // Explorer restarted, so the icon has to be put back.
            _tray.Restore();
        }

        private static int RegisterTaskbarCreatedMessage()
        {
            return (int)NativeTaskbar.RegisterWindowMessage("TaskbarCreated");
        }

        /// <summary>
        /// Exits, giving an outstanding Stop one last bounded chance to reach disk. Returns
        /// false when it could not be committed, so the caller can report that rather than
        /// letting a restart quietly resurrect the previous state.
        /// </summary>
        private bool ShutdownGracefully()
        {
            bool committed = _coordinator.TryFlushPendingStop();
            Shutdown();
            return committed;
        }

        private void Shutdown()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Order matters: stop deciding, release effects, then take the surfaces away.
            _coordinator.Dispose();
            _watcher.Dispose();

            _tray.Dispose();

            if (_settingsForm != null && !_settingsForm.IsDisposed)
            {
                _settingsForm.Close();
            }

            _messageWindow.MessageReceived -= OnWindowMessage;
            _messageWindow.Dispose();
            _marshaller.Dispose();

            _diagnostics.Dispose();
            _environment.Dispose();
            _executionState.Dispose();
            _store.Dispose();

            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Shutdown();
            }

            base.Dispose(disposing);
        }
    }

    internal static class NativeTaskbar
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        internal static extern uint RegisterWindowMessage(string message);
    }
}
