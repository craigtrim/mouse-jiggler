using System;
using System.Text;
using MouseJiggler.Core.Abstractions;
using MouseJiggler.Core.Settings;
using MouseJiggler.Windows.Settings;
using Xunit;

namespace MouseJiggler.Tests
{
    /// <summary>
    /// The settings contract: defaults are exactly what the epic specifies, and the document
    /// survives a round trip without drifting. Later issues extend this with the full
    /// validation and atomic-write matrix.
    /// </summary>
    public sealed class SettingsContractTests
    {
        [Fact]
        public void DefaultSettingsMatchTheSpecifiedFirstLaunchState()
        {
            SettingsV1 defaults = SettingsV1.CreateDefault();

            Assert.Equal(1, defaults.SchemaVersion);
            Assert.Equal(1, defaults.Revision);

            // A new install is stopped, in Scheduled mode, with the schedule switched off:
            // that combination means "runs continuously once started".
            Assert.True(defaults.Stopped);
            Assert.Equal(RunMode.Scheduled, defaults.RunMode);
            Assert.False(defaults.ScheduleEnabled);

            Assert.Equal("08:00", defaults.ScheduleStart);
            Assert.Equal("17:00", defaults.ScheduleEnd);
            Assert.Equal(127, defaults.DayMask);

            Assert.True(defaults.PauseOnBattery);
            Assert.True(defaults.KeepDisplayOn);
            Assert.True(defaults.JiggleMouse);
            Assert.Equal(30, defaults.IntervalSeconds);

            Assert.False(defaults.DiagnosticLogging);
            Assert.False(defaults.StartupInitialized);
            Assert.False(defaults.FirstRunCompleted);
        }

        [Fact]
        public void EveryFieldSurvivesARoundTrip()
        {
            var original = new SettingsV1(
                schemaVersion: 1,
                revision: 42,
                stopped: false,
                runMode: RunMode.Manual,
                scheduleEnabled: true,
                scheduleStart: "22:00",
                scheduleEnd: "06:00",
                dayMask: 16,
                pauseOnBattery: false,
                keepDisplayOn: false,
                jiggleMouse: false,
                intervalSeconds: 300,
                diagnosticLogging: true,
                startupInitialized: true,
                firstRunCompleted: true);

            OperationResult<byte[]> serialized = SettingsDocument.Serialize(original);
            Assert.True(serialized.Succeeded);
            Assert.NotNull(serialized.Value);

            OperationResult<SettingsV1> parsed = SettingsDocument.Deserialize(serialized.Value!);
            Assert.True(parsed.Succeeded);

            SettingsV1 result = parsed.Value!;
            Assert.Equal(original.SchemaVersion, result.SchemaVersion);
            Assert.Equal(original.Revision, result.Revision);
            Assert.Equal(original.Stopped, result.Stopped);
            Assert.Equal(original.RunMode, result.RunMode);
            Assert.Equal(original.ScheduleEnabled, result.ScheduleEnabled);
            Assert.Equal(original.ScheduleStart, result.ScheduleStart);
            Assert.Equal(original.ScheduleEnd, result.ScheduleEnd);
            Assert.Equal(original.DayMask, result.DayMask);
            Assert.Equal(original.PauseOnBattery, result.PauseOnBattery);
            Assert.Equal(original.KeepDisplayOn, result.KeepDisplayOn);
            Assert.Equal(original.JiggleMouse, result.JiggleMouse);
            Assert.Equal(original.IntervalSeconds, result.IntervalSeconds);
            Assert.Equal(original.DiagnosticLogging, result.DiagnosticLogging);
            Assert.Equal(original.StartupInitialized, result.StartupInitialized);
            Assert.Equal(original.FirstRunCompleted, result.FirstRunCompleted);
        }

        [Fact]
        public void RunModeIsWrittenAsTheExactCaseSensitiveWireValue()
        {
            string json = SettingsDocument.ToJson(SettingsV1.CreateDefault());

            Assert.Contains("\"runMode\":\"Scheduled\"", json);
            Assert.Contains("\"stopped\":true", json);
        }

