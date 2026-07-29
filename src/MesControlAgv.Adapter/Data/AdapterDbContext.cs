using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Adapter.Data;

public sealed class AdapterDbContext(DbContextOptions<AdapterDbContext> options) : DbContext(options)
{
    public DbSet<Entities.AdapterTask> Tasks => Set<Entities.AdapterTask>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Entities.AdapterTask>().HasKey(task => task.TaskId);
    }
}
