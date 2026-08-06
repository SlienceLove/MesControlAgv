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

    public DbSet<WorkflowAuditRecord> WorkflowAudits => Set<WorkflowAuditRecord>();

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
            entity.HasIndex(execution => new { execution.WorkflowId, execution.Version, execution.CreatedAtUtc });
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
