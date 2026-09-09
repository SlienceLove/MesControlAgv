using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MesControlAgv.Contracts.Materials;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Application service for the first, deliberately small material-management
/// slice.  The service owns all inventory invariants; HTTP and WPF clients only
/// send commands and consume projections.
/// </summary>
public interface IMaterialManagementService
{
    Task<IReadOnlyList<MaterialCatalogItem>> ListCatalogAsync(
        MaterialKind? kind,
        string? search,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WarehouseLocation>> ListLocationsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SampleMaterial>> ListSamplesAsync(
        string? barcode,
        SampleLifecycleStatus? status,
        string? search,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MaterialLotInventory>> ListInventoryAsync(
        string? materialCode,
        string? lotCode,
        string? locationCode,
        bool includeQuarantined,
        CancellationToken cancellationToken);

    Task<MaterialImportPreview> PreviewImportAsync(
        MaterialImportRequest request,
        CancellationToken cancellationToken);

    Task<MaterialCommandResult<MaterialImportResult>> ImportAsync(
        MaterialImportRequest request,
        CancellationToken cancellationToken);

    Task<MaterialScanResult> ScanAsync(
        MaterialScanRequest request,
        CancellationToken cancellationToken);

    Task<MaterialCommandResult<MaterialLotInventory>> ReceiveAsync(
        ReceiveMaterialRequest request,
        CancellationToken cancellationToken);

    Task<MaterialCommandResult<MaterialLotInventory>> MoveAsync(
        MoveMaterialRequest request,
        CancellationToken cancellationToken);

    Task<MaterialCommandResult<MaterialLotInventory>> AdjustAsync(
        AdjustMaterialRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MaterialTraceEvent>> TraceAsync(
        string? barcode,
        string? materialCode,
        string? lotCode,
        Guid? experimentJobId,
        int limit,
        CancellationToken cancellationToken);

    Task<MaterialReservationResult> ReserveAsync(
        ReserveExperimentMaterialsRequest request,
        CancellationToken cancellationToken);

    Task<MaterialReleaseResult> ReleaseAsync(
        ReleaseExperimentMaterialsRequest request,
        CancellationToken cancellationToken);

    Task<MaterialConsumeResult> ConsumeAsync(
        ConsumeExperimentMaterialsRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MaterialBinding>> ListBindingsAsync(
        Guid experimentJobId,
        CancellationToken cancellationToken);
}

public sealed class MaterialManagementService : IMaterialManagementService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly MesDbContext _database;
    private readonly MaterialOperationCoordinator _operations;
    private readonly TimeProvider _clock;

    public MaterialManagementService(
        MesDbContext database,
        MaterialOperationCoordinator operations,
        TimeProvider? clock = null)
    {
        _database = database;
        _operations = operations;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<MaterialCatalogItem>> ListCatalogAsync(
        MaterialKind? kind,
        string? search,
        CancellationToken cancellationToken)
    {
        var query = _database.MaterialCatalog.AsNoTracking().AsQueryable();
        if (kind is not null && kind != MaterialKind.Unknown)
        {
            var value = kind.Value.ToString();
            query = query.Where(item => item.Kind == value);
        }

        var normalizedSearch = NormalizeOptional(search);
        if (normalizedSearch is not null)
        {
            query = query.Where(item =>
                item.MaterialCode.Contains(normalizedSearch) ||
                item.Name.Contains(normalizedSearch));
        }

        return await query
            .OrderBy(item => item.MaterialCode)
            .Select(item => new MaterialCatalogItem
            {
                MaterialId = item.MaterialId,
                MaterialCode = item.MaterialCode,
                Name = item.Name,
                Kind = ParseEnum(item.Kind, MaterialKind.Unknown),
                Specification = item.Specification,
                Unit = item.Unit,
                IsEnabled = item.IsEnabled,
                CreatedAt = ToOffset(item.CreatedAtUtc),
                UpdatedAt = ToOffset(item.UpdatedAtUtc)
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WarehouseLocation>> ListLocationsAsync(
        CancellationToken cancellationToken)
    {
        return await _database.WarehouseLocations
            .AsNoTracking()
            .Where(location => location.IsEnabled)
            .OrderBy(location => location.WarehouseCode)
            .ThenBy(location => location.LocationCode)
            .Select(location => new WarehouseLocation
            {
                LocationId = location.LocationId,
                WarehouseCode = location.WarehouseCode,
                WarehouseName = location.WarehouseName,
                LocationCode = location.LocationCode,
                IsEnabled = location.IsEnabled
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SampleMaterial>> ListSamplesAsync(
        string? barcode,
        SampleLifecycleStatus? status,
        string? search,
        CancellationToken cancellationToken)
    {
        var query = _database.SampleMaterials.AsNoTracking().AsQueryable();
        var normalizedBarcode = NormalizeOptional(barcode);
        if (normalizedBarcode is not null)
        {
            query = query.Where(sample => sample.Barcode == normalizedBarcode);
        }

        if (status is not null && status != SampleLifecycleStatus.Unknown)
        {
            var statusValue = status.Value.ToString();
            query = query.Where(sample => sample.Status == statusValue);
        }

        var normalizedSearch = NormalizeOptional(search);
        if (normalizedSearch is not null)
        {
            query = query.Where(sample =>
                sample.Barcode.Contains(normalizedSearch) ||
                sample.SampleBatchId.Contains(normalizedSearch) ||
                (sample.MaterialCode != null && sample.MaterialCode.Contains(normalizedSearch)));
        }

        var rows = await query
            .OrderByDescending(sample => sample.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
        return await MapSamplesAsync(rows, cancellationToken);
    }

    public async Task<IReadOnlyList<MaterialLotInventory>> ListInventoryAsync(
        string? materialCode,
        string? lotCode,
        string? locationCode,
        bool includeQuarantined,
        CancellationToken cancellationToken)
    {
        var query = from balance in _database.InventoryBalances.AsNoTracking()
                    join lot in _database.MaterialLots.AsNoTracking()
                        on balance.LotId equals lot.LotId
                    join material in _database.MaterialCatalog.AsNoTracking()
                        on lot.MaterialId equals material.MaterialId
                    join location in _database.WarehouseLocations.AsNoTracking()
                        on balance.LocationId equals location.LocationId
                    select new { balance, lot, material, location };

        var normalizedMaterial = NormalizeOptional(materialCode);
        if (normalizedMaterial is not null)
        {
            query = query.Where(item => item.lot.MaterialCode == normalizedMaterial);
        }

        var normalizedLot = NormalizeOptional(lotCode);
        if (normalizedLot is not null)
        {
            query = query.Where(item => item.lot.LotCode == normalizedLot);
        }

        var normalizedLocation = NormalizeOptional(locationCode);
        if (normalizedLocation is not null)
        {
            query = query.Where(item => item.location.LocationCode == normalizedLocation);
        }

        if (!includeQuarantined)
        {
            query = query.Where(item => !item.lot.IsQuarantined);
        }

        var rows = await query
            .OrderBy(item => item.lot.MaterialCode)
            .ThenBy(item => item.lot.LotCode)
            .ThenBy(item => item.location.LocationCode)
            .ToListAsync(cancellationToken);
        return rows.Select(item => ToInventory(item.lot, item.material, item.location, item.balance)).ToList();
    }

    public async Task<MaterialImportPreview> PreviewImportAsync(
        MaterialImportRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequestMetadata(request.RequestId, request.Actor);
        var issues = new List<MaterialImportIssue>();
        var existingCount = 0;
        var acceptedCount = 0;

        var sampleBarcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var importBarcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < request.Samples.Count; index++)
        {
            var row = request.Samples[index];
            var rowNumber = row.RowNumber > 0 ? row.RowNumber : index + 1;
            var barcode = NormalizeOptional(row.Barcode);
            var batch = NormalizeOptional(row.SampleBatchId);
            if (barcode is null)
            {
                issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.SampleBarcodeRequired, "样品条码不能为空。", nameof(row.Barcode)));
                continue;
            }

            if (batch is null)
            {
                issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.ImportRowInvalid, "样品批次不能为空。", nameof(row.SampleBatchId)));
                continue;
            }

            if (!sampleBarcodes.Add(barcode))
            {
                issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.SampleAlreadyExists, "导入数据中存在重复样品条码。", nameof(row.Barcode)));
                continue;
            }
            if (!importBarcodes.Add(barcode))
            {
                issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.BarcodeAlreadyExists, "導入資料中存在跨類型重複條碼。", nameof(row.Barcode)));
                continue;
            }

            if (!await LocationExistsAsync(row.LocationCode, cancellationToken))
            {
                issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.LocationNotFound, "样品库位不存在或已停用。", nameof(row.LocationCode)));
                continue;
            }

            var existing = await _database.SampleMaterials
                .AsNoTracking()
                .SingleOrDefaultAsync(sample => sample.Barcode.ToUpper() == barcode, cancellationToken);
            if (existing is not null)
            {
                if (!string.Equals(existing.SampleBatchId, batch, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(existing.MaterialCode, NormalizeOptional(row.MaterialCode), StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.SampleAlreadyExists, "样品条码已存在且资料不一致。", nameof(row.Barcode)));
                }
                else
                {
                    existingCount++;
                }

                continue;
            }

            if (await _database.MaterialLots.AsNoTracking().AnyAsync(lot => lot.Barcode != null && lot.Barcode.ToUpper() == barcode, cancellationToken))
            {
                issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.BarcodeAlreadyExists, "條碼已被耗材批次使用。", nameof(row.Barcode)));
                continue;
            }

