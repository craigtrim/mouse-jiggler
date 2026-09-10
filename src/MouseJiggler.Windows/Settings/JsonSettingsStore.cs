using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Settings;
using MouseJiggler.Windows.Storage;

namespace MouseJiggler.Windows.Settings
{
    /// <summary>
    /// The settings store. Updates are serialized through one in-process queue and a per-user
    /// lock file, so two windows sessions of the same user cannot lose each other's changes.
    /// A write is atomic: it lands as a complete document or not at all.
    /// </summary>
    public sealed class JsonSettingsStore : ISettingsStore
    {
        /// <summary>How long a writer waits for the lock before giving up.</summary>
        public static readonly TimeSpan LockTimeout = SettingsFileLock.Timeout;

        private readonly SettingsPaths _paths;
        private readonly IFileOperations _files;

        /// <summary>Serializes writers inside this process; the file lock covers the others.</summary>
        private readonly SemaphoreSlim _queue = new SemaphoreSlim(1, 1);

        private readonly SettingsFileLock _lock;
        private readonly Func<DateTime> _utcNow;

        private bool _disposed;

        public JsonSettingsStore(SettingsPaths paths, IFileOperations files, Func<DateTime>? utcNow = null)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _files = files ?? throw new ArgumentNullException(nameof(files));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _lock = new SettingsFileLock(_files, _paths.LockFile, _utcNow);
        }

        public event EventHandler<SettingsV1>? ExternalChangeObserved;

        /// <summary>
        /// True between issuing a Stop and committing it. While this holds, a revision written
        /// by another session that clears Stop is rejected instead of applied, because this
        /// user has already asked this machine to stop.
        /// </summary>
        public bool HasUncommittedStop { get; private set; }

        /// <summary>The revision this store last read or wrote.</summary>
        public long LastKnownRevision { get; private set; }

