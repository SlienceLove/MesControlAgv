namespace MesControlAgv.Domain;

/// <summary>
/// Custom exception for compound task execution failures with detailed context.
/// </summary>
public sealed class CompoundTaskException : InvalidOperationException
{
    public Guid TaskId { get; }
    public CompoundTaskState State { get; }
    public string? DeviceId { get; }

    public CompoundTaskException(
        Guid taskId,
        CompoundTaskState state,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        TaskId = taskId;
        State = state;
    }

    public CompoundTaskException(
        Guid taskId,
        CompoundTaskState state,
        string deviceId,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        TaskId = taskId;
        State = state;
        DeviceId = deviceId;
    }
}

/// <summary>
/// Options for compound task execution.
/// </summary>
public sealed class CompoundTaskOptions
{
    public TimeSpan AgvNavigationTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan VisionRecognitionTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RobotArmOperationTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan DeviceStatusCheckTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public bool EnableDevicePreCheck { get; set; } = true;
    public bool EnableRollbackOnFailure { get; set; } = true;
    public TimeSpan ProgressReportInterval { get; set; } = TimeSpan.FromSeconds(10);
}
