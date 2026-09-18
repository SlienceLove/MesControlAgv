using MesControlAgv.Domain;
using Microsoft.EntityFrameworkCore;
using MesControlAgv.Mes.Entities;

namespace MesControlAgv.Mes.Data;

public sealed class MesDbContext(DbContextOptions<MesDbContext> options) : DbContext(options)
{
    public DbSet<TransportTask> TransportTasks => Set<TransportTask>();

    public DbSet<TaskEventRecord> TaskEvents => Set<TaskEventRecord>();

    public DbSet<AgvSnapshot> AgvSnapshots => Set<AgvSnapshot>();

    public DbSet<WorkflowVersionRecord> WorkflowVersions => Set<WorkflowVersionRecord>();

    public DbSet<WorkflowExecutionRecord> WorkflowExecutions => Set<WorkflowExecutionRecord>();

    public DbSet<WorkflowNodeExecutionRecord> WorkflowNodeExecutions => Set<WorkflowNodeExecutionRecord>();

    public DbSet<WorkflowDeviceOperationRecord> WorkflowDeviceOperations => Set<WorkflowDeviceOperationRecord>();

    public DbSet<WorkflowAuditRecord> WorkflowAudits => Set<WorkflowAuditRecord>();

    public DbSet<WorkflowRuntimeInteractionRecord> WorkflowRuntimeInteractions =>
        Set<WorkflowRuntimeInteractionRecord>();

    public DbSet<ExperimentPlanRecord> ExperimentPlans => Set<ExperimentPlanRecord>();

    public DbSet<ExperimentJobRecord> ExperimentJobs => Set<ExperimentJobRecord>();

    public DbSet<ExperimentSampleRecord> ExperimentSamples => Set<ExperimentSampleRecord>();

    public DbSet<ExperimentSampleVerificationRecord> ExperimentSampleVerifications =>
        Set<ExperimentSampleVerificationRecord>();

    public DbSet<ExperimentWorkstationPreparationRecord> ExperimentWorkstationPreparations =>
        Set<ExperimentWorkstationPreparationRecord>();

    public DbSet<ExperimentRunRecord> ExperimentRuns => Set<ExperimentRunRecord>();

    public DbSet<ScheduleEntryRecord> ScheduleEntries => Set<ScheduleEntryRecord>();

    public DbSet<ResourceReservationRecord> ResourceReservations => Set<ResourceReservationRecord>();

    public DbSet<WorkflowResourceLeaseRecord> WorkflowResourceLeases => Set<WorkflowResourceLeaseRecord>();

    public DbSet<ExperimentSchedulingAuditRecord> ExperimentSchedulingAudits =>
        Set<ExperimentSchedulingAuditRecord>();

    public DbSet<FieldNavigationAcceptance> FieldNavigationAcceptances => Set<FieldNavigationAcceptance>();

    public DbSet<FieldNavigationAcceptanceAudit> FieldNavigationAcceptanceAudits => Set<FieldNavigationAcceptanceAudit>();

    public DbSet<ShineLabTaskRecord> ShineLabTasks => Set<ShineLabTaskRecord>();

    public DbSet<ShineLabTaskEventRecord> ShineLabTaskEvents => Set<ShineLabTaskEventRecord>();

    public DbSet<PhysicalSafetyActionRecord> PhysicalSafetyActions => Set<PhysicalSafetyActionRecord>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureExperimentSampleVerificationSnapshotsAreAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnsureExperimentSampleVerificationSnapshotsAreAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TransportTask>(entity =>
        {
            entity.HasKey(task => task.Id);
            entity.Property(task => task.Status).HasConversion<string>();
            entity.Property(task => task.LastError).HasMaxLength(2048);
            entity.Property(task => task.Description).HasMaxLength(2048);
            entity.Property(task => task.ExternalId).HasMaxLength(256);
            entity.HasIndex(task => new { task.Status, task.Priority, task.CreatedAt });
        });

