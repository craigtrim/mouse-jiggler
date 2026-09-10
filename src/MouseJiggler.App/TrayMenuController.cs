using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Commands;
using MouseJiggler.Core.Settings;

namespace MouseJiggler.App
{
    /// <summary>
    /// The tray icon and its menu: what they say, and what they look like.
    /// </summary>
    /// <remarks>
    /// Split out of the application context so that the words on the menu and the shape of the
    /// icon are decided in one place, next to each other, rather than interleaved with process
    /// lifetime, IPC and window messages. Nothing here decides anything: it is handed the
    /// current effects and settings and renders them, and it raises the commands the user picks
    /// for someone else to run.
    /// </remarks>
    public sealed class TrayMenuController : IDisposable
    {
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _menu;

        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _startStopItem;
        private readonly ToolStripMenuItem _startNowItem;
        private readonly ToolStripMenuItem _scheduleItem;
        private readonly ToolStripMenuItem _settingsItem;
        private readonly ToolStripMenuItem _releasesItem;
        private readonly ToolStripMenuItem _exitItem;

        private Icon? _currentIcon;
        private TrayIconFactory.Shape _currentShape = TrayIconFactory.Shape.Stopped;
        private bool _disposed;

        public TrayMenuController()
        {
            _statusItem = new ToolStripMenuItem { Enabled = false };
            _startStopItem = new ToolStripMenuItem("&Start", null, (_, __) => OnCommand(CommandKind.Start));
            _startNowItem = new ToolStripMenuItem("Start &now", null, (_, __) => OnCommand(CommandKind.StartNow));
            _scheduleItem = new ToolStripMenuItem("Start on &schedule", null, (_, __) => OnCommand(CommandKind.UseSchedule));
            _settingsItem = new ToolStripMenuItem("Se&ttings...", null, (_, __) => SettingsRequested?.Invoke(this, EventArgs.Empty));
            _releasesItem = new ToolStripMenuItem("View &releases", null, (_, __) => ReleasesRequested?.Invoke(this, EventArgs.Empty));
            _exitItem = new ToolStripMenuItem("E&xit", null, (_, __) => OnCommand(CommandKind.Exit));

            _menu = new ContextMenuStrip();
            _menu.Items.AddRange(new ToolStripItem[]
            {
                _statusItem,
                _startStopItem,
                _startNowItem,
                _scheduleItem,
                new ToolStripSeparator(),
                _settingsItem,
                _releasesItem,
                new ToolStripSeparator(),
                _exitItem,
            });

            _currentIcon = TrayIconFactory.Create(TrayIconFactory.Shape.Stopped, SystemInformation.SmallIconSize.Width);

            _trayIcon = new NotifyIcon
            {
                Icon = _currentIcon,
                Text = "Mouse Jiggler",
                ContextMenuStrip = _menu,
                Visible = true,
            };

            // Double click opens Settings. A single click does nothing, so the app cannot be
            // started or stopped by accident while aiming for the icon.
            _trayIcon.DoubleClick += (_, __) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Raised when the user picks a command from the menu.</summary>
        public event EventHandler<CommandKind>? CommandRequested;

        public event EventHandler? SettingsRequested;

        public event EventHandler? ReleasesRequested;

        /// <summary>Updates every visible piece of the tray from the current state.</summary>
        public void Render(DesiredEffects effects, SettingsV1 settings)
        {
            if (effects == null)
            {
                throw new ArgumentNullException(nameof(effects));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            _statusItem.Text = StatusTextFormatter.Primary(effects, settings, CultureInfo.CurrentCulture);
            _trayIcon.Text = StatusTextFormatter.Tooltip(effects);

            _startStopItem.Text = settings.Stopped ? "&Start" : "S&top";

            // Being blocked by power or a schedule does not disable Start: the user is setting
            // intent, and the status line explains why nothing is happening yet.
            _startNowItem.Enabled = settings.Stopped || settings.RunMode != RunMode.Manual;

            _scheduleItem.Text = settings.ScheduleEnabled ? "Start on &schedule" : "Start &continuously";
            _scheduleItem.Checked = !settings.Stopped && settings.RunMode == RunMode.Scheduled;

            UpdateIcon(TrayIconFactory.ShapeFor(effects.Status));
        }

        /// <summary>Shows a balloon on the icon, if the shell is willing to.</summary>
        public void ShowBalloon(string title, string text, ToolTipIcon icon, int milliseconds = 10000)
        {
            try
            {
                _trayIcon.BalloonTipTitle = title;
                _trayIcon.BalloonTipText = text;
                _trayIcon.BalloonTipIcon = icon;
                _trayIcon.ShowBalloonTip(milliseconds);
            }
            catch (InvalidOperationException)
            {
                // The shell refused it. Nothing here is load bearing.
            }
        }

        /// <summary>
        /// Re-adds the icon after Explorer restarts. Toggling Visible is what puts exactly one
        /// icon back; creating another NotifyIcon would leave two.
        /// </summary>
        public void Restore()
        {
            _trayIcon.Visible = false;
            _trayIcon.Visible = true;
        }

        private void OnCommand(CommandKind kind)
        {
            CommandRequested?.Invoke(this, kind);
        }

        private void UpdateIcon(TrayIconFactory.Shape shape)
        {
            if (shape == _currentShape && _currentIcon != null)
            {
                return;
            }

            // Built at the current small icon size, so it is sharp at whatever scale the
            // notification area is running at.
            Icon replacement = TrayIconFactory.Create(shape, SystemInformation.SmallIconSize.Width);
            Icon? previous = _currentIcon;

            _currentShape = shape;
            _currentIcon = replacement;
            _trayIcon.Icon = replacement;

            // Replaced first, then released. The tray must never be pointed at a disposed icon.
            previous?.Dispose();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _menu.Dispose();
            _currentIcon?.Dispose();
            _currentIcon = null;
        }
    }
}
