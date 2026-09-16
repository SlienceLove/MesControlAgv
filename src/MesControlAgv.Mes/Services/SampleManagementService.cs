using System.Text.RegularExpressions;
using MesControlAgv.Contracts.Samples;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Minimal durable sample custody module for the P0 single-sample flow.
/// Barcode/sample identity and location transitions are stored independently
/// from device drivers, so a device timeout cannot silently lose custody.
/// </summary>
public sealed class SampleManagementService(
    MesDbContext database,
    TimeProvider timeProvider)
{
    private static readonly Regex SafeIdentifier = new("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}$", RegexOptions.Compiled);

    public async Task<SampleRecordResponse> RegisterAsync(
        RegisterSampleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sampleId = RequireCanonicalIdentity(request.SampleId, nameof(request.SampleId));
        var barcode = RequireCanonicalIdentity(request.Barcode, nameof(request.Barcode));
        var batch = RequireIdentifier(request.SampleBatchId, nameof(request.SampleBatchId));
        var source = RequireIdentifier(request.SourceLocation, nameof(request.SourceLocation));
        var actor = RequireText(request.OperatorName, nameof(request.OperatorName));
        var position = NormalizeOptional(request.ContainerPosition);

        var existing = await database.Samples.SingleOrDefaultAsync(
            item => item.SampleId == sampleId || item.Barcode == barcode,
            cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.SampleId, sampleId, StringComparison.Ordinal)
                || !string.Equals(existing.Barcode, barcode, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("SampleId and Barcode are already bound to different samples.");
            }

            return ToResponse(existing);
        }

        var now = timeProvider.GetUtcNow();
        var sample = new SampleRecord
        {
            SampleId = sampleId,
            Barcode = barcode,
            SampleBatchId = batch,
            SourceLocation = source,
            ContainerPosition = position,
            CurrentLocation = source,
            Status = SampleLifecycleStatus.Registered.ToString(),
            CreatedBy = actor,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        database.Samples.Add(sample);
        database.SampleEvents.Add(new SampleEventRecord
        {
            SampleRecordId = sample.Id,
            EventType = "Registered",
            DeviceId = "sample-management",
            ToLocation = source,
            Actor = actor,
            OccurredAtUtc = now,
            Detail = position is null ? null : $"containerPosition={position}"
        });
        await database.SaveChangesAsync(cancellationToken);
        return ToResponse(sample);
    }

    /// <summary>
    /// Imports rows from the WPF template.  Each row is committed independently
    /// so a bad barcode or duplicate never rolls back valid rows from the same
    /// operator file.  The registration event is still written by
    /// <see cref="RegisterAsync"/> and an optional RunId is bound immediately
    /// afterwards.
    /// </summary>
    public async Task<ImportSamplesResponse> ImportAsync(
        ImportSamplesRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceFileName = RequireText(request.SourceFileName, nameof(request.SourceFileName));
        var actor = RequireText(request.OperatorName, nameof(request.OperatorName));
        var rows = request.Rows ?? [];
        if (rows.Count == 0) throw new ArgumentException("At least one sample row is required.", nameof(request));
        if (rows.Count > 500) throw new ArgumentException("A single import is limited to 500 rows.", nameof(request));

        var results = new List<SampleImportRowResult>(rows.Count);
        var succeeded = 0;
        var existingCount = 0;
        foreach (var row in rows)
        {
            var rowNumber = row.RowNumber > 0 ? row.RowNumber : results.Count + 2;
            try
            {
                var sampleId = RequireCanonicalIdentity(row.SampleId, nameof(row.SampleId));
                var barcode = RequireCanonicalIdentity(row.Barcode, nameof(row.Barcode));
                var alreadyPresent = await database.Samples.AnyAsync(
                    item => item.SampleId == sampleId || item.Barcode == barcode,
                    cancellationToken);

                var sample = await RegisterAsync(
                    new RegisterSampleRequest
                    {
                        SampleId = sampleId,
                        Barcode = barcode,
                        SampleBatchId = row.SampleBatchId,
                        SourceLocation = row.SourceLocation,
                        ContainerPosition = row.ContainerPosition,
                        OperatorName = actor
                    },
                    cancellationToken);

                if (row.RunId is { } runId)
                {
                    sample = await BindRunAsync(
                        sample.SampleId,
                        new BindSampleRunRequest { RunId = runId, OperatorName = actor },
                        cancellationToken);
                }

                var outcome = alreadyPresent ? "existing" : "created";
                if (alreadyPresent) existingCount++;
                succeeded++;
                results.Add(new SampleImportRowResult(
                    rowNumber,
                    sample.SampleId,
                    sample.Barcode,
                    outcome,
                    null,
                    sample));
            }
            catch (ArgumentException exception)
            {
                results.Add(new SampleImportRowResult(rowNumber, row.SampleId, row.Barcode, "failed", exception.Message, null));
            }
            catch (InvalidOperationException exception)
            {
                results.Add(new SampleImportRowResult(rowNumber, row.SampleId, row.Barcode, "failed", exception.Message, null));
            }
            catch (DbUpdateException exception)
            {
                // A uniqueness/concurrency failure must not leave Added
                // entities tracked.  Detach the failed row so a later valid
                // row in the same import can still commit independently.
                DetachPendingChanges();
                results.Add(new SampleImportRowResult(
                    rowNumber,
                    row.SampleId,
                    row.Barcode,
                    "failed",
                    $"Database rejected the row: {exception.InnerException?.Message ?? exception.Message}",
                    null));
            }
        }

        return new ImportSamplesResponse(
            sourceFileName,
            rows.Count,
            succeeded,
            existingCount,
            rows.Count - succeeded,
            results);
    }

    public async Task<IReadOnlyList<SampleRecordResponse>> ListAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 500) throw new ArgumentException("Limit must be between 1 and 500.", nameof(limit));
        // SQLite cannot translate DateTimeOffset ORDER BY expressions.  The
        // The P0 list is intentionally bounded at the API boundary, so sort
        // the small projection in memory for SQLite compatibility.
        var samples = await database.Samples.AsNoTracking().ToListAsync(cancellationToken);
        return samples
            .OrderByDescending(item => item.UpdatedAtUtc)
            .Take(limit)
            .Select(ToResponse)
            .ToArray();
    }

    public async Task<SampleRecordResponse> BindRunAsync(
        string sampleId,
        BindSampleRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sample = await FindAsync(sampleId, cancellationToken);
        var actor = RequireText(request.OperatorName, nameof(request.OperatorName));
        if (request.RunId == Guid.Empty) throw new ArgumentException("RunId is required.", nameof(request));
        var run = await database.WorkflowExecutions.AsNoTracking().SingleOrDefaultAsync(
            item => item.ExecutionId == request.RunId,
            cancellationToken);
        if (run is null)
            throw new InvalidOperationException($"Workflow run '{request.RunId:D}' was not found.");
        var runRequest = WorkflowPersistence.DeserializeRequest(run.RequestJson);
        runRequest.Parameters.TryGetValue(WorkflowRuntimeParameterNames.SampleBatchId, out var runBatch);
        if (!string.IsNullOrWhiteSpace(runBatch) &&
            !string.Equals(runBatch, sample.SampleBatchId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Workflow run '{request.RunId:D}' belongs to batch '{runBatch}', not '{sample.SampleBatchId}'.");
        }
        if (sample.RunId is { } existingRun && existingRun != request.RunId)
            throw new InvalidOperationException($"Sample '{sample.SampleId}' is already bound to run {existingRun:N}.");

        if (sample.RunId == request.RunId && await database.SampleEvents.AnyAsync(
                item => item.SampleRecordId == sample.Id &&
                        item.EventType == "RunBound" &&
                        item.Detail == request.RunId.ToString("N"),
                cancellationToken))
        {
            return ToResponse(sample);
        }

        sample.RunId = request.RunId;
        if (sample.Status == SampleLifecycleStatus.Registered.ToString())
            sample.Status = SampleLifecycleStatus.Reserved.ToString();
        sample.UpdatedAtUtc = timeProvider.GetUtcNow();
        database.SampleEvents.Add(new SampleEventRecord
        {
            SampleRecordId = sample.Id,
            EventType = "RunBound",
            DeviceId = "mes-workflow",
            Actor = actor,
            OccurredAtUtc = sample.UpdatedAtUtc,
            Detail = request.RunId.ToString("N")
        });
        await database.SaveChangesAsync(cancellationToken);
        return ToResponse(sample);
    }

    public async Task<SampleRecordResponse> MoveAsync(
        string sampleId,
        MoveSampleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty) throw new ArgumentException("OperationId is required.", nameof(request));
        var sample = await FindAsync(sampleId, cancellationToken);
        var deviceId = RequireIdentifier(request.DeviceId, nameof(request.DeviceId));
        var destination = RequireIdentifier(request.ToLocation, nameof(request.ToLocation));
        var actor = RequireText(request.OperatorName, nameof(request.OperatorName));
        if (sample.RunId is null)
            throw new InvalidOperationException($"Sample '{sample.SampleId}' must be bound to a workflow run before a device move.");

        var replay = await database.SampleEvents.FirstOrDefaultAsync(
            item => item.SampleRecordId == sample.Id && item.OperationId == request.OperationId,
            cancellationToken);
        if (replay is not null) return ToResponse(sample);

        var from = sample.CurrentLocation;
        sample.CurrentLocation = destination;
        sample.LastDeviceId = deviceId;
        sample.Status = (request.Status ?? InferStatus(deviceId)).ToString();
        sample.LastError = null;
        sample.UpdatedAtUtc = timeProvider.GetUtcNow();
        database.SampleEvents.Add(new SampleEventRecord
        {
            SampleRecordId = sample.Id,
            OperationId = request.OperationId,
            EventType = "DeviceMove",
            DeviceId = deviceId,
            FromLocation = from,
            ToLocation = destination,
            Actor = actor,
            OccurredAtUtc = sample.UpdatedAtUtc
        });
        await database.SaveChangesAsync(cancellationToken);
        return ToResponse(sample);
    }

    public async Task<SampleRecordResponse?> GetAsync(string sampleId, CancellationToken cancellationToken)
    {
        var normalized = RequireIdentifier(sampleId, nameof(sampleId));
        var sample = await database.Samples.AsNoTracking().SingleOrDefaultAsync(
            item => item.SampleId == normalized || item.Barcode == normalized,
            cancellationToken);
        return sample is null ? null : ToResponse(sample);
    }

    public async Task<IReadOnlyList<SampleEventResponse>> GetEventsAsync(
        string sampleId,
        CancellationToken cancellationToken)
    {
        var sample = await FindAsync(sampleId, cancellationToken);
        var events = await database.SampleEvents.AsNoTracking()
            .Where(item => item.SampleRecordId == sample.Id)
            .ToListAsync(cancellationToken);
        return events
            .OrderBy(item => item.OccurredAtUtc)
            .Select(item => new SampleEventResponse(
                item.Id,
                item.SampleRecordId,
                item.OperationId,
                item.EventType,
                item.DeviceId,
                item.FromLocation,
                item.ToLocation,
                item.Actor,
                item.OccurredAtUtc,
                item.Detail))
            .ToList();
    }

    private async Task<SampleRecord> FindAsync(string sampleId, CancellationToken cancellationToken)
    {
        var normalized = RequireIdentifier(sampleId, nameof(sampleId));
        return await database.Samples.SingleOrDefaultAsync(
                   item => item.SampleId == normalized || item.Barcode == normalized,
                   cancellationToken)
               ?? throw new KeyNotFoundException($"Sample '{normalized}' was not found.");
    }

    private static SampleLifecycleStatus InferStatus(string deviceId) =>
        deviceId.StartsWith("AGV", StringComparison.OrdinalIgnoreCase)
            ? SampleLifecycleStatus.InTransit
            : deviceId.StartsWith("SAMPLE-WORKSTATION", StringComparison.OrdinalIgnoreCase)
                ? SampleLifecycleStatus.AtWorkstation
                : SampleLifecycleStatus.Processing;

    private static SampleRecordResponse ToResponse(SampleRecord sample) =>
        new(
            sample.Id,
            sample.SampleId,
            sample.Barcode,
            sample.SampleBatchId,
            sample.SourceLocation,
            sample.ContainerPosition,
            sample.CurrentLocation,
            Enum.TryParse<SampleLifecycleStatus>(sample.Status, true, out var status)
                ? status
                : SampleLifecycleStatus.Unknown,
            sample.RunId,
            sample.LastDeviceId,
            sample.CreatedAtUtc,
            sample.UpdatedAtUtc,
            sample.LastError);

    private static string RequireIdentifier(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value) && SafeIdentifier.IsMatch(value.Trim())
            ? value.Trim()
            : throw new ArgumentException($"{name} must be a bounded barcode/sample/location identifier.", name);

    private static string RequireCanonicalIdentity(string? value, string name) =>
        RequireIdentifier(value, name).ToUpperInvariant();

    private void DetachPendingChanges()
    {
        foreach (var entry in database.ChangeTracker.Entries()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                     .ToArray())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static string RequireText(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length > 256
            ? throw new ArgumentException($"{name} is required and must be at most 256 characters.", name)
            : value.Trim();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : RequireIdentifier(value, nameof(value));
}
