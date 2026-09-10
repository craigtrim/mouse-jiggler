using System;
using System.Windows.Forms;
using MouseJiggler.App.Runtime;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Diagnostics;
using MouseJiggler.Core.Environment;
using MouseJiggler.Core.Settings;
using MouseJiggler.Windows.Diagnostics;
using MouseJiggler.Windows.Startup;

namespace MouseJiggler.App
{
    /// <summary>
    /// The About and diagnostics section of the Settings window.
    /// </summary>
    /// <remarks>
    /// Separate because everything in it is about the installation rather than about what the
    /// app does: the version, the licence, the log, and the way back to defaults. None of it
    /// takes part in the save, apart from the one preference the checkbox holds.
    ///
    /// Copying diagnostics and resetting settings both live here rather than behind a menu.
    /// Someone trying to report a problem, or trying to get out of a broken state, should not
    /// have to already know where those are.
    /// </remarks>
    public sealed class AboutPanel : GroupBox
    {
        private readonly ActivityCoordinator _coordinator;
        private readonly LocalDiagnosticSink _diagnostics;
        private readonly DiagnosticRing _ring;

        private readonly CheckBox _diagnosticLogging = new CheckBox();
        private readonly Button _copyDiagnostics = new Button();
        private readonly Button _openLogFolder = new Button();
        private readonly Button _resetSettings = new Button();

        /// <summary>The expander. A checkbox, so a screen reader announces its state.</summary>
        private readonly CheckBox _expander = new CheckBox();

        private readonly Panel _details = new Panel();

        public AboutPanel(ActivityCoordinator coordinator, LocalDiagnosticSink diagnostics, DiagnosticRing ring)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _ring = ring ?? throw new ArgumentNullException(nameof(ring));

            Text = "About and diagnostics";
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Dock = DockStyle.Top;
            Padding = new Padding(8);

            Build();
        }

        /// <summary>Raised when the diagnostic logging checkbox changes, so the form can mark itself dirty.</summary>
        public event EventHandler? PreferenceChanged;

        /// <summary>Raised after a successful reset, so the form can reload from the new defaults.</summary>
        public event EventHandler? SettingsReset;

        /// <summary>Raised when the section opens or closes, so the window can resize to suit.</summary>
        public event EventHandler? ExpansionChanged;

        /// <summary>Whether the details are showing. Closed on every launch, never remembered.</summary>
        public bool Expanded
        {
            get => _expander.Checked;
            set => _expander.Checked = value;
        }

        /// <summary>The one preference this panel owns.</summary>
        public bool DiagnosticLogging
        {
            get => _diagnosticLogging.Checked;
            set => _diagnosticLogging.Checked = value;
        }

        private void Build()
        {
            TableLayoutPanel outer = SettingsLayout.CreateColumn();

            // Collapsed on every launch rather than remembered. This section is for the day
            // something is wrong, and on every other day it is four controls of noise above the
            // Save button. A checkbox rather than a chevron drawn on a label, because a checkbox
            // already reports its own state to a screen reader and already takes the keyboard.
            _expander.Text = "S&how details";
            _expander.AutoSize = true;
            _expander.Checked = false;
            _expander.AccessibleName = "Show about and diagnostics details";
            _expander.AccessibleDescription = "Version, licence, diagnostic logging and reset.";
            _expander.CheckedChanged += (_, __) =>
            {
                _details.Visible = _expander.Checked;
                ExpansionChanged?.Invoke(this, EventArgs.Empty);
            };

            _details.AutoSize = true;
            _details.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _details.Dock = DockStyle.Top;
            _details.Visible = false;

            TableLayoutPanel layout = SettingsLayout.CreateColumn();

            var version = new Label
            {
                Text = "Mouse Jiggler " + DiagnosticSnapshotBuilder.AppVersion + ", MIT licence.",
                AutoSize = true,
                AccessibleName = "Version and licence",
            };

            var source = new LinkLabel
            {
                Text = "View source and releases",
                AutoSize = true,
                AccessibleName = "View source and releases on GitHub",
            };

            source.LinkClicked += (_, __) => ShellLauncher.Open("https://github.com/craigtrim/mouse-jiggler/releases");

            var licence = new LinkLabel
            {
                Text = "View the MIT licence",
                AutoSize = true,
                AccessibleName = "View the MIT licence",
            };

            licence.LinkClicked += (_, __) => OpenLicence();

            _diagnosticLogging.Text = "Enable diagnostic &logging";
            _diagnosticLogging.AutoSize = true;
            _diagnosticLogging.AccessibleDescription = "Writes a bounded local log. Nothing is ever sent anywhere.";
            _diagnosticLogging.CheckedChanged += (_, __) => PreferenceChanged?.Invoke(this, EventArgs.Empty);

            _copyDiagnostics.Text = "Cop&y diagnostics";
            _copyDiagnostics.AutoSize = true;
            _copyDiagnostics.Click += (_, __) => CopyDiagnostics();

            _openLogFolder.Text = "Open log &folder";
            _openLogFolder.AutoSize = true;
            _openLogFolder.Click += (_, __) => OpenLogFolder();

            _resetSettings.Text = "Reset settin&gs";
            _resetSettings.AutoSize = true;
            _resetSettings.AccessibleDescription =
                "Keeps a copy of the current file and starts again from stopped defaults.";
            _resetSettings.Click += (_, __) => ResetSettings();

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
            buttons.Controls.Add(_copyDiagnostics);
            buttons.Controls.Add(_openLogFolder);
            buttons.Controls.Add(_resetSettings);

            layout.Controls.Add(version);
            layout.Controls.Add(source);
            layout.Controls.Add(licence);
            layout.Controls.Add(_diagnosticLogging);
            layout.Controls.Add(buttons);

            _details.Controls.Add(layout);

            outer.Controls.Add(_expander);
            outer.Controls.Add(_details);

            Controls.Add(outer);
        }

