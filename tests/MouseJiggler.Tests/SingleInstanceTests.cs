using System;
using System.Text;
using MouseJiggler.Windows.Process;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// Ownership and the request channel between two launches. Every test uses a unique name
    /// suffix so a run never collides with a real running instance or with another test.
    /// </summary>
    public sealed class SingleInstanceTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        private static SingleInstanceService Create(string suffix, string executablePath = @"C:\Program Files\MouseJiggler\MouseJiggler.exe")
        {
            return new SingleInstanceService("S-1-5-21-test", 1, executablePath, suffix);
        }

        [Fact]
        public void TheFirstProcessOwnsTheEngineAndTheSecondDoesNot()
        {
            string suffix = Guid.NewGuid().ToString("N");

            using (SingleInstanceService first = Create(suffix))
            using (SingleInstanceService second = Create(suffix))
            {
                Assert.True(first.TryAcquireOwnership());
                Assert.False(second.TryAcquireOwnership());

                Assert.True(first.IsOwner);
                Assert.False(second.IsOwner);
            }
        }

        [Fact]
        public void OwnershipIsReleasedWhenTheOwnerGoesAway()
        {
            string suffix = Guid.NewGuid().ToString("N");

            using (SingleInstanceService first = Create(suffix))
            {
                Assert.True(first.TryAcquireOwnership());
            }

            using (SingleInstanceService next = Create(suffix))
            {
                Assert.True(next.TryAcquireOwnership());
            }
        }

        [Fact]
        public void ASecondLaunchCanAskTheOwnerToShowSettings()
        {
            string suffix = Guid.NewGuid().ToString("N");

            using (SingleInstanceService owner = Create(suffix))
            using (SingleInstanceService caller = Create(suffix))
            {
                Assert.True(owner.TryAcquireOwnership());

                owner.RequestHandler = request =>
                    string.Equals(request, IpcCommands.ShowSettings, StringComparison.Ordinal)
                        ? IpcCommands.ResponseOk
                        : IpcCommands.ResponseUnknownCommand;

                owner.StartListening();

                Assert.False(caller.TryAcquireOwnership());

                string? response = caller.SendRequest(IpcCommands.ShowSettings, Timeout);

                Assert.Equal(IpcCommands.ResponseOk, response);
            }
        }

        [Fact]
        public void TheOwnerIdentifiesItselfWithItsRealProcessAndPath()
        {
            string suffix = Guid.NewGuid().ToString("N");
            const string Path = @"C:\Program Files\MouseJiggler\MouseJiggler.exe";

            using (SingleInstanceService owner = Create(suffix, Path))
            using (SingleInstanceService caller = Create(suffix, Path))
            {
                Assert.True(owner.TryAcquireOwnership());
                owner.StartListening();

                string? response = caller.SendRequest(IpcCommands.Identify, Timeout);

                Assert.NotNull(response);
                string[] parts = response!.Split('|');

                Assert.Equal(IpcCommands.ResponseOk, parts[0]);
                Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().Id, int.Parse(parts[1]));
                Assert.True(caller.IsSameExecutable(parts[2]));
            }
        }

        [Fact]
        public void AnUnknownCommandIsRefusedRatherThanGuessedAt()
        {
            string suffix = Guid.NewGuid().ToString("N");

            using (SingleInstanceService owner = Create(suffix))
            using (SingleInstanceService caller = Create(suffix))
            {
                Assert.True(owner.TryAcquireOwnership());
                owner.RequestHandler = _ => IpcCommands.ResponseUnknownCommand;
                owner.StartListening();

                Assert.Equal(IpcCommands.ResponseUnknownCommand, caller.SendRequest("DELETE_EVERYTHING", Timeout));

                // A second request must be answered too. The listener has to keep an instance
                // available rather than only serving the first caller it ever sees.
                Assert.Equal(IpcCommands.ResponseUnknownCommand, caller.SendRequest(@"C:\Windows\System32\cmd.exe", Timeout));
                Assert.Null(owner.LastListenerError);
            }
        }

        [Fact]
        public void AnOversizedRequestIsRejectedBeforeItIsSent()
        {
            string suffix = Guid.NewGuid().ToString("N");

            using (SingleInstanceService caller = Create(suffix))
            {
                string huge = new string('x', IpcCommands.MaxRequestBytes + 1);

                Assert.Throws<ArgumentException>(() => caller.SendRequest(huge, Timeout));
            }
        }

        [Fact]
        public void AnAbsentOwnerReportsNothingRatherThanStartingASecondEngine()
        {
            using (SingleInstanceService caller = Create(Guid.NewGuid().ToString("N")))
            {
                // Nothing is listening on this name.
                string? response = caller.SendRequest(IpcCommands.ShowSettings, TimeSpan.FromMilliseconds(500));

                Assert.Null(response);
            }
        }

        [Fact]
        public void OwnershipChangingCommandsRequireTheSameExecutablePath()
        {
            string suffix = Guid.NewGuid().ToString("N");

            using (SingleInstanceService installed = Create(suffix, @"C:\Program Files\MouseJiggler\MouseJiggler.exe"))
            {
                // A portable copy elsewhere must not be able to close or reconfigure this one.
                Assert.False(installed.IsSameExecutable(@"D:\Portable\MouseJiggler\MouseJiggler.exe"));

                // The same path, written differently, still counts as the same executable.
                Assert.True(installed.IsSameExecutable(@"C:\Program Files\MouseJiggler\..\MouseJiggler\MouseJiggler.exe"));
                Assert.True(installed.IsSameExecutable(@"c:\program files\mousejiggler\mousejiggler.exe"));
            }
        }

        // ------------------------------------------------------------------------------------
        // The listener pool. See issue #22.
        //
        // Every listener after the first was refused by the pipe descriptor this process had
        // just applied itself, because the descriptor granted read and write but not
        // FILE_CREATE_PIPE_INSTANCE. Up to three of the four died before any client connected,
        // and the only trace was one string on LastListenerError that whichever loop failed
        // last got to overwrite.
        //
        // It is a race, so a single pass proves nothing: the original scenario passed about two
        // runs in three with the bug present. These repeat, and they were run against the
        // unfixed code to confirm they fail there.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void ServingRepeatedlyNeverCostsAListener()
        {
            for (int attempt = 0; attempt < ReplayAttempts; attempt++)
            {
                string suffix = Guid.NewGuid().ToString("N");

                using (SingleInstanceService owner = Create(suffix))
                using (SingleInstanceService caller = Create(suffix))
                {
                    Assert.True(owner.TryAcquireOwnership());
                    owner.RequestHandler = _ => IpcCommands.ResponseOk;
                    owner.StartListening();

                    // Real exchanges, not just a quiet start. The answers matter as much as the
                    // absence of a failure: a listener that is gone cannot answer. IDENTIFY is
                    // deliberately not used here, because the service answers that one itself
                    // and it would never reach the handler.
                    Assert.Equal(IpcCommands.ResponseOk, caller.SendRequest(IpcCommands.ShowSettings, Timeout));
                    Assert.Equal(IpcCommands.ResponseOk, caller.SendRequest(IpcCommands.ShutdownForUpdate, Timeout));

                    Assert.True(
                        owner.LastListenerError == null,
                        "A listener failed on attempt " + attempt + ": " + owner.LastListenerError);
                }
            }
        }

        [Fact]
        public void EveryListenerInThePoolStartsAndStays()
        {
            string suffix = Guid.NewGuid().ToString("N");

            using (SingleInstanceService owner = Create(suffix))
            using (SingleInstanceService caller = Create(suffix))
            {
                Assert.True(owner.TryAcquireOwnership());
                owner.RequestHandler = _ => IpcCommands.ResponseOk;
                owner.StartListening();

                // The loops start on the thread pool, so the count is reached rather than
                // assumed. Waiting on the number itself, with a deadline, beats sleeping for a
                // guess at how long four tasks take to be scheduled.
                Assert.True(
                    WaitForListeners(owner, SingleInstanceService.ListenerPoolSize),
                    "Only " + owner.ActiveListenerCount + " of " +
                    SingleInstanceService.ListenerPoolSize + " listeners started.");

                Assert.Equal(IpcCommands.ResponseOk, caller.SendRequest(IpcCommands.ShowSettings, Timeout));

                // Serving a request must not cost a listener either.
                Assert.Equal(SingleInstanceService.ListenerPoolSize, owner.ActiveListenerCount);
                Assert.Equal(0, owner.ListenerFailureCount);
                Assert.Null(owner.LastListenerError);
            }
        }

        /// <summary>How many times the replay runs. It tripped within twenty on the unfixed code.</summary>
        private const int ReplayAttempts = 60;

        private static bool WaitForListeners(SingleInstanceService service, int expected)
        {
            DateTime deadline = DateTime.UtcNow + Timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (service.ActiveListenerCount >= expected)
                {
                    return true;
                }

                System.Threading.Thread.Sleep(10);
            }

            return false;
        }
    }
}
