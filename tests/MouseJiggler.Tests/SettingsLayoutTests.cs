using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MouseJiggler.App;
using MouseJiggler.App.Runtime;
using MouseJiggler.Core.Diagnostics;
using MouseJiggler.Core.Settings;
using MouseJiggler.Tests.Fakes;
using MouseJiggler.Windows.Diagnostics;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The Settings window measured rather than eyeballed.
    /// </summary>
    /// <remarks>
    /// Clipping is the failure mode that a screenshot at one font scale hides: the window looks
    /// right on the machine it was designed on and loses its bottom half at 125% text. Walking
    /// the built control tree and comparing every child against its parent's client area catches
    /// that without a person looking at it.
    ///
    /// These run on an STA thread because Windows Forms requires one, and they never call Show:
    /// the layout is computed by the constructor, so nothing has to appear on screen.
    /// </remarks>
    public sealed class SettingsLayoutTests
    {
        private static void OnFormThread(Action<SettingsForm> body)
        {
            Exception? failure = null;

            var thread = new System.Threading.Thread(() =>
            {
                using (var directory = new TemporaryDirectory())
                {
                    var ring = new DiagnosticRing();
                    var sink = new LocalDiagnosticSink(Path.Combine(directory.Path, "Logs"), ring);
                    var harness = new CoordinatorHarness();

                    try
                    {
                        using (var form = new SettingsForm(harness.Coordinator, sink, ring))
                        {
                            body(form);
                        }
                    }
                    catch (Exception error)
                    {
                        failure = error;
                    }
                    finally
                    {
                        harness.Dispose();
                        sink.Dispose();
                    }
                }
            });

            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                throw new Xunit.Sdk.XunitException(failure.ToString());
            }
        }

        /// <summary>The smallest coordinator that will construct, wired to fakes.</summary>
        private sealed class CoordinatorHarness : IDisposable
        {
            public CoordinatorHarness()
            {
                SettingsV1 settings = SettingsV1.CreateDefault();
                _store = new FakeSettingsStore(settings);
                _environment = new FakePowerSessionSource();
                _executionState = new FakeExecutionStateController();

                Coordinator = new ActivityCoordinator(
                    _store,
                    settings,
                    _environment,
                    _executionState,
                    new FakeIdleInputSource(),
                    new FakeMouseJiggler(),
                    new FakeClock(),
                    new FakeInputDesktopProbe(),
                    new ImmediateInvoker(),
                    new RecordingDiagnosticSink());
            }

            private readonly FakeSettingsStore _store;
            private readonly FakePowerSessionSource _environment;
            private readonly FakeExecutionStateController _executionState;

            public ActivityCoordinator Coordinator { get; }

            public void Dispose()
            {
                Coordinator.Dispose();
                _environment.Dispose();
                _executionState.Dispose();
                _store.Dispose();
            }
        }

        [Fact]
        public void NothingIsCutOffByTheWindowItWasLaidOutIn()
        {
            OnFormThread(form =>
            {
                var clipped = new List<string>();
                Inspect(form, clipped);

                Assert.True(clipped.Count == 0, string.Join(System.Environment.NewLine, clipped));
            });
        }

        [Fact]
        public void TheWindowOpensTallEnoughForItsOwnContent()
        {
            OnFormThread(form =>
            {
                Control root = form.Controls[0];

                // Width is not negotiable: a window narrower than its own labels clips them, and
                // no scrollbar helps, because the wrapping already happened at the wrong width.
                Assert.True(
                    form.ClientSize.Width >= root.Width,
                    "The window is " + form.ClientSize.Width + " wide but its content needs " + root.Width + ".");

                // Height may legitimately be bounded by a short screen, so the invariant is not
                // that everything fits. It is that everything is reachable: fully visible, or
                // scrollable to.
                Assert.True(
                    form.ClientSize.Height >= root.Height || form.AutoScroll,
                    "The window is " + form.ClientSize.Height + " tall, its content needs " + root.Height +
                    ", and it does not scroll.");
            });
        }

        [Fact]
        public void TheWindowOpensAtTheSizeTheSpecificationAsksFor()
        {
            OnFormThread(form =>
            {
                // Issue #10 names 540x620 as the initial client size and 480x540 as the minimum.
                // The initial size is a floor rather than a fixed value: content that needs more
                // gets more. What is asserted is that it is never less, so the window does not
                // open as a small box that reads as something having failed to load.
                Rectangle working = Screen.PrimaryScreen.WorkingArea;

                if (working.Width * 0.9 >= 540 && working.Height * 0.9 >= 620)
                {
                    Assert.True(
                        form.ClientSize.Width >= 540,
                        "The window opened " + form.ClientSize.Width + " wide.");
                    Assert.True(
                        form.ClientSize.Height >= 620,
                        "The window opened " + form.ClientSize.Height + " tall.");
                }

                Assert.Equal(new Size(480, 540), form.MinimumSize);
            });
        }

        [Fact]
        public void TheContentIsReachableWhenTheWindowIsTooSmallForIt()
        {
            OnFormThread(form =>
            {
                // Shrink it well below what the layout needs. A scrollbar has to appear, because
                // the alternative is content the user cannot reach by any means.
                form.ClientSize = new Size(form.ClientSize.Width, 300);
                form.PerformLayout();

                Assert.True(form.AutoScroll);
                Assert.True(
                    form.VerticalScroll.Visible,
                    "No vertical scrollbar appeared, so the content below the fold is unreachable.");
            });
        }

        [Theory]
        [InlineData(1.25f)]
        [InlineData(1.5f)]
        public void NothingIsTruncatedWhenTheUserHasLargerText(float scale)
        {
            OnFormThread(form =>
            {
                // Standing in for a display at 125% or 150%, and for a user who has simply
                // asked Windows for bigger text. Either makes every string wider than it was
                // on the machine the window was designed on.
                form.Font = new Font(form.Font.FontFamily, form.Font.Size * scale);
                form.PerformLayout();

                var clipped = new List<string>();
                Inspect(form, clipped);

                Assert.True(clipped.Count == 0, string.Join(System.Environment.NewLine, clipped));
            });
        }

        [Fact]
        public void TheStatusAndScheduleLinesAreMeasuredWithTheTextTheyActuallyShow()
        {
            OnFormThread(form =>
            {
                var clipped = new List<string>();

                // The longest sentence either line ever holds. These are filled in after the
                // layout is built, so a window measured before they arrive is sized for
                // placeholder text and cuts the real thing in half.
                foreach (Label label in FindLabels(form))
                {
                    if (label.MaximumSize.Width <= 0)
                    {
                        continue;
                    }

                    label.Text = "With the schedule off, the app runs continuously once started, " +
                                 "and keeps this computer awake until you stop it.";
                }

                form.PerformLayout();
                Inspect(form, clipped);

                Assert.True(clipped.Count == 0, string.Join(System.Environment.NewLine, clipped));
            });
        }

        [Fact]
        public void NothingIsTruncatedOnceTheWindowHasActuallyBeenShown()
        {
            OnFormThread(form =>
            {
                // Shown off screen, because font auto scaling and OnLoad only run for a window
                // that is really displayed, and that is exactly where the last line of a
                // wrapping label goes missing. Nothing appears on the tester's desktop.
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-32000, -32000);
                form.ShowInTaskbar = false;
                form.Show();

                try
                {
                    LayoutSettler.Settle(form);

                    var clipped = new List<string>();
                    Inspect(form, clipped);

                    Assert.True(clipped.Count == 0, string.Join(System.Environment.NewLine, clipped));
                }
                finally
                {
                    form.Hide();
                }
            });
        }

        private static IEnumerable<Label> FindLabels(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (child is Label label && child.GetType() == typeof(Label))
                {
                    yield return label;
                }

                foreach (Label nested in FindLabels(child))
                {
                    yield return nested;
                }
            }
        }

        /// <summary>Records any control whose box falls outside the one containing it.</summary>
        private static void Inspect(Control parent, List<string> clipped)
        {
            foreach (Control child in parent.Controls)
            {
                if (!child.Visible)
                {
                    continue;
                }

                Rectangle available = parent.ClientRectangle;

                // A scrolling parent is allowed to hold more than it shows: that is what the
                // scrollbar is for. Only a fixed container clips.
                bool scrolls = parent is ScrollableControl scrollable && scrollable.AutoScroll;

                if (!scrolls && child.Right > available.Right + 1)
                {
                    clipped.Add(Describe(child) + " is cut off on the right: it ends at " + child.Right +
                                " inside a container " + available.Right + " wide.");
                }

                if (!scrolls && child.Bottom > available.Bottom + 1)
                {
                    clipped.Add(Describe(child) + " is cut off at the bottom: it ends at " + child.Bottom +
                                " inside a container " + available.Bottom + " tall.");
                }

                if (child is Label label && label.AutoSize)
                {
                    // A label that has been given less room than its own text needs is showing a
                    // truncated sentence. Containment does not catch this: the label sits inside
                    // its parent perfectly well, it is simply too small for what it holds.
                    Size needed = label.GetPreferredSize(new Size(label.MaximumSize.Width, 0));

                    if (label.Height + 1 < needed.Height || label.Width + 1 < needed.Width)
                    {
                        clipped.Add(Describe(child) + " is truncated: it is " + label.Size +
                                    " but its text needs " + needed + ".");
                    }
                }

                Inspect(child, clipped);
            }
        }

        private static string Describe(Control control)
        {
            string label = string.IsNullOrEmpty(control.AccessibleName) ? control.Text : control.AccessibleName;

            if (string.IsNullOrEmpty(label))
            {
                label = control.GetType().Name;
            }

            return control.GetType().Name + " \"" + label.Replace(System.Environment.NewLine, " ") + "\"";
        }
    }
}
