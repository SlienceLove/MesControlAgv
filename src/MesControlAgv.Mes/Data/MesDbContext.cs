using MesControlAgv.Domain;
using Microsoft.EntityFrameworkCore;
using MesControlAgv.Mes.Entities;

namespace MesControlAgv.Mes.Data;

public sealed class MesDbContext(DbContextOptions<MesDbContext> options) : DbContext(options)
{
    public DbSet<TransportTask> TransportTasks => Set<TransportTask>();

    public DbSet<TaskEventRecord> TaskEvents => Set<TaskEventRecord>();

    public DbSet<AgvSnapshot> AgvSnapshots => Set<AgvSnapshot>();

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
    }
}
