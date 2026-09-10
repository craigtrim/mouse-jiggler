using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using MouseJiggler.App.Runtime;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Commands;
using MouseJiggler.Core.Diagnostics;
using MouseJiggler.Core.Environment;
using MouseJiggler.Core.Scheduling;
using MouseJiggler.Core.Settings;
using MouseJiggler.Windows.Diagnostics;
using MouseJiggler.Windows.Startup;

namespace MouseJiggler.App
{
    /// <summary>
    /// The one Settings window.
    /// </summary>
    /// <remarks>
    /// Layout is written by hand with TableLayoutPanel and AutoSize rather than a designer file,
    /// so it scales with the system font and text size instead of pinning controls to fixed
    /// coordinates. Every control carries an accessible name and a mnemonic, because the icons
    /// and abbreviated day names mean nothing to a screen reader on their own.
    ///
    /// Preference edits are a local draft. Start, Stop and mode buttons act immediately and
    /// never save the draft, so pressing Start cannot quietly commit half-finished edits.
    /// </remarks>
    public sealed class SettingsForm : Form
    {
        private readonly ActivityCoordinator _coordinator;
        private readonly LocalDiagnosticSink _diagnostics;
        private readonly DiagnosticRing _diagnosticRing;
        private readonly RunKeyStartupRegistration _startup = new RunKeyStartupRegistration();

        private readonly Label _statusLabel = new Label();
        private readonly Label _detailLabel = new Label();
        private readonly Label _powerLabel = new Label();
        private readonly Button _startStopButton = new Button();
        private readonly Button _startNowButton = new Button();
        private readonly Button _scheduleButton = new Button();
        private readonly Button _retryButton = new Button();
        private readonly Label _persistenceWarning = new Label();
        private readonly Label _jiggleExplanation = new Label();
        private readonly Label _noMovementNote = new Label();
        private readonly Label _externalChangeNote = new Label();
        private TableLayoutPanel _root = new TableLayoutPanel();
        private AboutPanel? _about;

        /// <summary>
        /// The labels that wrap. Their measured width has to be pinned to the width they are
        /// given, or the panel measures them on one line and lays them out on two.
        /// </summary>
        private readonly List<Label> _wrapping = new List<Label>();

        /// <summary>
        /// The width the window is built around, before font scaling. The wrapping labels take
        /// whatever width they are given, so this is a choice about how the window looks rather
        /// than a constraint anything depends on.
        /// </summary>
        private const int TextWrapWidth = 460;

        /// <summary>
        /// What a group box costs around its content: its own padding on both sides, the frame,
        /// and the inset of the layout panel inside it.
        /// </summary>
        private const int GroupInset = 32;

        /// <summary>The client size the window opens at, in logical pixels, from issue #10.</summary>
        private const int InitialClientWidth = 540;

        private const int InitialClientHeight = 620;

        /// <summary>The smallest the window may be dragged to. Outer size, as MinimumSize is.</summary>
        private const int MinimumWindowWidth = 480;

        private const int MinimumWindowHeight = 540;

        /// <summary>
        /// The longest sentence the schedule line ever shows, and the one it starts with.
        /// </summary>
        /// <remarks>
        /// The row this label sits in is measured once, when the layout is built. A label that is
        /// empty at that moment gets a single line, and the sentence that arrives afterwards
        /// wraps onto a second one that the row was never given room for: the group below is
        /// drawn straight over it. Starting with the longest text it will ever hold means the row
        /// is right from the beginning, and every shorter message fits inside it.
        /// </remarks>
        private const string ContinuousScheduleNote =
            "With the schedule off, the app runs continuously once started.";
        private readonly Label _recoveryBanner = new Label();

        private readonly CheckBox _scheduleEnabled = new CheckBox();
        private readonly DateTimePicker _startTime = new DateTimePicker();
        private readonly DateTimePicker _endTime = new DateTimePicker();
        private readonly CheckBox[] _days = new CheckBox[7];
        private readonly Label _schedulePreview = new Label();

        private readonly CheckBox _pauseOnBattery = new CheckBox();
        private readonly CheckBox _keepDisplayOn = new CheckBox();
        private readonly CheckBox _jiggleMouse = new CheckBox();
        private readonly NumericUpDown _interval = new NumericUpDown();

        private readonly CheckBox _startWithWindows = new CheckBox();
        private readonly LinkLabel _openStartupSettings = new LinkLabel();
        private readonly Label _startupNote = new Label();

        private bool _startupRegisteredAtLoad;

        private readonly Button _save = new Button();
        private readonly Button _cancel = new Button();
        private readonly ErrorProvider _errors = new ErrorProvider();

        private bool _loading;
        private bool _dirty;

        /// <summary>True while this window is committing its own draft, so its own change is not reported as somebody else's.</summary>
        private bool _saving;

