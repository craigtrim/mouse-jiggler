using MouseJiggler.App.Runtime;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The retry cadence on its own.
    /// </summary>
    /// <remarks>
    /// CoordinatorTests already prove the cadence through its effects, by counting native calls.
    /// These are the same rules stated directly, which is what makes a deliberate change to the
    /// schedule show up as an edit to a test rather than as a number nobody notices moving.
    /// </remarks>
    public sealed class RecoveryCoordinatorTests
    {
        [Fact]
        public void NothingIsBackedOffUntilSomethingHasFailed()
        {
            var recovery = new RecoveryCoordinator();

            Assert.True(recovery.IsWakeRetryDue(0));
            Assert.True(recovery.IsWakeRetryDue(1_000_000));
        }

        [Fact]
        public void TheWakeRequestWaitsOneThenFiveThenThirtySecondsAndKeepsWaitingThirty()
        {
            var recovery = new RecoveryCoordinator();
            long now = 500_000;

            foreach (int seconds in RecoveryCoordinator.Schedule)
            {
                recovery.NoteWakeFailure(now);

                // One millisecond short is not due. The boundary is the whole point of a
                // schedule: an off-by-one here is a retry a second early, every time, forever.
                Assert.False(recovery.IsWakeRetryDue(now + (seconds * 1000L) - 1));
                Assert.True(recovery.IsWakeRetryDue(now + (seconds * 1000L)));

                now += seconds * 1000L;
            }

            // Past the end of the schedule it repeats rather than escalating or giving up.
            recovery.NoteWakeFailure(now);
            Assert.False(recovery.IsWakeRetryDue(now + 29_999));
            Assert.True(recovery.IsWakeRetryDue(now + 30_000));
        }

        [Fact]
        public void ASuccessSendsTheNextFailureBackToTheStartOfTheSchedule()
        {
            var recovery = new RecoveryCoordinator();
            long now = 1_000;

            recovery.NoteWakeFailure(now);
            recovery.NoteWakeFailure(now);
            recovery.NoteWakeFailure(now);

            // Deep into the schedule: the next wait would be thirty seconds.
            recovery.NoteWakeFailure(now);
            Assert.False(recovery.IsWakeRetryDue(now + 29_000));

            recovery.NoteWakeSuccess();
            Assert.True(recovery.IsWakeRetryDue(now));

            // A request that worked and then failed is a new problem, not a continuation.
            recovery.NoteWakeFailure(now);
            Assert.False(recovery.IsWakeRetryDue(now + 999));
            Assert.True(recovery.IsWakeRetryDue(now + 1_000));
        }

        [Fact]
        public void AnUnwrittenStopIsOutstandingUntilItLands()
        {
            var recovery = new RecoveryCoordinator();

            Assert.False(recovery.HasUncommittedStop);
            Assert.False(recovery.IsStopRetryDue(1_000_000));

            recovery.NoteStopWriteFailed(1_000);
            Assert.True(recovery.HasUncommittedStop);

            Assert.False(recovery.IsStopRetryDue(1_999));
            Assert.True(recovery.IsStopRetryDue(2_000));

            recovery.NoteStopWritten();
            Assert.False(recovery.HasUncommittedStop);
            Assert.False(recovery.IsStopRetryDue(1_000_000));
        }

        [Fact]
        public void TheStopRetryFollowsTheSameScheduleAfterItsFirstAttempt()
        {
            var recovery = new RecoveryCoordinator();
            long now = 10_000;

            recovery.NoteStopWriteFailed(now);

            // The first wait is armed by the failure itself, so the schedule seen from here is
            // one second, then five, then thirty, then thirty.
            long due = now + 1_000;
            Assert.True(recovery.IsStopRetryDue(due));

            recovery.NoteStopRetryStarted(due);
            Assert.False(recovery.IsStopRetryDue(due + 4_999));
            Assert.True(recovery.IsStopRetryDue(due + 5_000));

            due += 5_000;
            recovery.NoteStopRetryStarted(due);
            Assert.False(recovery.IsStopRetryDue(due + 29_999));
            Assert.True(recovery.IsStopRetryDue(due + 30_000));

            due += 30_000;
            recovery.NoteStopRetryStarted(due);
            Assert.False(recovery.IsStopRetryDue(due + 29_999));
            Assert.True(recovery.IsStopRetryDue(due + 30_000));
        }

        [Fact]
        public void ArmingHappensBeforeTheAttemptSoAThrowCannotProduceABusyLoop()
        {
            var recovery = new RecoveryCoordinator();

            recovery.NoteStopWriteFailed(0);
            Assert.True(recovery.IsStopRetryDue(1_000));

            // Standing in for an attempt that blocks or throws: the deadline has already moved,
            // so the next tick does not find the retry due all over again.
            recovery.NoteStopRetryStarted(1_000);

            Assert.False(recovery.IsStopRetryDue(1_001));
        }

        [Fact]
        public void TheTwoBackoffsDoNotDelayEachOther()
        {
            var recovery = new RecoveryCoordinator();

            // A display that will not stay on must not hold up a Stop reaching disk, and a full
            // disk must not hold up the wake request.
            recovery.NoteWakeFailure(0);
            recovery.NoteWakeFailure(0);
            recovery.NoteWakeFailure(0);
            recovery.NoteWakeFailure(0);

            recovery.NoteStopWriteFailed(0);

            Assert.True(recovery.IsStopRetryDue(1_000));
            Assert.False(recovery.IsWakeRetryDue(1_000));
        }

        [Fact]
        public void ResettingTheSettingsForgetsBothOfThem()
        {
            var recovery = new RecoveryCoordinator();

            recovery.NoteWakeFailure(0);
            recovery.NoteStopWriteFailed(0);

            // The Stop was waiting to be written to a file that has just been replaced, so
            // carrying it forward would mean writing an intent nobody holds any more.
            recovery.Reset();

            Assert.True(recovery.IsWakeRetryDue(0));
            Assert.False(recovery.HasUncommittedStop);
            Assert.False(recovery.IsStopRetryDue(1_000_000));
        }

        [Fact]
        public void TheScheduleIsTheOneTheDocumentationClaims()
        {
            Assert.Equal(new[] { 1, 5, 30, 30 }, RecoveryCoordinator.Schedule);
        }
    }
}
