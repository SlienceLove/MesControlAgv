using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Adapter.Data;

public sealed class AdapterDbContext(DbContextOptions<AdapterDbContext> options) : DbContext(options)
{
    public DbSet<Entities.AdapterTask> Tasks => Set<Entities.AdapterTask>();
    public DbSet<Entities.AdapterTaskManualClosure> TaskManualClosures => Set<Entities.AdapterTaskManualClosure>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Entities.AdapterTask>().HasKey(task => task.TaskId);
        modelBuilder.Entity<Entities.AdapterTaskManualClosure>().HasKey(item => item.TaskId);
        modelBuilder.Entity<Entities.AdapterTaskManualClosure>().HasIndex(item => item.RequestId).IsUnique();
    }
}
