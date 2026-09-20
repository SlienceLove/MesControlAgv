using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Owns the device-free preparation snapshot and the one-shot import boundary.
/// The scheduling mutation gate is intentionally held across import I/O so
/// sample verification and job admission cannot cross the persisted intent.
/// </summary>
internal sealed class ExperimentWorkstationPreparationService(
    MesDbContext database,
    IExperimentSampleVerificationGateCore verificationGate,
    ISampleWorkstationTemplateReader templateReader,
    ISampleWorkstationTaskImporter taskImporter,
    ISampleWorkstationBarcodeCommands barcodeCommands,
    ExperimentSchedulingMutationGate mutationGate,
    ProfileConfiguration profile,
    TimeProvider timeProvider) : IExperimentWorkstationPreparationService
{
    private const string PreparedEventType = "ExperimentWorkstationTaskPrepared";
    private const string ImportEventType = "ExperimentWorkstationTaskImport";

    public async Task<ExperimentWorkstationPreparation?> GetCurrentAsync(
        Guid experimentJobId,
        CancellationToken cancellationToken)
    {
        if (experimentJobId == Guid.Empty)
            throw new ArgumentException("A non-empty experiment job id is required.", nameof(experimentJobId));
        _ = await FindJobAsync(experimentJobId, cancellationToken);
        var record = await CurrentRecordAsync(experimentJobId, tracking: false, cancellationToken);
        return record is null ? null : Map(record);
    }

    public Task<ExperimentWorkstationPreparation> PrepareAsync(
        Guid experimentJobId,
        PrepareExperimentWorkstationTaskRequest request,
        CancellationToken cancellationToken) => ExecuteMutationAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(request);
        var metadata = NormalizeMetadata(request.RequestId, request.Actor, request.Reason);
        var normalized = NormalizeRequest(request);
        var fingerprint = Fingerprint(PreparedEventType, experimentJobId, metadata, normalized);
        var replay = await TryReplayAsync(metadata.RequestId, PreparedEventType, fingerprint, cancellationToken);
        if (replay is not null) return replay with { IsIdempotentReplay = true };

        var job = await FindJobAsync(experimentJobId, cancellationToken);
        EnsureJobMutable(job);
        await EnsureNoUnresolvedPreparationAsync(experimentJobId, normalized.DeviceId, null, cancellationToken);
        var scopePin = await RequirePreparationScopeAsync(job, normalized.DeviceId, cancellationToken);
        await EnsureDeviceAvailableAsync(normalized.DeviceId, cancellationToken);
        var verification = await verificationGate.RequireVerifiedCurrentWhileMutationGateHeldAsync(
            experimentJobId,
            normalized.VerificationRevision,
            normalized.VerificationSnapshotHash,
            cancellationToken);

        var captured = await templateReader.GetTaskTemplateAsync(
            normalized.DeviceId,
            normalized.SourceTaskNo,
            cancellationToken);
        ValidateCapturedTemplate(captured, normalized.DeviceId, normalized.SourceTaskNo);

        var vendorTaskNo = CreateVendorTaskNo();
        if (normalized.Transfers is { Count: 0 })
            throw Conflict("An explicitly edited workstation task must contain at least one transfer.",
                ExperimentWorkstationPreparationIssueCodes.InvalidTemplateBinding);
        var generatedTemplate = captured.Template with
        {
            TaskNo = vendorTaskNo,
            Transfers = normalized.Transfers ?? captured.Template.Transfers
        };
        var generatedBytes = SampleWorkstationTemplateFile.Write(generatedTemplate);
        var generatedHash = Hash(generatedBytes);
        var payload = CreatePayload(captured, generatedTemplate, generatedHash, normalized.BottleBindings, verification);
        var payloadJson = ExperimentSchedulingPersistence.Serialize(payload);
        var payloadHash = Hash(payloadJson);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var revision = (await database.ExperimentWorkstationPreparations
            .Where(item => item.ExperimentJobId == experimentJobId)
            .Select(item => (long?)item.Revision)
            .MaxAsync(cancellationToken) ?? 0) + 1;
        var record = new ExperimentWorkstationPreparationRecord
        {
            PreparationId = Guid.NewGuid(),
            ExperimentJobId = experimentJobId,
            Revision = revision,
            WorkflowId = scopePin.WorkflowId,
            WorkflowVersion = scopePin.WorkflowVersion,
            ScheduleEntryId = scopePin.ScheduleEntryId,
            DeviceId = normalized.DeviceId,
            VendorTaskNo = vendorTaskNo,
            VerificationId = verification.VerificationId,
            VerificationRevision = verification.Revision,
            VerificationSnapshotHash = verification.SnapshotHash,
            PayloadJson = payloadJson,
            PayloadHash = payloadHash,
            Status = ExperimentWorkstationPreparationStatus.Prepared.ToString(),
            PreparedRequestId = metadata.RequestId,
            PreparedAtUtc = now
        };
        database.ExperimentWorkstationPreparations.Add(record);
        var result = Map(record);
        AddAudit(record, metadata, fingerprint, PreparedEventType, result, "Succeeded", null);
        await database.SaveChangesAsync(cancellationToken);
        return result;
    }, cancellationToken);

    public Task<ExperimentWorkstationPreparation> ImportAsync(
        Guid experimentJobId,
        Guid preparationId,
        ImportExperimentWorkstationTaskRequest request,
        CancellationToken cancellationToken) => ExecuteMutationAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(request);
        if (preparationId == Guid.Empty)
            throw new ArgumentException("A non-empty preparation id is required.", nameof(preparationId));
        var metadata = NormalizeMetadata(request.RequestId, request.Actor, request.Reason);
        var fingerprint = Fingerprint(ImportEventType, experimentJobId, metadata, new { preparationId });
        var replay = await TryReplayAsync(metadata.RequestId, ImportEventType, fingerprint, cancellationToken);
        if (replay is not null) return replay with { IsIdempotentReplay = true };

        var job = await FindJobAsync(experimentJobId, cancellationToken);
        EnsureJobMutable(job);
        var current = await CurrentRecordAsync(experimentJobId, tracking: true, cancellationToken)
            ?? throw Conflict("No workstation preparation exists for this job.", ExperimentWorkstationPreparationIssueCodes.VersionConflict);
        if (current.PreparationId != preparationId)
            throw Conflict("Only the current workstation preparation can be imported.", ExperimentWorkstationPreparationIssueCodes.VersionConflict);
        if (ParseStatus(current.Status) != ExperimentWorkstationPreparationStatus.Prepared)
            throw Conflict($"Preparation '{preparationId}' is already {current.Status}; device writes will not be replayed.",
                ParseStatus(current.Status) == ExperimentWorkstationPreparationStatus.Unknown
                    ? ExperimentWorkstationPreparationIssueCodes.ImportOutcomeUnknown
                    : ExperimentWorkstationPreparationIssueCodes.VersionConflict);

        await EnsureNoUnresolvedPreparationAsync(experimentJobId, current.DeviceId, current.PreparationId, cancellationToken);
        var scopePin = await RequirePreparationScopeAsync(job, current.DeviceId, cancellationToken);
        if (current.WorkflowId != scopePin.WorkflowId ||
            current.WorkflowVersion != scopePin.WorkflowVersion ||
            current.ScheduleEntryId != scopePin.ScheduleEntryId)
            throw Conflict("The job workflow or scheduled workstation changed after preparation; prepare a new task after resolving any uncertain import.",
                ExperimentWorkstationPreparationIssueCodes.RuntimeBindingInvalid);
        await EnsureDeviceAvailableAsync(current.DeviceId, cancellationToken);
        _ = await verificationGate.RequireVerifiedCurrentWhileMutationGateHeldAsync(
            experimentJobId,
            current.VerificationRevision,
            current.VerificationSnapshotHash,
            cancellationToken);

        var payload = Payload(current);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        current.Status = ExperimentWorkstationPreparationStatus.Importing.ToString();
        current.ImportRequestId = metadata.RequestId;
        current.ImportingAtUtc = now;
        current.LastError = null;
        var importing = Map(current);
        var audit = AddAudit(current, metadata, fingerprint, ImportEventType, importing, "Importing", null);
        await database.SaveChangesAsync(cancellationToken);

        try
        {
            var bytes = SampleWorkstationTemplateFile.Write(payload.GeneratedTemplate);
            if (!string.Equals(Hash(bytes), payload.GeneratedTemplateFileSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("The persisted generated template hash no longer matches its payload.");
            await using var stream = new MemoryStream(bytes, writable: false);
            var imported = await taskImporter.ImportTasksAsync(
                current.DeviceId,
                payload.GeneratedTemplateFileName,
                stream,
                cancellationToken);
            ValidateImportAcknowledgement(current, payload, imported);

            var barcodes = ToBarcodes(payload.BottleBindings);
            var updated = await barcodeCommands.UpdateTaskBarcodesAsync(
                current.DeviceId,
                current.VendorTaskNo,
                barcodes,
                cancellationToken);
            ValidateBarcodeAcknowledgement(current, updated);

            current.Status = ExperimentWorkstationPreparationStatus.Imported.ToString();
            current.ImportedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            current.LastError = null;
            var result = Map(current);
            audit.Outcome = "Succeeded";
            audit.ResultJson = ExperimentSchedulingPersistence.Serialize(result);
            await database.SaveChangesAsync(cancellationToken);
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            current.Status = ExperimentWorkstationPreparationStatus.Unknown.ToString();
            current.UnknownAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            current.LastError = TrimError(exception.Message);
            var result = Map(current);
            audit.Outcome = "Unknown";
            audit.Code = ExperimentWorkstationPreparationIssueCodes.ImportOutcomeUnknown;
            audit.ResultJson = ExperimentSchedulingPersistence.Serialize(result);
            await database.SaveChangesAsync(CancellationToken.None);
            return result;
        }
    }, cancellationToken);

    private async Task EnsureNoUnresolvedPreparationAsync(
        Guid experimentJobId,
        string deviceId,
        Guid? ownPreparationId,
        CancellationToken cancellationToken)
    {
        var unresolvedStatuses = new[]
        {
            ExperimentWorkstationPreparationStatus.Importing.ToString(),
            ExperimentWorkstationPreparationStatus.Unknown.ToString()
        };
        var normalizedDeviceId = deviceId.ToUpper();
        var unresolved = await database.ExperimentWorkstationPreparations.AsNoTracking()
            .Where(preparation =>
                (!ownPreparationId.HasValue || preparation.PreparationId != ownPreparationId.Value) &&
                unresolvedStatuses.Contains(preparation.Status) &&
                (preparation.ExperimentJobId == experimentJobId ||
                 preparation.DeviceId.ToUpper() == normalizedDeviceId))
            .OrderByDescending(preparation => preparation.Revision)
            .FirstOrDefaultAsync(cancellationToken);
        if (unresolved is null) return;
        var unknown = string.Equals(
            unresolved.Status,
            ExperimentWorkstationPreparationStatus.Unknown.ToString(),
            StringComparison.Ordinal);
        throw Conflict(
            $"Preparation '{unresolved.PreparationId}' remains {unresolved.Status}; resolve it before preparing or importing another task for this job or device.",
            unknown
                ? ExperimentWorkstationPreparationIssueCodes.ImportOutcomeUnknown
                : ExperimentWorkstationPreparationIssueCodes.DeviceBusy);
    }

    private async Task<PreparationScopePin> RequirePreparationScopeAsync(
        ExperimentJobRecord job,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var workflow = await database.WorkflowVersions.AsNoTracking().SingleOrDefaultAsync(
            item => item.WorkflowId == job.WorkflowId && item.Version == job.WorkflowVersion,
            cancellationToken);
        if (workflow is null ||
            !string.Equals(workflow.Status, WorkflowVersionStatus.Published.ToString(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(workflow.PublishStatus, WorkflowPublishStatus.Published.ToString(), StringComparison.OrdinalIgnoreCase))
            throw Conflict("The job's fixed workflow version is missing or not published.",
                ExperimentWorkstationPreparationIssueCodes.RuntimeBindingInvalid);
        var workstationNodes = WorkflowPersistence.DeserializeDefinition(workflow.DefinitionJson).Nodes
            .Where(node => string.Equals(
                node.NodeTypeId,
                WorkflowGraphNodeTypeIds.SampleWorkstationExecuteExistingTask,
                StringComparison.Ordinal))
            .ToArray();
        if (workstationNodes.Length != 1 ||
            !workstationNodes[0].Configuration.TryGetValue(WorkflowNodeConfigurationKeys.DeviceId, out var configuredDeviceId) ||
            !string.Equals(configuredDeviceId?.Trim(), deviceId, StringComparison.OrdinalIgnoreCase))
            throw Conflict("Preparation requires exactly one workstation node fixed to the requested device.",
                ExperimentWorkstationPreparationIssueCodes.RuntimeBindingInvalid);

        var schedules = await database.ScheduleEntries.AsNoTracking()
            .Where(entry => entry.ExperimentJobId == job.JobId)
            .ToListAsync(cancellationToken);
        if (schedules.Count != 1 ||
            !string.Equals(schedules[0].Status, ScheduleEntryStatus.Scheduled.ToString(), StringComparison.Ordinal))
            throw Conflict("Preparation requires exactly one Scheduled entry for the job.",
                ExperimentWorkstationPreparationIssueCodes.RuntimeBindingInvalid);
        var workstationResources = ExperimentSchedulingPersistence.Deserialize(
                schedules[0].RequestedResourcesJson,
                Array.Empty<ExperimentResourceReference>())
            .Where(resource => string.Equals(
                resource.ResourceType,
                ExperimentResourceTypeIds.Workstation,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (workstationResources.Length != 1 ||
            !string.Equals(workstationResources[0].ResourceId, deviceId, StringComparison.OrdinalIgnoreCase))
            throw Conflict("The scheduled workstation does not match the requested preparation device.",
                ExperimentWorkstationPreparationIssueCodes.RuntimeBindingInvalid);
        return new PreparationScopePin(
            workflow.WorkflowId,
            workflow.Version,
            schedules[0].ScheduleEntryId);
    }

    private async Task EnsureDeviceAvailableAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        var configured = (profile.WorkflowDevices ?? []).SingleOrDefault(device =>
            string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (configured is null || !configured.Enabled || !configured.ControlEnabled ||
            !string.Equals(configured.DeviceFamily, WorkflowDeviceFamilyIds.SampleWorkstation, StringComparison.OrdinalIgnoreCase) ||
            !(configured.CapabilityIds ?? []).Contains(WorkflowCapabilityIds.SampleWorkstationStartExistingTask, StringComparer.OrdinalIgnoreCase))
            throw Conflict($"Sample workstation '{deviceId}' is not enabled for controlled task execution.",
                ExperimentWorkstationPreparationIssueCodes.InvalidTemplateBinding);

        var resourceKey = ExperimentResourceKeys.Create(ExperimentResourceTypeIds.Workstation, deviceId);
        var activeLease = await database.WorkflowResourceLeases.AsNoTracking().AnyAsync(
            lease => lease.ActiveResourceKey == resourceKey,
            cancellationToken);
        var activeOperationStatuses = new[]
        {
            WorkflowDeviceOperationStatus.Prepared.ToString(),
            WorkflowDeviceOperationStatus.StartPending.ToString(),
            WorkflowDeviceOperationStatus.Accepted.ToString(),
            WorkflowDeviceOperationStatus.Running.ToString()
        };
        var activeOperation = await database.WorkflowDeviceOperations.AsNoTracking().AnyAsync(
            operation => operation.DeviceId != null &&
                operation.DeviceId.ToUpper() == deviceId.ToUpper() &&
                operation.CapabilityId == WorkflowCapabilityIds.SampleWorkstationStartExistingTask &&
                activeOperationStatuses.Contains(operation.Status),
            cancellationToken);
        if (activeLease || activeOperation)
            throw Conflict($"Sample workstation '{deviceId}' is reserved by an active run or import.",
                ExperimentWorkstationPreparationIssueCodes.DeviceBusy);
    }

    private static ExperimentWorkstationPreparationPayload CreatePayload(
        SampleWorkstationTemplateResponse captured,
        SampleWorkstationTaskTemplate generatedTemplate,
        string generatedHash,
        IReadOnlyList<PrepareWorkstationBottleBinding> requestBindings,
        ExperimentSampleVerification verification)
    {
        var sourceKeys = generatedTemplate.Transfers
            .Select(transfer => NormalizeSource(new WorkstationTemplateSourceKey
            {
                Module = transfer.SourceModule,
                X = transfer.SourceX,
                Y = transfer.SourceY
            }))
            .Distinct()
            .ToArray();
        if (sourceKeys.Length is < 1 or > 2)
            throw Conflict("The captured template must use one or two distinct source bottle positions.",
                ExperimentWorkstationPreparationIssueCodes.InvalidTemplateBinding);
        if (requestBindings.Count != 2 ||
            requestBindings.Select(binding => binding.BottleNumber).Order().SequenceEqual(new[] { 1, 2 }) == false)
            throw Conflict("Exactly two distinct barcode slots (1 and 2) must be bound.",
                ExperimentWorkstationPreparationIssueCodes.InvalidTemplateBinding);

        var rowsBySample = verification.Rows.ToDictionary(row => row.SampleId);
        var normalizedBindings = new List<PreparedWorkstationBottleBinding>();
        var usedSources = new HashSet<WorkstationTemplateSourceKey>();
        foreach (var binding in requestBindings.OrderBy(binding => binding.BottleNumber))
        {
            if (!rowsBySample.TryGetValue(binding.SampleId, out var row))
                throw Conflict($"Bottle {binding.BottleNumber} is not bound to a sample in the verified snapshot.",
                    ExperimentWorkstationPreparationIssueCodes.InvalidTemplateBinding);
            var source = binding.TemplateSource is null ? null : NormalizeSource(binding.TemplateSource);
            if (source is not null && (!sourceKeys.Contains(source) || !usedSources.Add(source)))
                throw Conflict($"Bottle {binding.BottleNumber} does not name a unique source key from the captured template.",
                    ExperimentWorkstationPreparationIssueCodes.InvalidTemplateBinding);
            normalizedBindings.Add(new PreparedWorkstationBottleBinding
            {
                BottleNumber = binding.BottleNumber,
                IsUsedByTemplate = source is not null,
                TemplateSource = source,
                VerificationRowId = row.RowId,
                SampleId = row.SampleId,
                BusinessSampleId = row.BusinessSampleId,
                SampleBarcode = row.SampleBarcode
            });
        }
        if (!sourceKeys.OrderBy(KeyText).SequenceEqual(usedSources.OrderBy(KeyText)))
            throw Conflict("Every source position used by the template must have one explicit bottle-slot binding, with no guessed positions.",
                ExperimentWorkstationPreparationIssueCodes.InvalidTemplateBinding);

        var bindingsBySource = normalizedBindings.Where(binding => binding.TemplateSource is not null)
            .ToDictionary(binding => binding.TemplateSource!);
        var transfers = generatedTemplate.Transfers.Select((transfer, index) =>
        {
            var source = NormalizeSource(new WorkstationTemplateSourceKey
            {
                Module = transfer.SourceModule,
                X = transfer.SourceX,
                Y = transfer.SourceY
            });
            var binding = bindingsBySource[source];
            return new PreparedWorkstationTransfer
            {
                Order = index + 1,
                Transfer = transfer,
                BottleNumber = binding.BottleNumber,
                VerificationRowId = binding.VerificationRowId,
                SourceSampleId = binding.SampleId,
                SourceBusinessSampleId = binding.BusinessSampleId,
                SourceSampleBarcode = binding.SampleBarcode
            };
        }).ToArray();

        return new ExperimentWorkstationPreparationPayload
        {
            SourceTemplateFileName = captured.FileName,
            SourceTemplateFileSha256 = captured.FileSha256,
            SourceTemplate = captured.Template,
            GeneratedTemplateFileName = generatedTemplate.TaskNo + ".xlsx",
            GeneratedTemplateFileSha256 = generatedHash,
            GeneratedTemplate = generatedTemplate,
            BottleBindings = normalizedBindings,
            Transfers = transfers
        };
    }

    private static SampleWorkstationTaskBarcodes ToBarcodes(
        IReadOnlyList<PreparedWorkstationBottleBinding> bindings) => new()
    {
        SampleBarcode1 = bindings.Single(binding => binding.BottleNumber == 1).SampleBarcode,
        SampleBarcode2 = bindings.Single(binding => binding.BottleNumber == 2).SampleBarcode
    };

    private static void ValidateCapturedTemplate(
        SampleWorkstationTemplateResponse response,
        string deviceId,
        string sourceTaskNo)
    {
        if (!string.Equals(response.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(response.Template.TaskNo, sourceTaskNo, StringComparison.Ordinal) ||
            response.FileContent is null ||
            !string.Equals(Hash(response.FileContent), response.FileSha256, StringComparison.Ordinal) ||
            !SampleWorkstationTemplateFile.SameContent(response.Template, SampleWorkstationTemplateFile.Read(response.FileContent)))
            throw Conflict("The captured workstation template identity or content hash did not match the request.",
                ExperimentWorkstationPreparationIssueCodes.InvalidTemplateBinding);
    }

    private static void ValidateImportAcknowledgement(
        ExperimentWorkstationPreparationRecord record,
        ExperimentWorkstationPreparationPayload payload,
        SampleWorkstationTaskImportResponse response)
    {
        if (!response.ReadbackVerified || response.Template is null ||
            !string.Equals(response.DeviceId, record.DeviceId, StringComparison.OrdinalIgnoreCase) ||
            response.TaskNos is null || !response.TaskNos.SequenceEqual(new[] { record.VendorTaskNo }) ||
            !string.Equals(response.FileSha256, payload.GeneratedTemplateFileSha256, StringComparison.Ordinal) ||
            !SampleWorkstationTemplateFile.SameContent(payload.GeneratedTemplate, response.Template))
            throw new InvalidOperationException("The workstation did not confirm the exact generated task snapshot.");
    }

    private static void ValidateBarcodeAcknowledgement(
        ExperimentWorkstationPreparationRecord record,
        SampleWorkstationCommandResponse response)
    {
        if (!response.Acknowledged || response.Operation != SampleWorkstationCommandOperation.UpdateTaskBarcodes ||
            !string.Equals(response.DeviceId, record.DeviceId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(response.TaskNo, record.VendorTaskNo, StringComparison.Ordinal))
            throw new InvalidOperationException("The workstation barcode acknowledgement did not match the prepared task identity.");
    }

    private ExperimentSchedulingAuditRecord AddAudit(
        ExperimentWorkstationPreparationRecord record,
        Metadata metadata,
        string fingerprint,
        string eventType,
        ExperimentWorkstationPreparation result,
        string outcome,
        string? code)
    {
        var payload = Payload(record);
        var audit = new ExperimentSchedulingAuditRecord
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            Outcome = outcome,
            Code = code,
            RequestId = metadata.RequestId,
            RequestFingerprint = fingerprint,
            Actor = metadata.Actor,
            Reason = metadata.Reason,
            ExperimentJobId = record.ExperimentJobId,
            DetailsJson = ExperimentSchedulingPersistence.Serialize(new
            {
                record.PreparationId,
                record.Revision,
                record.WorkflowId,
                record.WorkflowVersion,
                record.ScheduleEntryId,
                record.DeviceId,
                record.VendorTaskNo,
                record.VerificationId,
                record.VerificationRevision,
                record.VerificationSnapshotHash,
                record.PayloadHash,
                sourceTemplateTaskNo = payload.SourceTemplate.TaskNo,
                sourceTemplateHash = payload.SourceTemplateFileSha256,
                sources = payload.BottleBindings.Select(binding => new
                {
                    binding.BottleNumber,
                    binding.IsUsedByTemplate,
                    binding.TemplateSource,
                    binding.VerificationRowId,
                    binding.SampleId,
                    binding.BusinessSampleId,
                    binding.SampleBarcode
                })
            }),
            ResultJson = ExperimentSchedulingPersistence.Serialize(result),
            OccurredAtUtc = timeProvider.GetUtcNow().UtcDateTime
        };
        database.ExperimentSchedulingAudits.Add(audit);
        return audit;
    }

    private async Task<ExperimentWorkstationPreparation?> TryReplayAsync(
        Guid requestId,
        string eventType,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var audit = await database.ExperimentSchedulingAudits.AsNoTracking()
            .SingleOrDefaultAsync(item => item.RequestId == requestId, cancellationToken);
        if (audit is null) return null;
        if (!string.Equals(audit.EventType, eventType, StringComparison.Ordinal) ||
            !string.Equals(audit.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            throw Conflict($"Request id '{requestId}' was already used for a different action or payload.",
                ExperimentWorkstationPreparationIssueCodes.VersionConflict);
        return ExperimentSchedulingPersistence.Deserialize<ExperimentWorkstationPreparation?>(audit.ResultJson, null)
            ?? throw new InvalidOperationException($"Preparation audit '{audit.Id}' does not contain a replay result.");
    }

    private async Task<ExperimentJobRecord> FindJobAsync(Guid experimentJobId, CancellationToken cancellationToken) =>
        experimentJobId == Guid.Empty
            ? throw new ArgumentException("A non-empty experiment job id is required.", nameof(experimentJobId))
            : await database.ExperimentJobs.SingleOrDefaultAsync(job => job.JobId == experimentJobId, cancellationToken)
                ?? throw new KeyNotFoundException($"Experiment job '{experimentJobId}' was not found.");

    private Task<ExperimentWorkstationPreparationRecord?> CurrentRecordAsync(
        Guid experimentJobId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var query = tracking
            ? database.ExperimentWorkstationPreparations.AsQueryable()
            : database.ExperimentWorkstationPreparations.AsNoTracking();
        return query.Where(item => item.ExperimentJobId == experimentJobId)
            .OrderByDescending(item => item.Revision)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static void EnsureJobMutable(ExperimentJobRecord job)
    {
        var status = ExperimentSchedulingPersistence.ParseStatus<ExperimentJobStatus>(job.Status);
        if (job.WorkflowRunId is not null || status is ExperimentJobStatus.Admitted or ExperimentJobStatus.Running or
            ExperimentJobStatus.Completed or ExperimentJobStatus.Cancelled or ExperimentJobStatus.Failed)
            throw Conflict("Workstation preparation cannot change after runtime admission or terminal completion.",
                ExperimentWorkstationPreparationIssueCodes.VersionConflict);
    }

    private static PrepareExperimentWorkstationTaskRequest NormalizeRequest(PrepareExperimentWorkstationTaskRequest request) => request with
    {
        DeviceId = RequireText(request.DeviceId, nameof(request.DeviceId)),
        SourceTaskNo = RequireText(request.SourceTaskNo, nameof(request.SourceTaskNo)),
        VerificationSnapshotHash = RequireText(request.VerificationSnapshotHash, nameof(request.VerificationSnapshotHash)),
        Transfers = request.Transfers?.ToArray(),
        BottleBindings = (request.BottleBindings ?? []).Select(binding => binding with
        {
            TemplateSource = binding.TemplateSource is null ? null : NormalizeSource(binding.TemplateSource)
        }).OrderBy(binding => binding.BottleNumber).ToArray()
    };

    private static WorkstationTemplateSourceKey NormalizeSource(WorkstationTemplateSourceKey source)
    {
        if (source.X <= 0 || source.Y <= 0)
            throw new ArgumentException("Template source coordinates must be positive.", nameof(source));
        return source with { Module = RequireText(source.Module, nameof(source.Module)) };
    }

    private static string KeyText(WorkstationTemplateSourceKey key) =>
        $"{key.Module}\u001f{key.X.ToString(CultureInfo.InvariantCulture)}\u001f{key.Y.ToString(CultureInfo.InvariantCulture)}";

    private static ExperimentWorkstationPreparationPayload Payload(ExperimentWorkstationPreparationRecord record) =>
        ExperimentSchedulingPersistence.Deserialize<ExperimentWorkstationPreparationPayload?>(record.PayloadJson, null)
        ?? throw new InvalidOperationException($"Preparation '{record.PreparationId}' has no payload.");

    internal static ExperimentWorkstationPreparation Map(ExperimentWorkstationPreparationRecord record) => new()
    {
        PreparationId = record.PreparationId,
        ExperimentJobId = record.ExperimentJobId,
        Revision = record.Revision,
        WorkflowId = record.WorkflowId,
        WorkflowVersion = record.WorkflowVersion,
        ScheduleEntryId = record.ScheduleEntryId,
        DeviceId = record.DeviceId,
        VendorTaskNo = record.VendorTaskNo,
        VerificationId = record.VerificationId,
        VerificationRevision = record.VerificationRevision,
        VerificationSnapshotHash = record.VerificationSnapshotHash,
        Payload = Payload(record),
        PayloadHash = record.PayloadHash,
        Status = ParseStatus(record.Status),
        PreparedRequestId = record.PreparedRequestId,
        ImportRequestId = record.ImportRequestId,
        PreparedAt = ExperimentSchedulingPersistence.ToOffset(record.PreparedAtUtc),
        ImportingAt = ExperimentSchedulingPersistence.ToOffset(record.ImportingAtUtc),
        ImportedAt = ExperimentSchedulingPersistence.ToOffset(record.ImportedAtUtc),
        UnknownAt = ExperimentSchedulingPersistence.ToOffset(record.UnknownAtUtc),
        LastError = record.LastError
    };

    private static ExperimentWorkstationPreparationStatus ParseStatus(string value) =>
        ExperimentSchedulingPersistence.ParseStatus<ExperimentWorkstationPreparationStatus>(value);

    // Legacy vendor TaskNo is varchar(20). Keep 72 random bits in uppercase
    // ASCII; timestamps and full provenance remain in the central records.
    // Only new preparations use this format; persisted task numbers are untouched.
    private static string CreateVendorTaskNo() =>
        "WS" + Convert.ToHexString(RandomNumberGenerator.GetBytes(9));

    private static Metadata NormalizeMetadata(Guid requestId, string? actor, string? reason) => new(
        requestId == Guid.Empty ? throw new ArgumentException("A non-empty request id is required.", nameof(requestId)) : requestId,
        RequireText(actor, nameof(actor)),
        RequireText(reason, nameof(reason)));

    private static string Fingerprint(string action, Guid jobId, Metadata metadata, object payload) =>
        Hash(ExperimentSchedulingPersistence.Serialize(new { action, jobId, metadata.Actor, metadata.Reason, payload }));
    private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string RequireText(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A non-empty value is required.", name) : value.Trim();
    private static string TrimError(string value) => value.Length <= 2048 ? value : value[..2048];
    private static ExperimentWorkstationPreparationException Conflict(string message, string code) => new(message, code);

    private async Task<T> ExecuteMutationAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await mutationGate.EnterAsync(cancellationToken);
        try { return await action(); }
        finally { mutationGate.Exit(); }
    }

    private sealed record Metadata(Guid RequestId, string Actor, string Reason);
    private sealed record PreparationScopePin(Guid WorkflowId, int WorkflowVersion, Guid ScheduleEntryId);
}