        /// <summary>
        /// Reads the document. A missing file yields defaults, which are stopped. A damaged or
        /// unsupported file is reported as a fault and leaves the original bytes untouched for
        /// recovery; it never silently becomes a running app.
        /// </summary>
        public OperationResult<SettingsV1> Load()
        {
            ThrowIfDisposed();

            try
            {
                if (!_files.FileExists(_paths.SettingsFile))
                {
                    SettingsV1 defaults = SettingsV1.CreateDefault();
                    LastKnownRevision = defaults.Revision;
                    return OperationResult<SettingsV1>.Success(defaults);
                }

                if (_files.GetFileLength(_paths.SettingsFile) > SettingsDocument.MaxBytes)
                {
                    return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigRead, "settings.oversized", retryable: false);
                }

                byte[] contents = _files.ReadAllBytes(_paths.SettingsFile);
                OperationResult<SettingsV1> parsed = SettingsDocument.Deserialize(contents);
                if (!parsed.Succeeded)
                {
                    return parsed;
                }

                OperationResult valid = SettingsValidator.Validate(parsed.Value);
                if (!valid.Succeeded)
                {
                    return OperationResult<SettingsV1>.Failure(valid.Subsystem, valid.Code!, valid.NativeErrorCode, retryable: false);
                }

                LastKnownRevision = parsed.Value!.Revision;
                return parsed;
            }
            catch (IOException ex)
            {
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigRead, "settings.io", ex.HResult);
            }
            catch (UnauthorizedAccessException ex)
            {
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigRead, "settings.accessDenied", ex.HResult);
            }
        }

        /// <summary>
        /// Applies a change to the newest committed revision under the lock, then commits it
        /// atomically. The caller's function receives what is actually on disk, not a stale copy
        /// held by a form, so concurrent edits to different fields do not overwrite each other.
        /// </summary>
        public async Task<OperationResult<SettingsV1>> UpdateAsync(Func<SettingsV1, SettingsV1> patch, CancellationToken cancellationToken)
        {
            if (patch == null)
            {
                throw new ArgumentNullException(nameof(patch));
            }

            ThrowIfDisposed();

            try
            {
                await _queue.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigSave, "settings.canceled", retryable: false);
            }

            try
            {
                return await Task.Run(() => CommitUnderLock(patch, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigSave, "settings.canceled", retryable: false);
            }
            finally
            {
                _queue.Release();
            }
        }

        /// <summary>Records that a Stop has been issued but not yet committed.</summary>
        public void MarkStopIssued()
        {
            HasUncommittedStop = true;
        }

        /// <summary>
        /// Decides what to do with a revision another session committed. A remote change that
        /// clears Stop is refused while this session holds an uncommitted Stop; anything else,
        /// including a remote Stop, is applied at once.
        /// </summary>
        public bool ShouldApplyObservedRevision(SettingsV1 observed)
        {
            if (observed == null)
            {
                throw new ArgumentNullException(nameof(observed));
            }

            if (!observed.Stopped && HasUncommittedStop)
            {
                return false;
            }

            return true;
        }

        /// <summary>Called by the watcher when the file changes underneath us.</summary>
        public void NotifyExternalChange(SettingsV1 observed)
        {
            if (observed == null)
            {
                throw new ArgumentNullException(nameof(observed));
            }

            if (!ShouldApplyObservedRevision(observed))
            {
                return;
            }

            LastKnownRevision = observed.Revision;
            ExternalChangeObserved?.Invoke(this, observed);
        }

        /// <summary>
        /// Quarantines an unreadable file and writes stopped defaults. Only an explicit user
        /// action reaches this: recovering automatically could restart an app the user stopped.
        /// </summary>
        public OperationResult<SettingsV1> ResetToDefaults()
        {
            ThrowIfDisposed();

            try
            {
                _files.CreateDirectory(_paths.RootDirectory);

                if (_files.FileExists(_paths.SettingsFile))
                {
                    string quarantine = _paths.CreateInvalidBackupPath(_utcNow());
                    _files.Copy(_paths.SettingsFile, quarantine, overwrite: false);
                    TrimInvalidBackups();
                }

                SettingsV1 defaults = SettingsV1.CreateDefault();
                OperationResult<SettingsV1> written = WriteDocument(defaults);
                if (written.Succeeded)
                {
                    HasUncommittedStop = false;
                    LastKnownRevision = defaults.Revision;
                }

                return written;
            }
            catch (IOException ex)
            {
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigSave, "settings.io", ex.HResult);
            }
            catch (UnauthorizedAccessException ex)
            {
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigSave, "settings.accessDenied", ex.HResult);
            }
        }

        private OperationResult<SettingsV1> CommitUnderLock(Func<SettingsV1, SettingsV1> patch, CancellationToken cancellationToken)
        {
            try
            {
                _files.CreateDirectory(_paths.RootDirectory);
            }
            catch (IOException ex)
            {
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigSave, "settings.io", ex.HResult);
            }
            catch (UnauthorizedAccessException ex)
            {
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigSave, "settings.accessDenied", ex.HResult);
            }

            IDisposable? fileLock = _lock.TryAcquire(cancellationToken);
            if (fileLock == null)
            {
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigSave, "settings.lockTimeout");
            }

            try
            {
                // Re-read inside the lock: another session may have committed since we loaded.
                OperationResult<SettingsV1> current = Load();
                SettingsV1 baseline = current.Succeeded
                    ? current.Value!
                    : SettingsV1.CreateDefault();

                SettingsV1 candidate = patch(baseline);
                OperationResult valid = SettingsValidator.Validate(candidate);
                if (!valid.Succeeded)
                {
                    return OperationResult<SettingsV1>.Failure(valid.Subsystem, valid.Code!, valid.NativeErrorCode, retryable: false);
                }

                SettingsV1 committed = SettingsIntent.WithRevision(candidate, baseline.Revision + 1);
                OperationResult<SettingsV1> written = WriteDocument(committed);
                if (written.Succeeded)
                {
                    LastKnownRevision = committed.Revision;

                    // The warning about an unsaved Stop clears only once a stopped document is
                    // actually on disk.
                    if (committed.Stopped)
                    {
                        HasUncommittedStop = false;
                    }
                }

                return written;
            }
            finally
            {
                fileLock.Dispose();
            }
        }

        private OperationResult<SettingsV1> WriteDocument(SettingsV1 settings)
        {
            OperationResult<byte[]> serialized = SettingsDocument.Serialize(settings);
            if (!serialized.Succeeded)
            {
                return OperationResult<SettingsV1>.Failure(serialized.Outcome.Subsystem, serialized.Outcome.Code!, retryable: false);
            }

            string tempPath = _paths.CreateTempFilePath();

            try
            {
                _files.WriteAllBytesDurable(tempPath, serialized.Value!);

                if (_files.FileExists(_paths.SettingsFile))
                {
                    _files.Replace(tempPath, _paths.SettingsFile, _paths.BackupFile);
                }
                else
                {
                    _files.Move(tempPath, _paths.SettingsFile);
                }

                return OperationResult<SettingsV1>.Success(settings);
            }
            catch (IOException ex)
            {
                DeleteOwnTempFile(tempPath);
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigSave, "settings.io", ex.HResult);
            }
            catch (UnauthorizedAccessException ex)
            {
                DeleteOwnTempFile(tempPath);
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigSave, "settings.accessDenied", ex.HResult);
            }
        }

        /// <summary>Deletes only a scratch file this store created, never anything else.</summary>
        private void DeleteOwnTempFile(string path)
        {
            string name = Path.GetFileName(path);
            if (!name.StartsWith(SettingsPaths.TempFilePrefix, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                if (_files.FileExists(path))
                {
                    _files.Delete(path);
                }
            }
            catch (IOException)
            {
                // A leftover scratch file is harmless; failing to remove it must not fail the save.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private void TrimInvalidBackups()
        {
            try
            {
                string[] existing = _files.GetFiles(_paths.RootDirectory, SettingsPaths.InvalidBackupPrefix + "*.json");
                if (existing.Length <= SettingsPaths.MaxInvalidBackups)
                {
                    return;
                }

                var ordered = new List<string>(existing);
                ordered.Sort(StringComparer.Ordinal);

                int excess = ordered.Count - SettingsPaths.MaxInvalidBackups;
                for (int i = 0; i < excess; i++)
                {
                    _files.Delete(ordered[i]);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(JsonSettingsStore));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _queue.Dispose();
        }
    }
}