        modelBuilder.Entity<TaskEventRecord>(entity =>
        {
            entity.HasKey(taskEvent => taskEvent.Id);
            entity.Property(taskEvent => taskEvent.EventType).HasMaxLength(128);
            entity.Property(taskEvent => taskEvent.Payload).HasMaxLength(8192);
            entity.HasIndex(taskEvent => new { taskEvent.TaskId, taskEvent.CreatedAt });
        });

        modelBuilder.Entity<AgvSnapshot>(entity =>
        {
            entity.HasKey(snapshot => snapshot.AgvId);
            entity.Property(snapshot => snapshot.ControlOwner).HasMaxLength(128);
            entity.Property(snapshot => snapshot.CurrentStationId).HasMaxLength(128);
        });

        modelBuilder.Entity<WorkflowVersionRecord>(entity =>
        {
            entity.HasKey(version => new { version.WorkflowId, version.Version });
            entity.Property(version => version.DefinitionJson).HasMaxLength(65535);
            entity.Property(version => version.ValidationJson).HasMaxLength(65535);
            entity.Property(version => version.Status).HasMaxLength(32);
            entity.Property(version => version.PublishStatus).HasMaxLength(32);
            entity.Property(version => version.CreatedBy).HasMaxLength(256);
            entity.Property(version => version.ChangeSummary).HasMaxLength(2048);
            entity.Property(version => version.PublishedBy).HasMaxLength(256);
            entity.HasIndex(version => new { version.WorkflowId, version.PublishStatus });
        });

        modelBuilder.Entity<WorkflowExecutionRecord>(entity =>
        {
            entity.HasKey(execution => execution.RequestId);
            entity.Property(execution => execution.Fingerprint).HasMaxLength(8192);
            entity.Property(execution => execution.Outcome).HasMaxLength(32);
            entity.Property(execution => execution.RejectionCode).HasMaxLength(128);
            entity.Property(execution => execution.RequestJson).HasMaxLength(65535);
            entity.Property(execution => execution.ResultJson).HasMaxLength(65535);
            entity.Property(execution => execution.DefinitionSnapshotJson).HasMaxLength(65535);
            entity.Property(execution => execution.RuntimeStatus).HasMaxLength(32);
            entity.Property(execution => execution.PendingStepJson).HasMaxLength(65535);
            entity.Property(execution => execution.LastError).HasMaxLength(2048);
            entity.HasIndex(execution => new { execution.WorkflowId, execution.Version, execution.CreatedAtUtc });
            entity.HasIndex(execution => new { execution.RuntimeStatus, execution.UpdatedAtUtc });
        });

        modelBuilder.Entity<WorkflowNodeExecutionRecord>(entity =>
        {
            entity.HasKey(execution => execution.Id);
            entity.Property(execution => execution.NodeTypeId).HasMaxLength(128);
            entity.Property(execution => execution.NodeName).HasMaxLength(256);
            entity.Property(execution => execution.Status).HasMaxLength(32);
            entity.Property(execution => execution.InputJson).HasMaxLength(65535);
            entity.Property(execution => execution.OutputJson).HasMaxLength(65535);
            entity.Property(execution => execution.ResultFileReference).HasMaxLength(1024);
            entity.Property(execution => execution.LastError).HasMaxLength(2048);
            entity.HasIndex(execution => new { execution.WorkflowRunId, execution.CreatedAtUtc });
            entity.HasIndex(execution => execution.StepRequestId).IsUnique();
            entity.HasIndex(execution => new { execution.WorkflowRunId, execution.NodeId, execution.Attempt }).IsUnique();
        });

