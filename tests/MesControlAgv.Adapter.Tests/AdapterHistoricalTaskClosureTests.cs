using System.Text.Json;
using MesControlAgv.Adapter;
using MesControlAgv.Adapter.Data;
using MesControlAgv.Adapter.Entities;
using MesControlAgv.Adapter.Services;
using MesControlAgv.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Adapter.Tests;

public sealed partial class AdapterTaskManualClosureTests
{
    [Fact]
    public async Task Historical_closure_at_destination_is_durable_and_never_replays_device_commands()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AdapterDbContext>().UseSqlite(connection).Options;
        await using var db = new AdapterDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var oldId = await SeedAsync(db);
        var replacementId = await SeedReplacementAsync(db);
        var command = Request() with { Replacement = new(replacementId, "map", "hash") };
        var driver = new AbsenceDriver();
        var first = await new AdapterService(db, driver, profile: Profile)
            .CloseUnconfirmedNavigationAsync(oldId, command, default);
        Assert.Equal("LM1", first.Evidence.CurrentStationId);
        Assert.Equal(command.Replacement, first.Replacement);
        Assert.Equal("hash", first.Evidence.MapEvidence!.Md5);
        Assert.Equal("write-reset", first.PreviousError);
        Assert.Equal("arrived", (await db.Tasks.FindAsync(replacementId))!.State);
        await using var restarted = new AdapterDbContext(options);
        var service = new AdapterService(restarted, driver, profile: Profile);
        var replay = await service.CloseUnconfirmedNavigationAsync(oldId, command, default);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(replay));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CloseUnconfirmedNavigationAsync(oldId,
            command with { Replacement = command.Replacement with { TaskId = Guid.NewGuid() } }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DispatchAsync(oldId, "LM1", default));
        Assert.Equal(1, driver.Reads);
        Assert.Equal(0, driver.Mutations);
    }

    [Fact]
    public async Task Durable_receipt_is_readable_without_new_disposition_permissions()
    {
        await using var db = NewMemoryDb();
        var oldId = await SeedAsync(db);
        var command = Request() with { Replacement = new(await SeedReplacementAsync(db), "map", "hash") };
        var driver = new AbsenceDriver();
        var original = await new AdapterService(db, driver, profile: Profile)
            .CloseUnconfirmedNavigationAsync(oldId, command, default);
        var readOnlyService = new AdapterService(db, driver, profile: Profile with { PhysicalAcceptance = null },
            runMode: AdapterRunMode.ReadOnlyPreflight);
        var replay = await readOnlyService.CloseUnconfirmedNavigationAsync(oldId, command, default);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(replay));
        await Assert.ThrowsAsync<ReadOnlyPreflightModeException>(() => readOnlyService.CloseUnconfirmedNavigationAsync(
            Guid.NewGuid(), Request(), default));
        Assert.Equal(1, driver.Reads);
        Assert.Equal(0, driver.Mutations);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("self")]
    [InlineData("empty-id")]
    [InlineData("not-arrived")]
    [InlineData("other-agv")]
    [InlineData("other-target")]
    [InlineData("other-path")]
    [InlineData("device-id")]
    [InlineData("no-map")]
    [InlineData("map")]
    [InlineData("map-authority")]
    [InlineData("map-path")]
    [InlineData("source")]
    [InlineData("ids")]
    [InlineData("partial")]
    [InlineData("moving")]
    [InlineData("completed")]
    [InlineData("stale")]
    public async Task Historical_closure_rejects_invalid_replacement_or_current_evidence(string defect)
    {
        await using var db = NewMemoryDb();
        var oldId = await SeedAsync(db);
        var replacementId = defect == "missing" ? Guid.NewGuid() : await SeedReplacementAsync(db, defect);
        if (defect == "self") replacementId = oldId;
        if (defect == "empty-id") replacementId = Guid.Empty;
        var driver = new AbsenceDriver { Defect = defect };
        var command = Request() with { Replacement = new(replacementId, "map", "hash") };
        await Assert.ThrowsAnyAsync<Exception>(() => new AdapterService(db, driver, profile: Profile)
            .CloseUnconfirmedNavigationAsync(oldId, command, default));
        Assert.Equal("unknown", (await db.Tasks.FindAsync(oldId))!.State);
        Assert.Empty(await db.TaskManualClosures.ToListAsync());
        Assert.Equal(0, driver.Mutations);
    }

    [Fact]
    public void Legacy_closure_command_serialization_is_unchanged()
    {
        var command = Request();
        var legacy = new { command.RequestId, command.Actor, command.Reason, command.AgvId,
            command.SourceStationId, command.TargetStationId, command.PlannedPath };
        Assert.Equal(JsonSerializer.Serialize(legacy), JsonSerializer.Serialize(command));
        var evidence = new AgvTaskAbsenceEvidence(Guid.NewGuid(), "LM7", Route, [], DateTimeOffset.UtcNow);
        var result = new FieldNavigationManualClosureResult(evidence.TaskId, command.RequestId, command.Actor,
            command.Reason, command.AgvId, "LM7", "LM1", "error", evidence, DateTimeOffset.UtcNow);
        Assert.DoesNotContain("Replacement", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("MapEvidence", JsonSerializer.Serialize(result));
    }

    private static async Task<Guid> SeedReplacementAsync(AdapterDbContext db, string? defect = null)
    {
        var id = Guid.NewGuid();
        db.Tasks.Add(new AdapterTask
        {
            TaskId = id, DeviceTaskId = defect == "device-id" ? "unrelated" : id.ToString("N"),
            AgvId = defect == "other-agv" ? "AGV-02" : "AGV-01",
            State = defect == "not-arrived" ? "unknown" : "arrived",
            TargetStationId = defect == "other-target" ? "LM2" : "LM1",
            PathJson = JsonSerializer.Serialize(defect == "other-path" ? ["LM7", "LM2", "LM1"] : Route)
        });
        await db.SaveChangesAsync();
        return id;
    }
}
