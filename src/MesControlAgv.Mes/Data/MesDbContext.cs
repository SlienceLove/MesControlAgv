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

    public DbSet<ExperimentPlanRecord> ExperimentPlans => Set<ExperimentPlanRecord>();

    public DbSet<ExperimentJobRecord> ExperimentJobs => Set<ExperimentJobRecord>();

    public DbSet<ScheduleEntryRecord> ScheduleEntries => Set<ScheduleEntryRecord>();

    public DbSet<ResourceReservationRecord> ResourceReservations => Set<ResourceReservationRecord>();

    public DbSet<WorkflowResourceLeaseRecord> WorkflowResourceLeases => Set<WorkflowResourceLeaseRecord>();

    public DbSet<FieldNavigationAcceptance> FieldNavigationAcceptances => Set<FieldNavigationAcceptance>();

    public DbSet<FieldNavigationAcceptanceAudit> FieldNavigationAcceptanceAudits => Set<FieldNavigationAcceptanceAudit>();

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
            entity.Property(operation => operation.LastError).HasMaxLength(2048);
            entity.HasIndex(operation => new { operation.WorkflowRunId, operation.RequestedAtUtc });
            entity.HasIndex(operation => operation.NodeExecutionId);
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

        modelBuilder.Entity<ExperimentPlanRecord>(entity =>
        {
            entity.ToTable("ExperimentPlans");
            entity.HasKey(plan => new { plan.PlanId, plan.Version });
            entity.Property(plan => plan.Name).HasMaxLength(256);
            entity.Property(plan => plan.Description).HasMaxLength(2048);
            entity.Property(plan => plan.Status).HasMaxLength(32);
            entity.Property(plan => plan.MaterialRequirementsJson).HasMaxLength(65535);
            entity.Property(plan => plan.DefaultParametersJson).HasMaxLength(65535);
            entity.Property(plan => plan.ResourceRequirementsJson).HasMaxLength(65535);
            entity.Property(plan => plan.ProfileProductId).HasMaxLength(128);
            entity.Property(plan => plan.ProfileVersion).HasMaxLength(128);
            entity.Property(plan => plan.LayoutId).HasMaxLength(256);
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
            entity.Property(job => job.ParametersJson).HasMaxLength(65535);
            entity.Property(job => job.Status).HasMaxLength(32);
            entity.Property(job => job.CreatedBy).HasMaxLength(256);
            entity.Property(job => job.LastError).HasMaxLength(2048);
            entity.HasIndex(job => new { job.Status, job.CreatedAtUtc });
            entity.HasIndex(job => new { job.PlanId, job.PlanVersion });
            entity.HasIndex(job => job.WorkflowRunId).IsUnique();
        });

        modelBuilder.Entity<ScheduleEntryRecord>(entity =>
        {
            entity.ToTable("ScheduleEntries");
            entity.HasKey(entry => entry.ScheduleEntryId);
            entity.Property(entry => entry.Status).HasMaxLength(32);
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
            entity.Property(acceptance => acceptance.LastError).HasMaxLength(2048);
            entity.HasIndex(acceptance => acceptance.PermitId).IsUnique();
            entity.HasIndex(acceptance => new { acceptance.Status, acceptance.CreatedAtUtc });
        });

        modelBuilder.Entity<FieldNavigationAcceptanceAudit>(entity =>
        {
            entity.HasKey(audit => audit.Id);
            entity.Property(audit => audit.EventType).HasMaxLength(128);
            entity.Property(audit => audit.DetailsJson).HasMaxLength(8192);
            entity.HasIndex(audit => new { audit.AcceptanceId, audit.OccurredAtUtc });
        });
    }
}