        modelBuilder.Entity<WorkflowDeviceOperationRecord>(entity =>
        {
            entity.HasKey(operation => operation.OperationId);
            entity.Property(operation => operation.CapabilityId).HasMaxLength(128);
            entity.Property(operation => operation.DeviceId).HasMaxLength(128);
            entity.Property(operation => operation.IdempotencyKey).HasMaxLength(256);
            entity.Property(operation => operation.CorrelationId).HasMaxLength(256);
            entity.Property(operation => operation.Status).HasMaxLength(32);
            entity.Property(operation => operation.RequestSummaryJson).HasMaxLength(8192);
            entity.Property(operation => operation.ResultSummaryJson).HasMaxLength(8192);
            entity.Property(operation => operation.VendorTaskId).HasMaxLength(256);
            entity.Property(operation => operation.ResultFileReference).HasMaxLength(1024);
            entity.Property(operation => operation.UnknownReason).HasMaxLength(64);
            entity.Property(operation => operation.RawResponseSummaryJson).HasMaxLength(8192);
            entity.Property(operation => operation.LastError).HasMaxLength(2048);
            entity.HasIndex(operation => new { operation.WorkflowRunId, operation.RequestedAtUtc });
            entity.HasIndex(operation => operation.NodeExecutionId);
            entity.HasIndex(operation => operation.IdempotencyKey).IsUnique();
        });

        modelBuilder.Entity<WorkflowAuditRecord>(entity =>
        {
            entity.HasKey(audit => audit.Id);
            entity.Property(audit => audit.EventType).HasMaxLength(128);
            entity.Property(audit => audit.Outcome).HasMaxLength(64);
            entity.Property(audit => audit.Code).HasMaxLength(128);
            entity.Property(audit => audit.Reason).HasMaxLength(2048);
            entity.Property(audit => audit.Actor).HasMaxLength(256);
            entity.Property(audit => audit.CorrelationId).HasMaxLength(256);
            entity.Property(audit => audit.DetailsJson).HasMaxLength(8192);
            entity.HasIndex(audit => new { audit.WorkflowId, audit.Version, audit.OccurredAtUtc });
            entity.HasIndex(audit => audit.RequestId);
        });

        modelBuilder.Entity<WorkflowRuntimeInteractionRecord>(entity =>
        {
            entity.HasKey(interaction => interaction.RequestId);
            entity.Property(interaction => interaction.Fingerprint).HasMaxLength(8192);
            entity.Property(interaction => interaction.InteractionType).HasMaxLength(64);
            entity.Property(interaction => interaction.Status).HasMaxLength(32);
            entity.Property(interaction => interaction.SignalName).HasMaxLength(128);
            entity.Property(interaction => interaction.CorrelationValue).HasMaxLength(256);
            entity.Property(interaction => interaction.Actor).HasMaxLength(256);
            entity.Property(interaction => interaction.Reason).HasMaxLength(2048);
            entity.Property(interaction => interaction.RequestJson).HasMaxLength(16384);
            entity.Property(interaction => interaction.DataJson).HasMaxLength(16384);
            entity.HasIndex(interaction => new
            {
                interaction.WorkflowRunId,
                interaction.InteractionType,
                interaction.Status,
                interaction.ReceivedAtUtc
            });
            entity.HasIndex(interaction => new
            {
                interaction.WorkflowRunId,
                interaction.SignalName,
                interaction.CorrelationValue,
                interaction.Status
            });
        });

        modelBuilder.Entity<ExperimentPlanRecord>(entity =>
        {
            entity.ToTable("ExperimentPlans");
            entity.HasKey(plan => new { plan.PlanId, plan.Version });
            entity.Property(plan => plan.Name).HasMaxLength(256);
            entity.Property(plan => plan.Description).HasMaxLength(2048);
            entity.Property(plan => plan.Status).HasMaxLength(32);
            entity.Property(plan => plan.WorkflowStepsJson).HasMaxLength(65535);
            entity.Property(plan => plan.MaterialRequirementsJson).HasMaxLength(65535);
            entity.Property(plan => plan.DefaultParametersJson).HasMaxLength(65535);
            entity.Property(plan => plan.ResourceRequirementsJson).HasMaxLength(65535);
            entity.Property(plan => plan.ProfileProductId).HasMaxLength(128);
            entity.Property(plan => plan.ProfileVersion).HasMaxLength(128);
            entity.Property(plan => plan.LayoutId).HasMaxLength(256);
            entity.Property(plan => plan.ValidationJson).HasMaxLength(65535);
            entity.Property(plan => plan.ValidatedBy).HasMaxLength(256);
            entity.Property(plan => plan.CreatedBy).HasMaxLength(256);
            entity.Property(plan => plan.PublishedBy).HasMaxLength(256);
            entity.HasIndex(plan => new { plan.Status, plan.UpdatedAtUtc });
            entity.HasIndex(plan => new { plan.WorkflowId, plan.WorkflowVersion });
        });

