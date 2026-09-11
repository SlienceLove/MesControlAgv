namespace MesControlAgv.Contracts.Devices;

public enum UnknownReason
{
    Timeout, DisconnectedAfterWrite, IncompleteResponse, MissingVendorTaskId,
    MissingResult, IdentityMismatch, ProtocolPending, ManualReconciliationRequired
}

public enum DeviceOperationLifecycle
{
    Queued, Preparing, StartPending, Running, AcquiringResult, Completed,
    Failed, Cancelled, Unknown, ManualInterventionRequired
}

public sealed record DeviceOperationRequest
{
    public Guid OperationId { get; init; } = Guid.NewGuid();
    public Guid RunId { get; init; }
    public Guid NodeExecutionId { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public string CapabilityId { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public bool IsValid(out string error)
    {
        if (OperationId == Guid.Empty || RunId == Guid.Empty || NodeExecutionId == Guid.Empty) { error = "Operation and correlation ids are required."; return false; }
        if (string.IsNullOrWhiteSpace(DeviceId)) { error = "DeviceId is required."; return false; }
        if (string.IsNullOrWhiteSpace(IdempotencyKey)) { error = "IdempotencyKey is required."; return false; }
        error = string.Empty; return true;
    }
}

public enum ManualReconciliationDecision { Confirmed, Rejected, Cancelled }
public sealed record DeviceOperationReconciliationRequest
{
    public Guid OperationId { get; init; }
    public Guid RunId { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public string Actor { get; init; } = string.Empty;
    public ManualReconciliationDecision Decision { get; init; }
    public string Comment { get; init; } = string.Empty;
    public string? VendorTaskId { get; init; }
    public string? CorrelationId { get; init; }
}

public sealed record DeviceOperationResult
{
    public Guid OperationId { get; init; }
    public DeviceOperationLifecycle Status { get; init; }
    public UnknownReason? UnknownReason { get; init; }
    public string? VendorTaskId { get; init; }
    public string? ResultFileReference { get; init; }
    public IReadOnlyDictionary<string, string?> Result { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public string? Error { get; init; }
    public bool CanRetry => Status is not DeviceOperationLifecycle.Unknown and
        not DeviceOperationLifecycle.ManualInterventionRequired;
}
