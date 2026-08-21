using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Tests;

public sealed class ExperimentSchedulingPersistenceTests
{
    [Fact]
    public async Task Active_resource_key_prevents_double_lease_and_allows_released_history()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new MesDbContext(options);
        await database.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        var resourceKey = ExperimentResourceKeys.Create(ExperimentResourceTypeIds.Instrument, "CIC-D160-01");
        database.WorkflowResourceLeases.Add(CreateLease(resourceKey, now));
        var released = CreateLease(resourceKey, now);
        released.ActiveResourceKey = null;
        released.Status = ResourceLeaseStatus.Released.ToString();
        released.ReleasedAtUtc = now.AddMinutes(-1);
        database.WorkflowResourceLeases.Add(released);
        await database.SaveChangesAsync();

        database.WorkflowResourceLeases.Add(CreateLease(
            ExperimentResourceKeys.Create("INSTRUMENT", "cic-d160-01"),
            now.AddSeconds(1)));

        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        Assert.Equal(2, await database.WorkflowResourceLeases.AsNoTracking().CountAsync());
    }

    private static WorkflowResourceLeaseRecord CreateLease(string resourceKey, DateTime now) => new()
    {
        LeaseId = Guid.NewGuid(),
        WorkflowRunId = Guid.NewGuid(),
        ResourceType = ExperimentResourceTypeIds.Instrument,
        ResourceId = "CIC-D160-01",
        ResourceKey = resourceKey,
        ActiveResourceKey = resourceKey,
        Status = ResourceLeaseStatus.Active.ToString(),
        AcquiredBy = "test-runtime",
        AcquiredAtUtc = now,
        ExpiresAtUtc = now.AddMinutes(5),
        UpdatedAtUtc = now
    };
}