        modelBuilder.Entity<ExperimentJobRecord>(entity =>
        {
            entity.ToTable("ExperimentJobs");
            entity.HasKey(job => job.JobId);
            entity.Property(job => job.SampleBatchId).HasMaxLength(256);
            entity.Property(job => job.SampleId).HasMaxLength(256);
            entity.Property(job => job.WorkflowStepsJson).HasMaxLength(65535);
            entity.Property(job => job.ParametersJson).HasMaxLength(65535);
            entity.Property(job => job.Status).HasMaxLength(32);
            entity.Property(job => job.CreatedBy).HasMaxLength(256);
            entity.Property(job => job.LastError).HasMaxLength(2048);
            entity.HasIndex(job => new { job.Status, job.CreatedAtUtc });
            entity.HasIndex(job => new { job.PlanId, job.PlanVersion });
            entity.HasIndex(job => job.WorkflowRunId).IsUnique();
        });

        modelBuilder.Entity<ExperimentSampleRecord>(entity =>
        {
            entity.ToTable("ExperimentSamples");
            entity.HasKey(sample => sample.SampleId);
            entity.Property(sample => sample.BusinessSampleId).HasMaxLength(256);
            entity.Property(sample => sample.BatchId).HasMaxLength(256);
            entity.Property(sample => sample.Barcode).HasMaxLength(512);
            entity.Property(sample => sample.NormalizedBarcode).HasMaxLength(512);
            entity.Property(sample => sample.DisplayName).HasMaxLength(256);
            entity.Property(sample => sample.Status).HasMaxLength(32);
            entity.HasIndex(sample => sample.BusinessSampleId).IsUnique();
            entity.HasIndex(sample => sample.NormalizedBarcode).IsUnique();
            entity.HasIndex(sample => new { sample.BatchId, sample.Status });
        });

        modelBuilder.Entity<ExperimentSampleVerificationRecord>(entity =>
        {
            entity.ToTable("ExperimentSampleVerifications");
            entity.HasKey(verification => verification.VerificationId);
            entity.Property(verification => verification.Status).HasMaxLength(32);
            entity.Property(verification => verification.RowsJson).HasMaxLength(65535);
            entity.Property(verification => verification.SnapshotHash).HasMaxLength(128);
            entity.Property(verification => verification.VerifiedBy).HasMaxLength(256);
            entity.Property(verification => verification.VerificationNote).HasMaxLength(2048);
            entity.Property(verification => verification.InvalidationReason).HasMaxLength(2048);
            entity.HasIndex(verification => new { verification.ExperimentJobId, verification.Revision }).IsUnique();
            entity.HasIndex(verification => new { verification.ExperimentJobId, verification.Status, verification.UpdatedAtUtc });
        });

        modelBuilder.Entity<ExperimentWorkstationPreparationRecord>(entity =>
        {
            entity.ToTable("ExperimentWorkstationPreparations");
            entity.HasKey(preparation => preparation.PreparationId);
            entity.Property(preparation => preparation.DeviceId).HasMaxLength(128);
            entity.Property(preparation => preparation.VendorTaskNo).HasMaxLength(256);
            entity.Property(preparation => preparation.VerificationSnapshotHash).HasMaxLength(128);
            entity.Property(preparation => preparation.PayloadJson).HasMaxLength(262144);
            entity.Property(preparation => preparation.PayloadHash).HasMaxLength(128);
            entity.Property(preparation => preparation.Status).HasMaxLength(32);
            entity.Property(preparation => preparation.LastError).HasMaxLength(2048);
            entity.HasIndex(preparation => preparation.VendorTaskNo).IsUnique();
            entity.HasIndex(preparation => new { preparation.ExperimentJobId, preparation.PreparedAtUtc });
            entity.HasIndex(preparation => new { preparation.DeviceId, preparation.Status, preparation.PreparedAtUtc });
        });

