using System;
using System.Threading;

using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Environment;
using MouseJiggler.Windows.Power;
using MouseJiggler.Windows.Sessions;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// Power classification, session reads and the ownership rules on the keep-awake request.
    /// The classification tests use fake API values; the two tests that would change the state
    /// of this machine are gated.
    /// </summary>
    public sealed class PowerAndSessionTests
    {
        [Theory]
        [InlineData(1, PowerSource.External)]
        [InlineData(0, PowerSource.Battery)]
        [InlineData(255, PowerSource.Unknown)]
        [InlineData(7, PowerSource.Unknown)]
        public void OnlyTheLineStatusDecidesThePowerSource(byte acLineStatus, PowerSource expected)
        {
            Assert.Equal(expected, PowerStatusSource.Classify(acLineStatus));
        }

        [Fact]
        public void AFullOrAbsentBatteryDoesNotChangeTheClassification()
        {
            // BatteryFlag 128 means no battery is installed, and 100 percent charge means the
            // battery is full. Neither is evidence about external power, so the line status
            // alone is what the classifier reads.
            Assert.Equal(PowerSource.Battery, PowerStatusSource.Classify(0));
            Assert.Equal(PowerSource.External, PowerStatusSource.Classify(1));
        }

        [Fact]
        public void TheRealPowerQuerySucceedsAndReturnsAKnownClassification()
        {
            // Reading power status has no effect on the machine, so this runs everywhere.
            using (var source = new PowerStatusSource())
            {
                OperationResult<PowerSource> result = source.Query();

                Assert.True(result.Succeeded, "GetSystemPowerStatus failed: " + result.Outcome.Code);
                Assert.Contains(result.Value, new[] { PowerSource.External, PowerSource.Battery, PowerSource.Unknown });
            }
        }

        [Fact]
        public void TheRealSessionQueryReturnsAKnownStateOrAReportedFault()
        {
            using (var source = new SessionStatusSource())
            {
                OperationResult<SessionState> result = source.Query();

                if (result.Succeeded)
                {
                    Assert.Contains(result.Value, new[] { SessionState.ActiveUnlocked, SessionState.Locked, SessionState.Disconnected });
                }
                else
                {
                    // A failure must name itself rather than quietly becoming "unlocked".
                    Assert.Equal(FaultSubsystem.SessionQuery, result.Outcome.Subsystem);
                    Assert.False(string.IsNullOrEmpty(result.Outcome.Code));
                }
            }
        }

        [Fact]
        public void AnUnregisteredSourceExpectsToBePolled()
        {
            using (var power = new PowerStatusSource())
            using (var session = new SessionStatusSource())
            {
                // Until registration succeeds, the app must fall back to polling rather than
                // assume it will be told about changes.
                Assert.True(power.RequiresPolling);
                Assert.True(session.RequiresPolling);
            }
        }

        [Fact]
        public void ReleasingWithoutAnOutstandingRequestSucceedsAndTouchesNothing()
        {
            using (var controller = new ExecutionStateController())
            {
                OperationResult result = controller.Release();

                Assert.True(result.Succeeded);
                Assert.False(controller.SystemAwakeApplied);
                Assert.False(controller.DisplayAwakeApplied);
            }
        }

        [Fact]
        public void DisposingAnUnusedControllerIsSafeAndRepeatable()
        {
            var controller = new ExecutionStateController();
            controller.Dispose();
            controller.Dispose();

            Assert.Throws<ObjectDisposedException>(() => controller.Release());
        }

        [LiveInputFact]
        public void TheWakeRequestIsAcquiredAndReleasedOnTheOwnerThread()
        {
            using (var controller = new ExecutionStateController())
            {
                OperationResult applied = controller.Apply(systemAwake: true, displayAwake: false);

                Assert.True(applied.Succeeded, "SetThreadExecutionState failed: " + applied.Code);
                Assert.True(controller.SystemAwakeApplied);
                Assert.False(controller.DisplayAwakeApplied);

                // A repeated identical request does not call the API again.
                Assert.True(controller.Apply(systemAwake: true, displayAwake: false).Succeeded);

                Assert.True(controller.Release().Succeeded);
                Assert.False(controller.SystemAwakeApplied);
            }
        }

        [LiveInputFact]
        public void ARequestCannotBeDrivenFromAnotherThread()
        {
            using (var controller = new ExecutionStateController())
            {
                Assert.True(controller.Apply(systemAwake: true, displayAwake: false).Succeeded);

                try
                {
                    OperationResult? fromElsewhere = null;

                    // A dedicated thread, not Task.Run: waiting on a pool task can inline it
                    // onto this very thread, which would defeat the check under test.
                    var other = new Thread(() => fromElsewhere = controller.Apply(true, true));
                    other.Start();
                    other.Join();

                    // Releasing from the wrong thread would leave the original request standing,
                    // so the controller refuses instead.
                    Assert.NotNull(fromElsewhere);
                    Assert.False(fromElsewhere!.Succeeded);
                    Assert.Equal("wake.wrongThread", fromElsewhere.Code);
                    Assert.False(fromElsewhere.Retryable);
                }
                finally
                {
                    controller.Release();
                }
            }
        }
    }
}
