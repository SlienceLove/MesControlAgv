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

    [Fact]
    public async Task Startup_upgrades_pre_operation_material_schema_without_losing_audit_rows()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"mes-material-legacy-schema-{Guid.NewGuid():N}.db");
        var requestId = Guid.NewGuid();
        var materialId = Guid.NewGuid();
        var lotId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        try
        {
            var options = new DbContextOptionsBuilder<MesDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using (var database = new MesDbContext(options))
            {
                await database.Database.EnsureCreatedAsync();
            }

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    PRAGMA foreign_keys = OFF;
                    DROP TABLE IF EXISTS ExperimentJobMaterialBindings;
                    DROP TABLE IF EXISTS BarcodeScanEvents;
                    DROP TABLE IF EXISTS InventoryTransactions;
                    DROP TABLE IF EXISTS InventoryBalances;
                    DROP TABLE IF EXISTS MaterialLots;
                    DROP TABLE IF EXISTS SampleMaterials;
                    DROP TABLE IF EXISTS MaterialCatalog;
                    DROP TABLE IF EXISTS WarehouseLocations;
                    DROP TABLE IF EXISTS MaterialOperations;
                    CREATE TABLE MaterialCatalog (
                        MaterialId TEXT NOT NULL PRIMARY KEY, MaterialCode TEXT NOT NULL, Name TEXT NOT NULL,
                        Kind TEXT NOT NULL, Specification TEXT NULL, Unit TEXT NULL,
                        IsEnabled INTEGER NOT NULL DEFAULT 1, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL);
                    CREATE TABLE WarehouseLocations (
                        LocationId TEXT NOT NULL PRIMARY KEY, WarehouseCode TEXT NOT NULL, WarehouseName TEXT NOT NULL,
                        LocationCode TEXT NOT NULL, IsEnabled INTEGER NOT NULL DEFAULT 1,
                        CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL);
                    CREATE TABLE SampleMaterials (
                        SampleId TEXT NOT NULL PRIMARY KEY, Barcode TEXT NOT NULL, SampleBatchId TEXT NOT NULL,
                        MaterialCode TEXT NULL, SampleType TEXT NULL, Status TEXT NOT NULL, LocationId TEXT NULL,
                        BoundExperimentJobId TEXT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL,
                        FOREIGN KEY (LocationId) REFERENCES WarehouseLocations(LocationId));
                    CREATE TABLE MaterialLots (
                        LotId TEXT NOT NULL PRIMARY KEY, MaterialId TEXT NOT NULL, MaterialCode TEXT NOT NULL,
                        LotCode TEXT NOT NULL, Barcode TEXT NULL, Specification TEXT NULL, Unit TEXT NULL,
                        ManufactureDateUtc TEXT NULL, ExpiryDateUtc TEXT NULL, IsQuarantined INTEGER NOT NULL DEFAULT 0,
                        CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL,
                        FOREIGN KEY (MaterialId) REFERENCES MaterialCatalog(MaterialId));
                    CREATE TABLE InventoryBalances (
                        BalanceId TEXT NOT NULL PRIMARY KEY, LotId TEXT NOT NULL, LocationId TEXT NOT NULL,
                        OnHand NUMERIC NOT NULL DEFAULT 0, Reserved NUMERIC NOT NULL DEFAULT 0, UpdatedAtUtc TEXT NOT NULL,
                        FOREIGN KEY (LotId) REFERENCES MaterialLots(LotId),
                        FOREIGN KEY (LocationId) REFERENCES WarehouseLocations(LocationId));
                    CREATE TABLE InventoryTransactions (
                        Id TEXT NOT NULL PRIMARY KEY, RequestId TEXT NOT NULL, LineKey TEXT NOT NULL,
                        TransactionKind TEXT NOT NULL, LotId TEXT NULL, SampleId TEXT NULL,
                        FromLocationId TEXT NULL, ToLocationId TEXT NULL, MaterialCode TEXT NULL, LotCode TEXT NULL,
                        Barcode TEXT NULL, Quantity NUMERIC NULL, Unit TEXT NULL, ExperimentJobId TEXT NULL,
                        Actor TEXT NOT NULL, Reason TEXT NULL, CorrelationId TEXT NULL, DetailsJson TEXT NOT NULL,
                        OccurredAtUtc TEXT NOT NULL,
                        FOREIGN KEY (LotId) REFERENCES MaterialLots(LotId),
                        FOREIGN KEY (SampleId) REFERENCES SampleMaterials(SampleId));
                    CREATE TABLE BarcodeScanEvents (
                        Id TEXT NOT NULL PRIMARY KEY, RequestId TEXT NOT NULL, RawCode TEXT NOT NULL,
                        NormalizedCode TEXT NOT NULL, ScanKind TEXT NOT NULL, Source TEXT NOT NULL,
                        Outcome TEXT NOT NULL, IssueCode TEXT NULL, SampleId TEXT NULL, LotId TEXT NULL,
                        Actor TEXT NOT NULL, DetailsJson TEXT NOT NULL, OccurredAtUtc TEXT NOT NULL);
                    CREATE TABLE ExperimentJobMaterialBindings (
                        BindingId TEXT NOT NULL PRIMARY KEY, RequestId TEXT NOT NULL, LineKey TEXT NOT NULL,
                        ExperimentJobId TEXT NOT NULL, SampleId TEXT NULL, LotId TEXT NULL, Quantity NUMERIC NOT NULL,
                        Unit TEXT NULL, Status TEXT NOT NULL, InjectionPosition TEXT NULL, Actor TEXT NOT NULL,
                        Reason TEXT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL,
                        FOREIGN KEY (ExperimentJobId) REFERENCES ExperimentJobs(JobId),
                        FOREIGN KEY (SampleId) REFERENCES SampleMaterials(SampleId),
                        FOREIGN KEY (LotId) REFERENCES MaterialLots(LotId));
                    INSERT INTO WarehouseLocations
                        (LocationId, WarehouseCode, WarehouseName, LocationCode, IsEnabled, CreatedAtUtc, UpdatedAtUtc)
                        VALUES ($location, 'MAIN', '默认仓库', 'DEFAULT', 1, $now, $now);
                    INSERT INTO MaterialCatalog
                        (MaterialId, MaterialCode, Name, Kind, IsEnabled, CreatedAtUtc, UpdatedAtUtc)
                        VALUES ($material, 'LEGACY-MAT', 'Legacy material', 'Consumable', 1, $now, $now);
                    INSERT INTO MaterialLots
                        (LotId, MaterialId, MaterialCode, LotCode, Unit, IsQuarantined, CreatedAtUtc, UpdatedAtUtc)
                        VALUES ($lot, $material, 'LEGACY-MAT', 'LEGACY-LOT', 'EA', 0, $now, $now);
                    INSERT INTO InventoryBalances
                        (BalanceId, LotId, LocationId, OnHand, Reserved, UpdatedAtUtc)
                        VALUES ($balance, $lot, $location, 5, 0, $now);
                    INSERT INTO InventoryTransactions
                        (Id, RequestId, LineKey, TransactionKind, LotId, ToLocationId, MaterialCode, LotCode,
                         Quantity, Unit, Actor, DetailsJson, OccurredAtUtc)
                        VALUES ($transaction, $request, 'legacy:1', 'Receipt', $lot, $location,
                                'LEGACY-MAT', 'LEGACY-LOT', 5, 'EA', 'legacy', '{}', $now);
                    """;
                command.Parameters.Add(new SqliteParameter("$location", locationId.ToString().ToUpperInvariant()));
                command.Parameters.Add(new SqliteParameter("$material", materialId.ToString().ToUpperInvariant()));
                command.Parameters.Add(new SqliteParameter("$lot", lotId.ToString().ToUpperInvariant()));
                command.Parameters.Add(new SqliteParameter("$balance", Guid.NewGuid().ToString().ToUpperInvariant()));
                command.Parameters.Add(new SqliteParameter("$transaction", Guid.NewGuid().ToString().ToUpperInvariant()));
                command.Parameters.Add(new SqliteParameter("$request", requestId.ToString().ToUpperInvariant()));
                command.Parameters.Add(new SqliteParameter("$now", DateTime.UtcNow.ToString("O")));
                await command.ExecuteNonQueryAsync();
            }

            using (var factory = new MesWebApplicationFactory(databasePath))
            {
                using var client = factory.CreateClient();
                (await client.GetAsync("/health")).EnsureSuccessStatusCode();
                using var scope = factory.Services.CreateScope();
                var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
                Assert.True(await database.MaterialOperations.AnyAsync(item => item.RequestId == requestId));
                Assert.Equal(5m, await database.InventoryBalances
                    .Where(item => item.LotId == lotId)
                    .Select(item => item.OnHand)
                    .SingleAsync());

                database.InventoryTransactions.Add(new InventoryTransactionRecord
                {
                    Id = Guid.NewGuid(),
                    RequestId = Guid.NewGuid(),
                    LineKey = "invalid-request",
                    TransactionKind = "Adjustment",
                    Actor = "legacy-test",
                    DetailsJson = "{}",
                    OccurredAtUtc = DateTime.UtcNow
                });
                await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
            }

            SqliteConnection.ClearAllPools();
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var path = databasePath + suffix;
                if (!File.Exists(path)) continue;
                try { File.Delete(path); }
                catch (IOException) { }
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