        modelBuilder.Entity<ExperimentRunRecord>(entity =>
        {
            entity.ToTable("ExperimentRuns");
            entity.HasKey(run => run.ExperimentRunId);
            entity.Property(run => run.Status).HasMaxLength(32);
            entity.Property(run => run.StepsJson).HasMaxLength(65535);
            entity.Property(run => run.LastError).HasMaxLength(2048);
            entity.HasIndex(run => run.ExperimentJobId).IsUnique();
            entity.HasIndex(run => run.AdmissionRequestId).IsUnique();
            entity.HasIndex(run => new { run.Status, run.UpdatedAtUtc });
        });

        modelBuilder.Entity<ScheduleEntryRecord>(entity =>
        {
            entity.ToTable("ScheduleEntries");
            entity.HasKey(entry => entry.ScheduleEntryId);
            entity.Property(entry => entry.Status).HasMaxLength(32);
            entity.Property(entry => entry.RequestedResourcesJson).HasMaxLength(65535);
            entity.Property(entry => entry.BlockingReasonsJson).HasMaxLength(65535);
            entity.Property(entry => entry.CreatedBy).HasMaxLength(256);
            entity.HasIndex(entry => new { entry.Status, entry.PlannedStartUtc, entry.Priority });
            entity.HasIndex(entry => entry.ExperimentJobId);
        });

        modelBuilder.Entity<ResourceReservationRecord>(entity =>
        {
            entity.ToTable("ResourceReservations");
            entity.HasKey(reservation => reservation.ReservationId);
            entity.Property(reservation => reservation.ResourceType).HasMaxLength(128);
            entity.Property(reservation => reservation.ResourceId).HasMaxLength(256);
            entity.Property(reservation => reservation.ResourceKey).HasMaxLength(512);
            entity.Property(reservation => reservation.Status).HasMaxLength(32);
            entity.HasIndex(reservation => reservation.ScheduleEntryId);
            entity.HasIndex(reservation => new
            {
                reservation.ResourceKey,
                reservation.StartsAtUtc,
                reservation.EndsAtUtc
            });
        });

        modelBuilder.Entity<WorkflowResourceLeaseRecord>(entity =>
        {
            entity.ToTable("WorkflowResourceLeases");
            entity.HasKey(lease => lease.LeaseId);
            entity.Property(lease => lease.ResourceType).HasMaxLength(128);
            entity.Property(lease => lease.ResourceId).HasMaxLength(256);
            entity.Property(lease => lease.ResourceKey).HasMaxLength(512);
            entity.Property(lease => lease.ActiveResourceKey).HasMaxLength(512);
            entity.Property(lease => lease.Status).HasMaxLength(32);
            entity.Property(lease => lease.AcquiredBy).HasMaxLength(256);
            entity.Property(lease => lease.ReleasedBy).HasMaxLength(256);
            entity.Property(lease => lease.ReleaseReason).HasMaxLength(2048);
            entity.HasIndex(lease => lease.ActiveResourceKey).IsUnique();
            entity.HasIndex(lease => new { lease.WorkflowRunId, lease.AcquiredAtUtc });
            entity.HasIndex(lease => lease.ScheduleEntryId);
        });

