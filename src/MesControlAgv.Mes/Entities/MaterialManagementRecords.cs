namespace MesControlAgv.Mes.Entities;

public sealed class MaterialCatalogRecord
{
    public Guid MaterialId { get; set; }
    public string MaterialCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Specification { get; set; }
    public string? Unit { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class WarehouseLocationRecord
{
    public Guid LocationId { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public string WarehouseName { get; set; } = string.Empty;
    public string LocationCode { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class SampleMaterialRecord
{
    public Guid SampleId { get; set; }
    public string Barcode { get; set; } = string.Empty;
    public string SampleBatchId { get; set; } = string.Empty;
    public string? MaterialCode { get; set; }
    public string? SampleType { get; set; }
    public string Status { get; set; } = string.Empty;
    public Guid? LocationId { get; set; }
    public Guid? BoundExperimentJobId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class MaterialLotRecord
{
    public Guid LotId { get; set; }
    public Guid MaterialId { get; set; }
    public string MaterialCode { get; set; } = string.Empty;
    public string LotCode { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public string? Specification { get; set; }
    public string? Unit { get; set; }
    public DateTime? ManufactureDateUtc { get; set; }
    public DateTime? ExpiryDateUtc { get; set; }
    public bool IsQuarantined { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class InventoryBalanceRecord
{
    public Guid BalanceId { get; set; }
    public Guid LotId { get; set; }
    public Guid LocationId { get; set; }
    public decimal OnHand { get; set; }
    public decimal Reserved { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class InventoryTransactionRecord
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public string LineKey { get; set; } = string.Empty;
    public string TransactionKind { get; set; } = string.Empty;
    public Guid? LotId { get; set; }
    public Guid? SampleId { get; set; }
    public Guid? FromLocationId { get; set; }
    public Guid? ToLocationId { get; set; }
    public string? MaterialCode { get; set; }
    public string? LotCode { get; set; }
    public string? Barcode { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public Guid? ExperimentJobId { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? CorrelationId { get; set; }
    public string DetailsJson { get; set; } = "{}";
    public DateTime OccurredAtUtc { get; set; }
}

public sealed class BarcodeScanEventRecord
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public string RawCode { get; set; } = string.Empty;
    public string NormalizedCode { get; set; } = string.Empty;
    public string ScanKind { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string? IssueCode { get; set; }
    public Guid? SampleId { get; set; }
    public Guid? LotId { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = "{}";
    public DateTime OccurredAtUtc { get; set; }
}

public sealed class ExperimentJobMaterialBindingRecord
{
    public Guid BindingId { get; set; }
    public Guid RequestId { get; set; }
    public string LineKey { get; set; } = string.Empty;
    public Guid ExperimentJobId { get; set; }
    public Guid? SampleId { get; set; }
    public Guid? LotId { get; set; }
    public decimal Quantity { get; set; }
    public string? Unit { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? InjectionPosition { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
