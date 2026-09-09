using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Mes.Tests;

public sealed class MaterialManagementPersistenceTests : IClassFixture<MesWebApplicationFactory>
{
    private readonly MesWebApplicationFactory _factory;

    public MaterialManagementPersistenceTests(MesWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Startup_creates_material_tables_and_seeds_default_location()
    {
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();

        var location = await database.WarehouseLocations
            .SingleAsync(item => item.WarehouseCode == "MAIN" && item.LocationCode == "DEFAULT");

        Assert.Equal("默认仓库", location.WarehouseName);
        Assert.True(location.IsEnabled);
        Assert.Empty(await database.MaterialCatalog.ToListAsync());
        Assert.Empty(await database.SampleMaterials.ToListAsync());
        Assert.Empty(await database.MaterialLots.ToListAsync());
        Assert.Empty(await database.InventoryTransactions.ToListAsync());
        Assert.Empty(await database.BarcodeScanEvents.ToListAsync());
        Assert.Empty(await database.ExperimentJobMaterialBindings.ToListAsync());
    }

    [Fact]
    public async Task Material_code_and_transaction_request_are_unique()
    {
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        var now = DateTime.UtcNow;

        database.MaterialCatalog.AddRange(
            new MaterialCatalogRecord
            {
                MaterialId = Guid.NewGuid(),
                MaterialCode = "CONSUMABLE-UNIQUE",
                Name = "Unique test material",
                Kind = "Consumable",
                IsEnabled = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            },
            new MaterialCatalogRecord
            {
                MaterialId = Guid.NewGuid(),
                MaterialCode = "CONSUMABLE-UNIQUE",
                Name = "Duplicate test material",
                Kind = "Consumable",
                IsEnabled = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });

        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());

        database.ChangeTracker.Clear();
        var requestId = Guid.NewGuid();
        database.InventoryTransactions.AddRange(
            NewTransaction(requestId, now),
            NewTransaction(requestId, now.AddSeconds(1)));

        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
    }

    [Fact]
    public async Task Operation_request_is_unique_and_location_references_are_enforced()
    {
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        var requestId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        database.MaterialOperations.Add(new MaterialOperationRecord
        {
            RequestId = requestId,
            OperationKind = "receive",
            Fingerprint = "one",
            Outcome = "accepted",
            Actor = "material-test",
            CreatedAtUtc = now
        });
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        database.MaterialOperations.Add(new MaterialOperationRecord
        {
            RequestId = requestId,
            OperationKind = "receive",
            Fingerprint = "two",
            Outcome = "accepted",
            Actor = "material-test",
            CreatedAtUtc = now.AddSeconds(1)
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        database.ChangeTracker.Clear();

        database.InventoryTransactions.Add(new InventoryTransactionRecord
        {
            Id = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            LineKey = "invalid-location",
            TransactionKind = "Move",
            FromLocationId = Guid.NewGuid(),
            Actor = "material-test",
            DetailsJson = "{}",
            OccurredAtUtc = now
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
    }

    [Fact]
    public async Task Startup_adds_material_tables_to_existing_database_without_losing_jobs()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"mes-material-legacy-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<MesDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;

            var jobId = Guid.NewGuid();
            await using (var database = new MesDbContext(options))
            {
                await database.Database.EnsureCreatedAsync();
                database.ExperimentJobs.Add(new ExperimentJobRecord
                {
                    JobId = jobId,
                    PlanId = Guid.NewGuid(),
                    PlanVersion = 1,
                    WorkflowId = Guid.NewGuid(),
                    WorkflowVersion = 1,
                    SampleBatchId = "legacy-batch",
                    Status = "Draft",
                    CreatedBy = "legacy-test",
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                });
                await database.SaveChangesAsync();
            }

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA foreign_keys = OFF;
                    DROP TABLE IF EXISTS BarcodeScanEvents;
                    DROP TABLE IF EXISTS InventoryTransactions;
                    DROP TABLE IF EXISTS InventoryBalances;
                    DROP TABLE IF EXISTS ExperimentJobMaterialBindings;
                    DROP TABLE IF EXISTS MaterialLots;
                    DROP TABLE IF EXISTS SampleMaterials;
                    DROP TABLE IF EXISTS MaterialCatalog;
                    DROP TABLE IF EXISTS WarehouseLocations;
                    DROP TABLE IF EXISTS MaterialOperations;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            using (var factory = new MesWebApplicationFactory(databasePath))
            {
                using var client = factory.CreateClient();
                var health = await client.GetAsync("/health");
                health.EnsureSuccessStatusCode();

                using var scope = factory.Services.CreateScope();
                var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
                Assert.True(await database.ExperimentJobs.AnyAsync(job => job.JobId == jobId));
                Assert.True(await database.WarehouseLocations.AnyAsync(location =>
                    location.WarehouseCode == "MAIN" && location.LocationCode == "DEFAULT"));
            }

            SqliteConnection.ClearAllPools();
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var path = databasePath + suffix;
                if (!File.Exists(path)) continue;
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // A background host may release its SQLite handle just after
                    // WebApplicationFactory.Dispose; leaving this temp file is
                    // preferable to failing an otherwise valid compatibility test.
                }
            }
        }
    }

    private static InventoryTransactionRecord NewTransaction(Guid requestId, DateTime occurredAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        RequestId = requestId,
        LineKey = "receipt:1",
        TransactionKind = "Receipt",
        Actor = "material-test",
        DetailsJson = "{}",
        OccurredAtUtc = occurredAtUtc
    };
}