        private static readonly string[] DayNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        public SettingsForm(ActivityCoordinator coordinator, LocalDiagnosticSink diagnostics, DiagnosticRing diagnosticRing)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _diagnosticRing = diagnosticRing ?? throw new ArgumentNullException(nameof(diagnosticRing));

            Text = "Mouse Jiggler";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(MinimumWindowWidth, MinimumWindowHeight);
            AutoScroll = true;
            ShowInTaskbar = true;
            MaximizeBox = false;

            BuildLayout();
            LoadFromSettings();

            _coordinator.StateChanged += OnStateChanged;
            _coordinator.SettingsChanged += OnSettingsChangedElsewhere;
            OnStateChanged(this, _coordinator.LastEffects);

            // Measured last, once the labels hold the text they will actually show. The status
            // line and the schedule preview are filled in above and are the longest strings in
            // the window; measuring before they arrive sizes the form for placeholder text, and
            // the real sentence then wraps onto a second line the group box has no room for.
            PerformLayout();
            SizeToContent();
        }

        /// <summary>
        /// Regrows the window when the About section opens, and shrinks it again when it
        /// closes, rather than leaving the details behind a scrollbar or a band of empty space.
        /// </summary>
        private void OnAboutExpanded()
        {
            if (!IsHandleCreated)
            {
                return;
            }

            SizeToContent();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Font scaling happens between the constructor and here, and it scales the boxes
            // rather than re-measuring the text inside them. A label that wrapped onto two lines
            // at the design font can need three at 125%, while its container was only made 25%
            // taller, and the last line ends up behind the group box border.
            // Font scaling happens between the constructor and here, and it scales the boxes
            // rather than re-measuring the text inside them, so the window is measured again
            // once the scale has settled.
            SizeToContent();
            ClampIntoAConnectedScreen();
        }

        /// <summary>
        /// Moves the window back onto a monitor that is actually attached.
        /// </summary>
        /// <remarks>
        /// The window is centred on the primary screen and never remembers a position, so this
        /// should have nothing to do. It runs anyway because the cost of being wrong is a window
        /// nobody can reach: a monitor that vanished at exactly the wrong moment, or a working
        /// area that shrank when a dock disconnected, and the only recovery is a keyboard move
        /// the user has to already know about.
        /// </remarks>
        private void ClampIntoAConnectedScreen()
        {
            Rectangle working = Screen.FromControl(this).WorkingArea;

            // Never wider or taller than the screen it is on, or the clamp below cannot make
            // both edges fit and the title bar is what gets pushed off.
            int width = Math.Min(Width, working.Width);
            int height = Math.Min(Height, working.Height);

            int left = Math.Max(working.Left, Math.Min(Left, working.Right - width));
            int top = Math.Max(working.Top, Math.Min(Top, working.Bottom - height));

            Bounds = new Rectangle(left, top, width, height);
        }

        /// <summary>
        /// Pins each wrapping label's measured width to the width it is actually given.
        /// </summary>
        /// <remarks>
        /// A TableLayoutPanel measures a child by asking how big it would like to be, and a
        /// wrapping label answers with the width of its text on a single line. The row is sized
        /// for one line, the label is then laid out at the narrower column width, wraps onto a
        /// second line, and draws it outside the row it was given. Setting MaximumSize to the
        /// width the label already has makes the answer to that question the truth.
        /// </remarks>
        /// <summary>
        /// Registers a label whose text wraps, and fixes the width it wraps at.
        /// </summary>
        /// <remarks>
        /// The width is a constant, applied before anything has been laid out, and it is never
        /// changed again. That last part is what matters. Setting it afterwards, from the width
        /// the label had actually been given, re-wraps text inside a container that has already
        /// decided how tall it is, and the container does not always reconsider: the Startup
        /// group would end up forty-two pixels tall around seventy pixels of content, with its
        /// link drawn under the border.
        ///
        /// The window is sized so its columns are never narrower than this, so a label cannot
        /// wrap at one width and be drawn at another.
        /// </remarks>
        private void AddWrappingLabel(Label label, params string[] candidates)
        {
            label.MaximumSize = new Size(TextWrapWidth, 0);
            _wrapping.Add(label);

            // Reserved here, before the label is added to anything. Doing it afterwards changes
            // the label's height inside a container that has already worked out how tall it is,
            // and the container does not reliably reconsider. That was the last remaining cause
            // of a group box built one line short of its own contents.
            ReserveTallest(label, candidates);
        }

