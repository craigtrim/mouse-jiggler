using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Windows.Interop;

namespace MouseJiggler.Windows.Process
{
    /// <summary>The fixed set of requests one instance will accept from another.</summary>
    public static class IpcCommands
    {
        public const string ShowSettings = "SHOW_SETTINGS";
        public const string Identify = "IDENTIFY";
        public const string ShutdownForUpdate = "SHUTDOWN_FOR_UPDATE";
        public const string InstallInitialize = "INSTALL_INITIALIZE";

        public const string ResponseOk = "OK";
        public const string ResponseUnknownCommand = "UNKNOWN_COMMAND";
        public const string ResponseOwnershipConflict = "OWNERSHIP_CONFLICT";

        /// <summary>The instance exited, but an explicit Stop never reached disk.</summary>
        public const string ResponseStopNotPersisted = "STOP_NOT_PERSISTED";

        /// <summary>Requests are tiny by design; anything larger is refused unread.</summary>
        public const int MaxRequestBytes = 1024;

        /// <summary>Separates the fields of an identity response.</summary>
        public const char FieldSeparator = '|';
    }

    /// <summary>Who actually answered the pipe, according to the kernel.</summary>
    /// <remarks>
    /// Both fields come from the operating system. The executable path is the image of the
    /// process the client was really connected to, not the path that process claimed in its
    /// reply, so an ownership decision made on it cannot be talked out of by a reply.
    /// </remarks>
    public sealed class IpcIdentity
    {
        public IpcIdentity(uint processId, string executablePath)
        {
            ProcessId = processId;
            ExecutablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
        }

        public uint ProcessId { get; }

        public string ExecutablePath { get; }
    }

    /// <summary>
    /// One engine per user per Windows session.
    /// </summary>
    /// <remarks>
    /// Ownership is claimed with a named mutex scoped to the user and the session, and a second
    /// launch talks to the owner over a named pipe restricted to the same user rather than
    /// starting a second engine. When the pipe cannot be reached the second process reports that
    /// and exits: starting anyway would give the machine two components fighting over the same
    /// settings file and the same wake request.
    ///
    /// Commands that change ownership, shutting down for an update and installer initialization,
    /// require the caller to be running the same executable path as the owner. That stops an
    /// installer for one copy from closing or reconfiguring a different one.
    /// </remarks>
    public sealed class SingleInstanceService : IDisposable
    {
        private readonly string _mutexName;
        private readonly string _pipeName;
        private readonly string _executablePath;
        private const int ConnectRetryDelayMilliseconds = 25;

        /// <summary>How many server instances accept concurrently.</summary>
        private const int ListenerPoolSize = 4;

        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

        private Mutex? _mutex;
        private bool _ownsMutex;
        private Task? _listener;
        private readonly object _securityGate = new object();
        private bool _securityEstablished;
        private bool _disposed;

        public SingleInstanceService(string userSid, int sessionId, string executablePath, string? nameSuffix = null)
        {
            if (string.IsNullOrEmpty(userSid))
            {
                throw new ArgumentException("A user SID is required.", nameof(userSid));
            }

            string suffix = nameSuffix == null ? string.Empty : "." + nameSuffix;

            // Local\ is already per-session; the session id is included so the name reads
            // unambiguously in diagnostics.
            _mutexName = string.Format(CultureInfo.InvariantCulture, @"Local\MouseJiggler.{0}.{1}{2}", userSid, sessionId, suffix);
            _pipeName = string.Format(CultureInfo.InvariantCulture, "MouseJiggler.{0}.{1}{2}", userSid, sessionId, suffix);
            _executablePath = NormalizePath(executablePath);
        }

        /// <summary>Handles a request and returns the response to send back.</summary>
        public Func<string, string>? RequestHandler { get; set; }

        /// <summary>
        /// Turns a process id into the path of its image. Replaced in tests, where the pipe
        /// server is the test host rather than a copy of this application.
        /// </summary>
        public Func<uint, string?> ProcessPathResolver { get; set; } = ResolveProcessPath;

        public string PipeName => _pipeName;

        public string ExecutablePath => _executablePath;

        /// <summary>True when this process owns the engine.</summary>
        public bool IsOwner => _ownsMutex;

        public bool TryAcquireOwnership()
        {
            ThrowIfDisposed();

            // Ownership is whether this call created the kernel object, not whether WaitOne
            // succeeded. A mutex is reentrant for the thread that already holds it, so waiting
            // would hand ownership to a second instance living in the same process.
            _mutex = new Mutex(initiallyOwned: false, name: _mutexName, createdNew: out bool createdNew);
            _ownsMutex = createdNew;

            if (!_ownsMutex)
            {
                // Release the handle straight away. Holding it would keep the name alive after
                // the real owner exits, and the next launch would then refuse to start.
                _mutex.Dispose();
                _mutex = null;
            }

            return _ownsMutex;
        }

