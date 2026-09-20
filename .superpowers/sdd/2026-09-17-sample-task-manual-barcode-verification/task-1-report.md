# Task 1 Report: Manual Sample Verification Persistence

## Scope and implementation

- Added stable sample and verification contracts, write/query requests, status enums, ordered task-row snapshots, and stable issue codes.
- Added `ExperimentSampleRecord` and `ExperimentSampleVerificationRecord`, plus `MesDbContext` sets/mappings.
- Added additive SQLite startup DDL and indexes for `ExperimentSamples` and `ExperimentSampleVerifications`. The schema retains the existing scheduling audit table and changes neither endpoint registration, DI services, nor runtime admission.
- `BusinessSampleId`, trim-only `NormalizedBarcode`, and `(ExperimentJobId, Revision)` have unique database indexes. `ExperimentSampleRecord.Barcode` derives `NormalizedBarcode` with `Trim()` only; it does not change barcode case or infer a barcode format.
- Persisted verification snapshot identity, rows, hash, creation time, and deletion are append-only-protected in `MesDbContext`. Status and verification/invalidation metadata remain mutable for the later state machine.

## TDD evidence

### RED

Before production types and schema were added:

```powershell
dotnet test tests\MesControlAgv.WorkflowContract.Tests\MesControlAgv.WorkflowContract.Tests.csproj --no-restore --filter "FullyQualifiedName~ExperimentSampleVerificationContractTests"
dotnet test tests\MesControlAgv.Mes.Tests\MesControlAgv.Mes.Tests.csproj --no-restore --filter "FullyQualifiedName~Existing_experiment_database_adds_sample_verification_tables"
```

The contract test failed to compile because every new sample-verification type was absent. The schema test failed with SQLite `no such table: ExperimentSampleVerifications`.

### GREEN

```powershell
dotnet test tests\MesControlAgv.WorkflowContract.Tests\MesControlAgv.WorkflowContract.Tests.csproj --no-restore --filter "FullyQualifiedName~ExperimentSampleVerificationContractTests"
# Passed: 1/1

dotnet test tests\MesControlAgv.Mes.Tests\MesControlAgv.Mes.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkflowRuntimeSchemaUpgradeTests"
# Passed: 7/7

dotnet test tests\MesControlAgv.Mes.Tests\MesControlAgv.Mes.Tests.csproj --no-restore
# Passed: 323/323
```

The schema tests cover fresh database unique constraints (including whitespace barcode variants), append-only snapshot updates/deletions, permitted status transition, and additive upgrade retaining prior plan, job, run, and audit rows.

## Review and self-review

- Independent review initially identified missing authoritative barcode normalization and mutable/deletable snapshot data. Both were fixed and covered by focused tests.
- Reviewed the final diff and `git diff --check`; no whitespace errors.
- Confirmed changes stay within persistence/contracts/tests; no field services, endpoint registrations, service registrations, or runtime admission code changed.

## Commit

`feat: add experiment sample verification persistence`

## Concerns

No blocking concerns. The Task 2 state-machine service must use the append-only persistence model by creating new revisions for row/hash changes and only updating allowed lifecycle metadata on existing records.