        /// <summary>Sets a label's minimum height to that of the tallest text it can hold.</summary>
        private static void ReserveTallest(Label label, IEnumerable<string> candidates)
        {
            int tallest = 0;

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate))
                {
                    continue;
                }

                int height = TextRenderer.MeasureText(
                    candidate,
                    label.Font,
                    new Size(TextWrapWidth, int.MaxValue),
                    TextFormatFlags.WordBreak).Height;

                tallest = Math.Max(tallest, height);
            }

            if (tallest > 0 && label.MinimumSize.Height != tallest)
            {
                label.MinimumSize = new Size(0, tallest);
            }
        }

        /// <summary>Every sentence the primary status line can show, for measuring.</summary>
        private static string[] EveryPrimaryStatus()
        {
            SettingsV1 settings = SettingsV1.CreateDefault();
            var sentences = new List<string>();

            foreach (StatusCode status in Enum.GetValues(typeof(StatusCode)))
            {
                sentences.Add(StatusTextFormatter.Primary(
                    DesiredEffects.None(status), settings, CultureInfo.CurrentCulture));
            }

            return sentences.ToArray();
        }

        /// <summary>Every sentence the qualifying detail line can show.</summary>
        private static string[] EveryStatusDetail()
        {
            SettingsV1 settings = SettingsV1.CreateDefault();
            var sentences = new List<string>();

            foreach (StatusCode status in Enum.GetValues(typeof(StatusCode)))
            {
                sentences.Add(StatusTextFormatter.Detail(DesiredEffects.None(status), settings));

                // The failure variants say more than the plain ones do.
                sentences.Add(StatusTextFormatter.Detail(
                    new DesiredEffects(false, false, false, status, null, TransitionKind.None, true, false),
                    settings));

                sentences.Add(StatusTextFormatter.Detail(
                    new DesiredEffects(false, false, false, status, null, TransitionKind.None, false, true),
                    settings));
            }

            return sentences.ToArray();
        }

        private void BuildLayout()
        {
            // Docked to the top rather than filling. Fill and AutoSize contradict each other:
            // Fill pins the panel to the client area, so it can never grow past it, so AutoScroll
            // has nothing to scroll and the overflow is simply unreachable. That is what cut the
            // About group and the Save button off the bottom of the window.
            _root = SettingsLayout.CreateColumn();
            _root.Padding = new Padding(12);

            _root.Controls.Add(BuildStatusGroup());
            _root.Controls.Add(BuildScheduleGroup());
            _root.Controls.Add(BuildBehaviourGroup());
            _root.Controls.Add(BuildStartupGroup());
            _root.Controls.Add(BuildAboutGroup());
            _root.Controls.Add(BuildButtons());

            Controls.Add(_root);
        }

        /// <summary>
        /// Sizes the window to the layout that was actually built, bounded by the screen.
        /// </summary>
        /// <remarks>
        /// A fixed client size is a guess that holds at exactly one font scale. At 125% text, or
        /// in a language whose labels run longer, the content is taller than the guess and the
        /// bottom of the form is lost. Asking the layout how tall it is costs nothing and is
        /// right at every scale; the screen bound stops a tall form opening off the desktop, and
        /// AutoScroll covers what still does not fit.
        /// </remarks>
        private void SizeToContent()
        {
            // The primary screen, because StartPosition is CenterScreen and that is where the
            // window will land. Using the screen under the pointer made the size of the window
            // depend on where the mouse happened to be when it opened.
            Rectangle working = Screen.PrimaryScreen.WorkingArea;

            // Width is derived rather than measured. Every control in here is narrower than the
            // wrapping labels, so TextWrapWidth is the content width by definition. Asking the
            // root panel how wide it would like to be does not work: it is docked to the top, so
            // its preferred width is whatever its parent currently is, which at this point is
            // the default form size and has nothing to do with what is inside it. Measuring that
            // produced a window narrower than its own labels, which then clipped.
            int width = TextWrapWidth
                        + _root.Padding.Horizontal
                        + GroupInset
                        + SystemInformation.VerticalScrollBarWidth;

            // The size issue #10 specifies is a floor, not a target. Content that needs more than
            // this gets more, because a fixed size that clips is the failure the measuring above
            // exists to avoid; content that needs less still opens at the specified size rather
            // than as a small box that looks like something failed to load.
            width = Math.Max(width, InitialClientWidth);
            width = Math.Min(width, (int)(working.Width * 0.9));

            // Height second, measured at the width just chosen, because the wrapping labels are
            // taller in a narrow window than a wide one. Measuring both at once would size the
            // window for text that then rewraps and no longer fits.
            ClientSize = new Size(width, ClientSize.Height);
            int height = Math.Max(_root.GetPreferredSize(new Size(width, 0)).Height, InitialClientHeight);

            ClientSize = new Size(width, Math.Min(height, (int)(working.Height * 0.9)));
        }

        private Control BuildStatusGroup()
        {
            var group = new GroupBox
            {
                Text = "Status",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                Padding = new Padding(8),
            };

            TableLayoutPanel layout = SettingsLayout.CreateColumn();

            _statusLabel.AutoSize = true;
            _statusLabel.Font = new Font(Font, FontStyle.Bold);
            _statusLabel.AccessibleName = "Current status";
            AddWrappingLabel(_statusLabel, EveryPrimaryStatus());

            _detailLabel.AutoSize = true;
            _detailLabel.Dock = DockStyle.Top;
            AddWrappingLabel(_detailLabel, EveryStatusDetail());
            _detailLabel.AccessibleName = "Status detail";

            _powerLabel.AutoSize = true;
            _powerLabel.AccessibleName = "Detected power source";

            _startStopButton.AutoSize = true;
            _startStopButton.Text = "&Start";
            _startStopButton.AccessibleName = "Start or stop";
            _startStopButton.Click += (_, __) => Run(_coordinator.Settings.Stopped ? CommandKind.Start : CommandKind.Stop);

            _startNowButton.AutoSize = true;
            _startNowButton.Text = "Start &now";
            _startNowButton.AccessibleDescription = "Runs manually until stopped, ignoring the schedule.";
            _startNowButton.Click += (_, __) => Run(CommandKind.StartNow);

            _scheduleButton.AutoSize = true;
            _scheduleButton.Text = "Start on s&chedule";
            _scheduleButton.Click += (_, __) => Run(CommandKind.UseSchedule);

            // Shown only when something has actually failed, so its presence means something.
            _retryButton.AutoSize = true;
            _retryButton.Text = "&Retry";
            _retryButton.Visible = false;
            _retryButton.AccessibleDescription = "Try the failed operation again.";
            _retryButton.Click += (_, __) => Run(CommandKind.Retry);

            _persistenceWarning.AutoSize = true;
            _persistenceWarning.Dock = DockStyle.Top;
            AddWrappingLabel(
                _persistenceWarning,
                "Stopped, but this change could not be saved. It may not survive a restart.");
            _persistenceWarning.ForeColor = SystemColors.ControlText;
            _persistenceWarning.Visible = false;
            _persistenceWarning.AccessibleName = "Save warning";

            _externalChangeNote.AutoSize = true;
            _externalChangeNote.Dock = DockStyle.Top;
            _externalChangeNote.Visible = false;
            _externalChangeNote.AccessibleName = "Settings changed elsewhere";
            _externalChangeNote.Text =
                "These settings were changed somewhere else while you were editing. Your edits "
                + "are still here, and saving will apply them on top of the newer settings.";
            AddWrappingLabel(
                _externalChangeNote,
                "These settings were changed somewhere else while you were editing. Your edits "
                + "are still here, and saving will apply them on top of the newer settings.");

            // Shown only over a damaged file, and it names Reset settings, which is the one
            // action that recovers without discarding the original.
            _recoveryBanner.AutoSize = true;
            _recoveryBanner.Dock = DockStyle.Top;
            AddWrappingLabel(
                _recoveryBanner,
                "Your saved settings could not be read (settings.unsupportedSchema), so defaults "
                + "are shown. The original file has been left alone. Use Reset settings to start "
                + "again from defaults.");
            _recoveryBanner.Visible = false;
            _recoveryBanner.AccessibleName = "Settings recovery warning";

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
            buttons.Controls.Add(_startStopButton);
            buttons.Controls.Add(_startNowButton);
            buttons.Controls.Add(_scheduleButton);
            buttons.Controls.Add(_retryButton);

            layout.Controls.Add(_statusLabel);
            layout.Controls.Add(_detailLabel);
            layout.Controls.Add(_recoveryBanner);
            layout.Controls.Add(_externalChangeNote);
            layout.Controls.Add(_persistenceWarning);
            layout.Controls.Add(_powerLabel);
            layout.Controls.Add(buttons);

            group.Controls.Add(layout);
            return group;
        }

        private Control BuildScheduleGroup()
        {
            var group = new GroupBox
            {
                Text = "Schedule",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                Padding = new Padding(8),
            };

            TableLayoutPanel layout = SettingsLayout.CreateColumn();

            _scheduleEnabled.Text = "R&un on a schedule";
            _scheduleEnabled.AutoSize = true;
            _scheduleEnabled.AccessibleName = "Run on a schedule";
            _scheduleEnabled.CheckedChanged += (_, __) => { MarkDirty(); UpdateScheduleEnabledState(); };

            ConfigureTimePicker(_startTime, "Schedule start time");
            ConfigureTimePicker(_endTime, "Schedule end time");

            var times = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
            times.Controls.Add(new Label { Text = "&Begins", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
            times.Controls.Add(_startTime);
            times.Controls.Add(new Label { Text = "&Ends", AutoSize = true, Padding = new Padding(12, 6, 4, 0) });
            times.Controls.Add(_endTime);

            // Seven day checkboxes do not fit on one row, so this panel wraps, and how many
            // rows it takes depends on how wide it is. That is the same circularity the
            // wrapping labels have: measured at one width and laid out at another, it is
            // sometimes measured as one row and drawn as two, and the group box is then built a
            // row short. Capping it at the same fixed width the labels use means it always
            // wraps the same way, whatever it is asked at.
            var dayPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                MaximumSize = new Size(TextWrapWidth, 0),
            };
            for (int i = 0; i < _days.Length; i++)
            {
                _days[i] = new CheckBox
                {
                    Text = DayNames[i],
                    AutoSize = true,

                    // The abbreviation is unreadable to a screen reader, so the full name is
                    // carried separately.
                    AccessibleName = SettingsPresenter.DayOrder[i].ToString(),
                };

                _days[i].CheckedChanged += (_, __) => MarkDirty();
                dayPanel.Controls.Add(_days[i]);
            }

            _schedulePreview.AutoSize = true;
            _schedulePreview.Dock = DockStyle.Top;
            AddWrappingLabel(
                _schedulePreview,
                ContinuousScheduleNote,
                "Ends the following day.",
                "Next: starts Wednesday at 12:00 AM. Ends the following day.");
            _schedulePreview.AccessibleName = "Next scheduled change";
            _schedulePreview.Text = ContinuousScheduleNote;

            layout.Controls.Add(_scheduleEnabled);
            layout.Controls.Add(times);
            layout.Controls.Add(dayPanel);
            layout.Controls.Add(_schedulePreview);

            group.Controls.Add(layout);
            return group;
        }

        private void ConfigureTimePicker(DateTimePicker picker, string accessibleName)
        {
            picker.Format = DateTimePickerFormat.Time;
            picker.ShowUpDown = true;
            picker.Width = 100;
            picker.AccessibleName = accessibleName;
            picker.ValueChanged += (_, __) => MarkDirty();
        }

        private Control BuildBehaviourGroup()
        {
            var group = new GroupBox
            {
                Text = "Behaviour",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                Padding = new Padding(8),
            };

            TableLayoutPanel layout = SettingsLayout.CreateColumn();

            _pauseOnBattery.Text = "&Pause on battery";
            _pauseOnBattery.AutoSize = true;
            _pauseOnBattery.AccessibleName = "Pause on battery";
            _pauseOnBattery.AccessibleDescription = "Runs only while the computer is on external power.";
            _pauseOnBattery.CheckedChanged += (_, __) => MarkDirty();

            _keepDisplayOn.Text = "Keep &display on";
            _keepDisplayOn.AutoSize = true;
            _keepDisplayOn.AccessibleName = "Keep display on";
            _keepDisplayOn.AccessibleDescription =
                "When off, Windows controls display sleep. Mouse jiggling may still keep the display on.";
            _keepDisplayOn.CheckedChanged += (_, __) => MarkDirty();

            _jiggleMouse.Text = "&Jiggle mouse";
            _jiggleMouse.AutoSize = true;
            _jiggleMouse.AccessibleName = "Jiggle mouse";
            _jiggleMouse.AccessibleDescription =
                "Moves the pointer one pixel and back after inactivity. No clicks or keystrokes.";
            _jiggleMouse.CheckedChanged += (_, __) => { MarkDirty(); UpdateIntervalEnabledState(); };

            _interval.Minimum = SettingsV1.MinIntervalSeconds;
            _interval.Maximum = SettingsV1.MaxIntervalSeconds;
            _interval.Increment = 5;
            _interval.Width = 80;
            _interval.AccessibleName = "Inactivity interval in seconds";
            _interval.ValueChanged += (_, __) => MarkDirty();

            var intervalRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
            intervalRow.Controls.Add(new Label { Text = "&After", AutoSize = true, Padding = new Padding(20, 6, 4, 0) });
            intervalRow.Controls.Add(_interval);
            intervalRow.Controls.Add(new Label { Text = "seconds of inactivity", AutoSize = true, Padding = new Padding(4, 6, 0, 0) });

            _jiggleExplanation.Text =
                "Moves the pointer one pixel and back after inactivity. No clicks or keystrokes.";
            _jiggleExplanation.AutoSize = true;
            _jiggleExplanation.Dock = DockStyle.Top;
            _jiggleExplanation.AccessibleName = "Movement explanation";
            AddWrappingLabel(
                _jiggleExplanation,
                "Moves the pointer one pixel and back after inactivity. No clicks or keystrokes.");

            // Shown only with jiggling off, which is exactly when the distinction matters: a
            // keep-awake request tells Windows the machine is in use, and Windows may still
            // lock an idle session. Saying so once here is better than a support question.
            _noMovementNote.Text =
                "Keeping the computer awake alone may not prevent idle locking.";
            _noMovementNote.AutoSize = true;
            _noMovementNote.Dock = DockStyle.Top;
            _noMovementNote.Visible = false;
            _noMovementNote.AccessibleName = "Movement disabled note";
            AddWrappingLabel(
                _noMovementNote,
                "Keeping the computer awake alone may not prevent idle locking.");

            layout.Controls.Add(_pauseOnBattery);
            layout.Controls.Add(_keepDisplayOn);
            layout.Controls.Add(_jiggleMouse);
            layout.Controls.Add(intervalRow);
            layout.Controls.Add(_jiggleExplanation);
            layout.Controls.Add(_noMovementNote);

            group.Controls.Add(layout);
            return group;
        }

        private Control BuildStartupGroup()
        {
            var group = new GroupBox
            {
                Text = "Startup",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                Padding = new Padding(8),
            };

            TableLayoutPanel layout = SettingsLayout.CreateColumn();

            _startWithWindows.Text = "Start with &Windows";
            _startWithWindows.AutoSize = true;
            _startWithWindows.AccessibleName = "Start with Windows";
            _startWithWindows.CheckedChanged += (_, __) => MarkDirty();

            // Always visible, never a tooltip. Windows really can override this, and a user who
            // does not know that will think the app is broken.
            _startupNote.Text = "Windows Startup apps can override this setting.";
            _startupNote.AutoSize = true;
            _startupNote.Dock = DockStyle.Top;
            AddWrappingLabel(
                _startupNote,
                "Windows Startup apps can override this setting.");

            _openStartupSettings.Text = "Open Windows startup settings";
            _openStartupSettings.AutoSize = true;
            _openStartupSettings.AccessibleName = "Open Windows startup settings";
            _openStartupSettings.LinkClicked += (_, __) => ShellLauncher.Open("ms-settings:startupapps");

            layout.Controls.Add(_startWithWindows);
            layout.Controls.Add(_startupNote);
            layout.Controls.Add(_openStartupSettings);

            group.Controls.Add(layout);
            return group;
        }

        private Control BuildAboutGroup()
        {
            _about = new AboutPanel(_coordinator, _diagnostics, _diagnosticRing);
            _about.PreferenceChanged += (_, __) => MarkDirty();
            _about.SettingsReset += (_, __) => LoadFromSettings();
            _about.ExpansionChanged += (_, __) => OnAboutExpanded();
            return _about;
        }

        private Control BuildButtons()
        {
            _save.Text = "Sa&ve";
            _save.AutoSize = true;
            _save.AccessibleName = "Save settings";
            _save.Click += async (_, __) => await SaveAsync().ConfigureAwait(true);

            _cancel.Text = "Cancel";
            _cancel.AutoSize = true;
            _cancel.AccessibleName = "Cancel";
            _cancel.Click += (_, __) => Close();

            AcceptButton = _save;
            CancelButton = _cancel;

            var panel = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.RightToLeft,
            };

            panel.Controls.Add(_cancel);
            panel.Controls.Add(_save);
            return panel;
        }

        private void LoadFromSettings()
        {
            _loading = true;

            SettingsV1 settings = _coordinator.Settings;

            _scheduleEnabled.Checked = settings.ScheduleEnabled;
            _startTime.Value = ToPickerValue(settings.ScheduleStart);
            _endTime.Value = ToPickerValue(settings.ScheduleEnd);

            for (int i = 0; i < _days.Length; i++)
            {
                _days[i].Checked = DailySchedule.IsDaySelected(settings.DayMask, SettingsPresenter.DayOrder[i]);
            }

            _pauseOnBattery.Checked = settings.PauseOnBattery;
            _keepDisplayOn.Checked = settings.KeepDisplayOn;
            _jiggleMouse.Checked = settings.JiggleMouse;
            _interval.Value = settings.IntervalSeconds;
            _about!.DiagnosticLogging = settings.DiagnosticLogging;

            // Read the real registration rather than trusting a saved flag: Windows and the user
            // can both change it behind the app's back.
            OperationResult<bool> registered = _startup.IsRegistered();
            _startupRegisteredAtLoad = registered.Succeeded && registered.Value;
            _startWithWindows.Checked = _startupRegisteredAtLoad;

            _loading = false;
            _dirty = false;
            _externalChangeNote.Visible = false;

            UpdateScheduleEnabledState();
            UpdateIntervalEnabledState();
            ValidateDraft();
        }

        private static DateTime ToPickerValue(string hhmm)
        {
            SettingsValidator.TryParseTimeOfDay(hhmm, out int minutes);
            return DateTime.Today.AddMinutes(minutes);
        }

        /// <summary>The values on screen, in one place, so nothing reads a control twice.</summary>
        private SettingsDraft ReadDraft()
        {
            var draft = new SettingsDraft
            {
                ScheduleEnabled = _scheduleEnabled.Checked,
                StartMinutes = (int)_startTime.Value.TimeOfDay.TotalMinutes,
                EndMinutes = (int)_endTime.Value.TimeOfDay.TotalMinutes,
                PauseOnBattery = _pauseOnBattery.Checked,
                KeepDisplayOn = _keepDisplayOn.Checked,
                JiggleMouse = _jiggleMouse.Checked,
                IntervalSeconds = (int)_interval.Value,
                DiagnosticLogging = _about!.DiagnosticLogging,
            };

            for (int i = 0; i < _days.Length; i++)
            {
                draft.Days[i] = _days[i].Checked;
            }

            return draft;
        }

        /// <summary>
        /// Checks the draft as it is edited and holds Save closed while it cannot be committed.
        /// </summary>
        /// <remarks>
        /// A Save button that is enabled for a document the store will reject teaches the user
        /// that pressing it is how you find out. The message appears beside the control at
        /// fault as soon as the problem exists, rather than after a round trip to disk.
        /// </remarks>
        private void ValidateDraft()
        {
            if (_loading || _about == null)
            {
                return;
            }

            _errors.Clear();

            OperationResult outcome = SettingsPresenter.ValidateDraft(ReadDraft(), _coordinator.Settings);

            if (outcome.Succeeded)
            {
                // Never re-enable in the middle of a write; the save path owns the button then.
                if (!_saving)
                {
                    _save.Enabled = true;
                }

                return;
            }

            ValidationMessage message = SettingsValidationPresenter.Describe(outcome.Code);

            if (message.BelongsToAControl)
            {
                _errors.SetError(ControlFor(message.Field), message.Text);
            }

            _save.Enabled = false;
        }

        /// <summary>
        /// Another writer committed a new revision while this window was open.
        /// </summary>
        /// <remarks>
        /// A dirty draft is never overwritten. Someone halfway through changing a schedule
        /// should not have the fields move under their hands because a second instance saved
        /// something, so the edits stay and a note explains what happened. With nothing to lose
        /// the form simply reloads, because showing stale values would be worse.
        /// </remarks>
        private void OnSettingsChangedElsewhere(object? sender, SettingsV1 settings)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnSettingsChangedElsewhere(sender, settings)));
                return;
            }

            // Our own save raises this too. Reporting it as somebody else's change would be
            // both wrong and alarming.
            if (_saving)
            {
                return;
            }

            if (!_dirty)
            {
                LoadFromSettings();
                return;
            }

            _externalChangeNote.Visible = true;
            PerformLayout();
        }

        private void MarkDirty()
        {
            if (_loading)
            {
                return;
            }

            _dirty = true;
            ValidateDraft();
        }

        private void UpdateScheduleEnabledState()
        {
            bool enabled = _scheduleEnabled.Checked;

            _startTime.Enabled = enabled;
            _endTime.Enabled = enabled;

            foreach (CheckBox day in _days)
            {
                day.Enabled = enabled;
            }

            if (!enabled)
            {
                _schedulePreview.Text = ContinuousScheduleNote;
            }
        }

        private void UpdateIntervalEnabledState()
        {
            _interval.Enabled = _jiggleMouse.Checked;

            // The two notes are alternatives: one describes what movement does, the other what
            // its absence cannot do. Showing both at once would be contradictory.
            _jiggleExplanation.Visible = _jiggleMouse.Checked;
            _noMovementNote.Visible = !_jiggleMouse.Checked;
        }

        private void OnStateChanged(object? sender, DesiredEffects effects)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnStateChanged(sender, effects)));
                return;
            }

            SettingsV1 settings = _coordinator.Settings;

            _statusLabel.Text = StatusTextFormatter.Primary(effects, settings, CultureInfo.CurrentCulture);
            _detailLabel.Text = StatusTextFormatter.Detail(effects, settings);

            // An empty qualifier is not a blank line. Docked labels reserve a row whether or not
            // they have anything to say, and a healthy status has nothing to qualify.
            _detailLabel.Visible = _detailLabel.Text.Length > 0;
            _powerLabel.Text = "Power: " + DescribePower(effects);

            _startStopButton.Text = settings.Stopped ? "&Start" : "S&top";
            _scheduleButton.Text = settings.ScheduleEnabled ? "Start on s&chedule" : "Start &continuously";

            // Retry appears exactly when there is something to retry.
            _retryButton.Visible = effects.WakeRequestFailed || effects.InputRequestFailed;

            bool unreadable = _coordinator.SettingsUnreadable;
            _recoveryBanner.Visible = unreadable;
            _recoveryBanner.Text = unreadable
                ? "Your saved settings could not be read (" + (_coordinator.SettingsFaultCode ?? "unknown") +
                  "), so defaults are shown. The original file has been left alone. Use Reset settings to start again from defaults."
                : string.Empty;

            bool unsavedStop = _coordinator.HasUncommittedStop;
            _persistenceWarning.Visible = unsavedStop;
            _persistenceWarning.Text = unsavedStop
                ? "Stopped, but this change could not be saved. It may not survive a restart."
                : string.Empty;

            // Every branch assigns. Leaving the label untouched would strand text describing a
            // state the app is no longer in, and the most likely leftover is the "runs
            // continuously" sentence, which would then be flatly wrong.
            if (!_scheduleEnabled.Checked)
            {
                _schedulePreview.Text = ContinuousScheduleNote;
            }
            else if (effects.NextTransitionUtc.HasValue)
            {
                DateTime local = TimeZoneInfo.ConvertTimeFromUtc(effects.NextTransitionUtc.Value, TimeZoneInfo.Local);
                string verb = effects.NextTransitionKind == TransitionKind.Start ? "starts" : "ends";
                string preview = "Next: " + verb + " " + local.ToString("dddd", CultureInfo.CurrentCulture) +
                                 " at " + local.ToString("t", CultureInfo.CurrentCulture) + ".";

                _schedulePreview.Text = IsOvernightDraft()
                    ? preview + " Ends the following day."
                    : preview;
            }
            else if (IsOvernightDraft())
            {
                _schedulePreview.Text = "Ends the following day.";
            }
            else
            {
                _schedulePreview.Text = string.Empty;
            }
        }

        private bool IsOvernightDraft()
        {
            return SettingsPresenter.IsOvernight(
                (int)_startTime.Value.TimeOfDay.TotalMinutes,
                (int)_endTime.Value.TimeOfDay.TotalMinutes);
        }

        private static string DescribePower(DesiredEffects effects)
        {
            switch (effects.Status)
            {
                case StatusCode.PausedOnBattery:
                    return "On battery";

                case StatusCode.PowerStatusUnavailable:
                    return "Unavailable";

                default:
                    return "Plugged in";
            }
        }

        private void Run(CommandKind kind)
        {
            // Deliberately does not save the draft. Pressing Start acts on the settings that are
            // actually saved, so a half-finished edit cannot take effect by accident.
            RunAsync(kind);
        }

        private async void RunAsync(CommandKind kind)
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

            if (outcome.Succeeded || IsDisposed)
            {
                return;
            }

            MessageBox.Show(
                this,
                StatusTextFormatter.CommandFailure(kind, outcome),
                "Mouse Jiggler",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private async Task SaveAsync()
        {
            _errors.Clear();

            SettingsPatch patch = SettingsPresenter.ToPatch(ReadDraft());

            _save.Enabled = false;
            _saving = true;

            try
            {
                OperationResult<SettingsV1> result = await _coordinator.ApplySettingsAsync(patch).ConfigureAwait(true);

                if (result.Succeeded)
                {
                    // Preferences are committed. The startup registration is a change against a
                    // different store, so it is applied second and reported on its own terms
                    // rather than folded into one claim that everything succeeded.
                    if (!ApplyStartupChange())
                    {
                        return;
                    }

                    _dirty = false;
                    Close();
                    return;
                }

                // The window stays open on failure, so the edits are not lost.
                ShowValidationError(result.Outcome.Code);
            }
            finally
            {
                _saving = false;
                _save.Enabled = true;
            }
        }

        /// <summary>
        /// Applies a staged startup change. Returns false when the form must stay open because
        /// the preferences were saved but the registration was not.
        /// </summary>
        private bool ApplyStartupChange()
        {
            if (_startWithWindows.Checked == _startupRegisteredAtLoad)
            {
                return true;
            }

            string executablePath = System.Reflection.Assembly.GetExecutingAssembly().Location;

            OperationResult result = _startWithWindows.Checked
                ? _startup.Register(executablePath)
                : _startup.UnregisterIfPathMatches(executablePath);

            if (result.Succeeded)
            {
                _startupRegisteredAtLoad = _startWithWindows.Checked;
                return true;
            }

            // Reload the real state rather than leave the checkbox asserting something untrue.
            OperationResult<bool> actual = _startup.IsRegistered();
            _startupRegisteredAtLoad = actual.Succeeded && actual.Value;
            _startWithWindows.Checked = _startupRegisteredAtLoad;

            MessageBox.Show(
                this,
                "Your preferences were saved, but the Windows startup setting could not be changed (" + result.Code + ").",
                "Mouse Jiggler",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return false;
        }

        private void ShowValidationError(string? code)
        {
            ValidationMessage message = SettingsValidationPresenter.Describe(code);

            if (!message.BelongsToAControl)
            {
                MessageBox.Show(this, message.Text, "Mouse Jiggler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Beside the control at fault, so the eye lands on the thing to change rather than
            // on a dialog that has to be dismissed before the field can be reached.
            _errors.SetError(ControlFor(message.Field), message.Text);
        }

        private Control ControlFor(SettingsField field)
        {
            switch (field)
            {
                case SettingsField.Days:
                    return _days[0];

                case SettingsField.Interval:
                    return _interval;

                default:
                    return _endTime;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_dirty && e.CloseReason == CloseReason.UserClosing)
            {
                DialogResult answer = MessageBox.Show(
                    this,
                    "Discard your unsaved changes?",
                    "Mouse Jiggler",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (answer == DialogResult.No)
                {
                    e.Cancel = true;
                    return;
                }
            }

            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _coordinator.StateChanged -= OnStateChanged;
                _coordinator.SettingsChanged -= OnSettingsChangedElsewhere;
                _errors.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