        /// <summary>Starts accepting requests from later launches.</summary>
        public void StartListening()
        {
            ThrowIfDisposed();

            if (!_ownsMutex || _listener != null)
            {
                return;
            }

            // A small pool rather than one listener. A single instance is unavailable for the
            // moment between finishing one request and waiting for the next, and a caller that
            // arrives exactly then is told the pipe is busy. With several, one is always
            // accepting, so a second launch is never turned away by timing.
            var loops = new Task[ListenerPoolSize];
            for (int index = 0; index < ListenerPoolSize; index++)
            {
                loops[index] = Task.Run(() => ListenLoopAsync(_cancellation.Token));
            }

            _listener = Task.WhenAll(loops);
        }

        /// <summary>
        /// Accepts and answers requests until disposal.
        /// </summary>
        /// <remarks>
        /// One server instance is created and then reused across connections through Disconnect,
        /// rather than being disposed and rebuilt each time. Rebuilding made the pipe name vanish
        /// between requests, and a caller arriving in that window got "not found" rather than
        /// waiting, so a second launch could conclude the app was unreachable. Keeping the
        /// instance alive also sidesteps the rule that only the first instance of a pipe may
        /// carry a security descriptor.
        /// </remarks>
        private async Task ListenLoopAsync(CancellationToken cancellationToken)
        {
            NamedPipeServerStream? server = null;

            try
            {
                server = CreateServer();

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                        string request = ReadRequest(server);
                        string response = Dispatch(request);

                        byte[] payload = Encoding.UTF8.GetBytes(response);
                        server.Write(payload, 0, payload.Length);
                        server.Flush();

                        // Wait for the client to read it. Disconnecting straight after Flush can
                        // tear the connection down with the response still buffered, and the
                        // caller then sees a broken pipe instead of an answer.
                        server.WaitForPipeDrain();
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (IOException)
                    {
                        // A client that went away mid-exchange. Keep serving.
                    }
                    finally
                    {
                        if (server.IsConnected)
                        {
                            server.Disconnect();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (UnauthorizedAccessException ex)
            {
                LastListenerError = ex.ToString();
            }
            catch (Exception ex)
            {
                // The listener runs detached, so an unexpected failure here would otherwise be
                // invisible and the app would simply never answer a second launch.
                LastListenerError = ex.ToString();
            }
            finally
            {
                server?.Dispose();
            }
        }

        /// <summary>Set when the listener stopped because of an error.</summary>
        public string? LastListenerError { get; private set; }

        /// <summary>
        /// Creates one server instance.
        /// </summary>
        /// <remarks>
        /// Only the first instance of a named pipe may carry a security descriptor. Passing one
        /// again when a further instance is created fails with access denied, which would kill
        /// the accept loop and leave the app unreachable after its very first request. Later
        /// instances inherit the ACL the first one established, so they are created without.
        /// </remarks>
        private NamedPipeServerStream CreateServer()
        {
            bool firstInstance;
            lock (_securityGate)
            {
                firstInstance = !_securityEstablished;
                _securityEstablished = true;
            }

            if (!firstInstance)
            {
                return new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: IpcCommands.MaxRequestBytes,
                    outBufferSize: IpcCommands.MaxRequestBytes);
            }

            // Only this user and SYSTEM. Impersonation is refused so a client cannot borrow
            // this process's identity.
            var security = new PipeSecurity();
            var currentUser = WindowsIdentity.GetCurrent().User;

            if (currentUser != null)
            {
                // Synchronize as well as read and write: without it a client cannot open the
                // handle at all, and the connect attempt simply times out.
                security.AddAccessRule(new PipeAccessRule(
                    currentUser,
                    PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
                    AccessControlType.Allow));
            }

            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

            var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: IpcCommands.MaxRequestBytes,
                outBufferSize: IpcCommands.MaxRequestBytes,
                pipeSecurity: security);

            return server;
        }

        private static string ReadRequest(NamedPipeServerStream server)
        {
            var buffer = new byte[IpcCommands.MaxRequestBytes];
            int read = server.Read(buffer, 0, buffer.Length);

            if (read <= 0)
            {
                return string.Empty;
            }

            return Encoding.UTF8.GetString(buffer, 0, read).Trim();
        }

        private string Dispatch(string request)
        {
            if (string.IsNullOrEmpty(request) || request.Length > IpcCommands.MaxRequestBytes)
            {
                return IpcCommands.ResponseUnknownCommand;
            }

            if (string.Equals(request, IpcCommands.Identify, StringComparison.Ordinal))
            {
                // The caller verifies this against the real pipe server process rather than
                // trusting a path sent in a request.
                return IpcCommands.ResponseOk + IpcCommands.FieldSeparator +
                       System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) +
                       IpcCommands.FieldSeparator + _executablePath;
            }

            Func<string, string>? handler = RequestHandler;
            if (handler == null)
            {
                return IpcCommands.ResponseUnknownCommand;
            }

            try
            {
                return handler(request);
            }
            catch (Exception ex)
            {
                // A handler that throws must not take the listener down with it, or the app
                // stops answering every later launch.
                LastListenerError = ex.ToString();
                return IpcCommands.ResponseUnknownCommand;
            }
        }

