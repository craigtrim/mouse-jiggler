using System;
using System.Globalization;
using System.IO;
using System.Text;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Activity;
using MouseJiggler.Core.Diagnostics;
using MouseJiggler.Windows.Settings;

namespace MouseJiggler.Windows.Diagnostics
{
    /// <summary>
    /// Optional local logging, off by default.
    /// </summary>
    /// <remarks>
    /// What is never written matters more than what is: no keystrokes, no pointer coordinates,
    /// no window or process names, no account names, no SIDs, no computer name, no full paths
    /// and no copy of the settings file. Records are a timestamp, a subsystem, a stable code and
    /// a Win32 error number. Nothing is ever sent anywhere: there is no endpoint, no upload and
    /// no telemetry identifier in the product at all.
    ///
    /// The file is bounded at 256 KiB with four numbered backups, so a failing subsystem cannot
    /// fill a disk while the user is away from the machine.
    /// </remarks>
    public sealed class LocalDiagnosticSink : IDiagnosticSink, IDisposable
    {
        public const int MaxFileBytes = 256 * 1024;
        public const int MaxBackups = 4;
        public const string LogFileName = "mousejiggler.log";

        private readonly DiagnosticRing _ring;
        private readonly string _logDirectory;
        private readonly Func<DateTime> _utcNow;
        private readonly object _gate = new object();

        private bool _diskLoggingEnabled;
        private bool _diskLoggingFailed;
        private bool _disposed;

        public LocalDiagnosticSink(string logDirectory, DiagnosticRing ring, Func<DateTime>? utcNow = null)
        {
            _logDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));
            _ring = ring ?? throw new ArgumentNullException(nameof(ring));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>Set once a write has failed; disk logging stays off for the rest of the session.</summary>
        public bool DiskLoggingFailed => _diskLoggingFailed;

        public string LogFilePath => Path.Combine(_logDirectory, LogFileName);

        /// <summary>Turns disk logging on or off. The in-memory ring is unaffected either way.</summary>
        public void SetDiskLogging(bool enabled)
        {
            lock (_gate)
            {
                _diskLoggingEnabled = enabled && !_diskLoggingFailed;
            }
        }

        public void Record(FaultSubsystem subsystem, string code, int? nativeErrorCode = null)
        {
            if (_disposed)
            {
                return;
            }

            var entry = new DiagnosticEvent(_utcNow(), subsystem, code, nativeErrorCode);

            // The ring decides whether this is a repeat worth keeping.
            if (!_ring.Record(entry))
            {
                return;
            }

            WriteToDisk(entry);
        }

        public void RecordTransition(StatusCode from, StatusCode to)
        {
            if (from == to)
            {
                return;
            }

            // Clear suppression for this exact code, not for a "status" key that is never
            // written: the codes are per transition, so a generic key could never match one.
            string code = "status." + from + "." + to;

            _ring.NoteRecovery(FaultSubsystem.None, code);
            Record(FaultSubsystem.None, code);
        }

        private void WriteToDisk(DiagnosticEvent entry)
        {
            lock (_gate)
            {
                if (!_diskLoggingEnabled || _diskLoggingFailed)
                {
                    return;
                }

                try
                {
                    Directory.CreateDirectory(_logDirectory);
                    RotateIfNeeded();

                    File.AppendAllText(LogFilePath, FormatLine(entry) + System.Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException)
                {
                    // Logging is a convenience. Losing it must not stop an otherwise healthy
                    // jiggler, and it must not recurse into another log write.
                    _diskLoggingFailed = true;
                    _diskLoggingEnabled = false;
                }
                catch (UnauthorizedAccessException)
                {
                    _diskLoggingFailed = true;
                    _diskLoggingEnabled = false;
                }
            }
        }

        /// <summary>One JSON object per line, with no field that could carry user data.</summary>
        public static string FormatLine(DiagnosticEvent entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            var builder = new StringBuilder(160);
            builder.Append("{\"utc\":\"");
            builder.Append(entry.UtcTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            builder.Append("\",\"version\":\"");
            builder.Append(DiagnosticSnapshotBuilder.AppVersion);
            builder.Append("\",\"subsystem\":\"");
            builder.Append(entry.Subsystem);
            builder.Append("\",\"code\":\"");
            builder.Append(Escape(entry.Code));
            builder.Append('"');

            if (entry.NativeErrorCode.HasValue)
            {
                builder.Append(",\"nativeError\":");
                builder.Append(entry.NativeErrorCode.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (entry.SuppressedRepeats > 0)
            {
                // Present only on a line that is standing in for others, so an ordinary line is
                // not padded with a zero that means nothing.
                builder.Append(",\"suppressedRepeats\":");
                builder.Append(entry.SuppressedRepeats.ToString(CultureInfo.InvariantCulture));
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private void RotateIfNeeded()
        {
            var info = new FileInfo(LogFilePath);
            if (!info.Exists || info.Length < MaxFileBytes)
            {
                return;
            }

            // Rotate before exceeding the cap, so the total never passes 256 KiB times five.
            string oldest = LogFilePath + "." + MaxBackups.ToString(CultureInfo.InvariantCulture);
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (int index = MaxBackups - 1; index >= 1; index--)
            {
                string source = LogFilePath + "." + index.ToString(CultureInfo.InvariantCulture);
                string destination = LogFilePath + "." + (index + 1).ToString(CultureInfo.InvariantCulture);

                if (File.Exists(source))
                {
                    if (File.Exists(destination))
                    {
                        File.Delete(destination);
                    }

                    File.Move(source, destination);
                }
            }

            File.Move(LogFilePath, LogFilePath + ".1");
        }

        /// <summary>Removes only the log files this sink owns.</summary>
        public OperationResult ClearLogs()
        {
            lock (_gate)
            {
                try
                {
                    if (File.Exists(LogFilePath))
                    {
                        File.Delete(LogFilePath);
                    }

                    for (int index = 1; index <= MaxBackups; index++)
                    {
                        string backup = LogFilePath + "." + index.ToString(CultureInfo.InvariantCulture);
                        if (File.Exists(backup))
                        {
                            File.Delete(backup);
                        }
                    }

                    return OperationResult.Success();
                }
                catch (IOException ex)
                {
                    return OperationResult.Failure(FaultSubsystem.ConfigSave, "log.deleteFailed", ex.HResult);
                }
                catch (UnauthorizedAccessException ex)
                {
                    return OperationResult.Failure(FaultSubsystem.ConfigSave, "log.deleteFailed", ex.HResult);
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
