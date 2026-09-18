# Task 1 report: central workstation task preparation backend

Status: DONE

## Delivered behavior

- MES persists append-only `ExperimentWorkstationPreparations` payloads and mutable lifecycle evidence (`Prepared -> Importing -> Imported` or `Unknown`). Startup creates the new table/indexes for both fresh and existing SQLite databases without destructive schema changes.
- Prepare captures the adapter's read-only template, generates a server-owned unique vendor task number, optionally applies edited transfer rows, generates a deterministic XLSX/hash, binds verified samples to explicit bottle slots and actual generated-template source keys, and performs no device write.
- Import verifies that the preparation and verified sample snapshot are still current, persists `Importing` before I/O, calls `ISampleWorkstationTaskImporter.ImportTasksAsync` once, then calls `ISampleWorkstationBarcodeCommands.UpdateTaskBarcodesAsync` once, and verifies the exact device/task/operation acknowledgement. It never starts the task.
- Import request IDs are durable. A same-request replay returns `IsIdempotentReplay=true` without I/O. A crash-visible `Importing` state or an `Unknown` result blocks all new import requests; it never automatically reissues an uncertain write.
- Admission keeps the legacy no-preparation behavior. Once any preparation exists for a job, only the latest, `Imported`, current-verification preparation is accepted. Admission also requires exactly one workstation node, its fixed device to match, and the same workstation resource to be scheduled/leased.
- Admission freezes preparation ID/hash, generated task number, and both barcodes into the admitted run's immutable definition snapshot and current/future workstation node input. Device-operation request summaries retain the same evidence. The published workflow is unchanged.
- The workstation worker validates the frozen binding against job/preparation persistence, then uses the existing barcode start overload exactly once. Legacy task-number-only starts and read-only restart recovery remain unchanged.
- Audit entries link the preparation, source template/hash, generated task, verification ID/revision/hash, source positions, bottle slots, sample IDs, business IDs, and barcodes. Transfer provenance is descriptive and makes no per-well execution-success claim.

## HTTP contract for Task 2 (WPF)

Existing read-only template route:

- `GET /api/workstations/{deviceId}/tasks/{taskNo}/template`
- Returns `SampleWorkstationTemplateResponse` with `DeviceId`, `FileName`, raw `FileContent`, raw `FileSha256`, parsed `Template`, and `ObservedAtUtc`.

New preparation routes:

- `GET /api/experiment-jobs/{jobId}/workstation-preparations/current`
  - `200 ExperimentWorkstationPreparation`, or `404` when none exists.
- `POST /api/experiment-jobs/{jobId}/workstation-preparations/prepare`
  - Body: `PrepareExperimentWorkstationTaskRequest`.
  - Returns `201 ExperimentWorkstationPreparation` and `Location: /api/experiment-jobs/{jobId}/workstation-preparations/current`.
- `POST /api/experiment-jobs/{jobId}/workstation-preparations/{preparationId}/import`
  - Body: `ImportExperimentWorkstationTaskRequest`.
  - Returns `200 ExperimentWorkstationPreparation` for `Imported`, `Unknown`, or an idempotent replay.
  - Validation/state conflicts return `409` with `{ detail, code }`.

Application boundary:

```csharp
public interface IExperimentWorkstationPreparationService
{
    Task<ExperimentWorkstationPreparation?> GetCurrentAsync(Guid experimentJobId, CancellationToken cancellationToken);
    Task<ExperimentWorkstationPreparation> PrepareAsync(Guid experimentJobId, PrepareExperimentWorkstationTaskRequest request, CancellationToken cancellationToken);
    Task<ExperimentWorkstationPreparation> ImportAsync(Guid experimentJobId, Guid preparationId, ImportExperimentWorkstationTaskRequest request, CancellationToken cancellationToken);
}
```

Prepare DTO:

