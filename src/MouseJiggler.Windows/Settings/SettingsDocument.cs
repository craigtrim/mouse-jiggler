using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Settings;

namespace MouseJiggler.Windows.Settings
{
    /// <summary>
    /// The wire shape of settings.json. A flat DTO of primitives only: the reader never
    /// deserializes into live domain objects, and unknown fields are not silently interpreted.
    ///
    /// Every member is required. Without that, an absent field deserializes to its default, and
    /// since the default for a bool is false, a truncated document missing "stopped" would parse
    /// cleanly into a running app. A field that is not there means the document is damaged.
    /// </summary>
    [DataContract(Name = "settings", Namespace = "")]
    internal sealed class SettingsDto
    {
        [DataMember(Name = "schemaVersion", Order = 0, IsRequired = true)]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "revision", Order = 1, IsRequired = true)]
        public long Revision { get; set; }

        [DataMember(Name = "stopped", Order = 2, IsRequired = true)]
        public bool Stopped { get; set; }

        [DataMember(Name = "runMode", Order = 3, IsRequired = true)]
        public string? RunMode { get; set; }

        [DataMember(Name = "scheduleEnabled", Order = 4, IsRequired = true)]
        public bool ScheduleEnabled { get; set; }

        [DataMember(Name = "scheduleStart", Order = 5, IsRequired = true)]
        public string? ScheduleStart { get; set; }

        [DataMember(Name = "scheduleEnd", Order = 6, IsRequired = true)]
        public string? ScheduleEnd { get; set; }

        [DataMember(Name = "dayMask", Order = 7, IsRequired = true)]
        public int DayMask { get; set; }

        [DataMember(Name = "pauseOnBattery", Order = 8, IsRequired = true)]
        public bool PauseOnBattery { get; set; }

        [DataMember(Name = "keepDisplayOn", Order = 9, IsRequired = true)]
        public bool KeepDisplayOn { get; set; }

        [DataMember(Name = "jiggleMouse", Order = 10, IsRequired = true)]
        public bool JiggleMouse { get; set; }

        [DataMember(Name = "intervalSeconds", Order = 11, IsRequired = true)]
        public int IntervalSeconds { get; set; }

        [DataMember(Name = "diagnosticLogging", Order = 12, IsRequired = true)]
        public bool DiagnosticLogging { get; set; }

        [DataMember(Name = "startupInitialized", Order = 13, IsRequired = true)]
        public bool StartupInitialized { get; set; }

        [DataMember(Name = "firstRunCompleted", Order = 14, IsRequired = true)]
        public bool FirstRunCompleted { get; set; }
    }

    /// <summary>
    /// Reads and writes the settings document with the framework serializer. Input is bounded;
    /// a file larger than the cap is rejected rather than parsed.
    /// </summary>
    public static class SettingsDocument
    {
        /// <summary>Anything larger than this is treated as corrupt.</summary>
        public const int MaxBytes = 64 * 1024;

        private const string RunModeManual = "Manual";
        private const string RunModeScheduled = "Scheduled";

        public static OperationResult<byte[]> Serialize(SettingsV1 settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            var dto = new SettingsDto
            {
                SchemaVersion = settings.SchemaVersion,
                Revision = settings.Revision,
                Stopped = settings.Stopped,
                RunMode = settings.RunMode == Core.Settings.RunMode.Manual ? RunModeManual : RunModeScheduled,
                ScheduleEnabled = settings.ScheduleEnabled,
                ScheduleStart = settings.ScheduleStart,
                ScheduleEnd = settings.ScheduleEnd,
                DayMask = settings.DayMask,
                PauseOnBattery = settings.PauseOnBattery,
                KeepDisplayOn = settings.KeepDisplayOn,
                JiggleMouse = settings.JiggleMouse,
                IntervalSeconds = settings.IntervalSeconds,
                DiagnosticLogging = settings.DiagnosticLogging,
                StartupInitialized = settings.StartupInitialized,
                FirstRunCompleted = settings.FirstRunCompleted,
            };

            try
            {
                using (var buffer = new MemoryStream())
                {
                    var serializer = new DataContractJsonSerializer(typeof(SettingsDto));
                    serializer.WriteObject(buffer, dto);
                    return OperationResult<byte[]>.Success(buffer.ToArray());
                }
            }
            catch (SerializationException)
            {
                return OperationResult<byte[]>.Failure(FaultSubsystem.ConfigSave, "settings.serialize.failed");
            }
        }

        /// <summary>
        /// Parses the document. Structural problems are reported as faults, never thrown:
        /// a damaged file must leave the app stopped rather than crash it.
        /// </summary>
        public static OperationResult<SettingsV1> Deserialize(byte[] utf8Json)
        {
            if (utf8Json == null)
            {
                throw new ArgumentNullException(nameof(utf8Json));
            }

            if (utf8Json.Length == 0)
            {
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigRead, "settings.empty", retryable: false);
            }

            if (utf8Json.Length > MaxBytes)
            {
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigRead, "settings.oversized", retryable: false);
            }

            SettingsDto? dto;
            try
            {
                using (var buffer = new MemoryStream(utf8Json, writable: false))
                {
                    var serializer = new DataContractJsonSerializer(typeof(SettingsDto));
                    dto = serializer.ReadObject(buffer) as SettingsDto;
                }
            }
            catch (SerializationException)
            {
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigRead, "settings.malformed", retryable: false);
            }
            catch (FormatException)
            {
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigRead, "settings.malformed", retryable: false);
            }

            if (dto == null)
            {
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigRead, "settings.malformed", retryable: false);
            }

            if (dto.SchemaVersion != SettingsV1.CurrentSchemaVersion)
            {
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigRead, "settings.unsupportedSchema", retryable: false);
            }

            RunMode runMode;
            if (string.Equals(dto.RunMode, RunModeManual, StringComparison.Ordinal))
            {
                runMode = Core.Settings.RunMode.Manual;
            }
            else if (string.Equals(dto.RunMode, RunModeScheduled, StringComparison.Ordinal))
            {
                runMode = Core.Settings.RunMode.Scheduled;
            }
            else
            {
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigRead, "settings.runMode.invalid", retryable: false);
            }

            if (dto.ScheduleStart == null || dto.ScheduleEnd == null)
            {
                return OperationResult<SettingsV1>.Failure(FaultSubsystem.ConfigRead, "settings.schedule.missing", retryable: false);
            }

            var settings = new SettingsV1(
                dto.SchemaVersion,
                dto.Revision,
                dto.Stopped,
                runMode,
                dto.ScheduleEnabled,
                dto.ScheduleStart,
                dto.ScheduleEnd,
                dto.DayMask,
                dto.PauseOnBattery,
                dto.KeepDisplayOn,
                dto.JiggleMouse,
                dto.IntervalSeconds,
                dto.DiagnosticLogging,
                dto.StartupInitialized,
                dto.FirstRunCompleted);

            return OperationResult<SettingsV1>.Success(settings);
        }

        /// <summary>Convenience for tests and diagnostics; the store itself works in bytes.</summary>
        public static string ToJson(SettingsV1 settings)
        {
            OperationResult<byte[]> result = Serialize(settings);
            return result.Succeeded && result.Value != null
                ? Encoding.UTF8.GetString(result.Value)
                : string.Empty;
        }
    }
}
