using System.Security.Cryptography;
using System.Text;
using MesControlAgv.Application;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

/// <summary>Internal entry point for callers that already hold the scheduling mutation gate.</summary>
internal interface IExperimentSampleVerificationGateCore
{
    Task<ExperimentSampleVerification> RequireVerifiedCurrentWhileMutationGateHeldAsync(
        Guid experimentJobId, int revision, string snapshotHash, CancellationToken cancellationToken);
}

/// <summary>Persists centrally registered samples and append-only task-row snapshots.</summary>
public sealed class ExperimentSampleVerificationService(
    MesDbContext database,
    ExperimentSchedulingMutationGate mutationGate,
    TimeProvider timeProvider) : IExperimentSampleVerificationService, IExperimentSampleVerificationGate, IExperimentSampleVerificationGateCore
{
    public async Task<IReadOnlyList<ExperimentSample>> ListSamplesAsync(
        QueryExperimentSamplesRequest request,
        CancellationToken cancellationToken)
    {
        var batchId = request?.BatchId?.Trim();
        var query = database.ExperimentSamples.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(batchId)) query = query.Where(item => item.BatchId == batchId);
        return (await query.OrderBy(item => item.BusinessSampleId).ThenBy(item => item.SampleId).ToListAsync(cancellationToken))
            .Select(MapSample).ToArray();
    }

    public Task<ExperimentSample> SaveSampleAsync(Guid sampleId, SaveExperimentSampleRequest request, CancellationToken cancellationToken) =>
        ExecuteMutationAsync(async () =>
        {
            var metadata = NormalizeMetadata(request?.RequestId ?? Guid.Empty, request?.Actor, request?.Reason);
            if (sampleId == Guid.Empty) throw new ArgumentException("A non-empty sample id is required.", nameof(sampleId));
            var sample = NormalizeSample(request?.Sample, sampleId);
            var fingerprint = CreateFingerprint("SaveExperimentSample", metadata, sample);
            var replay = await TryReplayAsync<ExperimentSample>(metadata.RequestId, "ExperimentSampleSaved", fingerprint, cancellationToken);
            if (replay is not null) return replay;

            var duplicateBarcode = await database.ExperimentSamples.AsNoTracking().AnyAsync(item =>
                item.SampleId != sampleId && item.NormalizedBarcode == sample.Barcode, cancellationToken);
            if (duplicateBarcode) throw Conflict("A different sample already uses this barcode.", ExperimentSampleVerificationIssueCodes.BarcodeDuplicate);
            var duplicateBusinessId = await database.ExperimentSamples.AsNoTracking().AnyAsync(item =>
                item.SampleId != sampleId && item.BusinessSampleId == sample.BusinessSampleId, cancellationToken);
            if (duplicateBusinessId) throw Conflict("A different sample already uses this business sample id.", ExperimentSampleVerificationIssueCodes.BarcodeDuplicate);

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var record = await database.ExperimentSamples.SingleOrDefaultAsync(item => item.SampleId == sampleId, cancellationToken);
            var drifted = record is not null &&
                (!string.Equals(record.Barcode.Trim(), sample.Barcode, StringComparison.Ordinal) ||
                 !string.Equals(record.BatchId, sample.BatchId, StringComparison.Ordinal) ||
                 !string.Equals(record.Status, sample.Status.ToString(), StringComparison.Ordinal));
            if (record is null)
            {
                record = new ExperimentSampleRecord { SampleId = sampleId, CreatedAtUtc = now };
                database.ExperimentSamples.Add(record);
            }
            record.BusinessSampleId = sample.BusinessSampleId;
            record.BatchId = sample.BatchId;
            record.Barcode = sample.Barcode;
            record.DisplayName = sample.DisplayName;
            record.Status = sample.Status.ToString();
            record.UpdatedAtUtc = now;

            if (drifted)
            {
                var verifications = await database.ExperimentSampleVerifications.ToListAsync(cancellationToken);
                foreach (var verification in verifications.Where(item => Rows(item).Any(row => row.SampleId == sampleId)))
                    Invalidate(verification, now, "Registered sample barcode, batch, or status changed.");
            }

            var result = MapSample(record);
            AddAudit(metadata, fingerprint, "ExperimentSampleSaved", result, details: new Dictionary<string, string?> { ["sampleId"] = sampleId.ToString("D") });
            await database.SaveChangesAsync(cancellationToken);
            return result;
        }, cancellationToken);

    public async Task<ExperimentSampleVerification?> GetCurrentAsync(Guid experimentJobId, CancellationToken cancellationToken)
    {
        if (experimentJobId == Guid.Empty) throw new ArgumentException("A non-empty experiment job id is required.", nameof(experimentJobId));
        var job = await FindJobAsync(experimentJobId, cancellationToken);
        var record = await database.ExperimentSampleVerifications.AsNoTracking()
            .Where(item => item.ExperimentJobId == experimentJobId).OrderByDescending(item => item.Revision)
            .FirstOrDefaultAsync(cancellationToken);
        if (record is null) return null;
        var rows = Rows(record);
        var issues = await ValidateRowsAsync(job, rows, cancellationToken);
        return MapVerification(record) with { ValidationIssues = issues };
    }

    public Task<ExperimentSampleVerification> SaveCurrentAsync(Guid experimentJobId, SaveExperimentSampleVerificationRequest request, CancellationToken cancellationToken) =>
        ExecuteMutationAsync(async () =>
        {
            var metadata = NormalizeMetadata(request?.RequestId ?? Guid.Empty, request?.Actor, request?.Reason);
            var rows = CanonicalizeRows(request?.Rows);
            var fingerprint = CreateFingerprint("SaveExperimentSampleVerification", metadata, new { ExperimentJobId = experimentJobId, Rows = rows });
            var replay = await TryReplayAsync<ExperimentSampleVerification>(metadata.RequestId, "ExperimentSampleVerificationSaved", fingerprint, cancellationToken);
            if (replay is not null) return replay;
            var job = await FindJobAsync(experimentJobId, cancellationToken);
            EnsureMutable(job);
            var issues = await ValidateRowsAsync(job, rows, cancellationToken);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var rowsJson = ExperimentSchedulingPersistence.Serialize(rows);
            var hash = Hash(rowsJson);
            var current = await database.ExperimentSampleVerifications
                .Where(item => item.ExperimentJobId == experimentJobId).OrderByDescending(item => item.Revision)
                .FirstOrDefaultAsync(cancellationToken);
            ExperimentSampleVerificationRecord result;
            if (current is not null &&
                string.Equals(current.SnapshotHash, hash, StringComparison.Ordinal) &&
                !string.Equals(current.Status, ExperimentSampleVerificationStatus.Invalidated.ToString(), StringComparison.Ordinal))
            {
                result = current;
                if (!string.Equals(current.Status, ExperimentSampleVerificationStatus.Verified.ToString(), StringComparison.Ordinal))
                {
                    current.Status = issues.Count == 0 ? ExperimentSampleVerificationStatus.ReadyForVerification.ToString() : ExperimentSampleVerificationStatus.Draft.ToString();
                    current.InvalidatedAtUtc = null;
                    current.InvalidationReason = null;
                    current.UpdatedAtUtc = now;
                }
            }
            else
            {
                if (current is not null && string.Equals(current.Status, ExperimentSampleVerificationStatus.Verified.ToString(), StringComparison.Ordinal))
                    Invalidate(current, now, "Task sample rows changed.");
                result = new ExperimentSampleVerificationRecord
                {
                    VerificationId = Guid.NewGuid(), ExperimentJobId = experimentJobId,
                    Revision = (current?.Revision ?? 0) + 1,
                    Status = issues.Count == 0 ? ExperimentSampleVerificationStatus.ReadyForVerification.ToString() : ExperimentSampleVerificationStatus.Draft.ToString(),
                    RowsJson = rowsJson, SnapshotHash = hash, CreatedAtUtc = now, UpdatedAtUtc = now
                };
                database.ExperimentSampleVerifications.Add(result);
            }
            var mapped = MapVerification(result) with { ValidationIssues = issues };
            AddAudit(metadata, fingerprint, "ExperimentSampleVerificationSaved", mapped, experimentJobId, details: new Dictionary<string, string?>
            {
                ["revision"] = mapped.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["snapshotHash"] = mapped.SnapshotHash,
                ["validationIssues"] = string.Join("|", issues.Select(issue => issue.Code))
            });
            await database.SaveChangesAsync(cancellationToken);
            return mapped;
        }, cancellationToken);

    public Task<ExperimentSampleVerification> VerifyAsync(Guid experimentJobId, int revision, CompleteExperimentSampleVerificationRequest request, CancellationToken cancellationToken) =>
        ExecuteMutationAsync(async () =>
        {
            var metadata = NormalizeMetadata(request?.RequestId ?? Guid.Empty, request?.Actor, request?.Reason);
            if (revision <= 0 || request!.Revision != revision) throw Conflict("The requested verification revision does not match the route.", ExperimentSampleVerificationIssueCodes.VersionConflict);
            var expectedHash = RequireText(request.SnapshotHash, nameof(request.SnapshotHash));
            var fingerprint = CreateFingerprint("VerifyExperimentSampleVerification", metadata, new { ExperimentJobId = experimentJobId, revision, expectedHash, VerificationNote = NormalizeOptionalText(request.VerificationNote) });
            var replay = await TryReplayAsync<ExperimentSampleVerification>(metadata.RequestId, "ExperimentSampleVerificationVerified", fingerprint, cancellationToken);
            if (replay is not null) return replay;
            var job = await FindJobAsync(experimentJobId, cancellationToken);
            EnsureMutable(job);
            var current = await database.ExperimentSampleVerifications.Where(item => item.ExperimentJobId == experimentJobId)
                .OrderByDescending(item => item.Revision).FirstOrDefaultAsync(cancellationToken);
            if (current is null || current.Revision != revision || !string.Equals(current.SnapshotHash, expectedHash, StringComparison.Ordinal))
                throw Conflict("The task sample snapshot has changed; refresh it before verifying.", ExperimentSampleVerificationIssueCodes.VersionConflict);
            if (!string.Equals(current.Status, ExperimentSampleVerificationStatus.ReadyForVerification.ToString(), StringComparison.Ordinal))
                throw Conflict("The current task sample snapshot is not ready for verification.", ExperimentSampleVerificationIssueCodes.VerificationRequired);
            var issues = await ValidateRowsAsync(job, Rows(current), cancellationToken);
            if (issues.Count != 0)
            {
                Invalidate(current, timeProvider.GetUtcNow().UtcDateTime, "Registered sample data no longer matches the snapshot: " + string.Join("; ", issues.Select(issue => issue.Code)));
                await database.SaveChangesAsync(cancellationToken);
                throw Conflict("The task sample snapshot was invalidated by registered sample drift.", ExperimentSampleVerificationIssueCodes.VerificationInvalidated);
            }
            var now = timeProvider.GetUtcNow().UtcDateTime;
            current.Status = ExperimentSampleVerificationStatus.Verified.ToString();
            current.VerifiedBy = metadata.Actor;
            current.VerifiedAtUtc = now;
            current.VerificationNote = NormalizeOptionalText(request.VerificationNote);
            current.UpdatedAtUtc = now;
            var result = MapVerification(current);
            AddAudit(metadata, fingerprint, "ExperimentSampleVerificationVerified", result, experimentJobId, details: new Dictionary<string, string?> { ["revision"] = revision.ToString(System.Globalization.CultureInfo.InvariantCulture), ["snapshotHash"] = expectedHash });
            await database.SaveChangesAsync(cancellationToken);
            return result;
        }, cancellationToken);

    public Task<ExperimentSampleVerification> RequireVerifiedCurrentAsync(Guid experimentJobId, int revision, string snapshotHash, CancellationToken cancellationToken) =>
        ExecuteMutationAsync(
            () => RequireVerifiedCurrentWhileMutationGateHeldAsync(experimentJobId, revision, snapshotHash, cancellationToken),
            cancellationToken);

    public async Task<ExperimentSampleVerification> RequireVerifiedCurrentWhileMutationGateHeldAsync(
        Guid experimentJobId, int revision, string snapshotHash, CancellationToken cancellationToken)
    {
            var job = await FindJobAsync(experimentJobId, cancellationToken);
            var current = await database.ExperimentSampleVerifications.Where(item => item.ExperimentJobId == experimentJobId)
                .OrderByDescending(item => item.Revision).FirstOrDefaultAsync(cancellationToken);
            if (current is null || current.Revision != revision || !string.Equals(current.SnapshotHash, snapshotHash?.Trim(), StringComparison.Ordinal))
                throw Conflict("A verified current sample snapshot is required.", ExperimentSampleVerificationIssueCodes.VersionConflict);
            if (!string.Equals(current.Status, ExperimentSampleVerificationStatus.Verified.ToString(), StringComparison.Ordinal))
                throw Conflict("A verified current sample snapshot is required.", current.Status == ExperimentSampleVerificationStatus.Invalidated.ToString() ? ExperimentSampleVerificationIssueCodes.VerificationInvalidated : ExperimentSampleVerificationIssueCodes.VerificationRequired);
            var issues = await ValidateRowsAsync(job, Rows(current), cancellationToken);
            if (issues.Count != 0)
            {
                Invalidate(current, timeProvider.GetUtcNow().UtcDateTime, "Registered sample data no longer matches the snapshot: " + string.Join("; ", issues.Select(issue => issue.Code)));
                await database.SaveChangesAsync(cancellationToken);
                throw Conflict("The verified sample snapshot was invalidated by registered sample drift.", ExperimentSampleVerificationIssueCodes.VerificationInvalidated);
            }
        return MapVerification(current);
    }

    private async Task<IReadOnlyList<ExperimentSampleVerificationValidationIssue>> ValidateRowsAsync(ExperimentJobRecord job, IReadOnlyList<ExperimentSampleTaskRow> rows, CancellationToken cancellationToken)
    {
        var issues = new List<ExperimentSampleVerificationValidationIssue>();
        if (rows.Count == 0) issues.Add(Issue(null, "EXP-SAMPLE-ROWS-REQUIRED", "At least one sample row is required."));
        var samples = await database.ExperimentSamples.AsNoTracking().Where(item => rows.Select(row => row.SampleId).Contains(item.SampleId)).ToDictionaryAsync(item => item.SampleId, cancellationToken);
        var positions = new HashSet<string>(StringComparer.Ordinal);
        var rowIds = new HashSet<Guid>();
        var barcodeSamples = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var prefix = $"Row {row.Order}";
            if (row.RowId == Guid.Empty || !rowIds.Add(row.RowId)) issues.Add(Issue(row, "EXP-SAMPLE-ROW-ID-INVALID", $"{prefix}: row id is missing or duplicated."));
            if (row.SampleId == Guid.Empty || !samples.TryGetValue(row.SampleId, out var sample)) { issues.Add(Issue(row, ExperimentSampleVerificationIssueCodes.SampleNotFound, $"{prefix}: sample was not found.")); continue; }
            if (string.IsNullOrWhiteSpace(row.SampleBarcode)) issues.Add(Issue(row, "EXP-SAMPLE-BARCODE-REQUIRED", $"{prefix}: barcode is required."));
            var barcode = row.SampleBarcode.Trim();
            if (!string.Equals(barcode, sample.NormalizedBarcode, StringComparison.Ordinal)) issues.Add(Issue(row, "EXP-SAMPLE-BARCODE-MISMATCH", $"{prefix}: barcode does not match the registered sample."));
            if (!string.Equals(sample.BatchId, job.SampleBatchId, StringComparison.Ordinal)) issues.Add(Issue(row, ExperimentSampleVerificationIssueCodes.BatchMismatch, $"{prefix}: sample belongs to another batch."));
            if (!string.Equals(sample.Status, ExperimentSampleStatus.Active.ToString(), StringComparison.Ordinal)) issues.Add(Issue(row, "EXP-SAMPLE-DISABLED", $"{prefix}: registered sample is disabled."));
            if (string.IsNullOrWhiteSpace(row.Position) || !positions.Add(row.Position.Trim())) issues.Add(Issue(row, ExperimentSampleVerificationIssueCodes.PositionDuplicate, $"{prefix}: position is missing or duplicated."));
            if (!barcodeSamples.TryAdd(barcode, row.SampleId)) issues.Add(Issue(row, ExperimentSampleVerificationIssueCodes.BarcodeDuplicate, $"{prefix}: barcode is duplicated."));
        }
        return issues
            .GroupBy(issue => new { issue.RowId, issue.Order, issue.Code, issue.Message })
            .Select(group => group.First()).ToArray();
    }

    private static IReadOnlyList<ExperimentSampleTaskRow> CanonicalizeRows(IReadOnlyList<ExperimentSampleTaskRow>? source) => (source ?? [])
        .Select(row => new ExperimentSampleTaskRow { RowId = row.RowId, SampleId = row.SampleId, SampleBarcode = row.SampleBarcode?.Trim() ?? string.Empty, Position = row.Position?.Trim() ?? string.Empty, DisplayName = row.DisplayName?.Trim() ?? string.Empty, Order = row.Order })
        .OrderBy(row => row.Order).ThenBy(row => row.RowId).ToArray();

    private static IReadOnlyList<ExperimentSampleTaskRow> Rows(ExperimentSampleVerificationRecord record) =>
        ExperimentSchedulingPersistence.Deserialize(record.RowsJson, Array.Empty<ExperimentSampleTaskRow>());

    private static ExperimentSample MapSample(ExperimentSampleRecord record) => new()
    {
        SampleId = record.SampleId, BusinessSampleId = record.BusinessSampleId, BatchId = record.BatchId, Barcode = record.Barcode.Trim(), DisplayName = record.DisplayName,
        Status = ExperimentSchedulingPersistence.ParseStatus<ExperimentSampleStatus>(record.Status), CreatedAt = ExperimentSchedulingPersistence.ToOffset(record.CreatedAtUtc), UpdatedAt = ExperimentSchedulingPersistence.ToOffset(record.UpdatedAtUtc)
    };

    private static ExperimentSampleVerification MapVerification(ExperimentSampleVerificationRecord record) => new()
    {
        VerificationId = record.VerificationId, ExperimentJobId = record.ExperimentJobId, Revision = record.Revision,
        Status = ExperimentSchedulingPersistence.ParseStatus<ExperimentSampleVerificationStatus>(record.Status), Rows = Rows(record), SnapshotHash = record.SnapshotHash,
        VerifiedBy = record.VerifiedBy, VerifiedAt = ExperimentSchedulingPersistence.ToOffset(record.VerifiedAtUtc), VerificationNote = record.VerificationNote,
        InvalidatedAt = ExperimentSchedulingPersistence.ToOffset(record.InvalidatedAtUtc), InvalidationReason = record.InvalidationReason,
        CreatedAt = ExperimentSchedulingPersistence.ToOffset(record.CreatedAtUtc), UpdatedAt = ExperimentSchedulingPersistence.ToOffset(record.UpdatedAtUtc)
    };

    private static ExperimentSample NormalizeSample(ExperimentSample? source, Guid sampleId)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SampleId != Guid.Empty && source.SampleId != sampleId) throw new ArgumentException("The sample id does not match the route.", nameof(source));
        return new ExperimentSample { SampleId = sampleId, BusinessSampleId = RequireText(source.BusinessSampleId, nameof(source.BusinessSampleId)), BatchId = RequireText(source.BatchId, nameof(source.BatchId)), Barcode = RequireText(source.Barcode, nameof(source.Barcode)), DisplayName = source.DisplayName?.Trim() ?? string.Empty, Status = source.Status };
    }

    private static void EnsureMutable(ExperimentJobRecord job)
    {
        if (!Enum.TryParse<ExperimentJobStatus>(job.Status, true, out var status) || status is ExperimentJobStatus.Admitted or ExperimentJobStatus.Running or ExperimentJobStatus.Cancelled or ExperimentJobStatus.Completed or ExperimentJobStatus.Failed)
            throw Conflict("Task sample rows cannot be changed or re-verified after admission, execution, or terminal completion.", ExperimentSampleVerificationIssueCodes.VersionConflict);
    }

    private static void Invalidate(ExperimentSampleVerificationRecord record, DateTime now, string reason)
    {
        if (string.Equals(record.Status, ExperimentSampleVerificationStatus.Invalidated.ToString(), StringComparison.Ordinal)) return;
        record.Status = ExperimentSampleVerificationStatus.Invalidated.ToString();
        record.InvalidatedAtUtc = now;
        record.InvalidationReason = reason;
        record.UpdatedAtUtc = now;
    }

    private static ExperimentSampleVerificationValidationIssue Issue(ExperimentSampleTaskRow? row, string code, string message) => new()
    {
        RowId = row?.RowId == Guid.Empty ? null : row?.RowId,
        Order = row?.Order,
        Code = code,
        Message = message
    };

    private async Task<ExperimentJobRecord> FindJobAsync(Guid id, CancellationToken cancellationToken) =>
        id == Guid.Empty ? throw new ArgumentException("A non-empty experiment job id is required.", nameof(id)) :
        await database.ExperimentJobs.SingleOrDefaultAsync(item => item.JobId == id, cancellationToken) ?? throw new KeyNotFoundException($"Experiment job '{id}' was not found.");

    private async Task<T?> TryReplayAsync<T>(Guid requestId, string eventType, string fingerprint, CancellationToken cancellationToken) where T : class
    {
        var audit = await database.ExperimentSchedulingAudits.AsNoTracking().SingleOrDefaultAsync(item => item.RequestId == requestId, cancellationToken);
        if (audit is null) return null;
        if (!string.Equals(audit.EventType, eventType, StringComparison.Ordinal) || !string.Equals(audit.RequestFingerprint, fingerprint, StringComparison.Ordinal)) throw Conflict($"Request id '{requestId}' was already used for a different action or payload.", ExperimentSampleVerificationIssueCodes.VersionConflict);
        return ExperimentSchedulingPersistence.Deserialize<T?>(audit.ResultJson, null) ?? throw new InvalidOperationException($"Sample verification audit '{audit.Id}' does not contain a replay result.");
    }

    private void AddAudit<T>(NormalizedMetadata metadata, string fingerprint, string eventType, T result, Guid? jobId = null, IReadOnlyDictionary<string, string?>? details = null) => database.ExperimentSchedulingAudits.Add(new ExperimentSchedulingAuditRecord
    {
        Id = Guid.NewGuid(), EventType = eventType, Outcome = "Succeeded", RequestId = metadata.RequestId, RequestFingerprint = fingerprint, Actor = metadata.Actor, Reason = metadata.Reason, ExperimentJobId = jobId,
        DetailsJson = ExperimentSchedulingPersistence.Serialize(details ?? new Dictionary<string, string?>()), ResultJson = ExperimentSchedulingPersistence.Serialize(result), OccurredAtUtc = timeProvider.GetUtcNow().UtcDateTime
    });

    private static NormalizedMetadata NormalizeMetadata(Guid requestId, string? actor, string? reason) => new(requestId == Guid.Empty ? throw new ArgumentException("A non-empty request id is required.", nameof(requestId)) : requestId, RequireText(actor, nameof(actor)), RequireText(reason, nameof(reason)));
    private static string CreateFingerprint(string action, NormalizedMetadata metadata, object payload) => Hash(ExperimentSchedulingPersistence.Serialize(new { Action = action, metadata.Actor, metadata.Reason, Payload = payload }));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string RequireText(string? value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A non-empty value is required.", name) : value.Trim();
    private static string? NormalizeOptionalText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static ExperimentSampleVerificationException Conflict(string message, string code) => new(message, code);
    private async Task<T> ExecuteMutationAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken) { await mutationGate.EnterAsync(cancellationToken); try { return await action(); } finally { mutationGate.Exit(); } }
    private sealed record NormalizedMetadata(Guid RequestId, string Actor, string Reason);
}