        modelBuilder.Entity<ExperimentSchedulingAuditRecord>(entity =>
        {
            entity.ToTable("ExperimentSchedulingAudits");
            entity.HasKey(audit => audit.Id);
            entity.Property(audit => audit.EventType).HasMaxLength(128);
            entity.Property(audit => audit.Outcome).HasMaxLength(64);
            entity.Property(audit => audit.Code).HasMaxLength(128);
            entity.Property(audit => audit.Actor).HasMaxLength(256);
            entity.Property(audit => audit.Reason).HasMaxLength(2048);
            entity.Property(audit => audit.RequestFingerprint).HasMaxLength(128);
            entity.Property(audit => audit.DetailsJson).HasMaxLength(8192);
            entity.Property(audit => audit.ResultJson).HasMaxLength(65535);
            entity.HasIndex(audit => audit.RequestId).IsUnique();
            entity.HasIndex(audit => new { audit.PlanId, audit.PlanVersion, audit.OccurredAtUtc });
            entity.HasIndex(audit => new { audit.ExperimentJobId, audit.OccurredAtUtc });
            entity.HasIndex(audit => new { audit.ScheduleEntryId, audit.OccurredAtUtc });
        });

        modelBuilder.Entity<FieldNavigationAcceptance>(entity =>
        {
            entity.HasKey(acceptance => acceptance.Id);
            entity.Property(acceptance => acceptance.Status).HasMaxLength(32);
            entity.Property(acceptance => acceptance.AgvId).HasMaxLength(128);
            entity.Property(acceptance => acceptance.SourceStationId).HasMaxLength(128);
            entity.Property(acceptance => acceptance.TargetStationId).HasMaxLength(128);
            entity.Property(acceptance => acceptance.MapName).HasMaxLength(256);
            entity.Property(acceptance => acceptance.MapMd5).HasMaxLength(32);
            entity.Property(acceptance => acceptance.PlannedPathJson).HasMaxLength(8192);
            entity.Property(acceptance => acceptance.Description).HasMaxLength(2048);
            entity.Property(acceptance => acceptance.OperatorName).HasMaxLength(256);
            entity.Property(acceptance => acceptance.SafetyObserverName).HasMaxLength(256);
            entity.Property(acceptance => acceptance.PermitId).HasMaxLength(256);
            entity.Property(acceptance => acceptance.DeviceTaskId).HasMaxLength(256);
            entity.Property(acceptance => acceptance.DeviceEpoch);
            entity.Property(acceptance => acceptance.ReadinessSupervisorInstanceId).HasMaxLength(128);
            entity.Property(acceptance => acceptance.LastError).HasMaxLength(2048);
            entity.HasIndex(acceptance => acceptance.PermitId).IsUnique();
            entity.HasIndex(acceptance => acceptance.WorkflowNodeExecutionId).IsUnique();
            entity.HasIndex(acceptance => acceptance.WorkflowRunId);
            entity.HasIndex(acceptance => new { acceptance.Status, acceptance.CreatedAtUtc });
        });

        modelBuilder.Entity<FieldNavigationAcceptanceAudit>(entity =>
        {
            entity.HasKey(audit => audit.Id);
            entity.Property(audit => audit.EventType).HasMaxLength(128);
            entity.Property(audit => audit.DetailsJson).HasMaxLength(8192);
            entity.HasIndex(audit => new { audit.AcceptanceId, audit.OccurredAtUtc });
        });

        modelBuilder.Entity<ShineLabTaskRecord>(entity =>
        {
            entity.ToTable("ShineLabTasks");
            entity.HasKey(task => task.Id);
            entity.Property(task => task.TaskUuid).HasMaxLength(256);
            entity.Property(task => task.EquipmentCode).HasMaxLength(128);
            entity.Property(task => task.Status).HasMaxLength(32);
            entity.Property(task => task.CurrentStage).HasMaxLength(64);
            entity.Property(task => task.RequestFingerprint).HasMaxLength(64);
            entity.Property(task => task.ConfigJson).HasMaxLength(65535);
            entity.Property(task => task.ConfigResponseJson).HasMaxLength(65535);
            entity.Property(task => task.CommandResponseJson).HasMaxLength(65535);
            entity.Property(task => task.ResultJson).HasMaxLength(65535);
            entity.Property(task => task.LastError).HasMaxLength(2048);
            entity.HasIndex(task => task.TaskUuid).IsUnique();
            entity.HasIndex(task => new { task.Status, task.UpdatedAtUtc });
        });

