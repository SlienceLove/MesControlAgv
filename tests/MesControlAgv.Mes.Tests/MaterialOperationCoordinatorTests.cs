using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

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

    [Fact]
    public async Task Execute_is_atomic_and_replay_does_not_run_mutation_again()
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
        var mutationCount = 0;

        var first = await coordinator.ExecuteAsync(
            database,
            requestId,
            "receive",
            "fingerprint-atomic",
            "operator",
            () =>
            {
                mutationCount++;
                database.InventoryTransactions.Add(NewTransaction(requestId, "line-1", DateTime.UtcNow));
                return Task.FromResult(7);
            },
            value => JsonSerializer.Serialize(value),
            value => JsonSerializer.Deserialize<int>(value),
            CancellationToken.None);

        var replay = await coordinator.ExecuteAsync(
            database,
            requestId,
            "receive",
            "fingerprint-atomic",
            "operator",
            () =>
            {
                mutationCount++;
                return Task.FromResult(99);
            },
            value => JsonSerializer.Serialize(value),
            value => JsonSerializer.Deserialize<int>(value),
            CancellationToken.None);

        Assert.False(first.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(7, replay.Value);
        Assert.Equal(1, mutationCount);
        Assert.Single(await database.InventoryTransactions.ToListAsync());
    }

    [Fact]
    public async Task Execute_rolls_back_pending_operation_when_mutation_fails()
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

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteAsync(
                database,
                requestId,
                "receive",
                "fingerprint-failing",
                "operator",
                () => throw new InvalidOperationException("boom"),
                value => JsonSerializer.Serialize(value),
                value => JsonSerializer.Deserialize<int>(value),
                CancellationToken.None));

        Assert.False(await database.MaterialOperations.AnyAsync(item => item.RequestId == requestId));

        var retry = await coordinator.ExecuteAsync(
            database,
            requestId,
            "receive",
            "fingerprint-failing",
            "operator",
            () => Task.FromResult(11),
            value => JsonSerializer.Serialize(value),
            value => JsonSerializer.Deserialize<int>(value),
            CancellationToken.None);
        Assert.Equal(11, retry.Value);
        Assert.False(retry.IsReplay);
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
