using System;
using System.IO;
using System.Threading;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Settings;

namespace MouseJiggler.Windows.Settings
{
    /// <summary>
    /// Notices when another Windows session of the same user commits a new revision. Change
    /// notifications are debounced, because one atomic replace produces several of them, and a
    /// slower metadata poll runs alongside so a missed notification cannot leave this session
    /// permanently out of date.
    /// </summary>
    public sealed class SettingsFileWatcher : IDisposable
    {
        public static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(250);

        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

        private readonly SettingsPaths _paths;
        private readonly JsonSettingsStore _store;
        private readonly object _gate = new object();

        private FileSystemWatcher? _watcher;
        private Timer? _debounceTimer;
        private Timer? _pollTimer;
        private DateTime _lastSeenWriteTimeUtc;
        private bool _disposed;

        public SettingsFileWatcher(SettingsPaths paths, JsonSettingsStore store)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed || _watcher != null)
                {
                    return;
                }

                Directory.CreateDirectory(_paths.RootDirectory);

                _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
                _pollTimer = new Timer(OnPollElapsed, null, PollInterval, PollInterval);

                _watcher = new FileSystemWatcher(_paths.RootDirectory, SettingsPaths.SettingsFileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };

                _watcher.Changed += OnFileChanged;
                _watcher.Created += OnFileChanged;
                _watcher.Renamed += OnFileChanged;
                _watcher.EnableRaisingEvents = true;
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            lock (_gate)
            {
                // Restart the debounce window: File.Replace raises more than one event.
                _debounceTimer?.Change(DebounceInterval, Timeout.InfiniteTimeSpan);
            }
        }

        private void OnDebounceElapsed(object? state) => ReadIfChanged();

        private void OnPollElapsed(object? state) => ReadIfChanged();

        private void ReadIfChanged()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
            }

            DateTime writeTime;
            try
            {
                if (!File.Exists(_paths.SettingsFile))
                {
                    return;
                }

                writeTime = File.GetLastWriteTimeUtc(_paths.SettingsFile);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            lock (_gate)
            {
                if (writeTime == _lastSeenWriteTimeUtc)
                {
                    return;
                }

                _lastSeenWriteTimeUtc = writeTime;
            }

            // Read the baseline first: Load updates LastKnownRevision as a side effect, so
            // comparing after the load would always compare the new revision against itself.
            long previousRevision = _store.LastKnownRevision;

            OperationResult<SettingsV1> loaded = _store.Load();
            if (!loaded.Succeeded)
            {
                return;
            }

            if (loaded.Value!.Revision <= previousRevision)
            {
                return;
            }

            // The store decides whether to accept it; a remote Start loses to a local Stop.
            _store.NotifyExternalChange(loaded.Value);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Changed -= OnFileChanged;
                    _watcher.Created -= OnFileChanged;
                    _watcher.Renamed -= OnFileChanged;
                    _watcher.Dispose();
                    _watcher = null;
                }

                _debounceTimer?.Dispose();
                _debounceTimer = null;

                _pollTimer?.Dispose();
                _pollTimer = null;
            }
        }
    }
}
