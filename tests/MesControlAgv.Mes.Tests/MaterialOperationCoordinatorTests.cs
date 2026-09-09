using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Tests;

public sealed class MaterialOperationCoordinatorTests
{
    [Fact]
    public async Task Same_fingerprint_replays_and_different_payload_is_rejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new MesDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var coordinator = new MaterialOperationCoordinator();
        var requestId = Guid.NewGuid();

        var first = await coordinator.GetOrAddAsync(
            database,
            requestId,
            "receive",
            "fingerprint-1",
            "operator",
            CancellationToken.None);
        await database.SaveChangesAsync();

        var replay = await coordinator.GetOrAddAsync(
            database,
            requestId,
            "receive",
            "fingerprint-1",
            "operator",
            CancellationToken.None);
        Assert.Equal(first.RequestId, replay.RequestId);
        Assert.Equal(first.CreatedAtUtc, replay.CreatedAtUtc);

        await Assert.ThrowsAsync<MaterialRequestIdReusedException>(() =>
            coordinator.GetOrAddAsync(
                database,
                requestId,
                "receive",
                "fingerprint-2",
                "operator",
                CancellationToken.None));
    }

    [Fact]
    public async Task One_operation_can_own_multiple_line_transactions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new MesDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var coordinator = new MaterialOperationCoordinator();
        var requestId = Guid.NewGuid();
        await coordinator.GetOrAddAsync(
            database,
            requestId,
            "receive",
            "fingerprint-1",
            "operator",
            CancellationToken.None);

        var now = DateTime.UtcNow;
        database.InventoryTransactions.AddRange(
            NewTransaction(requestId, "line-1", now),
            NewTransaction(requestId, "line-2", now.AddSeconds(1)));
        await database.SaveChangesAsync();

        Assert.Equal(2, await database.InventoryTransactions.CountAsync(item => item.RequestId == requestId));
    }

    private static InventoryTransactionRecord NewTransaction(
        Guid requestId,
        string lineKey,
        DateTime occurredAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        RequestId = requestId,
        LineKey = lineKey,
        TransactionKind = "Receipt",
        Actor = "operator",
        DetailsJson = "{}",
        OccurredAtUtc = occurredAtUtc
    };
}
