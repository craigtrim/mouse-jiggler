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
    }
}
