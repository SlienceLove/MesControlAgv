using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
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
