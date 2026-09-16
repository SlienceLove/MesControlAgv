using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Tests;

public sealed partial class FieldNavigationManualClosureTests
{
    [Fact]
    public async Task Oversized_actor_is_rejected_before_persisting_a_pending_request()
    {
        await using var db = MemoryDb();
        var (old, replacement) = await SeedHistoricalPairAsync(db);
        var gateway = new ClosureGateway();
        await Assert.ThrowsAsync<ArgumentException>(() => Service(db, gateway).CloseAsync(old.Id,
            new(Guid.NewGuid(), new string('a', 257), "historical closure", replacement.Id), default));
        Assert.Equal(0, gateway.Calls);
        Assert.Empty(await db.FieldNavigationAcceptanceAudits.ToListAsync());
    }
    [Fact]
    public async Task Historical_replacement_is_audited_and_lost_ack_recovers_after_restart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>().UseSqlite(connection).Options;
        await using var db = new MesDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var (old, replacement) = await SeedHistoricalPairAsync(db);
        var gateway = new ClosureGateway { LoseFirstAcknowledgement = true };
        var request = new FieldNavigationManualCloseRequest(Guid.NewGuid(), "admin", "verified historical replacement", replacement.Id);
        await Assert.ThrowsAsync<HttpRequestException>(() => Service(db, gateway).CloseAsync(old.Id, request, default));
        await using var restarted = new MesDbContext(options);
        var service = Service(restarted, gateway);
        var result = await service.CloseAsync(old.Id, request, default);
        var replay = await service.CloseAsync(old.Id, request, default);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(replay));
        Assert.Equal(replacement.Id, result.Replacement!.TaskId);
        Assert.Equal("LM1", result.Evidence.CurrentStationId);
        Assert.Equal(1, gateway.Dispositions);
        Assert.Equal(0, gateway.Mutations);
        Assert.Equal("manually_closed", (await restarted.FieldNavigationAcceptances.FindAsync(old.Id))!.Status);
        Assert.Equal("write-reset", (await restarted.FieldNavigationAcceptances.FindAsync(old.Id))!.LastError);
        Assert.Equal("arrived", (await restarted.FieldNavigationAcceptances.FindAsync(replacement.Id))!.Status);
        var audits = await restarted.FieldNavigationAcceptanceAudits.ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, audit => Assert.Contains(replacement.Id.ToString(), audit.DetailsJson));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CloseAsync(old.Id,
            request with { ReplacementAcceptanceId = Guid.NewGuid() }, default));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("self")]
    [InlineData("empty-id")]
    [InlineData("status")]
    [InlineData("not-consumed")]
    [InlineData("earlier-created")]
    [InlineData("earlier-dispatched")]
    [InlineData("not-completed")]
    [InlineData("agv")]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("path")]
    [InlineData("map")]
    [InlineData("md5")]
    [InlineData("device-id")]
    [InlineData("workflow")]
    public async Task Historical_replacement_must_be_later_consumed_independent_arrival_of_exact_route(string defect)
    {
        await using var db = MemoryDb();
        var (old, replacement) = await SeedHistoricalPairAsync(db, defect);
        var gateway = new ClosureGateway();
        var reference = defect switch { "missing" => Guid.NewGuid(), "self" => old.Id, "empty-id" => Guid.Empty, _ => replacement.Id };
        await Assert.ThrowsAnyAsync<Exception>(() => Service(db, gateway).CloseAsync(old.Id,
            new(Guid.NewGuid(), "admin", "historical closure", reference), default));
        Assert.Equal(0, gateway.Calls);
        Assert.Empty(await db.FieldNavigationAcceptanceAudits.ToListAsync());
        Assert.Equal("unknown", old.Status);
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("station")]
    [InlineData("map")]
    [InlineData("map-authority")]
    [InlineData("stale")]
    [InlineData("stale-map")]
    [InlineData("segment")]
    [InlineData("active")]
    public async Task Historical_adapter_proof_must_match_the_persisted_reference(string defect)
    {
        await using var db = MemoryDb();
        var (old, replacement) = await SeedHistoricalPairAsync(db);
        var gateway = new ClosureGateway { Defect = defect };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db, gateway).CloseAsync(old.Id,
            new(Guid.NewGuid(), "admin", "historical closure", replacement.Id), default));
        Assert.Equal("unknown", old.Status);
        Assert.DoesNotContain(await db.FieldNavigationAcceptanceAudits.ToListAsync(), audit => audit.EventType == "ManualClosureConfirmed");
    }

    private static async Task<(FieldNavigationAcceptance Old, FieldNavigationAcceptance Replacement)> SeedHistoricalPairAsync(
        MesDbContext db, string? defect = null)
    {
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var oldId = Guid.NewGuid();
        var old = new FieldNavigationAcceptance
        {
            Id = oldId, AgvId = "AGV-01", SourceStationId = "LM7", TargetStationId = "LM1",
            MapName = "map", MapMd5 = "hash", PlannedPathJson = "[\"LM7\",\"LM6\",\"LM1\"]",
            Status = "unknown", CreatedAtUtc = start, PermitConsumedAtUtc = start.AddMinutes(1),
            UpdatedAtUtc = start.AddMinutes(1), DeviceTaskId = oldId.ToString("N"), LastError = "write-reset"
        };
        var id = Guid.NewGuid();
        var replacement = new FieldNavigationAcceptance
        {
            Id = id, AgvId = defect == "agv" ? "AGV-02" : "AGV-01",
            SourceStationId = defect == "source" ? "LM2" : "LM7", TargetStationId = defect == "target" ? "LM2" : "LM1",
            MapName = defect == "map" ? "other" : "map", MapMd5 = defect == "md5" ? "other" : "hash",
            PlannedPathJson = defect == "path" ? "[\"LM7\",\"LM1\"]" : old.PlannedPathJson,
            Status = defect == "status" ? "unknown" : "arrived",
            CreatedAtUtc = start.AddMinutes(defect == "earlier-created" ? -1 : 2),
            PermitConsumedAtUtc = defect == "not-consumed" ? null : start.AddMinutes(defect == "earlier-dispatched" ? 0 : 3),
            UpdatedAtUtc = start.AddMinutes(defect == "not-completed" ? 2 : 4),
            DeviceTaskId = defect == "device-id" ? "unrelated" : id.ToString("N"),
            WorkflowRunId = defect == "workflow" ? Guid.NewGuid() : null
        };
        db.FieldNavigationAcceptances.AddRange(old, replacement);
        await db.SaveChangesAsync();
        return (old, replacement);
    }
}