            acceptedCount++;
        }

        var lotKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < request.Consumables.Count; index++)
        {
            var row = request.Consumables[index];
            var rowNumber = row.RowNumber > 0 ? row.RowNumber : index + 1;
            var materialCode = NormalizeOptional(row.MaterialCode);
            var lotCode = NormalizeOptional(row.LotCode);
            if (materialCode is null)
            {
                issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.MaterialCodeRequired, "耗材编码不能为空。", nameof(row.MaterialCode)));
                continue;
            }

            if (NormalizeOptional(row.Name) is null || lotCode is null)
            {
                issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.ImportRowInvalid, "耗材名称和批号不能为空。", nameof(row.LotCode)));
                continue;
            }

            if (row.Quantity <= 0)
            {
                issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.InvalidQuantity, "入库数量必须大于 0。", nameof(row.Quantity)));
                continue;
            }

            if (row.ExpiryDate is not null && row.ExpiryDate <= Now())
            {
                issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.Expired, "有效期已过，不能导入。", nameof(row.ExpiryDate)));
                continue;
            }

            var lotKey = materialCode + "\u001f" + lotCode;
            if (!lotKeys.Add(lotKey))
            {
                issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.LotAlreadyExists, "导入数据中存在重复物料批号。", nameof(row.LotCode)));
                continue;
            }

            var lotBarcode = NormalizeOptional(row.Barcode);
            if (lotBarcode is not null)
            {
                if (!importBarcodes.Add(lotBarcode))
                {
                    issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.BarcodeAlreadyExists, "導入資料中存在跨類型重複條碼。", nameof(row.Barcode)));
                    continue;
                }
                if (await _database.SampleMaterials.AnyAsync(item => item.Barcode.ToUpper() == lotBarcode, cancellationToken))
                {
                    issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.BarcodeAlreadyExists, "條碼已被樣品使用。", nameof(row.Barcode)));
                    continue;
                }
                if (await _database.MaterialLots.AnyAsync(item => item.Barcode != null && item.Barcode.ToUpper() == lotBarcode && !(item.MaterialCode.ToUpper() == materialCode && item.LotCode.ToUpper() == lotCode), cancellationToken))
                {
                    issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.BarcodeAlreadyExists, "條碼已被其他耗材批次使用。", nameof(row.Barcode)));
                    continue;
                }
            }

            if (!await LocationExistsAsync(row.LocationCode, cancellationToken))
            {
                issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.LocationNotFound, "耗材库位不存在或已停用。", nameof(row.LocationCode)));
                continue;
            }

            var existingLot = await _database.MaterialLots
                .AsNoTracking()
                .SingleOrDefaultAsync(lot => lot.MaterialCode.ToUpper() == materialCode && lot.LotCode.ToUpper() == lotCode, cancellationToken);
            var existingCatalog = await _database.MaterialCatalog.AsNoTracking()
                .SingleOrDefaultAsync(item => item.MaterialCode.ToUpper() == materialCode, cancellationToken);
            if (existingCatalog is not null && !existingCatalog.IsEnabled)
            {
                issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.MaterialDisabled, "物料已停用，不能导入库存。", nameof(row.MaterialCode)));
                continue;
            }
            if (existingLot is not null)
            {
                if (existingLot.IsQuarantined)
                {
                    issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.Quarantined, "耗材批次已隔離，不能增加庫存。", nameof(row.LotCode)));
                    continue;
                }
                if (existingLot.ExpiryDateUtc is not null && existingLot.ExpiryDateUtc <= Now())
                {
                    issues.Add(ImportIssue(rowNumber, MaterialIssueCodes.Expired, "耗材批次已過期，不能增加庫存。", nameof(row.LotCode)));
                    continue;
                }
                existingCount++;
            }

            acceptedCount++;
        }

        return new MaterialImportPreview
        {
            RequestId = request.RequestId,
            AcceptedCount = acceptedCount,
            ExistingCount = existingCount,
            Issues = issues
        };
    }

    public async Task<MaterialCommandResult<MaterialImportResult>> ImportAsync(
        MaterialImportRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequestMetadata(request.RequestId, request.Actor);
        var preview = await PreviewImportAsync(request, cancellationToken);
        if (!preview.CanCommit)
        {
            throw new MaterialManagementException(
                MaterialIssueCodes.ImportRowInvalid,
                "物料导入校验未通过。",
                StatusCodes.Status422UnprocessableEntity,
                preview.Issues);
        }

        var fingerprint = CreateFingerprint("import", request);
        var execution = await _operations.ExecuteAsync(
            _database,
            request.RequestId,
            "import",
            fingerprint,
            request.Actor,
            async () => await ImportCoreAsync(request, cancellationToken),
            Serialize,
            Deserialize<MaterialImportResult>,
            cancellationToken);
        return new MaterialCommandResult<MaterialImportResult>
        {
            RequestId = request.RequestId,
            Data = execution.Value,
            IsIdempotentReplay = execution.IsReplay
        };
    }

    public async Task<MaterialScanResult> ScanAsync(
        MaterialScanRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequestMetadata(request.RequestId, request.Actor);
        var rawCode = request.RawCode?.Trim();
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            throw new MaterialManagementException(
                MaterialIssueCodes.BarcodeUnknown,
                "条码不能为空。",
                StatusCodes.Status400BadRequest);
        }

        var fingerprint = CreateFingerprint("scan", request);
        var execution = await _operations.ExecuteAsync(
            _database,
            request.RequestId,
            "scan",
            fingerprint,
            request.Actor,
            async () => await ScanCoreAsync(request, rawCode!, cancellationToken),
            Serialize,
            Deserialize<MaterialScanResult>,
            cancellationToken);
        return execution.Value with { IsIdempotentReplay = execution.IsReplay };
    }

    public async Task<MaterialCommandResult<MaterialLotInventory>> ReceiveAsync(
        ReceiveMaterialRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequestMetadata(request.RequestId, request.Actor);
        var fingerprint = CreateFingerprint("receive", request);
        var execution = await _operations.ExecuteAsync(
            _database,
            request.RequestId,
            "receive",
            fingerprint,
            request.Actor,
            async () => await ReceiveCoreAsync(request, cancellationToken),
            Serialize,
            Deserialize<MaterialLotInventory>,
            cancellationToken);
        return new MaterialCommandResult<MaterialLotInventory>
        {
            RequestId = request.RequestId,
            Data = execution.Value,
            IsIdempotentReplay = execution.IsReplay
        };
    }

    public async Task<MaterialCommandResult<MaterialLotInventory>> MoveAsync(
        MoveMaterialRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequestMetadata(request.RequestId, request.Actor);
        var fingerprint = CreateFingerprint("move", request);
        var execution = await _operations.ExecuteAsync(
            _database,
            request.RequestId,
            "move",
            fingerprint,
            request.Actor,
            async () => await MoveCoreAsync(request, cancellationToken),
            Serialize,
            Deserialize<MaterialLotInventory>,
            cancellationToken);
        return new MaterialCommandResult<MaterialLotInventory>
        {
            RequestId = request.RequestId,
            Data = execution.Value,
            IsIdempotentReplay = execution.IsReplay
        };
    }

    public async Task<MaterialCommandResult<MaterialLotInventory>> AdjustAsync(
        AdjustMaterialRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequestMetadata(request.RequestId, request.Actor);
        var fingerprint = CreateFingerprint("adjust", request);
        var execution = await _operations.ExecuteAsync(
            _database,
            request.RequestId,
            "adjust",
            fingerprint,
            request.Actor,
            async () => await AdjustCoreAsync(request, cancellationToken),
            Serialize,
            Deserialize<MaterialLotInventory>,
            cancellationToken);
        return new MaterialCommandResult<MaterialLotInventory>
        {
            RequestId = request.RequestId,
            Data = execution.Value,
            IsIdempotentReplay = execution.IsReplay
        };
    }

    public async Task<IReadOnlyList<MaterialTraceEvent>> TraceAsync(
        string? barcode,
        string? materialCode,
        string? lotCode,
        Guid? experimentJobId,
        int limit,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit <= 0 ? 200 : limit, 1, 500);
        var normalizedBarcode = NormalizeOptional(barcode);
        var normalizedMaterial = NormalizeOptional(materialCode);
        var normalizedLot = NormalizeOptional(lotCode);

        var transactions = await _database.InventoryTransactions
            .AsNoTracking()
            .Where(item =>
                (normalizedBarcode == null || item.Barcode != null && item.Barcode.ToUpper() == normalizedBarcode) &&
                (normalizedMaterial == null || item.MaterialCode != null && item.MaterialCode.ToUpper() == normalizedMaterial) &&
                (normalizedLot == null || item.LotCode != null && item.LotCode.ToUpper() == normalizedLot) &&
                (experimentJobId == null || item.ExperimentJobId == experimentJobId))
            .OrderByDescending(item => item.OccurredAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var scanQuery = _database.BarcodeScanEvents.AsNoTracking().AsQueryable();
        if (normalizedBarcode is not null)
            scanQuery = scanQuery.Where(item => item.NormalizedCode.ToUpper() == normalizedBarcode);
        if (experimentJobId is not null)
            scanQuery = scanQuery.Where(_ => false);
        if (normalizedMaterial is not null || normalizedLot is not null)
        {
            scanQuery = scanQuery.Where(item =>
                item.LotId != null && _database.MaterialLots.Any(lot => lot.LotId == item.LotId &&
                    (normalizedMaterial == null || lot.MaterialCode.ToUpper() == normalizedMaterial) &&
                    (normalizedLot == null || lot.LotCode.ToUpper() == normalizedLot)) ||
                item.SampleId != null && normalizedLot == null && _database.SampleMaterials.Any(sample => sample.SampleId == item.SampleId &&
                    (normalizedMaterial == null || sample.MaterialCode != null && sample.MaterialCode.ToUpper() == normalizedMaterial)));
        }
        var scans = await scanQuery
            .OrderByDescending(item => item.OccurredAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var bindings = await _database.ExperimentJobMaterialBindings
            .AsNoTracking()
            .Where(item =>
                (experimentJobId == null || item.ExperimentJobId == experimentJobId) &&
                (normalizedBarcode == null ||
                    (item.LotId != null && _database.MaterialLots.Any(lot => lot.LotId == item.LotId && lot.Barcode != null && lot.Barcode.ToUpper() == normalizedBarcode)) ||
                    (item.SampleId != null && _database.SampleMaterials.Any(sample => sample.SampleId == item.SampleId && sample.Barcode.ToUpper() == normalizedBarcode))) &&
                (normalizedMaterial == null || item.LotId != null &&
                    _database.MaterialLots.Any(lot => lot.LotId == item.LotId && lot.MaterialCode.ToUpper() == normalizedMaterial)) &&
                (normalizedLot == null || item.LotId != null &&
                    _database.MaterialLots.Any(lot => lot.LotId == item.LotId && lot.LotCode.ToUpper() == normalizedLot)))
            .OrderByDescending(item => item.UpdatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var lotIds = bindings.Where(item => item.LotId is not null).Select(item => item.LotId!.Value).Distinct().ToArray();
        var sampleIds = bindings.Where(item => item.SampleId is not null).Select(item => item.SampleId!.Value).Distinct().ToArray();
        var bindingLots = await _database.MaterialLots.AsNoTracking()
            .Where(item => lotIds.Contains(item.LotId)).ToDictionaryAsync(item => item.LotId, cancellationToken);
        var bindingSamples = await _database.SampleMaterials.AsNoTracking()
            .Where(item => sampleIds.Contains(item.SampleId)).ToDictionaryAsync(item => item.SampleId, cancellationToken);

        var result = new List<MaterialTraceEvent>(transactions.Count + scans.Count + bindings.Count);
        result.AddRange(transactions.Select(item => new MaterialTraceEvent
        {
            Id = item.Id,
            TransactionKind = ParseNullableEnum<InventoryTransactionKind>(item.TransactionKind),
            EventType = "inventory." + item.TransactionKind.ToLowerInvariant(),
            Barcode = item.Barcode,
            MaterialCode = item.MaterialCode,
            LotCode = item.LotCode,
            Quantity = item.Quantity,
            Unit = item.Unit,
            ExperimentJobId = item.ExperimentJobId,
            Actor = item.Actor,
            Reason = item.Reason,
            OccurredAt = ToOffset(item.OccurredAtUtc)
        }));
        result.AddRange(scans.Select(item => new MaterialTraceEvent
        {
            Id = item.Id,
            EventType = "scan." + item.Outcome.ToLowerInvariant(),
            Barcode = item.NormalizedCode,
            Actor = item.Actor,
            Reason = item.IssueCode,
            OccurredAt = ToOffset(item.OccurredAtUtc)
        }));
        result.AddRange(bindings.Select(item =>
        {
            bindingLots.TryGetValue(item.LotId ?? Guid.Empty, out var bindingLot);
            bindingSamples.TryGetValue(item.SampleId ?? Guid.Empty, out var bindingSample);
            return new MaterialTraceEvent
            {
                Id = item.BindingId,
                EventType = "binding." + item.Status.ToLowerInvariant(),
                Barcode = bindingLot?.Barcode ?? bindingSample?.Barcode,
                MaterialCode = bindingLot?.MaterialCode ?? bindingSample?.MaterialCode,
                LotCode = bindingLot?.LotCode,
                Quantity = item.Quantity,
                Unit = item.Unit,
                ExperimentJobId = item.ExperimentJobId,
                Actor = item.Actor,
                Reason = item.Reason,
                OccurredAt = ToOffset(item.UpdatedAtUtc)
            };
        }));

        return result
            .OrderByDescending(item => item.OccurredAt)
            .Take(limit)
            .ToList();
    }

    public async Task<MaterialReservationResult> ReserveAsync(
        ReserveExperimentMaterialsRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequestMetadata(request.RequestId, request.Actor);
        var fingerprint = CreateFingerprint("reserve", request);
        var execution = await _operations.ExecuteAsync(
            _database,
            request.RequestId,
            "reserve",
            fingerprint,
            request.Actor,
            async () => await ReserveCoreAsync(request, cancellationToken),
            Serialize,
            Deserialize<MaterialReservationResult>,
            cancellationToken);
        return execution.Value with { IsIdempotentReplay = execution.IsReplay };
    }

    public async Task<MaterialReleaseResult> ReleaseAsync(
        ReleaseExperimentMaterialsRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequestMetadata(request.RequestId, request.Actor);
        var fingerprint = CreateFingerprint("release", request);
        var execution = await _operations.ExecuteAsync(
            _database,
            request.RequestId,
            "release",
            fingerprint,
            request.Actor,
            async () => await ReleaseCoreAsync(request, cancellationToken),
            Serialize,
            Deserialize<MaterialReleaseResult>,
            cancellationToken);
        return execution.Value with { IsIdempotentReplay = execution.IsReplay };
    }

    public async Task<MaterialConsumeResult> ConsumeAsync(
        ConsumeExperimentMaterialsRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequestMetadata(request.RequestId, request.Actor);
        var fingerprint = CreateFingerprint("consume", request);
        var execution = await _operations.ExecuteAsync(
            _database,
            request.RequestId,
            "consume",
            fingerprint,
            request.Actor,
            async () => await ConsumeCoreAsync(request, cancellationToken),
            Serialize,
            Deserialize<MaterialConsumeResult>,
            cancellationToken);
        return execution.Value with { IsIdempotentReplay = execution.IsReplay };
    }

    public async Task<IReadOnlyList<MaterialBinding>> ListBindingsAsync(
        Guid experimentJobId,
        CancellationToken cancellationToken)
    {
        if (experimentJobId == Guid.Empty) throw new ArgumentException("实验任务编号不能为空。", nameof(experimentJobId));
        var records = await _database.ExperimentJobMaterialBindings
            .AsNoTracking()
            .Where(binding => binding.ExperimentJobId == experimentJobId)
            .OrderBy(binding => binding.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        return records.Select(ToBinding).ToList();
    }

    private async Task<MaterialImportResult> ImportCoreAsync(
        MaterialImportRequest request,
        CancellationToken cancellationToken)
    {
        var now = NowUtc();
        var importedSamples = 0;
        var importedLots = 0;
        var existingCount = 0;

        foreach (var row in request.Samples)
        {
            var barcode = Normalize(row.Barcode);
            var existing = await _database.SampleMaterials
                .SingleOrDefaultAsync(sample => sample.Barcode == barcode, cancellationToken);
            if (existing is not null)
            {
                existingCount++;
                continue;
            }

            if (await _database.MaterialLots.AnyAsync(lot => lot.Barcode == barcode, cancellationToken))
                throw Issue(MaterialIssueCodes.BarcodeAlreadyExists, "條碼已被耗材批次使用。", StatusCodes.Status409Conflict);

            var location = await RequireLocationAsync(row.LocationCode, cancellationToken);
            var sample = new SampleMaterialRecord
            {
                SampleId = Guid.NewGuid(),
                Barcode = barcode,
                SampleBatchId = Normalize(row.SampleBatchId),
                MaterialCode = NormalizeOptional(row.MaterialCode),
                SampleType = NormalizeOptional(row.SampleType),
                Status = SampleLifecycleStatus.Available.ToString(),
                LocationId = location.LocationId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _database.SampleMaterials.Add(sample);
            _database.InventoryTransactions.Add(new InventoryTransactionRecord
            {
                Id = Guid.NewGuid(),
                RequestId = request.RequestId,
                LineKey = "sample:" + barcode,
                TransactionKind = InventoryTransactionKind.Receipt.ToString(),
                SampleId = sample.SampleId,
                ToLocationId = location.LocationId,
                Barcode = barcode,
                Quantity = 1m,
                Unit = "EA",
                Actor = request.Actor.Trim(),
                Reason = "sample-import",
                DetailsJson = Serialize(new { source = request.Source }),
                OccurredAtUtc = now
            });
            importedSamples++;
        }

        foreach (var row in request.Consumables)
        {
            var materialCode = Normalize(row.MaterialCode);
            var lotCode = Normalize(row.LotCode);
            var location = await RequireLocationAsync(row.LocationCode, cancellationToken);
            var catalog = await GetOrCreateCatalogAsync(
                materialCode,
                row.Name,
                MaterialKind.Consumable,
                row.Specification,
                row.Unit,
                now,
                cancellationToken);
            var lot = await _database.MaterialLots
                .SingleOrDefaultAsync(item => item.MaterialCode == materialCode && item.LotCode == lotCode, cancellationToken);
            if (lot is null)
            {
                lot = new MaterialLotRecord
                {
                    LotId = Guid.NewGuid(),
                    MaterialId = catalog.MaterialId,
                    MaterialCode = materialCode,
                    LotCode = lotCode,
                    Barcode = NormalizeOptional(row.Barcode),
                    Specification = NormalizeOptional(row.Specification) ?? catalog.Specification,
                    Unit = NormalizeOptional(row.Unit) ?? catalog.Unit,
                    ManufactureDateUtc = row.ManufactureDate?.UtcDateTime,
                    ExpiryDateUtc = row.ExpiryDate?.UtcDateTime,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                await ValidateLotBarcodeConflictAsync(lot.Barcode, null, cancellationToken);
                await ValidateCrossTypeBarcodeConflictAsync(lot.Barcode, cancellationToken);
                _database.MaterialLots.Add(lot);
                importedLots++;
            }
            else
            {
                existingCount++;
                EnsureLotCompatible(lot, row.Barcode, row.ExpiryDate);
                EnsureLotUsableForMutation(lot, now, catalog);
                await ValidateCrossTypeBarcodeConflictAsync(lot.Barcode, cancellationToken);
                if (lot.Barcode is null && NormalizeOptional(row.Barcode) is { } suppliedBarcode)
                {
                    await ValidateLotBarcodeConflictAsync(suppliedBarcode, lot.LotId, cancellationToken);
                    await ValidateCrossTypeBarcodeConflictAsync(suppliedBarcode, cancellationToken);
                    lot.Barcode = suppliedBarcode;
                }
            }

            var balance = await GetOrCreateBalanceAsync(lot, location, now, cancellationToken);
            balance.OnHand += row.Quantity;
            balance.UpdatedAtUtc = now;
            _database.InventoryTransactions.Add(new InventoryTransactionRecord
            {
                Id = Guid.NewGuid(),
                RequestId = request.RequestId,
                LineKey = "lot:" + materialCode + ":" + lotCode,
                TransactionKind = InventoryTransactionKind.Receipt.ToString(),
                LotId = lot.LotId,
                FromLocationId = null,
                ToLocationId = location.LocationId,
                MaterialCode = materialCode,
                LotCode = lotCode,
                Barcode = lot.Barcode,
                Quantity = row.Quantity,
                Unit = lot.Unit,
                Actor = request.Actor.Trim(),
                Reason = "consumable-import",
                DetailsJson = Serialize(new { source = request.Source }),
                OccurredAtUtc = now
            });
        }

        return new MaterialImportResult
        {
            RequestId = request.RequestId,
            ImportedSamples = importedSamples,
            ImportedConsumableLots = importedLots,
            ExistingCount = existingCount
        };
    }

    private async Task<MaterialScanResult> ScanCoreAsync(
        MaterialScanRequest request,
        string rawCode,
        CancellationToken cancellationToken)
    {
        var normalizedCode = Normalize(rawCode);
        var samples = await _database.SampleMaterials
            .Where(item => item.Barcode.ToUpper() == normalizedCode)
            .Take(2)
            .ToListAsync(cancellationToken);
        var lots = await _database.MaterialLots
            .Where(item => item.Barcode != null && item.Barcode.ToUpper() == normalizedCode || item.LotCode.ToUpper() == normalizedCode)
            .Take(3)
            .ToListAsync(cancellationToken);
        var duplicate = samples.Count > 1 || lots.Count > 1 || samples.Count > 0 && lots.Count > 0;
        var sample = samples.Count == 1 ? samples[0] : null;
        var lot = lots.Count == 1 ? lots[0] : null;
        var requestedKind = request.Kind;
        var actualKind = sample is not null ? MaterialScanKind.Sample : lot is not null ? MaterialScanKind.ConsumableLot : MaterialScanKind.Unknown;
        var issueCode = (string?)null;
        var message = (string?)null;
        var resolved = true;

        if (duplicate)
        {
            resolved = false;
            issueCode = MaterialIssueCodes.BarcodeAlreadyExists;
            message = "條碼同時匹配樣品和耗材批次，無法解析。";
        }
        else if (requestedKind != MaterialScanKind.Unknown && actualKind != MaterialScanKind.Unknown && requestedKind != actualKind)
        {
            resolved = false;
            issueCode = MaterialIssueCodes.BarcodeKindMismatch;
            message = "条码类型与请求类型不一致。";
        }
        else if (actualKind == MaterialScanKind.Unknown)
        {
            resolved = false;
            issueCode = MaterialIssueCodes.BarcodeUnknown;
            message = "未找到对应样品或耗材批次。";
        }

        if (resolved && sample is not null && ParseEnum(sample.Status, SampleLifecycleStatus.Unknown) == SampleLifecycleStatus.Expired)
        {
            resolved = false;
            issueCode = MaterialIssueCodes.Expired;
            message = "样品已过期。";
        }

        if (resolved && lot is not null)
        {
            var now = NowUtc();
            if (lot.IsQuarantined)
            {
                resolved = false;
                issueCode = MaterialIssueCodes.Quarantined;
                message = "耗材批次已隔離。";
            }
            else if (lot.ExpiryDateUtc is not null && lot.ExpiryDateUtc <= now)
            {
                resolved = false;
                issueCode = MaterialIssueCodes.Expired;
                message = "耗材批次已過期。";
            }
        }

        var locationById = await _database.WarehouseLocations
            .AsNoTracking()
            .ToDictionaryAsync(item => item.LocationId, cancellationToken);
        var catalogByCode = await _database.MaterialCatalog
            .AsNoTracking()
            .ToDictionaryAsync(item => item.MaterialCode, StringComparer.OrdinalIgnoreCase, cancellationToken);
        if (resolved && lot is not null && catalogByCode.TryGetValue(lot.MaterialCode, out var lotCatalog) && !lotCatalog.IsEnabled)
        {
            resolved = false;
            issueCode = MaterialIssueCodes.MaterialDisabled;
            message = "物料已停用。";
        }
        var mappedSample = sample is null ? null : ToSample(sample, locationById);
        var mappedLot = lot is null
            ? null
            : ToInventory(
                lot,
                catalogByCode.GetValueOrDefault(lot.MaterialCode),
                await FindPrimaryLocationAsync(lot.LotId, cancellationToken),
                await FindPrimaryBalanceAsync(lot.LotId, cancellationToken));
        var scan = new BarcodeScanEventRecord
        {
            Id = Guid.NewGuid(),
            RequestId = request.RequestId,
            RawCode = rawCode,
            NormalizedCode = normalizedCode,
            ScanKind = (requestedKind == MaterialScanKind.Unknown ? actualKind : requestedKind).ToString(),
            Source = NormalizeOptional(request.Source) ?? "manual",
            Outcome = resolved ? "Resolved" : "Rejected",
            IssueCode = issueCode,
            SampleId = sample?.SampleId,
            LotId = lot?.LotId,
            Actor = request.Actor.Trim(),
            DetailsJson = Serialize(new { message }),
            OccurredAtUtc = NowUtc()
        };
        _database.BarcodeScanEvents.Add(scan);

        return new MaterialScanResult
        {
            ScanId = scan.Id,
            RawCode = rawCode,
            NormalizedCode = normalizedCode,
            Kind = requestedKind == MaterialScanKind.Unknown ? actualKind : requestedKind,
            IsResolved = resolved,
            IssueCode = issueCode,
            Message = message,
            Sample = mappedSample,
            Lot = mappedLot,
            AllowedActions = resolved ? AllowedActions(actualKind, sample, lot) : Array.Empty<string>()
        };
    }

    private async Task<MaterialLotInventory> ReceiveCoreAsync(
        ReceiveMaterialRequest request,
        CancellationToken cancellationToken)
    {
        ValidatePositive(request.Quantity);
        var materialCode = Normalize(request.MaterialCode);
        var lotCode = Normalize(request.LotCode);
        var location = await RequireLocationAsync(request.LocationCode, cancellationToken);
        var now = NowUtc();
        if (request.ExpiryDate is not null && request.ExpiryDate.Value.UtcDateTime <= now)
        {
            throw Issue(MaterialIssueCodes.Expired, "耗材批次已过期，不能入库。", StatusCodes.Status409Conflict);
        }
        var catalog = await GetOrCreateCatalogAsync(
            materialCode,
            request.MaterialName,
            MaterialKind.Consumable,
            request.Specification,
            request.Unit,
            now,
            cancellationToken);
        var lot = await _database.MaterialLots
            .SingleOrDefaultAsync(item => item.MaterialCode == materialCode && item.LotCode == lotCode, cancellationToken);
        if (lot is null)
        {
            lot = new MaterialLotRecord
            {
                LotId = Guid.NewGuid(),
                MaterialId = catalog.MaterialId,
                MaterialCode = materialCode,
                LotCode = lotCode,
                Barcode = NormalizeOptional(request.Barcode),
                Specification = NormalizeOptional(request.Specification) ?? catalog.Specification,
                Unit = NormalizeOptional(request.Unit) ?? catalog.Unit,
                ManufactureDateUtc = request.ManufactureDate?.UtcDateTime,
                ExpiryDateUtc = request.ExpiryDate?.UtcDateTime,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            await ValidateLotBarcodeConflictAsync(lot.Barcode, null, cancellationToken);
            await ValidateCrossTypeBarcodeConflictAsync(lot.Barcode, cancellationToken);
            _database.MaterialLots.Add(lot);
        }
        else
        {
            EnsureLotCompatible(lot, request.Barcode, request.ExpiryDate);
            await ValidateCrossTypeBarcodeConflictAsync(lot.Barcode, cancellationToken);
            if (lot.Barcode is null && NormalizeOptional(request.Barcode) is { } suppliedBarcode)
            {
                await ValidateLotBarcodeConflictAsync(suppliedBarcode, lot.LotId, cancellationToken);
                await ValidateCrossTypeBarcodeConflictAsync(suppliedBarcode, cancellationToken);
                lot.Barcode = suppliedBarcode;
            }
            if (lot.IsQuarantined) throw Issue(MaterialIssueCodes.Quarantined, "耗材批次已隔离。", StatusCodes.Status409Conflict);
            if (lot.ExpiryDateUtc is not null && lot.ExpiryDateUtc <= now) throw Issue(MaterialIssueCodes.Expired, "耗材批次已过期。", StatusCodes.Status409Conflict);
        }

        var balance = await GetOrCreateBalanceAsync(lot, location, now, cancellationToken);
        balance.OnHand += request.Quantity;
        balance.UpdatedAtUtc = now;
        _database.InventoryTransactions.Add(new InventoryTransactionRecord
        {
            Id = Guid.NewGuid(),
            RequestId = request.RequestId,
            LineKey = "receipt:" + lot.LotId,
            TransactionKind = InventoryTransactionKind.Receipt.ToString(),
            LotId = lot.LotId,
            ToLocationId = location.LocationId,
            MaterialCode = materialCode,
            LotCode = lotCode,
            Barcode = lot.Barcode,
            Quantity = request.Quantity,
            Unit = lot.Unit,
            Actor = request.Actor.Trim(),
            Reason = request.Reason,
            DetailsJson = "{}",
            OccurredAtUtc = now
        });
        return await ProjectInventoryAsync(lot, catalog, location, balance, cancellationToken);
    }

    private async Task<MaterialLotInventory> MoveCoreAsync(
        MoveMaterialRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LotId == Guid.Empty) throw Issue(MaterialIssueCodes.LotNotFound, "耗材批次不能为空。", StatusCodes.Status400BadRequest);
        var lot = await _database.MaterialLots.SingleOrDefaultAsync(item => item.LotId == request.LotId, cancellationToken)
            ?? throw Issue(MaterialIssueCodes.LotNotFound, "耗材批次不存在。", StatusCodes.Status404NotFound);
        var catalog = await _database.MaterialCatalog.SingleAsync(item => item.MaterialId == lot.MaterialId, cancellationToken);
        EnsureLotUsableForMutation(lot, NowUtc(), catalog);
        var source = await ResolveBalanceLocationAsync(lot.LotId, request.FromLocationCode, cancellationToken);
        var target = await RequireLocationAsync(request.ToLocationCode, cancellationToken);
        if (source.Location.LocationId == target.LocationId)
        {
            throw Issue(MaterialIssueCodes.InventoryStateInvalid, "源库位和目标库位不能相同。", StatusCodes.Status409Conflict);
        }

        var quantity = request.Quantity ?? source.Balance.OnHand;
        ValidatePositive(quantity);
        if (quantity > source.Balance.OnHand || quantity < source.Balance.Reserved)
        {
            throw Issue(MaterialIssueCodes.InventoryInsufficient, "可移动库存不足。", StatusCodes.Status409Conflict);
        }

        var now = NowUtc();
        var destinationBalance = await GetOrCreateBalanceAsync(lot, target, now, cancellationToken);
        source.Balance.OnHand -= quantity;
        destinationBalance.OnHand += quantity;
        var reservedMove = source.Balance.Reserved * (quantity / (source.Balance.OnHand + quantity));
        source.Balance.Reserved -= reservedMove;
        destinationBalance.Reserved += reservedMove;
        source.Balance.UpdatedAtUtc = now;
        destinationBalance.UpdatedAtUtc = now;
        _database.InventoryTransactions.Add(new InventoryTransactionRecord
        {
            Id = Guid.NewGuid(),
            RequestId = request.RequestId,
            LineKey = "move:" + lot.LotId + ":" + source.Location.LocationId + ":" + target.LocationId,
            TransactionKind = InventoryTransactionKind.Move.ToString(),
            LotId = lot.LotId,
            FromLocationId = source.Location.LocationId,
            ToLocationId = target.LocationId,
            MaterialCode = lot.MaterialCode,
            LotCode = lot.LotCode,
            Barcode = lot.Barcode,
            Quantity = quantity,
            Unit = lot.Unit,
            Actor = request.Actor.Trim(),
            Reason = request.Reason,
            DetailsJson = Serialize(new { reserved = reservedMove }),
            OccurredAtUtc = now
        });
        return await ProjectInventoryAsync(lot, catalog, target, destinationBalance, cancellationToken);
    }

    private async Task<MaterialLotInventory> AdjustCoreAsync(
        AdjustMaterialRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw Issue(MaterialIssueCodes.ImportRowInvalid, "库存调整必须填写原因。", StatusCodes.Status400BadRequest);
        var lot = await _database.MaterialLots.SingleOrDefaultAsync(item => item.LotId == request.LotId, cancellationToken)
            ?? throw Issue(MaterialIssueCodes.LotNotFound, "耗材批次不存在。", StatusCodes.Status404NotFound);
        var catalog = await _database.MaterialCatalog.SingleAsync(item => item.MaterialId == lot.MaterialId, cancellationToken);
        EnsureLotUsableForMutation(lot, NowUtc(), catalog);
        var selected = await ResolveBalanceLocationAsync(lot.LotId, request.LocationCode, cancellationToken);
        var next = selected.Balance.OnHand + request.QuantityDelta;
        if (next < 0 || next < selected.Balance.Reserved)
            throw Issue(MaterialIssueCodes.InventoryInsufficient, "调整后库存不能低于已预占数量。", StatusCodes.Status409Conflict);
        var now = NowUtc();
        selected.Balance.OnHand = next;
        selected.Balance.UpdatedAtUtc = now;
        _database.InventoryTransactions.Add(new InventoryTransactionRecord
        {
            Id = Guid.NewGuid(),
            RequestId = request.RequestId,
            LineKey = "adjust:" + lot.LotId + ":" + selected.Location.LocationId,
            TransactionKind = InventoryTransactionKind.Adjustment.ToString(),
            LotId = lot.LotId,
            ToLocationId = selected.Location.LocationId,
            MaterialCode = lot.MaterialCode,
            LotCode = lot.LotCode,
            Barcode = lot.Barcode,
            Quantity = request.QuantityDelta,
            Unit = lot.Unit,
            Actor = request.Actor.Trim(),
            Reason = request.Reason.Trim(),
            DetailsJson = "{}",
            OccurredAtUtc = now
        });
        return await ProjectInventoryAsync(lot, catalog, selected.Location, selected.Balance, cancellationToken);
    }

    private async Task<MaterialReservationResult> ReserveCoreAsync(
        ReserveExperimentMaterialsRequest request,
        CancellationToken cancellationToken)
    {
        var job = await _database.ExperimentJobs.SingleOrDefaultAsync(item => item.JobId == request.ExperimentJobId, cancellationToken)
            ?? throw Issue(MaterialIssueCodes.BindingConflict, "实验任务不存在。", StatusCodes.Status404NotFound);
        if (request.Requirements.Count == 0 && string.IsNullOrWhiteSpace(request.SampleBarcode))
            throw Issue(MaterialIssueCodes.BindingConflict, "至少需要一个样品或耗材需求。", StatusCodes.Status400BadRequest);
        if (await _database.ExperimentJobMaterialBindings.AnyAsync(
                item => item.ExperimentJobId == request.ExperimentJobId &&
                        item.Status == MaterialBindingStatus.Reserved.ToString(),
                cancellationToken))
        {
            throw Issue(MaterialIssueCodes.BindingConflict, "该实验任务已经存在有效物料预占。", StatusCodes.Status409Conflict);
        }

        var now = NowUtc();
        var bindings = new List<ExperimentJobMaterialBindingRecord>();
        var lineIndex = 0;
        if (!string.IsNullOrWhiteSpace(request.SampleBarcode))
        {
            var sample = await _database.SampleMaterials
                .SingleOrDefaultAsync(item => item.Barcode == Normalize(request.SampleBarcode), cancellationToken)
                ?? throw Issue(MaterialIssueCodes.SampleNotFound, "样品不存在。", StatusCodes.Status404NotFound);
            var sampleStatus = ParseEnum(sample.Status, SampleLifecycleStatus.Unknown);
            if (sampleStatus != SampleLifecycleStatus.Available)
                throw Issue(MaterialIssueCodes.SampleUnavailable, "样品当前不可预占。", StatusCodes.Status409Conflict);
            sample.Status = SampleLifecycleStatus.Reserved.ToString();
            sample.BoundExperimentJobId = request.ExperimentJobId;
            sample.UpdatedAtUtc = now;
            var binding = new ExperimentJobMaterialBindingRecord
            {
                BindingId = Guid.NewGuid(),
                RequestId = request.RequestId,
                LineKey = "sample:" + (++lineIndex),
                ExperimentJobId = job.JobId,
                SampleId = sample.SampleId,
                Quantity = 1m,
                Unit = "EA",
                Status = MaterialBindingStatus.Reserved.ToString(),
                Actor = request.Actor.Trim(),
                Reason = request.Reason,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _database.ExperimentJobMaterialBindings.Add(binding);
            _database.InventoryTransactions.Add(NewBindingTransaction(
                request,
                binding,
                InventoryTransactionKind.Reserve,
                sample.Barcode,
                null,
                1m,
                "EA",
                null,
                now));
            bindings.Add(binding);
        }

        foreach (var requirement in request.Requirements)
        {
            ValidatePositive(requirement.Quantity);
            var materialCode = Normalize(requirement.MaterialCode);
            if (NormalizeOptional(requirement.LotCode) is { } requestedLotCode)
            {
                var requestedLot = await _database.MaterialLots.SingleOrDefaultAsync(
                    item => item.MaterialCode == materialCode && item.LotCode == requestedLotCode,
                    cancellationToken);
                if (requestedLot is not null)
                {
                    var requestedCatalog = await _database.MaterialCatalog.SingleAsync(item => item.MaterialId == requestedLot.MaterialId, cancellationToken);
                    EnsureLotUsableForMutation(requestedLot, now, requestedCatalog);
                }
            }
            var allocations = await AllocateLotBalancesAsync(
                materialCode,
                requirement.LotCode,
                requirement.Quantity,
                requirement.Unit,
                cancellationToken);
            foreach (var allocation in allocations)
            {
                allocation.Balance.Reserved += allocation.Quantity;
                allocation.Balance.UpdatedAtUtc = now;
                var binding = new ExperimentJobMaterialBindingRecord
                {
                    BindingId = Guid.NewGuid(),
                    RequestId = request.RequestId,
                    LineKey = "material:" + (++lineIndex),
                    ExperimentJobId = job.JobId,
                    LotId = allocation.Lot.LotId,
                    Quantity = allocation.Quantity,
                    Unit = allocation.Lot.Unit,
                    Status = MaterialBindingStatus.Reserved.ToString(),
                    Actor = request.Actor.Trim(),
                    Reason = request.Reason,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                _database.ExperimentJobMaterialBindings.Add(binding);
                _database.InventoryTransactions.Add(NewBindingTransaction(
                    request,
                    binding,
                    InventoryTransactionKind.Reserve,
                    allocation.Lot.Barcode,
                    allocation.Lot,
                    allocation.Quantity,
                    allocation.Lot.Unit,
                    allocation.Location.LocationId,
                    now));
                bindings.Add(binding);
            }
        }

        return new MaterialReservationResult
        {
            RequestId = request.RequestId,
            ExperimentJobId = request.ExperimentJobId,
            Bindings = bindings.Select(ToBinding).ToList()
        };
    }

    private async Task<MaterialReleaseResult> ReleaseCoreAsync(
        ReleaseExperimentMaterialsRequest request,
        CancellationToken cancellationToken)
    {
        _ = await _database.ExperimentJobs.SingleOrDefaultAsync(item => item.JobId == request.ExperimentJobId, cancellationToken)
            ?? throw Issue(MaterialIssueCodes.BindingConflict, "实验任务不存在。", StatusCodes.Status404NotFound);
        var bindings = await _database.ExperimentJobMaterialBindings
            .Where(item => item.ExperimentJobId == request.ExperimentJobId && item.Status == MaterialBindingStatus.Reserved.ToString())
            .ToListAsync(cancellationToken);
        var now = NowUtc();
        foreach (var binding in bindings)
        {
            if (binding.LotId is not null)
            {
                var reservationTransaction = await _database.InventoryTransactions
                    .AsNoTracking()
                    .SingleOrDefaultAsync(item =>
                        item.RequestId == binding.RequestId &&
                        item.LineKey == "reserve:" + binding.LineKey,
                        cancellationToken);
                var balance = await FindBalanceForReservedQuantityAsync(
                        binding.LotId.Value,
                        binding.Quantity,
                        reservationTransaction?.ToLocationId,
                        cancellationToken)
                    ?? throw Issue(MaterialIssueCodes.InventoryStateInvalid, "预占库存记录不完整。", StatusCodes.Status409Conflict);
                balance.Reserved -= binding.Quantity;
                balance.UpdatedAtUtc = now;
                var lot = await _database.MaterialLots.SingleAsync(item => item.LotId == binding.LotId, cancellationToken);
                _database.InventoryTransactions.Add(NewBindingTransaction(
                    request,
                    binding,
                    InventoryTransactionKind.Release,
                    lot.Barcode,
                    lot,
                    binding.Quantity,
                    lot.Unit,
                    balance.LocationId,
                    now));
            }
            else if (binding.SampleId is not null)
            {
                var sample = await _database.SampleMaterials.SingleAsync(item => item.SampleId == binding.SampleId, cancellationToken);
                sample.Status = SampleLifecycleStatus.Available.ToString();
                sample.BoundExperimentJobId = null;
                sample.UpdatedAtUtc = now;
                _database.InventoryTransactions.Add(NewBindingTransaction(
                    request,
                    binding,
                    InventoryTransactionKind.Release,
                    sample.Barcode,
                    null,
                    1m,
                    "EA",
                    null,
                    now));
            }

            binding.Status = MaterialBindingStatus.Released.ToString();
            binding.UpdatedAtUtc = now;
            binding.Reason = request.Reason;
        }

        return new MaterialReleaseResult
        {
            RequestId = request.RequestId,
            ExperimentJobId = request.ExperimentJobId,
            ReleasedCount = bindings.Count
        };
    }

    private async Task<MaterialConsumeResult> ConsumeCoreAsync(
        ConsumeExperimentMaterialsRequest request,
        CancellationToken cancellationToken)
    {
        _ = await _database.ExperimentJobs.SingleOrDefaultAsync(item => item.JobId == request.ExperimentJobId, cancellationToken)
            ?? throw Issue(MaterialIssueCodes.BindingConflict, "实验任务不存在。", StatusCodes.Status404NotFound);
        var bindings = await _database.ExperimentJobMaterialBindings
            .Where(item => item.ExperimentJobId == request.ExperimentJobId && item.Status == MaterialBindingStatus.Reserved.ToString())
            .ToListAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.SampleBarcode))
        {
            var sampleCode = Normalize(request.SampleBarcode);
            var sampleId = await _database.SampleMaterials
                .Where(sample => sample.Barcode == sampleCode)
                .Select(sample => (Guid?)sample.SampleId)
                .SingleOrDefaultAsync(cancellationToken);
            bindings = bindings.Where(item => item.SampleId == sampleId).ToList();
        }

        if (request.Materials.Count > 0)
        {
            var lotIds = bindings
                .Where(item => item.LotId is not null)
                .Select(item => item.LotId!.Value)
                .Distinct()
                .ToList();
            var lots = await _database.MaterialLots
                .Where(lot => lotIds.Contains(lot.LotId))
                .ToDictionaryAsync(lot => lot.LotId, cancellationToken);
            var selected = new List<ExperimentJobMaterialBindingRecord>();
            foreach (var requirement in request.Materials)
            {
                ValidatePositive(requirement.Quantity);
                var materialCode = Normalize(requirement.MaterialCode);
                var lotCode = NormalizeOptional(requirement.LotCode);
                var matches = bindings.Where(binding =>
                    binding.LotId is not null &&
                    lots.TryGetValue(binding.LotId.Value, out var lot) &&
                    string.Equals(lot.MaterialCode, materialCode, StringComparison.OrdinalIgnoreCase) &&
                    (lotCode is null || string.Equals(lot.LotCode, lotCode, StringComparison.OrdinalIgnoreCase))).ToList();
                var matchedQuantity = matches.Sum(binding => binding.Quantity);
                if (matches.Count == 0 || matchedQuantity != requirement.Quantity)
                {
                    throw Issue(MaterialIssueCodes.BindingConflict, "请求的消耗数量与已预占物料不一致。", StatusCodes.Status409Conflict);
                }
                selected.AddRange(matches);
            }

            bindings = selected
                .DistinctBy(binding => binding.BindingId)
                .ToList();
        }
        if (bindings.Count == 0)
            throw Issue(MaterialIssueCodes.BindingConflict, "没有可消耗的预占物料。", StatusCodes.Status409Conflict);

        var now = NowUtc();
        foreach (var binding in bindings)
        {
            if (binding.LotId is not null)
            {
                var reservationTransaction = await _database.InventoryTransactions
                    .AsNoTracking()
                    .SingleOrDefaultAsync(item =>
                        item.RequestId == binding.RequestId &&
                        item.LineKey == "reserve:" + binding.LineKey,
                        cancellationToken);
                var balance = await FindBalanceForReservedQuantityAsync(
                        binding.LotId.Value,
                        binding.Quantity,
                        reservationTransaction?.ToLocationId,
                        cancellationToken)
                    ?? throw Issue(MaterialIssueCodes.InventoryInsufficient, "预占库存不足以完成消耗。", StatusCodes.Status409Conflict);
                balance.OnHand -= binding.Quantity;
                balance.Reserved -= binding.Quantity;
                balance.UpdatedAtUtc = now;
                var lot = await _database.MaterialLots.SingleAsync(item => item.LotId == binding.LotId, cancellationToken);
                _database.InventoryTransactions.Add(NewBindingTransaction(
                    request,
                    binding,
                    InventoryTransactionKind.Consume,
                    lot.Barcode,
                    lot,
                    binding.Quantity,
                    lot.Unit,
                    balance.LocationId,
                    now));
            }
            else if (binding.SampleId is not null)
            {
                var sample = await _database.SampleMaterials.SingleAsync(item => item.SampleId == binding.SampleId, cancellationToken);
                sample.Status = SampleLifecycleStatus.Consumed.ToString();
                sample.BoundExperimentJobId = request.ExperimentJobId;
                sample.UpdatedAtUtc = now;
                _database.InventoryTransactions.Add(NewBindingTransaction(
                    request,
                    binding,
                    InventoryTransactionKind.Consume,
                    sample.Barcode,
                    null,
                    1m,
                    "EA",
                    null,
                    now));
            }

            binding.Status = MaterialBindingStatus.Consumed.ToString();
            binding.InjectionPosition = NormalizeOptional(request.InjectionPosition);
            binding.UpdatedAtUtc = now;
            binding.Reason = request.Reason;
        }

        return new MaterialConsumeResult
        {
            RequestId = request.RequestId,
            ExperimentJobId = request.ExperimentJobId,
            ConsumedCount = bindings.Count
        };
    }

    private async Task<MaterialCatalogRecord> GetOrCreateCatalogAsync(
        string materialCode,
        string name,
        MaterialKind kind,
        string? specification,
        string? unit,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var catalog = await _database.MaterialCatalog
            .SingleOrDefaultAsync(item => item.MaterialCode == materialCode, cancellationToken);
        if (catalog is not null)
        {
            if (!string.Equals(catalog.Kind, kind.ToString(), StringComparison.OrdinalIgnoreCase))
                throw Issue(MaterialIssueCodes.MaterialDisabled, "物料类型不匹配。", StatusCodes.Status409Conflict);
            if (!catalog.IsEnabled)
                throw Issue(MaterialIssueCodes.MaterialDisabled, "物料已停用。", StatusCodes.Status409Conflict);
            if (string.IsNullOrWhiteSpace(catalog.Name) && !string.IsNullOrWhiteSpace(name)) catalog.Name = name.Trim();
            if (catalog.Specification is null) catalog.Specification = NormalizeOptional(specification);
            if (catalog.Unit is null) catalog.Unit = NormalizeOptional(unit);
            catalog.UpdatedAtUtc = now;
            return catalog;
        }

        if (string.IsNullOrWhiteSpace(name))
            throw Issue(MaterialIssueCodes.ImportRowInvalid, "物料名称不能为空。", StatusCodes.Status400BadRequest);
        catalog = new MaterialCatalogRecord
        {
            MaterialId = Guid.NewGuid(),
            MaterialCode = materialCode,
            Name = name.Trim(),
            Kind = kind.ToString(),
            Specification = NormalizeOptional(specification),
            Unit = NormalizeOptional(unit),
            IsEnabled = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _database.MaterialCatalog.Add(catalog);
        return catalog;
    }

    private async Task<WarehouseLocationRecord> RequireLocationAsync(
        string? locationCode,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeOptional(locationCode) ?? "DEFAULT";
        return await _database.WarehouseLocations
            .SingleOrDefaultAsync(item => item.LocationCode == normalized && item.IsEnabled, cancellationToken)
            ?? throw Issue(MaterialIssueCodes.LocationNotFound, "库位不存在或已停用。", StatusCodes.Status404NotFound);
    }

    private async Task<bool> LocationExistsAsync(string? locationCode, CancellationToken cancellationToken)
    {
        var normalized = NormalizeOptional(locationCode) ?? "DEFAULT";
        return await _database.WarehouseLocations.AnyAsync(
            item => item.LocationCode == normalized && item.IsEnabled,
            cancellationToken);
    }

    private async Task<InventoryBalanceRecord> GetOrCreateBalanceAsync(
        MaterialLotRecord lot,
        WarehouseLocationRecord location,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var balance = await _database.InventoryBalances
            .SingleOrDefaultAsync(item => item.LotId == lot.LotId && item.LocationId == location.LocationId, cancellationToken);
        if (balance is not null) return balance;
        balance = new InventoryBalanceRecord
        {
            BalanceId = Guid.NewGuid(),
            LotId = lot.LotId,
            LocationId = location.LocationId,
            OnHand = 0,
            Reserved = 0,
            UpdatedAtUtc = now
        };
        _database.InventoryBalances.Add(balance);
        return balance;
    }

    private async Task<(InventoryBalanceRecord Balance, WarehouseLocationRecord Location)> ResolveBalanceLocationAsync(
        Guid lotId,
        string? locationCode,
        CancellationToken cancellationToken)
    {
        var query = from balance in _database.InventoryBalances
                    join location in _database.WarehouseLocations
                        on balance.LocationId equals location.LocationId
                    where balance.LotId == lotId
                    select new { balance, location };
        var normalized = NormalizeOptional(locationCode);
        if (normalized is not null) query = query.Where(item => item.location.LocationCode == normalized);
        var rows = await query.OrderBy(item => item.location.LocationCode).ToListAsync(cancellationToken);
        if (rows.Count == 0)
            throw Issue(MaterialIssueCodes.InventoryStateInvalid, "该批次尚无库存库位。", StatusCodes.Status409Conflict);
        if (normalized is null)
        {
            var nonEmpty = rows.Where(item => item.balance.OnHand > 0 || item.balance.Reserved > 0).ToList();
            if (nonEmpty.Count != 1)
                throw Issue(MaterialIssueCodes.LocationRequired, "该批次存在多个库位，请明确源库位。", StatusCodes.Status409Conflict);
            return (nonEmpty[0].balance, nonEmpty[0].location);
        }

        return (rows[0].balance, rows[0].location);
    }

    private async Task<(MaterialLotRecord Lot, WarehouseLocationRecord Location, InventoryBalanceRecord Balance, decimal Quantity)[]> AllocateLotBalancesAsync(
        string materialCode,
        string? lotCode,
        decimal quantity,
        string? requestedUnit,
        CancellationToken cancellationToken)
    {
        var normalizedLot = NormalizeOptional(lotCode);
        var lots = await _database.MaterialLots
            .Where(lot => lot.MaterialCode == materialCode && (normalizedLot == null || lot.LotCode == normalizedLot))
            .OrderBy(lot => lot.ExpiryDateUtc ?? DateTime.MaxValue)
            .ThenBy(lot => lot.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var now = NowUtc();
        var catalog = await _database.MaterialCatalog
            .SingleOrDefaultAsync(item => item.MaterialCode == materialCode, cancellationToken);
        if (catalog is null)
        {
            throw Issue(MaterialIssueCodes.MaterialNotFound, "物料编码不存在。", StatusCodes.Status404NotFound);
        }

        if (!catalog.IsEnabled)
        {
            throw Issue(MaterialIssueCodes.MaterialDisabled, "物料已停用，不能预占。", StatusCodes.Status409Conflict);
        }

        var allocations = new List<(MaterialLotRecord, WarehouseLocationRecord, InventoryBalanceRecord, decimal)>();
        var remaining = quantity;
        foreach (var lot in lots)
        {
            if (lot.IsQuarantined || (lot.ExpiryDateUtc is not null && lot.ExpiryDateUtc <= now)) continue;
            if (string.IsNullOrWhiteSpace(lot.Unit) == false &&
                !string.IsNullOrWhiteSpace(requestedUnit) &&
                !string.Equals(lot.Unit, requestedUnit.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            var balances = await (from balance in _database.InventoryBalances
                                  join location in _database.WarehouseLocations
                                      on balance.LocationId equals location.LocationId
                                  where balance.LotId == lot.LotId && balance.OnHand > balance.Reserved
                                  orderby location.LocationCode
                                  select new { balance, location }).ToListAsync(cancellationToken);
            foreach (var row in balances)
            {
                var available = row.balance.OnHand - row.balance.Reserved;
                var allocation = Math.Min(available, remaining);
                if (allocation <= 0) continue;
                allocations.Add((lot, row.location, row.balance, allocation));
                remaining -= allocation;
                if (remaining <= 0) break;
            }
            if (remaining <= 0) break;
        }

        if (remaining > 0)
        {
            throw Issue(
                MaterialIssueCodes.InventoryInsufficient,
                $"物料 {materialCode} 可用库存不足。",
                StatusCodes.Status409Conflict);
        }

        return allocations
            .Select(item => (item.Item1, item.Item2, item.Item3, item.Item4))
            .ToArray();
    }

    private async Task<InventoryBalanceRecord?> FindBalanceForReservedQuantityAsync(
        Guid lotId,
        decimal quantity,
        Guid? locationId,
        CancellationToken cancellationToken)
    {
        return await _database.InventoryBalances
            .Where(item => item.LotId == lotId &&
                           (locationId == null || item.LocationId == locationId) &&
                           item.Reserved >= quantity &&
                           item.OnHand >= quantity)
            .OrderBy(item => item.LocationId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<WarehouseLocationRecord?> FindPrimaryLocationAsync(
        Guid lotId,
        CancellationToken cancellationToken)
    {
        var rows = await (from balance in _database.InventoryBalances.AsNoTracking()
                          join location in _database.WarehouseLocations.AsNoTracking()
                              on balance.LocationId equals location.LocationId
                          where balance.LotId == lotId
                          select new { balance, location })
            .OrderByDescending(item => (double)item.balance.OnHand)
            .ThenBy(item => item.location.LocationCode)
            .Take(256)
            .ToListAsync(cancellationToken);
        return rows
            .Select(item => item.location)
            .FirstOrDefault();
    }

    private async Task<InventoryBalanceRecord?> FindPrimaryBalanceAsync(
        Guid lotId,
        CancellationToken cancellationToken)
    {
        return await _database.InventoryBalances.AsNoTracking()
            .Where(balance => balance.LotId == lotId)
            .OrderByDescending(balance => (double)balance.OnHand)
            .Take(256)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<MaterialLotInventory> ProjectInventoryAsync(
        MaterialLotRecord lot,
        MaterialCatalogRecord catalog,
        WarehouseLocationRecord location,
        InventoryBalanceRecord balance,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return ToInventory(lot, catalog, location, balance);
    }

    private async Task<IReadOnlyList<SampleMaterial>> MapSamplesAsync(
        IReadOnlyList<SampleMaterialRecord> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return Array.Empty<SampleMaterial>();
        var locationIds = rows.Where(row => row.LocationId is not null).Select(row => row.LocationId!.Value).Distinct().ToList();
        var locations = await _database.WarehouseLocations
            .AsNoTracking()
            .Where(location => locationIds.Contains(location.LocationId))
            .ToDictionaryAsync(item => item.LocationId, cancellationToken);
        return rows.Select(row => ToSample(row, locations)).ToList();
    }

    private static SampleMaterial ToSample(
        SampleMaterialRecord sample,
        IReadOnlyDictionary<Guid, WarehouseLocationRecord> locations) => new()
        {
            SampleId = sample.SampleId,
            Barcode = sample.Barcode,
            SampleBatchId = sample.SampleBatchId,
            MaterialCode = sample.MaterialCode,
            SampleType = sample.SampleType,
            Status = ParseEnum(sample.Status, SampleLifecycleStatus.Unknown),
            Location = sample.LocationId is not null && locations.TryGetValue(sample.LocationId.Value, out var location)
                ? ToLocation(location)
                : null,
            BoundExperimentJobId = sample.BoundExperimentJobId,
            CreatedAt = ToOffset(sample.CreatedAtUtc),
            UpdatedAt = ToOffset(sample.UpdatedAtUtc)
        };

    private static MaterialLotInventory ToInventory(
        MaterialLotRecord lot,
        MaterialCatalogRecord? catalog,
        WarehouseLocationRecord? location,
        InventoryBalanceRecord? balance) => new()
        {
            LotId = lot.LotId,
            MaterialCode = lot.MaterialCode,
            MaterialName = catalog?.Name ?? lot.MaterialCode,
            LotCode = lot.LotCode,
            Barcode = lot.Barcode,
            Specification = lot.Specification ?? catalog?.Specification,
            Unit = lot.Unit ?? catalog?.Unit,
            OnHand = balance?.OnHand ?? 0,
            Reserved = balance?.Reserved ?? 0,
            ManufactureDate = ToOffset(lot.ManufactureDateUtc),
            ExpiryDate = ToOffset(lot.ExpiryDateUtc),
            IsQuarantined = lot.IsQuarantined,
            Location = location is null ? null : ToLocation(location)
        };

    private static WarehouseLocation ToLocation(WarehouseLocationRecord location) => new()
    {
        LocationId = location.LocationId,
        WarehouseCode = location.WarehouseCode,
        WarehouseName = location.WarehouseName,
        LocationCode = location.LocationCode,
        IsEnabled = location.IsEnabled
    };

    private static MaterialBinding ToBinding(ExperimentJobMaterialBindingRecord binding) => new()
    {
        BindingId = binding.BindingId,
        ExperimentJobId = binding.ExperimentJobId,
        SampleId = binding.SampleId,
        LotId = binding.LotId,
        Quantity = binding.Quantity,
        Unit = binding.Unit,
        Status = ParseEnum(binding.Status, MaterialBindingStatus.Unknown),
        InjectionPosition = binding.InjectionPosition,
        CreatedAt = ToOffset(binding.CreatedAtUtc),
        UpdatedAt = ToOffset(binding.UpdatedAtUtc)
    };

    private InventoryTransactionRecord NewBindingTransaction(
        ReserveExperimentMaterialsRequest request,
        ExperimentJobMaterialBindingRecord binding,
        InventoryTransactionKind kind,
        string? barcode,
        MaterialLotRecord? lot,
        decimal quantity,
        string? unit,
        Guid? locationId,
        DateTime now) => new()
        {
            Id = Guid.NewGuid(),
            RequestId = request.RequestId,
            LineKey = kind.ToString().ToLowerInvariant() + ":" + binding.LineKey,
            TransactionKind = kind.ToString(),
            LotId = lot?.LotId,
            SampleId = binding.SampleId,
            ToLocationId = locationId,
            MaterialCode = lot?.MaterialCode,
            LotCode = lot?.LotCode,
            Barcode = barcode,
            Quantity = quantity,
            Unit = unit,
            ExperimentJobId = request.ExperimentJobId,
            Actor = request.Actor.Trim(),
            Reason = request.Reason,
            DetailsJson = "{}",
            OccurredAtUtc = now
        };

    private InventoryTransactionRecord NewBindingTransaction(
        ReleaseExperimentMaterialsRequest request,
        ExperimentJobMaterialBindingRecord binding,
        InventoryTransactionKind kind,
        string? barcode,
        MaterialLotRecord? lot,
        decimal quantity,
        string? unit,
        Guid? locationId,
        DateTime now) => new()
        {
            Id = Guid.NewGuid(),
            RequestId = request.RequestId,
            LineKey = kind.ToString().ToLowerInvariant() + ":" + binding.LineKey,
            TransactionKind = kind.ToString(),
            LotId = lot?.LotId,
            SampleId = binding.SampleId,
            ToLocationId = locationId,
            MaterialCode = lot?.MaterialCode,
            LotCode = lot?.LotCode,
            Barcode = barcode,
            Quantity = quantity,
            Unit = unit,
            ExperimentJobId = request.ExperimentJobId,
            Actor = request.Actor.Trim(),
            Reason = request.Reason,
            DetailsJson = "{}",
            OccurredAtUtc = now
        };

    private InventoryTransactionRecord NewBindingTransaction(
        ConsumeExperimentMaterialsRequest request,
        ExperimentJobMaterialBindingRecord binding,
        InventoryTransactionKind kind,
        string? barcode,
        MaterialLotRecord? lot,
        decimal quantity,
        string? unit,
        Guid? locationId,
        DateTime now) => new()
        {
            Id = Guid.NewGuid(),
            RequestId = request.RequestId,
            LineKey = kind.ToString().ToLowerInvariant() + ":" + binding.LineKey,
            TransactionKind = kind.ToString(),
            LotId = lot?.LotId,
            SampleId = binding.SampleId,
            ToLocationId = locationId,
            MaterialCode = lot?.MaterialCode,
            LotCode = lot?.LotCode,
            Barcode = barcode,
            Quantity = quantity,
            Unit = unit,
            ExperimentJobId = request.ExperimentJobId,
            Actor = request.Actor.Trim(),
            Reason = request.Reason,
            DetailsJson = "{}",
            OccurredAtUtc = now
        };

    private static IReadOnlyList<string> AllowedActions(
        MaterialScanKind kind,
        SampleMaterialRecord? sample,
        MaterialLotRecord? lot)
    {
        if (kind == MaterialScanKind.Sample && sample is not null)
        {
            return ParseEnum(sample.Status, SampleLifecycleStatus.Unknown) == SampleLifecycleStatus.Available
                ? new[] { "bind", "trace" }
                : new[] { "trace" };
        }

        if (kind == MaterialScanKind.ConsumableLot && lot is not null)
        {
            return lot.IsQuarantined || lot.ExpiryDateUtc is not null && lot.ExpiryDateUtc <= DateTime.UtcNow
                ? new[] { "trace" }
                : new[] { "receive", "move", "reserve", "trace" };
        }

        return Array.Empty<string>();
    }

    private static void EnsureLotCompatible(MaterialLotRecord lot, string? barcode, DateTimeOffset? expiry)
    {
        var normalizedBarcode = NormalizeOptional(barcode);
        if (normalizedBarcode is not null && lot.Barcode is not null && !string.Equals(lot.Barcode, normalizedBarcode, StringComparison.OrdinalIgnoreCase))
            throw Issue(MaterialIssueCodes.LotAlreadyExists, "同一物料批号对应的条码不一致。", StatusCodes.Status409Conflict);
        if (expiry is not null && lot.ExpiryDateUtc is not null && lot.ExpiryDateUtc != expiry.Value.UtcDateTime)
            throw Issue(MaterialIssueCodes.LotAlreadyExists, "同一物料批号的有效期不一致。", StatusCodes.Status409Conflict);
    }

    private static void EnsureLotUsableForMutation(MaterialLotRecord lot, DateTime now, MaterialCatalogRecord? catalog = null)
    {
        if (catalog is not null && !catalog.IsEnabled)
            throw Issue(MaterialIssueCodes.MaterialDisabled, "物料已停用，不能改变库存。", StatusCodes.Status409Conflict);
        if (lot.IsQuarantined)
            throw Issue(MaterialIssueCodes.Quarantined, "耗材批次已隔离，不能改变库存。", StatusCodes.Status409Conflict);
        if (lot.ExpiryDateUtc is not null && lot.ExpiryDateUtc <= now)
            throw Issue(MaterialIssueCodes.Expired, "耗材批次已过期，不能改变库存。", StatusCodes.Status409Conflict);
    }

    private async Task ValidateLotBarcodeConflictAsync(
        string? barcode,
        Guid? currentLotId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return;
        var normalized = Normalize(barcode);
        if (_database.ChangeTracker.Entries<MaterialLotRecord>().Any(entry =>
                entry.Entity.LotId != currentLotId && string.Equals(entry.Entity.Barcode, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw Issue(MaterialIssueCodes.LotAlreadyExists, "耗材条码已被其他批次使用。", StatusCodes.Status409Conflict);
        }

        if (await _database.MaterialLots.AnyAsync(
                item => item.Barcode != null && item.Barcode.ToUpper() == normalized && (currentLotId == null || item.LotId != currentLotId),
                cancellationToken))
        {
            throw Issue(MaterialIssueCodes.LotAlreadyExists, "耗材条码已被其他批次使用。", StatusCodes.Status409Conflict);
        }
    }

    private async Task ValidateCrossTypeBarcodeConflictAsync(string? barcode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return;
        var normalized = Normalize(barcode);
        if (await _database.SampleMaterials.AnyAsync(item => item.Barcode.ToUpper() == normalized, cancellationToken) ||
            _database.ChangeTracker.Entries<SampleMaterialRecord>().Any(entry => string.Equals(entry.Entity.Barcode, normalized, StringComparison.OrdinalIgnoreCase)))
            throw Issue(MaterialIssueCodes.BarcodeAlreadyExists, "條碼已被樣品使用。", StatusCodes.Status409Conflict);
    }

    private static void ValidateRequestMetadata(Guid requestId, string actor)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("请求编号不能为空。", nameof(requestId));
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("操作人不能为空。", nameof(actor));
    }

    private static void ValidatePositive(decimal quantity)
    {
        if (quantity <= 0) throw Issue(MaterialIssueCodes.InvalidQuantity, "数量必须大于 0。", StatusCodes.Status400BadRequest);
    }

    private static MaterialImportIssue ImportIssue(int row, string code, string message, string? field) => new()
    {
        RowNumber = row,
        Code = code,
        Message = message,
        Field = field
    };

    private static MaterialManagementException Issue(string code, string message, int statusCode) =>
        new(code, message, statusCode);

    private DateTime NowUtc() => _clock.GetUtcNow().UtcDateTime;

    private DateTime Now() => NowUtc();

    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Normalize(value);

    private static T ParseEnum<T>(string? value, T fallback)
        where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;

    private static T? ParseNullableEnum<T>(string? value)
        where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var parsed) ? parsed : null;

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException("物料操作结果无法解析。");

    private static string CreateFingerprint<T>(string operationKind, T payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(operationKind + "\u001f" + json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed class MaterialManagementException : InvalidOperationException
{
    public MaterialManagementException(
        string code,
        string message,
        int statusCode,
        IReadOnlyList<MaterialImportIssue>? issues = null)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
        Issues = issues ?? Array.Empty<MaterialImportIssue>();
    }

    public string Code { get; }
    public int StatusCode { get; }
    public IReadOnlyList<MaterialImportIssue> Issues { get; }
}