```csharp
public sealed record PrepareExperimentWorkstationTaskRequest
{
    Guid RequestId;
    string Actor;
    string Reason;
    string DeviceId;
    string SourceTaskNo;
    int VerificationRevision;
    string VerificationSnapshotHash;
    IReadOnlyList<SampleWorkstationTransferRow>? Transfers;
    IReadOnlyList<PrepareWorkstationBottleBinding> BottleBindings;
}

public sealed record PrepareWorkstationBottleBinding
{
    int BottleNumber; // exactly slots 1 and 2
    Guid SampleId;    // must be present in the current verified snapshot
    WorkstationTemplateSourceKey? TemplateSource;
}

public sealed record WorkstationTemplateSourceKey
{
    string Module;
    int X;
    int Y;
}
```

`Transfers == null` means clone the captured source template. A supplied nonempty list is the edited transfer table. The server always owns the generated `TaskNo`; the client cannot override it. An explicitly supplied empty transfer list is invalid.

`BottleBindings` must contain exactly slots 1 and 2. Every distinct source key used by the generated transfers must be bound exactly once, using the exact `SourceModule/SourceX/SourceY` values from those rows. Do not infer slot 1/2 from row order or coordinates and do not ask the operator to re-enter target XYZ data.

For a one-source template, the unused second barcode slot still names a verified sample/barcode but has `TemplateSource=null`. Its returned `PreparedWorkstationBottleBinding.IsUsedByTemplate` is `false`, and no returned `PreparedWorkstationTransfer` points to it. This is barcode-slot identity only; the UI must not claim that unused bottle dispensed anything.

Import DTO:

```csharp
public sealed record ImportExperimentWorkstationTaskRequest
{
    Guid RequestId;
    string Actor;
    string Reason;
}
```

Result DTO highlights:

```csharp
public sealed record ExperimentWorkstationPreparation
{
    Guid PreparationId;
    Guid ExperimentJobId;
    string DeviceId;
    string VendorTaskNo;
    Guid VerificationId;
    int VerificationRevision;
    string VerificationSnapshotHash;
    ExperimentWorkstationPreparationPayload Payload;
    string PayloadHash;
    ExperimentWorkstationPreparationStatus Status; // Prepared, Importing, Imported, Unknown
    Guid PreparedRequestId;
    Guid? ImportRequestId;
    DateTimeOffset PreparedAt;
    DateTimeOffset? ImportingAt;
    DateTimeOffset? ImportedAt;
    DateTimeOffset? UnknownAt;
    string? LastError;
    bool IsIdempotentReplay;
}
```

`Payload` contains separate original-source template/file/hash evidence, generated template/file/hash evidence, `BottleBindings`, and ordered `Transfers`. Each prepared transfer includes its original template transfer values plus exact bottle number, verification row ID, source sample ID, business ID, and barcode.

Suggested WPF button rules:

- Save/prepare only with a current `Verified` sample snapshot and explicit bindings for slots 1 and 2.
- Disable import while local edits are dirty; backend still treats persisted preparation as authoritative.
- Enable import only for `Prepared` current records.
- Enable job admission only for `Imported` current records. `Importing` and `Unknown` require operator/manual reconciliation; there is intentionally no retry/reconcile command in this task.
- Switching jobs should discard stale HTTP responses by job/preparation identity.

## Verification

- `dotnet build src/MesControlAgv.Mes/MesControlAgv.Mes.csproj --no-restore` — passed, 0 warnings/errors.
- Focused MES lifecycle/schema/legacy tests — passed, 18/18.
- Focused deterministic XLSX test — passed, 1/1.
- `dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore` — passed, 369/369.
- `dotnet test tests/MesControlAgv.WorkflowContract.Tests/MesControlAgv.WorkflowContract.Tests.csproj --no-restore` — passed, 74/74.

All tests use local temporary SQLite databases and in-process workstation fakes; no real IP, adapter, service, or device was contacted.

## Concerns / follow-up boundary

- `Importing` after a process crash and `Unknown` after uncertain/partial I/O intentionally remain fail-closed. A future manually authorized reconciliation feature may inspect the instrument and append a resolution, but automatic retry is unsafe and is not implemented here.
- The WPF should display the two barcode slots separately from used-template provenance so `IsUsedByTemplate=false` is not presented as a dispensed source.