        [Fact]
        public void AnUnsupportedSchemaVersionIsRejectedRatherThanGuessedAt()
        {
            const string FutureSchema =
                "{\"schemaVersion\":2,\"revision\":1,\"stopped\":true,\"runMode\":\"Scheduled\"," +
                "\"scheduleEnabled\":false,\"scheduleStart\":\"08:00\",\"scheduleEnd\":\"17:00\"," +
                "\"dayMask\":127,\"pauseOnBattery\":true,\"keepDisplayOn\":true,\"jiggleMouse\":true," +
                "\"intervalSeconds\":30,\"diagnosticLogging\":false,\"startupInitialized\":false," +
                "\"firstRunCompleted\":false}";

            OperationResult<SettingsV1> parsed = SettingsDocument.Deserialize(Encoding.UTF8.GetBytes(FutureSchema));

            Assert.False(parsed.Succeeded);
            Assert.Equal("settings.unsupportedSchema", parsed.Outcome.Code);
            Assert.False(parsed.Outcome.Retryable);
        }

        [Fact]
        public void MalformedAndOversizedDocumentsAreRejectedWithoutThrowing()
        {
            OperationResult<SettingsV1> malformed = SettingsDocument.Deserialize(Encoding.UTF8.GetBytes("{ not json"));
            Assert.False(malformed.Succeeded);
            Assert.Equal("settings.malformed", malformed.Outcome.Code);

            OperationResult<SettingsV1> oversized = SettingsDocument.Deserialize(new byte[SettingsDocument.MaxBytes + 1]);
            Assert.False(oversized.Succeeded);
            Assert.Equal("settings.oversized", oversized.Outcome.Code);

            OperationResult<SettingsV1> empty = SettingsDocument.Deserialize(new byte[0]);
            Assert.False(empty.Succeeded);
            Assert.Equal("settings.empty", empty.Outcome.Code);
        }

        [Fact]
        public void ADocumentMissingAFieldIsRejectedRatherThanDefaulted()
        {
            // "stopped" is absent. An unrequired member would deserialize to the default for a
            // bool, which is false, so a truncated document would parse cleanly into a RUNNING
            // app: exactly the silent activation the settings contract exists to prevent.
            const string MissingStopped =
                "{\"schemaVersion\":1,\"revision\":7,\"runMode\":\"Scheduled\"," +
                "\"scheduleEnabled\":false,\"scheduleStart\":\"08:00\",\"scheduleEnd\":\"17:00\"," +
                "\"dayMask\":127,\"pauseOnBattery\":true,\"keepDisplayOn\":true,\"jiggleMouse\":true," +
                "\"intervalSeconds\":30,\"diagnosticLogging\":false,\"startupInitialized\":true," +
                "\"firstRunCompleted\":true}";

            OperationResult<SettingsV1> parsed = SettingsDocument.Deserialize(Encoding.UTF8.GetBytes(MissingStopped));

            Assert.False(parsed.Succeeded);
            Assert.Equal("settings.malformed", parsed.Outcome.Code);
            Assert.False(parsed.Outcome.Retryable);
        }

        [Theory]
        [InlineData("pauseOnBattery")]
        [InlineData("dayMask")]
        [InlineData("intervalSeconds")]
        [InlineData("firstRunCompleted")]
        public void EveryFieldIsRequired(string omitted)
        {
            SettingsV1 complete = SettingsV1.CreateDefault();
            string json = SettingsDocument.ToJson(complete);

            // Remove just the one member, leaving the rest of the document well formed.
            int start = json.IndexOf("\"" + omitted + "\"", StringComparison.Ordinal);
            Assert.True(start > 0, "The field should be present to begin with.");

            int end = json.IndexOf(',', start);
            if (end < 0)
            {
                end = json.IndexOf('}', start);
                start--;
            }
            else
            {
                end++;
            }

            string damaged = json.Remove(start, end - start);

            OperationResult<SettingsV1> parsed = SettingsDocument.Deserialize(Encoding.UTF8.GetBytes(damaged));

            Assert.False(parsed.Succeeded);
        }

        [Fact]
        public void AnUnknownRunModeIsRejected()
        {
            const string BadMode =
                "{\"schemaVersion\":1,\"revision\":1,\"stopped\":true,\"runMode\":\"manual\"," +
                "\"scheduleEnabled\":false,\"scheduleStart\":\"08:00\",\"scheduleEnd\":\"17:00\"," +
                "\"dayMask\":127,\"pauseOnBattery\":true,\"keepDisplayOn\":true,\"jiggleMouse\":true," +
                "\"intervalSeconds\":30,\"diagnosticLogging\":false,\"startupInitialized\":false," +
                "\"firstRunCompleted\":false}";

            OperationResult<SettingsV1> parsed = SettingsDocument.Deserialize(Encoding.UTF8.GetBytes(BadMode));

            Assert.False(parsed.Succeeded);
            Assert.Equal("settings.runMode.invalid", parsed.Outcome.Code);
        }
    }
}