        modelBuilder.Entity<ShineLabTaskEventRecord>(entity =>
        {
            entity.ToTable("ShineLabTaskEvents");
            entity.HasKey(taskEvent => taskEvent.Id);
            entity.Property(taskEvent => taskEvent.TaskUuid).HasMaxLength(256);
            entity.Property(taskEvent => taskEvent.EventType).HasMaxLength(128);
            entity.Property(taskEvent => taskEvent.PayloadJson).HasMaxLength(65535);
            entity.HasIndex(taskEvent => new { taskEvent.TaskUuid, taskEvent.OccurredAtUtc });
        });

        modelBuilder.Entity<PhysicalSafetyActionRecord>(entity =>
        {
            entity.ToTable("PhysicalSafetyActions");
            entity.HasKey(action => action.Id);
            entity.Property(action => action.Fingerprint).HasMaxLength(128).IsRequired();
            entity.Property(action => action.ActionType).HasMaxLength(64).IsRequired();
            entity.Property(action => action.DeviceId).HasMaxLength(128).IsRequired();
            entity.Property(action => action.OperatorName).HasMaxLength(256).IsRequired();
            entity.Property(action => action.Reason).HasMaxLength(2048).IsRequired();
            entity.Property(action => action.Status).HasMaxLength(32).IsRequired();
            entity.Property(action => action.ResultSummary).HasMaxLength(2048);
            entity.Property(action => action.CorrelationId).HasMaxLength(256);
            entity.HasIndex(action => action.RequestId).IsUnique();
            entity.HasIndex(action => action.Fingerprint).IsUnique();
            entity.HasIndex(action => new { action.DeviceId, action.PreparedAtUtc });
            entity.Property(action => action.SupervisorInstanceId).HasMaxLength(128);
        });
    }

    private void EnsureExperimentSampleVerificationSnapshotsAreAppendOnly()
    {
        var changedSnapshot = ChangeTracker.Entries<ExperimentSampleVerificationRecord>()
            .Any(entry =>
                entry.State == EntityState.Deleted ||
                (entry.State == EntityState.Modified &&
                 (entry.Property(verification => verification.ExperimentJobId).IsModified ||
                  entry.Property(verification => verification.Revision).IsModified ||
                  entry.Property(verification => verification.RowsJson).IsModified ||
                  entry.Property(verification => verification.SnapshotHash).IsModified ||
                  entry.Property(verification => verification.CreatedAtUtc).IsModified)));

        if (changedSnapshot)
        {
            throw new InvalidOperationException(
                "Experiment sample verification snapshots are append-only and cannot be deleted.");
        }


        var changedPreparationPayload = ChangeTracker.Entries<ExperimentWorkstationPreparationRecord>()
            .Any(entry =>
                entry.State == EntityState.Deleted ||
                (entry.State == EntityState.Modified &&
                 (entry.Property(preparation => preparation.ExperimentJobId).IsModified ||
                  entry.Property(preparation => preparation.DeviceId).IsModified ||
                  entry.Property(preparation => preparation.VendorTaskNo).IsModified ||
                  entry.Property(preparation => preparation.VerificationId).IsModified ||
                  entry.Property(preparation => preparation.VerificationRevision).IsModified ||
                  entry.Property(preparation => preparation.VerificationSnapshotHash).IsModified ||
                  entry.Property(preparation => preparation.PayloadJson).IsModified ||
                  entry.Property(preparation => preparation.PayloadHash).IsModified ||
                  entry.Property(preparation => preparation.PreparedRequestId).IsModified ||
                  entry.Property(preparation => preparation.PreparedAtUtc).IsModified)));

        if (changedPreparationPayload)
        {
            throw new InvalidOperationException(
                "Experiment workstation preparation payloads are append-only and cannot be deleted.");
        }
    }
}