        /// <summary>
        /// Sends a request to the running owner. Returns null when nothing answered, which the
        /// caller reports rather than treating as permission to start a second engine.
        /// </summary>
        public string? SendRequest(string command, TimeSpan timeout)
        {
            return Exchange(command, timeout)?.Response;
        }

        /// <summary>
        /// Asks who owns this session, and confirms the answer against the process that gave it.
        /// </summary>
        /// <remarks>
        /// The reply carries a process id and an executable path, and neither is believed on its
        /// own. The kernel is asked which process is on the other end of this pipe, that process
        /// is resolved to its image path, and the reply has to agree with both.
        ///
        /// The pipe already only admits this user, so this is not the only thing standing between
        /// the app and a hostile caller. It closes a narrower gap: the pipe name is predictable
        /// and the mutex is a separate kernel object, so a same-user process can hold the name
        /// before this application ever starts. Without this check that process could name any
        /// path it liked, and an installer would take its word for which copy it was about to
        /// shut down or configure startup for. With it, the worst a squatter can do is fail.
        ///
        /// A path that cannot be resolved is a failure rather than a shrug. Every alternative to
        /// failing means acting on an unverified claim, and the two commands guarded by this are
        /// the ones that close a running application and write to the Run key.
        /// </remarks>
        public OperationResult<IpcIdentity> RequestIdentity(TimeSpan timeout)
        {
            PipeExchange? exchange = Exchange(IpcCommands.Identify, timeout);

            if (exchange == null)
            {
                // Nothing answered at all. That is a different problem from an answer that does
                // not hold up, and the caller reports the two differently.
                return OperationResult<IpcIdentity>.Failure(FaultSubsystem.ShellOrIpc, "ipc.noAnswer");
            }

            if (!exchange.ServerProcessKnown)
            {
                return OperationResult<IpcIdentity>.Failure(
                    FaultSubsystem.ShellOrIpc,
                    "ipc.identityUnverifiable",
                    exchange.ServerProcessError,
                    retryable: false);
            }

            string? actualPath = ProcessPathResolver(exchange.ServerProcessId);
            if (string.IsNullOrEmpty(actualPath))
            {
                return OperationResult<IpcIdentity>.Failure(
                    FaultSubsystem.ShellOrIpc, "ipc.identityUnverifiable", retryable: false);
            }

            string[] parts = exchange.Response.Split(IpcCommands.FieldSeparator);
            if (parts.Length < 3 || !string.Equals(parts[0], IpcCommands.ResponseOk, StringComparison.Ordinal))
            {
                return OperationResult<IpcIdentity>.Failure(
                    FaultSubsystem.ShellOrIpc, "ipc.identityMalformed", retryable: false);
            }

            if (!uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint claimedProcessId) ||
                claimedProcessId != exchange.ServerProcessId)
            {
                return OperationResult<IpcIdentity>.Failure(
                    FaultSubsystem.ShellOrIpc, "ipc.identityMismatch", retryable: false);
            }

            string resolved = NormalizePath(actualPath!);
            if (!string.Equals(NormalizePath(parts[2]), resolved, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult<IpcIdentity>.Failure(
                    FaultSubsystem.ShellOrIpc, "ipc.identityMismatch", retryable: false);
            }

            // Ownership is decided on the verified path, never on the claimed one.
            return OperationResult<IpcIdentity>.Success(new IpcIdentity(exchange.ServerProcessId, resolved));
        }

        /// <summary>One completed round trip, and who the kernel says answered it.</summary>
        private sealed class PipeExchange
        {
            public PipeExchange(string response, bool serverProcessKnown, uint serverProcessId, int? serverProcessError)
            {
                Response = response;
                ServerProcessKnown = serverProcessKnown;
                ServerProcessId = serverProcessId;
                ServerProcessError = serverProcessError;
            }

