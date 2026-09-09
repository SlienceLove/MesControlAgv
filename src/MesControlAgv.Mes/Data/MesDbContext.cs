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

    public DbSet<MaterialCatalogRecord> MaterialCatalog => Set<MaterialCatalogRecord>();

    public DbSet<WarehouseLocationRecord> WarehouseLocations => Set<WarehouseLocationRecord>();

    public DbSet<SampleMaterialRecord> SampleMaterials => Set<SampleMaterialRecord>();

    public DbSet<MaterialLotRecord> MaterialLots => Set<MaterialLotRecord>();

    public DbSet<InventoryBalanceRecord> InventoryBalances => Set<InventoryBalanceRecord>();

    public DbSet<InventoryTransactionRecord> InventoryTransactions => Set<InventoryTransactionRecord>();

    public DbSet<BarcodeScanEventRecord> BarcodeScanEvents => Set<BarcodeScanEventRecord>();

    public DbSet<ExperimentJobMaterialBindingRecord> ExperimentJobMaterialBindings =>
        Set<ExperimentJobMaterialBindingRecord>();

    public DbSet<MaterialOperationRecord> MaterialOperations => Set<MaterialOperationRecord>();

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

        modelBuilder.Entity<MaterialOperationRecord>(entity =>
        {
            entity.ToTable("MaterialOperations");
            entity.HasKey(operation => operation.RequestId);
            entity.Property(operation => operation.OperationKind).HasMaxLength(64).IsRequired();
            entity.Property(operation => operation.Fingerprint).HasMaxLength(256).IsRequired();
            entity.Property(operation => operation.Outcome).HasMaxLength(32).IsRequired();
            entity.Property(operation => operation.ResultJson).HasMaxLength(65535).IsRequired();
            entity.Property(operation => operation.Actor).HasMaxLength(256).IsRequired();
            entity.HasIndex(operation => new { operation.OperationKind, operation.CreatedAtUtc });
        });

        modelBuilder.Entity<MaterialCatalogRecord>(entity =>
        {
            entity.ToTable("MaterialCatalog");
            entity.HasKey(material => material.MaterialId);
            entity.Property(material => material.MaterialCode).HasMaxLength(128).IsRequired();
            entity.Property(material => material.Name).HasMaxLength(256).IsRequired();
            entity.Property(material => material.Kind).HasMaxLength(32).IsRequired();
            entity.Property(material => material.Specification).HasMaxLength(512);
            entity.Property(material => material.Unit).HasMaxLength(64);
            entity.HasIndex(material => material.MaterialCode).IsUnique();
            entity.HasIndex(material => new { material.Kind, material.IsEnabled });
        });

        modelBuilder.Entity<WarehouseLocationRecord>(entity =>
        {
            entity.ToTable("WarehouseLocations");
            entity.HasKey(location => location.LocationId);
            entity.Property(location => location.WarehouseCode).HasMaxLength(64).IsRequired();
            entity.Property(location => location.WarehouseName).HasMaxLength(128).IsRequired();
            entity.Property(location => location.LocationCode).HasMaxLength(128).IsRequired();
            entity.HasIndex(location => new { location.WarehouseCode, location.LocationCode }).IsUnique();
            entity.HasIndex(location => new { location.WarehouseCode, location.IsEnabled });
        });

        modelBuilder.Entity<SampleMaterialRecord>(entity =>
        {
            entity.ToTable("SampleMaterials");
            entity.HasKey(sample => sample.SampleId);
            entity.Property(sample => sample.Barcode).HasMaxLength(256).IsRequired();
            entity.Property(sample => sample.SampleBatchId).HasMaxLength(256).IsRequired();
            entity.Property(sample => sample.MaterialCode).HasMaxLength(128);
            entity.Property(sample => sample.SampleType).HasMaxLength(128);
            entity.Property(sample => sample.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(sample => sample.Barcode).IsUnique();
            entity.HasIndex(sample => new { sample.Status, sample.UpdatedAtUtc });
            entity.HasIndex(sample => sample.BoundExperimentJobId);
            entity.HasOne<WarehouseLocationRecord>()
                .WithMany()
                .HasForeignKey(sample => sample.LocationId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<ExperimentJobRecord>()
                .WithMany()
                .HasForeignKey(sample => sample.BoundExperimentJobId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<MaterialLotRecord>(entity =>
        {
            entity.ToTable("MaterialLots");
            entity.HasKey(lot => lot.LotId);
            entity.Property(lot => lot.MaterialCode).HasMaxLength(128).IsRequired();
            entity.Property(lot => lot.LotCode).HasMaxLength(128).IsRequired();
            entity.Property(lot => lot.Barcode).HasMaxLength(256);
            entity.Property(lot => lot.Specification).HasMaxLength(512);
            entity.Property(lot => lot.Unit).HasMaxLength(64);
            entity.HasIndex(lot => new { lot.MaterialCode, lot.LotCode }).IsUnique();
            entity.HasIndex(lot => lot.Barcode).IsUnique();
            entity.HasIndex(lot => new { lot.IsQuarantined, lot.ExpiryDateUtc });
            entity.HasOne<MaterialCatalogRecord>()
                .WithMany()
                .HasForeignKey(lot => lot.MaterialId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<InventoryBalanceRecord>(entity =>
        {
            entity.ToTable("InventoryBalances");
            entity.HasKey(balance => balance.BalanceId);
            entity.Property(balance => balance.OnHand).HasPrecision(18, 6);
            entity.Property(balance => balance.Reserved).HasPrecision(18, 6);
            entity.HasIndex(balance => new { balance.LotId, balance.LocationId }).IsUnique();
            entity.HasIndex(balance => balance.LocationId);
            entity.HasOne<MaterialLotRecord>()
                .WithMany()
                .HasForeignKey(balance => balance.LotId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<WarehouseLocationRecord>()
                .WithMany()
                .HasForeignKey(balance => balance.LocationId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<InventoryTransactionRecord>(entity =>
        {
            entity.ToTable("InventoryTransactions");
            entity.HasKey(transaction => transaction.Id);
            entity.Property(transaction => transaction.LineKey).HasMaxLength(256).IsRequired();
            entity.Property(transaction => transaction.TransactionKind).HasMaxLength(32).IsRequired();
            entity.Property(transaction => transaction.MaterialCode).HasMaxLength(128);
            entity.Property(transaction => transaction.LotCode).HasMaxLength(128);
            entity.Property(transaction => transaction.Barcode).HasMaxLength(256);
            entity.Property(transaction => transaction.Quantity).HasPrecision(18, 6);
            entity.Property(transaction => transaction.Unit).HasMaxLength(64);
            entity.Property(transaction => transaction.Actor).HasMaxLength(256).IsRequired();
            entity.Property(transaction => transaction.Reason).HasMaxLength(2048);
            entity.Property(transaction => transaction.CorrelationId).HasMaxLength(256);
            entity.Property(transaction => transaction.DetailsJson).HasMaxLength(8192);
            entity.HasIndex(transaction => new { transaction.RequestId, transaction.LineKey }).IsUnique();
            entity.HasIndex(transaction => new { transaction.LotId, transaction.OccurredAtUtc });
            entity.HasIndex(transaction => new { transaction.SampleId, transaction.OccurredAtUtc });
            entity.HasIndex(transaction => new { transaction.ExperimentJobId, transaction.OccurredAtUtc });
            entity.HasOne<MaterialLotRecord>()
                .WithMany()
                .HasForeignKey(transaction => transaction.LotId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<SampleMaterialRecord>()
                .WithMany()
                .HasForeignKey(transaction => transaction.SampleId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<WarehouseLocationRecord>()
                .WithMany()
                .HasForeignKey(transaction => transaction.FromLocationId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<WarehouseLocationRecord>()
                .WithMany()
                .HasForeignKey(transaction => transaction.ToLocationId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<ExperimentJobRecord>()
                .WithMany()
                .HasForeignKey(transaction => transaction.ExperimentJobId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<BarcodeScanEventRecord>(entity =>
        {
            entity.ToTable("BarcodeScanEvents");
            entity.HasKey(scan => scan.Id);
            entity.Property(scan => scan.RawCode).HasMaxLength(512).IsRequired();
            entity.Property(scan => scan.NormalizedCode).HasMaxLength(512).IsRequired();
            entity.Property(scan => scan.ScanKind).HasMaxLength(32).IsRequired();
            entity.Property(scan => scan.Source).HasMaxLength(64).IsRequired();
            entity.Property(scan => scan.Outcome).HasMaxLength(32).IsRequired();
            entity.Property(scan => scan.IssueCode).HasMaxLength(128);
            entity.Property(scan => scan.Actor).HasMaxLength(256).IsRequired();
            entity.Property(scan => scan.DetailsJson).HasMaxLength(8192);
            entity.HasIndex(scan => scan.RequestId).IsUnique();
            entity.HasIndex(scan => new { scan.NormalizedCode, scan.OccurredAtUtc });
            entity.HasIndex(scan => new { scan.SampleId, scan.OccurredAtUtc });
            entity.HasIndex(scan => new { scan.LotId, scan.OccurredAtUtc });
            entity.HasOne<SampleMaterialRecord>()
                .WithMany()
                .HasForeignKey(scan => scan.SampleId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<MaterialLotRecord>()
                .WithMany()
                .HasForeignKey(scan => scan.LotId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<ExperimentJobMaterialBindingRecord>(entity =>
        {
            entity.ToTable("ExperimentJobMaterialBindings");
            entity.HasKey(binding => binding.BindingId);
            entity.Property(binding => binding.LineKey).HasMaxLength(256).IsRequired();
            entity.Property(binding => binding.Quantity).HasPrecision(18, 6);
            entity.Property(binding => binding.Unit).HasMaxLength(64);
            entity.Property(binding => binding.Status).HasMaxLength(32).IsRequired();
            entity.Property(binding => binding.InjectionPosition).HasMaxLength(128);
            entity.Property(binding => binding.Actor).HasMaxLength(256).IsRequired();
            entity.Property(binding => binding.Reason).HasMaxLength(2048);
            entity.HasIndex(binding => new { binding.RequestId, binding.LineKey }).IsUnique();
            entity.HasIndex(binding => new { binding.ExperimentJobId, binding.Status });
            entity.HasIndex(binding => binding.SampleId);
            entity.HasIndex(binding => binding.LotId);
            entity.HasOne<ExperimentJobRecord>()
                .WithMany()
                .HasForeignKey(binding => binding.ExperimentJobId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<SampleMaterialRecord>()
                .WithMany()
                .HasForeignKey(binding => binding.SampleId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<MaterialLotRecord>()
                .WithMany()
                .HasForeignKey(binding => binding.LotId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }
}
