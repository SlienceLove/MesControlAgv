using System.Text.Json.Serialization;

namespace MesControlAgv.Contracts.Materials;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterialKind
{
    Unknown,
    Sample,
    Consumable
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SampleLifecycleStatus
{
    Unknown,
    Available,
    Reserved,
    InUse,
    Consumed,
    Quarantined,
    Expired
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InventoryTransactionKind
{
    Unknown,
    Receipt,
    Move,
    Reserve,
    Release,
    Consume,
    Adjustment,
    Quarantine,
    Unquarantine
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterialScanKind
{
    Unknown,
    Sample,
    ConsumableLot,
    Container
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterialBindingStatus
{
    Unknown,
    Reserved,
    Released,
    Consumed,
    Cancelled
}

public static class MaterialIssueCodes
{
    public const string RequestIdReused = "MAT-REQUEST-ID-REUSED";
    public const string MaterialCodeRequired = "MAT-CODE-REQUIRED";
    public const string MaterialNotFound = "MAT-NOT-FOUND";
    public const string MaterialDisabled = "MAT-DISABLED";
    public const string SampleBarcodeRequired = "MAT-SAMPLE-BARCODE-REQUIRED";
    public const string SampleAlreadyExists = "MAT-SAMPLE-ALREADY-EXISTS";
    public const string SampleNotFound = "MAT-SAMPLE-NOT-FOUND";
    public const string SampleUnavailable = "MAT-SAMPLE-UNAVAILABLE";
    public const string LotRequired = "MAT-LOT-REQUIRED";
    public const string LotAlreadyExists = "MAT-LOT-ALREADY-EXISTS";
    public const string LotNotFound = "MAT-LOT-NOT-FOUND";
    public const string LocationRequired = "MAT-LOCATION-REQUIRED";
    public const string LocationNotFound = "MAT-LOCATION-NOT-FOUND";
    public const string InventoryInsufficient = "MAT-INVENTORY-INSUFFICIENT";
    public const string InventoryStateInvalid = "MAT-INVENTORY-STATE-INVALID";
    public const string Expired = "MAT-EXPIRED";
    public const string Quarantined = "MAT-QUARANTINED";
    public const string ImportRowInvalid = "MAT-IMPORT-ROW-INVALID";
    public const string BarcodeUnknown = "MAT-BARCODE-UNKNOWN";
    public const string BarcodeKindMismatch = "MAT-BARCODE-KIND-MISMATCH";
    public const string BindingConflict = "MAT-BINDING-CONFLICT";
    public const string InvalidQuantity = "MAT-INVALID-QUANTITY";
}

public sealed record MaterialCatalogItem
{
    public Guid MaterialId { get; init; }
    public string MaterialCode { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public MaterialKind Kind { get; init; }
    public string? Specification { get; init; }
    public string? Unit { get; init; }
    public bool IsEnabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record WarehouseLocation
{
    public Guid LocationId { get; init; }
    public string WarehouseCode { get; init; } = string.Empty;
    public string WarehouseName { get; init; } = string.Empty;
    public string LocationCode { get; init; } = string.Empty;
    public bool IsEnabled { get; init; }
}

public sealed record SampleMaterial
{
    public Guid SampleId { get; init; }
    public string Barcode { get; init; } = string.Empty;
    public string SampleBatchId { get; init; } = string.Empty;
    public string? MaterialCode { get; init; }
    public string? SampleType { get; init; }
    public SampleLifecycleStatus Status { get; init; }
    public WarehouseLocation? Location { get; init; }
    public Guid? BoundExperimentJobId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record MaterialLotInventory
{
    public Guid LotId { get; init; }
    public string MaterialCode { get; init; } = string.Empty;
    public string MaterialName { get; init; } = string.Empty;
    public string LotCode { get; init; } = string.Empty;
    public string? Barcode { get; init; }
    public string? Specification { get; init; }
    public string? Unit { get; init; }
    public decimal OnHand { get; init; }
    public decimal Reserved { get; init; }
    public decimal Available => Math.Max(0m, OnHand - Reserved);
    public DateTimeOffset? ManufactureDate { get; init; }
    public DateTimeOffset? ExpiryDate { get; init; }
    public bool IsQuarantined { get; init; }
    public WarehouseLocation? Location { get; init; }
}

public sealed record MaterialTraceEvent
{
    public Guid Id { get; init; }
    public InventoryTransactionKind? TransactionKind { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string? Barcode { get; init; }
    public string? MaterialCode { get; init; }
    public string? LotCode { get; init; }
    public decimal? Quantity { get; init; }
    public string? Unit { get; init; }
    public Guid? ExperimentJobId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}

public sealed record MaterialScanResult
{
    public Guid ScanId { get; init; }
    public string RawCode { get; init; } = string.Empty;
    public string NormalizedCode { get; init; } = string.Empty;
    public MaterialScanKind Kind { get; init; }
    public bool IsResolved { get; init; }
    public string? IssueCode { get; init; }
    public string? Message { get; init; }
    public bool IsIdempotentReplay { get; init; }
    public SampleMaterial? Sample { get; init; }
    public MaterialLotInventory? Lot { get; init; }
    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
}

public sealed record MaterialImportIssue
{
    public int RowNumber { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? Field { get; init; }
}

public sealed record SampleImportRow
{
    public int RowNumber { get; init; }
    public string Barcode { get; init; } = string.Empty;
    public string SampleBatchId { get; init; } = string.Empty;
    public string? MaterialCode { get; init; }
    public string? SampleType { get; init; }
    public string? LocationCode { get; init; }
}

public sealed record ConsumableImportRow
{
    public int RowNumber { get; init; }
    public string MaterialCode { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Specification { get; init; }
    public string? Unit { get; init; }
    public string LotCode { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public DateTimeOffset? ManufactureDate { get; init; }
    public DateTimeOffset? ExpiryDate { get; init; }
    public string? LocationCode { get; init; }
    public string? Barcode { get; init; }
}

public sealed record MaterialImportRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Source { get; init; } = "manual";
    public IReadOnlyList<SampleImportRow> Samples { get; init; } = Array.Empty<SampleImportRow>();
    public IReadOnlyList<ConsumableImportRow> Consumables { get; init; } = Array.Empty<ConsumableImportRow>();
}

public sealed record MaterialImportPreview
{
    public Guid RequestId { get; init; }
    public int AcceptedCount { get; init; }
    public int ExistingCount { get; init; }
    public IReadOnlyList<MaterialImportIssue> Issues { get; init; } = Array.Empty<MaterialImportIssue>();
    public bool CanCommit => Issues.Count == 0;
}

/// <summary>Durable result returned after a sample/consumable import is committed.</summary>
public sealed record MaterialImportResult
{
    public Guid RequestId { get; init; }
    public int ImportedSamples { get; init; }
    public int ImportedConsumableLots { get; init; }
    public int ExistingCount { get; init; }
    public IReadOnlyList<MaterialImportIssue> Issues { get; init; } = Array.Empty<MaterialImportIssue>();
    public bool IsIdempotentReplay { get; init; }
}

public sealed record MaterialScanRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string RawCode { get; init; } = string.Empty;
    public MaterialScanKind Kind { get; init; }
    public string Source { get; init; } = "manual";
}

public sealed record ReceiveMaterialRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string MaterialCode { get; init; } = string.Empty;
    public string MaterialName { get; init; } = string.Empty;
    public string LotCode { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public string? Unit { get; init; }
    public string LocationCode { get; init; } = string.Empty;
    public string? Barcode { get; init; }
    public string? Specification { get; init; }
    public DateTimeOffset? ManufactureDate { get; init; }
    public DateTimeOffset? ExpiryDate { get; init; }
    public string? Reason { get; init; }
}

public sealed record MoveMaterialRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public Guid LotId { get; init; }
    /// <summary>Optional source location. If omitted, the only non-empty lot balance is used.</summary>
    public string? FromLocationCode { get; init; }
    public string ToLocationCode { get; init; } = string.Empty;
    public decimal? Quantity { get; init; }
    public string? Reason { get; init; }
}

public sealed record AdjustMaterialRequest
{
    public Guid RequestId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public Guid LotId { get; init; }
    /// <summary>Optional location. If omitted, the only non-empty lot balance is used.</summary>
    public string? LocationCode { get; init; }
    public decimal QuantityDelta { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed record MaterialReservationLine
{
    public string MaterialCode { get; init; } = string.Empty;
    public string? LotCode { get; init; }
    public decimal Quantity { get; init; }
    public string? Unit { get; init; }
}

public sealed record ReserveExperimentMaterialsRequest
{
    public Guid RequestId { get; init; }
    public Guid ExperimentJobId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string? SampleBarcode { get; init; }
    public IReadOnlyList<MaterialReservationLine> Requirements { get; init; } = Array.Empty<MaterialReservationLine>();
}

public sealed record ReleaseExperimentMaterialsRequest
{
    public Guid RequestId { get; init; }
    public Guid ExperimentJobId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed record ConsumeExperimentMaterialsRequest
{
    public Guid RequestId { get; init; }
    public Guid ExperimentJobId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string? SampleBarcode { get; init; }
    public string? InjectionPosition { get; init; }
    public IReadOnlyList<MaterialReservationLine> Materials { get; init; } = Array.Empty<MaterialReservationLine>();
}

public sealed record MaterialBinding
{
    public Guid BindingId { get; init; }
    public Guid ExperimentJobId { get; init; }
    public Guid? SampleId { get; init; }
    public Guid? LotId { get; init; }
    public decimal Quantity { get; init; }
    public string? Unit { get; init; }
    public MaterialBindingStatus Status { get; init; }
    public string? InjectionPosition { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record MaterialReservationResult
{
    public Guid RequestId { get; init; }
    public Guid ExperimentJobId { get; init; }
    public IReadOnlyList<MaterialBinding> Bindings { get; init; } = Array.Empty<MaterialBinding>();
    public bool IsIdempotentReplay { get; init; }
}

public sealed record MaterialReleaseResult
{
    public Guid RequestId { get; init; }
    public Guid ExperimentJobId { get; init; }
    public int ReleasedCount { get; init; }
    public bool IsIdempotentReplay { get; init; }
}

public sealed record MaterialConsumeResult
{
    public Guid RequestId { get; init; }
    public Guid ExperimentJobId { get; init; }
    public int ConsumedCount { get; init; }
    public bool IsIdempotentReplay { get; init; }
}

public sealed record MaterialCommandResult<T>
{
    public Guid RequestId { get; init; }
    public T? Data { get; init; }
    public bool IsIdempotentReplay { get; init; }
}