            public string Response { get; }

            public bool ServerProcessKnown { get; }

            public uint ServerProcessId { get; }

            public int? ServerProcessError { get; }
        }

        private PipeExchange? Exchange(string command, TimeSpan timeout)
        {
            if (string.IsNullOrEmpty(command))
            {
                throw new ArgumentException("A command is required.", nameof(command));
            }

            byte[] payload = Encoding.UTF8.GetBytes(command);
            if (payload.Length > IpcCommands.MaxRequestBytes)
            {
                throw new ArgumentException("The request is too large.", nameof(command));
            }

            // The listener serves one request, tears the instance down and builds the next one,
            // so there is a brief window with nothing listening. A caller that happens to arrive
            // in that window must not conclude the app is unreachable, so connect attempts are
            // retried until the overall timeout expires.
            var elapsed = Stopwatch.StartNew();

            while (true)
            {
                try
                {
                    // Identification, not Impersonation: the server may confirm who is calling
                    // but cannot act as them. None is not a level a client can connect with.
                    using (var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification))
                    {
                        TimeSpan remaining = timeout - elapsed.Elapsed;
                        if (remaining <= TimeSpan.Zero)
                        {
                            return null;
                        }

                        client.Connect((int)remaining.TotalMilliseconds);

                        // Asked here, immediately on connecting, and not after the reply has been
                        // read. The server answers one request and then tears that pipe instance
                        // down, so by the time a reply is in hand the connection is usually gone
                        // and this fails with ERROR_PIPE_NOT_CONNECTED. Which is a question about
                        // timing rather than about identity, and answering it as though it were
                        // an identity problem refused every legitimate caller.
                        bool known = NativeMethods.GetNamedPipeServerProcessId(
                            client.SafePipeHandle, out uint serverProcessId);
                        int? error = known ? (int?)null : Marshal.GetLastWin32Error();

                        client.Write(payload, 0, payload.Length);
                        client.Flush();

                        var buffer = new byte[IpcCommands.MaxRequestBytes];
                        int read = client.Read(buffer, 0, buffer.Length);
                        string response = read <= 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, read);

                        return new PipeExchange(response, known, serverProcessId, error);
                    }
                }
                catch (TimeoutException)
                {
                    return null;
                }
                catch (IOException)
                {
                    // Most likely the gap between server instances. Try again while there is time.
                    if (elapsed.Elapsed >= timeout)
                    {
                        return null;
                    }

                    Thread.Sleep(ConnectRetryDelayMilliseconds);
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
            }
        }

        /// <summary>The image path of a process, or null when it cannot be established.</summary>
        private static string? ResolveProcessPath(uint processId)
        {
            try
            {
                using (System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById((int)processId))
                {
                    return process.MainModule?.FileName;
                }
            }
            catch (ArgumentException)
            {
                // No process with that id: it exited between answering and being asked about.
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Access denied reading the module list. Unverified is unverified.
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>
        /// Whether a caller running this path may issue an ownership-changing command.
        /// </summary>
        public bool IsSameExecutable(string otherPath)
        {
            return string.Equals(_executablePath, NormalizePath(otherPath), StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            }
            catch (ArgumentException)
            {
                return path;
            }
            catch (NotSupportedException)
            {
                return path;
            }
            catch (PathTooLongException)
            {
                return path;
            }
        }

        public static string CurrentUserSid()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return identity.User?.Value ?? "unknown";
            }
        }

        public static int CurrentSessionId()
        {
            return System.Diagnostics.Process.GetCurrentProcess().SessionId;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SingleInstanceService));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Cancel runs its registered callbacks on this thread, and the pipe server
            // registers one that calls CancelIoEx on its own handle. If the listener has
            // already finished and closed that handle, the callback throws
            // ObjectDisposedException straight back out of Cancel, and everything below here,
            // including releasing the mutex that lets the next launch start, never happens.
            //
            // The race is inherent: the handle belongs to another thread and can close at any
            // moment. Cancelling is best effort, and a listener that has already stopped needs
            // no cancelling anyway.
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (AggregateException)
            {
            }

            try
            {
                _listener?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            if (_mutex != null)
            {
                // Never acquired, so nothing to release: closing the handle destroys the name
                // once no other handle remains, which frees the next launch to take ownership.
                _mutex.Dispose();
                _mutex = null;
            }

            _ownsMutex = false;

            _cancellation.Dispose();
        }
    }
}
