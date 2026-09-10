using System;
using System.Threading;
using MouseJiggler.Windows.Storage;

namespace MouseJiggler.Windows.Settings
{
    /// <summary>
    /// The cross-session write lock on the settings file.
    /// </summary>
    /// <remarks>
    /// A semaphore would only serialize writers inside one process. The same user can be signed
    /// in to more than one Windows session at once, each with its own copy of the app and its
    /// own view of the same file, so the lock has to live where both can see it: a file.
    ///
    /// It is a lock rather than a queue, so a writer that cannot get in gives up and reports
    /// that instead of waiting. Two seconds is long enough to ride out another session's write,
    /// and short enough that a stuck holder does not turn a click on Save into a hung window.
    /// </remarks>
    public sealed class SettingsFileLock
    {
        /// <summary>How long a writer waits for the lock before giving up.</summary>
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

        private readonly IFileOperations _files;
        private readonly string _path;
        private readonly Func<DateTime> _utcNow;

        public SettingsFileLock(IFileOperations files, string path, Func<DateTime> utcNow)
        {
            _files = files ?? throw new ArgumentNullException(nameof(files));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));

            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("A lock file path is required.", nameof(path));
            }

            _path = path;
        }

        /// <summary>
        /// Takes the lock, or returns null once the timeout has passed. Disposing the result
        /// releases it.
        /// </summary>
        /// <remarks>
        /// The deadline is read from the injected clock rather than measured with a stopwatch,
        /// so a test can drive the timeout without waiting two real seconds for it.
        /// </remarks>
        public IDisposable? TryAcquire(CancellationToken cancellationToken)
        {
            DateTime deadline = _utcNow().Add(Timeout);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IDisposable? acquired = _files.TryAcquireExclusive(_path);
                if (acquired != null)
                {
                    return acquired;
                }

                if (_utcNow() >= deadline)
                {
                    return null;
                }

                // Polling rather than waiting on a handle, because the thing being waited for is
                // a file another process holds open and there is no handle to wait on.
                Thread.Sleep(RetryDelay);
            }
        }
    }
}
