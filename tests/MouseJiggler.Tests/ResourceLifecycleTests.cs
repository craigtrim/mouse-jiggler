using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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
    /// What repetition does to the process.
    /// </summary>
    /// <remarks>
    /// A window that leaks a handle or leaves a subscription behind works perfectly for the
    /// first hundred opens. The symptom arrives hours later as an interface that stops drawing,
    /// or as an event firing into a disposed form, and by then nothing points back to the cause.
    /// The only way to see it is to do the thing many times and count.
    /// </remarks>
    public sealed class ResourceLifecycleTests
    {
        private const int UserObjects = 1;
        private const int GdiObjects = 0;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetGuiResources(IntPtr process, int flags);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private static void OnStaThread(Action body)
        {
            Exception? failure = null;

            var thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception error)
                {
                    failure = error;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                throw new Xunit.Sdk.XunitException(failure.ToString());
            }
        }

        private static void Settle()
        {
            Application.DoEvents();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Application.DoEvents();
        }

        [Fact]
        public void AHundredSettingsWindowsOpenAndCloseWithoutLeakingHandles()
        {
            OnStaThread(() =>
            {
                const int Cycles = 100;

                using (var directory = new TemporaryDirectory())
                {
                    SettingsV1 settings = SettingsV1.CreateDefault();

                    var ring = new DiagnosticRing();
                    var sink = new LocalDiagnosticSink(Path.Combine(directory.Path, "Logs"), ring);
                    var store = new FakeSettingsStore(settings);
                    var environment = new FakePowerSessionSource();
                    var executionState = new FakeExecutionStateController();

                    var coordinator = new ActivityCoordinator(
                        store,
                        settings,
                        environment,
                        executionState,
                        new FakeIdleInputSource(),
                        new FakeMouseJiggler(),
                        new FakeClock(),
                        new FakeInputDesktopProbe(),
                        new ImmediateInvoker(),
                        new RecordingDiagnosticSink());

                    try
                    {
                        // Warm up: the first few windows load fonts, themes and control classes
                        // that are not part of the loop, and counting those would fail at random.
                        for (int i = 0; i < 5; i++)
                        {
                            using (new SettingsForm(coordinator, sink, ring))
                            {
                            }
                        }

                        Settle();

                        IntPtr process = GetCurrentProcess();
                        uint userBefore = GetGuiResources(process, UserObjects);
                        uint gdiBefore = GetGuiResources(process, GdiObjects);

                        Assert.True(userBefore > 0, "GetGuiResources is unavailable, so this proves nothing.");

                        for (int i = 0; i < Cycles; i++)
                        {
                            using (var form = new SettingsForm(coordinator, sink, ring))
                            {
                                // Force the handle, which is where the window really costs
                                // something. Constructing without it would prove very little.
                                _ = form.Handle;
                            }
                        }

                        Settle();

                        uint userAfter = GetGuiResources(process, UserObjects);
                        uint gdiAfter = GetGuiResources(process, GdiObjects);

                        // A per-cycle leak shows as a hundred or more retained objects. The
                        // allowance covers caches that are not proportional to the loop.
                        Assert.True(
                            userAfter <= userBefore + 25,
                            "USER objects grew from " + userBefore + " to " + userAfter + " over " + Cycles + " windows.");

                        Assert.True(
                            gdiAfter <= gdiBefore + 25,
                            "GDI objects grew from " + gdiBefore + " to " + gdiAfter + " over " + Cycles + " windows.");
                    }
                    finally
                    {
                        coordinator.Dispose();
                        environment.Dispose();
                        executionState.Dispose();
                        store.Dispose();
                        sink.Dispose();
                    }
                }
            });
        }

        [Fact]
        public void AHundredSettingsWindowsLeaveNoSubscriptionsBehind()
        {
            OnStaThread(() =>
            {
                const int Cycles = 100;

                using (var directory = new TemporaryDirectory())
                {
                    SettingsV1 settings = SettingsV1.CreateDefault();

                    var ring = new DiagnosticRing();
                    var sink = new LocalDiagnosticSink(Path.Combine(directory.Path, "Logs"), ring);
                    var store = new FakeSettingsStore(settings);
                    var environment = new FakePowerSessionSource();
                    var executionState = new FakeExecutionStateController();

                    var coordinator = new ActivityCoordinator(
                        store,
                        settings,
                        environment,
                        executionState,
                        new FakeIdleInputSource(),
                        new FakeMouseJiggler(),
                        new FakeClock(),
                        new FakeInputDesktopProbe(),
                        new ImmediateInvoker(),
                        new RecordingDiagnosticSink());

                    try
                    {
                        for (int i = 0; i < Cycles; i++)
                        {
                            using (new SettingsForm(coordinator, sink, ring))
                            {
                            }
                        }

                        Settle();

                        // Every window subscribed to StateChanged and SettingsChanged and had to
                        // detach on the way out. A subscription left behind would fire into a
                        // disposed form, and a hundred of them would fire a hundred times.
                        int handlers = CountHandlers(coordinator, "StateChanged")
                                       + CountHandlers(coordinator, "SettingsChanged");

                        Assert.Equal(0, handlers);

                        // And evaluating must not throw into anything that is no longer there.
                        coordinator.Evaluate();
                    }
                    finally
                    {
                        coordinator.Dispose();
                        environment.Dispose();
                        executionState.Dispose();
                        store.Dispose();
                        sink.Dispose();
                    }
                }
            });
        }

        /// <summary>How many delegates are attached to a named event on an instance.</summary>
        private static int CountHandlers(object instance, string eventName)
        {
            System.Reflection.FieldInfo? field = instance.GetType().GetField(
                eventName,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public);

            Assert.True(field != null, "No backing field for " + eventName + ".");

            var handler = field!.GetValue(instance) as Delegate;
            return handler == null ? 0 : handler.GetInvocationList().Length;
        }

        [Fact]
        public void AHundredTrayRestorationsLeaveOneIconAndNoLeak()
        {
            OnStaThread(() =>
            {
                const int Cycles = 100;

                using (var tray = new TrayMenuController())
                {
                    Settle();

                    IntPtr process = GetCurrentProcess();
                    uint before = GetGuiResources(process, UserObjects);

                    // Explorer restarting is the real scenario. Each restoration hides the icon
                    // and shows it again; creating a second NotifyIcon instead would leave two
                    // icons in the notification area, which is the bug this guards.
                    for (int i = 0; i < Cycles; i++)
                    {
                        tray.Restore();
                    }

                    Settle();

                    uint after = GetGuiResources(process, UserObjects);

                    Assert.True(
                        after <= before + 25,
                        "USER objects grew from " + before + " to " + after + " over " + Cycles + " restorations.");
                }
            });
        }

        [Fact]
        public void AHundredStatusChangesReplaceTheIconRatherThanAccumulateIcons()
        {
            OnStaThread(() =>
            {
                using (var tray = new TrayMenuController())
                {
                    SettingsV1 settings = SettingsV1.CreateDefault();

                    Settle();

                    IntPtr process = GetCurrentProcess();
                    uint before = GetGuiResources(process, GdiObjects);

                    var statuses = new[]
                    {
                        MouseJiggler.Core.Activity.StatusCode.Stopped,
                        MouseJiggler.Core.Activity.StatusCode.RunningManual,
                        MouseJiggler.Core.Activity.StatusCode.WaitingForSchedule,
                        MouseJiggler.Core.Activity.StatusCode.Error,
                    };

                    for (int i = 0; i < 100; i++)
                    {
                        tray.Render(
                            MouseJiggler.Core.Activity.DesiredEffects.None(statuses[i % statuses.Length]),
                            settings);
                    }

                    Settle();

                    uint after = GetGuiResources(process, GdiObjects);

                    // The previous icon is disposed each time. Without that, a hundred status
                    // changes is a hundred retained HICONs.
                    Assert.True(
                        after <= before + 25,
                        "GDI objects grew from " + before + " to " + after + " over 100 status changes.");
                }
            });
        }
    }
}
