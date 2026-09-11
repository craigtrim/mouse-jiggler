using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Windows.Process;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// Who the installer is really talking to.
    /// </summary>
    /// <remarks>
    /// Two commands can act on a running copy of this application: one closes it, the other
    /// writes a startup entry for it. Both are refused unless the copy that answers the pipe is
    /// running the same executable as the caller, so the identity behind that decision has to be
    /// worth something.
    ///
    /// The pipe is restricted to one user, so nothing here is the only thing standing between the
    /// app and a hostile caller. What it covers is narrower and quite reachable: the pipe name is
    /// derived from the SID and the session id, so it is predictable, and it is a separate kernel
    /// object from the ownership mutex. A same-user process can hold that name before this
    /// application starts. These tests are what stops it being believed when it does.
    ///
    /// The pipe server in these tests lives in the test host, so the path a service was
    /// constructed with is not the path the operating system reports for the process answering.
    /// That is what ProcessPathResolver is for, and substituting it is the only way any of this
    /// can be exercised without installing the application.
    ///
    /// One property is deliberately not asserted here, because it cannot be. The kernel has to be
    /// asked who answered at the moment of connecting, not after the reply has been read: the
    /// real server closes its pipe instance as soon as it has answered, so a later query fails
    /// with ERROR_PIPE_NOT_CONNECTED and a question about timing gets reported as a question
    /// about identity. That refused every legitimate --shutdown-for-update while every test here
    /// passed, because an in-process server lingers long enough for the late query to work. There
    /// is no seam that makes the ordering observable in one process, and adding one that exists
    /// only to be watched would be testing the seam. scripts/verify-lifecycle.ps1 covers it with
    /// two real processes, which is where the difference is real, and it is what caught it.
    /// </remarks>
    public sealed class IpcIdentityTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        private const string InstalledPath = @"C:\Program Files\MouseJiggler\MouseJiggler.exe";
        private const string PortablePath = @"D:\Portable\MouseJiggler\MouseJiggler.exe";

        private static SingleInstanceService Create(string suffix, string executablePath)
        {
            return new SingleInstanceService("S-1-5-21-test", 1, executablePath, suffix);
        }

        [Fact]
        public void AVerifiedIdentityNamesTheProcessThatActuallyAnswered()
        {
            string suffix = Guid.NewGuid().ToString("N");

            using (SingleInstanceService owner = Create(suffix, InstalledPath))
            using (SingleInstanceService caller = Create(suffix, InstalledPath))
            {
                Assert.True(owner.TryAcquireOwnership());
                owner.StartListening();

                // The owner is the test host, so this stands in for "the real image path of the
                // process on the other end of the pipe".
                caller.ProcessPathResolver = _ => InstalledPath;

                OperationResult<IpcIdentity> identity = caller.RequestIdentity(Timeout);

                Assert.True(identity.Succeeded);
                Assert.Equal(
                    (uint)System.Diagnostics.Process.GetCurrentProcess().Id,
                    identity.Value!.ProcessId);
                Assert.True(caller.IsSameExecutable(identity.Value.ExecutablePath));
            }
        }

        [Fact]
        public void TheVerifiedPathComesFromTheOperatingSystemRatherThanTheReply()
        {
            string suffix = Guid.NewGuid().ToString("N");

            using (SingleInstanceService owner = Create(suffix, InstalledPath))
            using (SingleInstanceService caller = Create(suffix, InstalledPath))
            {
                Assert.True(owner.TryAcquireOwnership());
                owner.StartListening();

                // The reply says the installed path. The operating system says a portable one.
                // Believing the reply is the mistake this exists to prevent, so the disagreement
                // has to be fatal rather than resolved in the reply's favour.
                caller.ProcessPathResolver = _ => PortablePath;

                OperationResult<IpcIdentity> identity = caller.RequestIdentity(Timeout);

                Assert.False(identity.Succeeded);
                Assert.Equal("ipc.identityMismatch", identity.Outcome.Code);
            }
        }

        [Fact]
        public void AnUnresolvableOwnerIsRefusedRatherThanAssumedToBeUs()
        {
            string suffix = Guid.NewGuid().ToString("N");

            using (SingleInstanceService owner = Create(suffix, InstalledPath))
            using (SingleInstanceService caller = Create(suffix, InstalledPath))
            {
                Assert.True(owner.TryAcquireOwnership());
                owner.StartListening();

                // Access denied, or a process that exited between answering and being asked
                // about. Either way there is nothing to check the reply against.
                caller.ProcessPathResolver = _ => null;

                OperationResult<IpcIdentity> identity = caller.RequestIdentity(Timeout);

                Assert.False(identity.Succeeded);
                Assert.Equal("ipc.identityUnverifiable", identity.Outcome.Code);
                Assert.False(identity.Outcome.Retryable);
            }
        }

        [Fact]
        public void NothingListeningIsReportedAsNoAnswerRatherThanAsAConflict()
        {
            string suffix = Guid.NewGuid().ToString("N");

            using (SingleInstanceService caller = Create(suffix, InstalledPath))
            {
                // Distinguishing the two matters to the installer: no answer is something to
                // report as a failure to communicate, a conflict is something to ask the user to
                // resolve by closing the other copy.
                OperationResult<IpcIdentity> identity = caller.RequestIdentity(TimeSpan.FromMilliseconds(400));

                Assert.False(identity.Succeeded);
                Assert.Equal("ipc.noAnswer", identity.Outcome.Code);
            }
        }

        [Fact]
        public async Task AProcessIdInventedByTheServerDoesNotMatchTheOneTheKernelReports()
        {
            string suffix = Guid.NewGuid().ToString("N");
            string pipeName = "MouseJiggler.S-1-5-21-test.1." + suffix;

            using (var squatter = new CancellationTokenSource())
            using (SingleInstanceService caller = Create(suffix, InstalledPath))
            {
                // A process holding the pipe name and answering with whatever it likes. The
                // claimed path is exactly the caller's own, which is the answer that would let it
                // through if the reply were taken at face value.
                Task server = AnswerOnce(
                    pipeName,
                    IpcCommands.ResponseOk + IpcCommands.FieldSeparator + "999999" +
                        IpcCommands.FieldSeparator + InstalledPath,
                    squatter.Token);

                caller.ProcessPathResolver = _ => InstalledPath;

                OperationResult<IpcIdentity> identity = caller.RequestIdentity(Timeout);

                Assert.False(identity.Succeeded);
                Assert.Equal("ipc.identityMismatch", identity.Outcome.Code);

                squatter.Cancel();
                await server;
            }
        }

        [Fact]
        public async Task AReplyThatIsNotAnIdentityAtAllIsRefused()
        {
            string suffix = Guid.NewGuid().ToString("N");
            string pipeName = "MouseJiggler.S-1-5-21-test.1." + suffix;

            using (var squatter = new CancellationTokenSource())
            using (SingleInstanceService caller = Create(suffix, InstalledPath))
            {
                Task server = AnswerOnce(pipeName, "OK", squatter.Token);

                caller.ProcessPathResolver = _ => InstalledPath;

                OperationResult<IpcIdentity> identity = caller.RequestIdentity(Timeout);

                Assert.False(identity.Succeeded);
                Assert.Equal("ipc.identityMalformed", identity.Outcome.Code);

                squatter.Cancel();
                await server;
            }
        }

        [Fact]
        public async Task AnOwnerThatHangsUpWithoutAnsweringIsNoAnswerRatherThanAConflict()
        {
            string suffix = Guid.NewGuid().ToString("N");
            string pipeName = "MouseJiggler.S-1-5-21-test.1." + suffix;

            using (SingleInstanceService caller = Create(suffix, InstalledPath))
            {
                // A server that accepts the connection and drops it again without replying.
                Task server = AcceptThenHangUp(pipeName);

                caller.ProcessPathResolver = _ => InstalledPath;

                OperationResult<IpcIdentity> identity = caller.RequestIdentity(Timeout);

                // Something was there and it did not answer, which is a communication failure
                // rather than a statement about who owns the session. The installer reports the
                // two differently, so the distinction is asserted on the code.
                Assert.False(identity.Succeeded);
                Assert.Equal("ipc.noAnswer", identity.Outcome.Code);

                await server;
            }
        }

        /// <summary>Accepts one connection and closes it again without saying anything.</summary>
        private static Task AcceptThenHangUp(string pipeName)
        {
            return Task.Run(async () =>
            {
                using (var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous))
                {
                    try
                    {
                        await Task.Factory.FromAsync(
                            server.BeginWaitForConnection, server.EndWaitForConnection, null);

                        server.Disconnect();
                    }
                    catch (System.IO.IOException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            });
        }

        /// <summary>A bare pipe server that sends one fixed reply. It is the impostor.</summary>
        private static Task AnswerOnce(string pipeName, string reply, CancellationToken token)
        {
            return Task.Run(async () =>
            {
                using (var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous))
                {
                    try
                    {
                        await Task.Factory.FromAsync(
                            server.BeginWaitForConnection, server.EndWaitForConnection, null)
                            .ConfigureAwait(false);

                        var buffer = new byte[IpcCommands.MaxRequestBytes];
                        await server.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);

                        byte[] payload = Encoding.UTF8.GetBytes(reply);
                        await server.WriteAsync(payload, 0, payload.Length, token).ConfigureAwait(false);
                        server.Flush();
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (System.IO.IOException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            });
        }

        // ------------------------------------------------------------------------------------
        // A command is only as trustworthy as the connection that carried it. See issue #22.
        //
        // Checking ownership and then sending the command are two exchanges, and whoever
        // answered the first is not necessarily whoever answers the second. The pipe now grants
        // CreateNewInstance so the listener pool can work at all, which means another server can
        // join while this one is running, so the command's own connection is what gets checked.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void AVerifiedCommandIsAnsweredByThisExecutable()
        {
            string suffix = Guid.NewGuid().ToString("N");

            using (SingleInstanceService owner = Create(suffix, InstalledPath))
            using (SingleInstanceService caller = Create(suffix, InstalledPath))
            {
                Assert.True(owner.TryAcquireOwnership());
                owner.RequestHandler = _ => IpcCommands.ResponseOk;
                owner.StartListening();

                caller.ProcessPathResolver = _ => InstalledPath;

                OperationResult<string> result =
                    caller.SendVerifiedRequest(IpcCommands.ShutdownForUpdate, Timeout);

                Assert.True(result.Succeeded);
                Assert.Equal(IpcCommands.ResponseOk, result.Value);
            }
        }

        [Fact]
        public void AVerifiedCommandIsRefusedWhenAnotherExecutableAnswers()
        {
            string suffix = Guid.NewGuid().ToString("N");

            using (SingleInstanceService owner = Create(suffix, InstalledPath))
            using (SingleInstanceService caller = Create(suffix, InstalledPath))
            {
                Assert.True(owner.TryAcquireOwnership());

                // The answer is a perfectly good OK. What makes it worthless is who sent it.
                bool handled = false;
                owner.RequestHandler = _ =>
                {
                    handled = true;
                    return IpcCommands.ResponseOk;
                };
                owner.StartListening();

                // The operating system says a portable copy answered this connection.
                caller.ProcessPathResolver = _ => PortablePath;

                OperationResult<string> result =
                    caller.SendVerifiedRequest(IpcCommands.ShutdownForUpdate, Timeout);

                // An installer acting on this would replace files under a running application.
                Assert.False(result.Succeeded);
                Assert.Equal("ipc.identityMismatch", result.Outcome.Code);

                // And the refusal has to come before the command is sent, not after. A verdict
                // delivered afterwards would mean this copy had already shut down, and the
                // caller would be reporting a conflict about an application it just closed.
                Assert.False(
                    handled,
                    "The command reached the handler before the identity was refused, so the wrong copy acted on it.");
            }
        }

        [Fact]
        public void AVerifiedCommandIsRefusedWhenTheAnsweringProcessCannotBeResolved()
        {
            string suffix = Guid.NewGuid().ToString("N");

            using (SingleInstanceService owner = Create(suffix, InstalledPath))
            using (SingleInstanceService caller = Create(suffix, InstalledPath))
            {
                Assert.True(owner.TryAcquireOwnership());
                bool handled = false;
                owner.RequestHandler = _ =>
                {
                    handled = true;
                    return IpcCommands.ResponseOk;
                };
                owner.StartListening();

                // Unresolvable is refused rather than shrugged at, because every alternative
                // means acting on an unverified claim.
                caller.ProcessPathResolver = _ => null;

                OperationResult<string> result =
                    caller.SendVerifiedRequest(IpcCommands.ShutdownForUpdate, Timeout);

                Assert.False(result.Succeeded);
                Assert.Equal("ipc.identityUnverifiable", result.Outcome.Code);
                Assert.False(handled, "An unverifiable server was still sent the command.");
            }
        }

        [Fact]
        public void AVerifiedCommandReportsSilenceDifferentlyFromRefusal()
        {
            string suffix = Guid.NewGuid().ToString("N");

            using (SingleInstanceService caller = Create(suffix, InstalledPath))
            {
                // Nothing is listening. An installer has to tell "nobody answered" apart from
                // "somebody answered and was refused", because they mean different things.
                OperationResult<string> result =
                    caller.SendVerifiedRequest(IpcCommands.ShutdownForUpdate, TimeSpan.FromMilliseconds(400));

                Assert.False(result.Succeeded);
                Assert.Equal("ipc.noAnswer", result.Outcome.Code);
            }
        }
    }
}
