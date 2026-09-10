using System;

namespace MouseJiggler.Core.Abstractions
{
    /// <summary>Which subsystem a fault came from. Shared by diagnostics and the status UI.</summary>
    public enum FaultSubsystem
    {
        None = 0,
        ConfigRead = 1,
        ConfigSave = 2,
        PowerQuery = 3,
        SessionQuery = 4,
        WakeAcquire = 5,
        WakeRelease = 6,
        InputRead = 7,
        InputSend = 8,
        StartupRegistration = 9,
        ShellOrIpc = 10,
    }

    /// <summary>
    /// The outcome of an expected-to-sometimes-fail operation. Operational failures are values,
    /// not exceptions, so callers cannot ignore them by accident.
    /// </summary>
    public sealed class OperationResult
    {
        private OperationResult(bool succeeded, FaultSubsystem subsystem, string? code, int? nativeErrorCode, bool retryable)
        {
            Succeeded = succeeded;
            Subsystem = subsystem;
            Code = code;
            NativeErrorCode = nativeErrorCode;
            Retryable = retryable;
        }

        public bool Succeeded { get; }

        public FaultSubsystem Subsystem { get; }

        /// <summary>A stable short identifier, never a raw exception message or user path.</summary>
        public string? Code { get; }

        /// <summary>An optional sanitized Win32 error number.</summary>
        public int? NativeErrorCode { get; }

        public bool Retryable { get; }

        public static OperationResult Success()
        {
            return new OperationResult(true, FaultSubsystem.None, null, null, false);
        }

        public static OperationResult Failure(FaultSubsystem subsystem, string code, int? nativeErrorCode = null, bool retryable = true)
        {
            if (string.IsNullOrEmpty(code))
            {
                throw new ArgumentException("A fault code is required.", nameof(code));
            }

            return new OperationResult(false, subsystem, code, nativeErrorCode, retryable);
        }
    }

    /// <summary>An <see cref="OperationResult"/> that carries a value when it succeeds.</summary>
    /// <typeparam name="T">The value produced on success.</typeparam>
    public sealed class OperationResult<T>
    {
        private OperationResult(bool succeeded, T? value, OperationResult outcome)
        {
            Succeeded = succeeded;
            Value = value;
            Outcome = outcome;
        }

        public bool Succeeded { get; }

        /// <summary>Meaningful only when <see cref="Succeeded"/> is true.</summary>
        public T? Value { get; }

        public OperationResult Outcome { get; }

        public static OperationResult<T> Success(T value)
        {
            return new OperationResult<T>(true, value, OperationResult.Success());
        }

        public static OperationResult<T> Failure(FaultSubsystem subsystem, string code, int? nativeErrorCode = null, bool retryable = true)
        {
            return new OperationResult<T>(false, default, OperationResult.Failure(subsystem, code, nativeErrorCode, retryable));
        }
    }
}