        /// <summary>
        /// Opens the licence that ships beside the executable, or the canonical one if it is
        /// missing. The local copy is the one that actually governs this build.
        /// </summary>
        private static void OpenLicence()
        {
            string directory = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? string.Empty;

            string local = System.IO.Path.Combine(directory, "LICENSE");

            ShellLauncher.Open(System.IO.File.Exists(local)
                ? local
                : "https://github.com/craigtrim/mouse-jiggler/blob/main/LICENSE");
        }

        private void CopyDiagnostics()
        {
            string text = DiagnosticSnapshotBuilder.Build(
                _coordinator.Settings,
                _coordinator.LastEffects,
                DescribePowerSource(_coordinator.LastEffects),
                _coordinator.LastEffects.Status == StatusCode.LockedKeepingAwake ? SessionState.Locked : SessionState.ActiveUnlocked,
                installedEdition: IsInstalledEdition(),
                recentEvents: _ring.Snapshot());

            try
            {
                Clipboard.SetText(text);
                MessageBox.Show(this, "Diagnostics copied to the clipboard.", "Mouse Jiggler", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Another program is holding the clipboard. Say so rather than fail silently.
                MessageBox.Show(this, "The clipboard is unavailable just now. Try again in a moment.", "Mouse Jiggler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// The power source as the visible status implies it, for the diagnostic snapshot.
        /// </summary>
        /// <remarks>
        /// Derived from the status rather than queried again, so the snapshot describes what the
        /// user was actually looking at when they copied it. A fresh query could disagree with
        /// the window and turn the report into a puzzle.
        /// </remarks>
        private static PowerSource DescribePowerSource(DesiredEffects effects)
        {
            switch (effects.Status)
            {
                case StatusCode.PausedOnBattery:
                    return PowerSource.Battery;

                case StatusCode.PowerStatusUnavailable:
                    return PowerSource.Unknown;

                default:
                    return PowerSource.External;
            }
        }

        private static bool IsInstalledEdition()
        {
            string directory = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? string.Empty;

            return System.IO.File.Exists(InstalledMarker.PathFor(directory));
        }

        private void ResetSettings()
        {
            DialogResult answer = MessageBox.Show(
                this,
                "Start again from default settings?" + System.Environment.NewLine + System.Environment.NewLine +
                "A copy of your current settings is kept alongside them, and the app stays stopped.",
                "Mouse Jiggler",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                return;
            }

            OperationResult<SettingsV1> reset = _coordinator.ResetSettings();

            if (reset.Succeeded)
            {
                SettingsReset?.Invoke(this, EventArgs.Empty);
                return;
            }

            MessageBox.Show(
                this,
                "The settings could not be reset (" + reset.Outcome.Code + "). Your existing settings are unchanged.",
                "Mouse Jiggler",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private void OpenLogFolder()
        {
            string folder = System.IO.Path.GetDirectoryName(_diagnostics.LogFilePath) ?? string.Empty;

            try
            {
                // Created only on this explicit action, never as a side effect of running.
                System.IO.Directory.CreateDirectory(folder);
                ShellLauncher.Open(folder);
            }
            catch (System.IO.IOException)
            {
                MessageBox.Show(this, "The log folder could not be opened.", "Mouse Jiggler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

    }
}
