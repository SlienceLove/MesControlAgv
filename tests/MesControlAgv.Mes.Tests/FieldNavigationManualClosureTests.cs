using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Tests;

public sealed partial class FieldNavigationManualClosureTests
{
    [Fact]
    public async Task Lost_adapter_ack_is_reconciled_by_same_request_and_survives_mes_restart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MesDbContext>().UseSqlite(connection).Options;
        await using var db = new MesDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var record = await SeedAsync(db);
        var gateway = new ClosureGateway { LoseFirstAcknowledgement = true };
        var request = new FieldNavigationManualCloseRequest(Guid.NewGuid(), "admin", "approved reset disposition");
        var service = Service(db, gateway);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.CloseAsync(record.Id, request, default));
        Assert.Equal("unknown", record.Status);
        Assert.Equal("write-reset", record.LastError);
        Assert.Single(await db.FieldNavigationAcceptanceAudits.ToListAsync());
        await using var restarted = new MesDbContext(options);
        var second = Service(restarted, gateway);
        var result = await second.CloseAsync(record.Id, request, default);
        var replay = await second.CloseAsync(record.Id, request, default);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(replay));
        Assert.Equal(2, gateway.Calls);
        Assert.Equal(1, gateway.Dispositions);
        Assert.Equal(0, gateway.Mutations);
        var stored = await restarted.FieldNavigationAcceptances.FindAsync(record.Id);
        Assert.Equal(FieldNavigationAcceptanceStatuses.ManuallyClosed, stored!.Status);
        Assert.Equal("write-reset", stored.LastError);
        Assert.Equal(2, await restarted.FieldNavigationAcceptanceAudits.CountAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.CloseAsync(record.Id,
            request with { Reason = "different" }, default));
    }

    [Theory]
    [InlineData("guest")]
    [InlineData("no-request")]
    [InlineData("no-reason")]
    [InlineData("workflow")]
    [InlineData("draft")]
    [InlineData("not-consumed")]
    public async Task Invalid_or_unauthorized_closure_never_calls_adapter(string condition)
    {
        await using var db = MemoryDb();
        var record = await SeedAsync(db, condition);
        var gateway = new ClosureGateway();
        var request = new FieldNavigationManualCloseRequest(condition == "no-request" ? Guid.Empty : Guid.NewGuid(),
            condition == "guest" ? "guest" : "admin", condition == "no-reason" ? " " : "approved");
        await Assert.ThrowsAnyAsync<Exception>(() => Service(db, gateway).CloseAsync(record.Id, request, default));
        Assert.Equal(0, gateway.Calls);
        Assert.Empty(await db.FieldNavigationAcceptanceAudits.ToListAsync());
        Assert.NotEqual(FieldNavigationAcceptanceStatuses.ManuallyClosed, record.Status);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("actor")]
    [InlineData("path")]
    [InlineData("segment")]
    [InlineData("active")]
    public async Task Adapter_identity_or_absence_mismatch_does_not_close_the_mes_record(string defect)
    {
        await using var db = MemoryDb();
        var record = await SeedAsync(db);
        var gateway = new ClosureGateway { Defect = defect };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db, gateway).CloseAsync(record.Id,
            new(Guid.NewGuid(), "admin", "approved"), default));
        Assert.Equal("unknown", record.Status);
        Assert.Equal("write-reset", record.LastError);
        Assert.DoesNotContain(await db.FieldNavigationAcceptanceAudits.ToListAsync(), item => item.EventType == "ManualClosureConfirmed");
    }

    [Fact]
    public async Task Concurrent_requests_on_different_scopes_commit_only_one_closure_audit()
    {
        var options = new DbContextOptionsBuilder<MesDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var one = new MesDbContext(options);
        await using var two = new MesDbContext(options);
        var record = await SeedAsync(one);
        await two.FieldNavigationAcceptances.FindAsync(record.Id);
        var gateway = new ClosureGateway();
        var request = new FieldNavigationManualCloseRequest(Guid.NewGuid(), "admin", "approved");
        await Task.WhenAll(Service(one, gateway).CloseAsync(record.Id, request, default),
            Service(two, gateway).CloseAsync(record.Id, request, default));
        Assert.Equal(1, gateway.Calls);
        Assert.Single((await one.FieldNavigationAcceptanceAudits.ToListAsync()).Where(item => item.EventType == "ManualClosureConfirmed"));
    }

    private static MesDbContext MemoryDb() => new(new DbContextOptionsBuilder<MesDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static FieldNavigationManualClosureService Service(MesDbContext db, ClosureGateway gateway) =>
        new(new FieldNavigationAcceptanceRepository(db), gateway, new ConfiguredWorkflowRunControlAuthorizer(
            Options.Create(new WorkflowRunControlAuthorizationOptions
            {
                Operators = [new() { Name = "admin", Permissions = [WorkflowRunControlPermissions.ResolveUnknown] }]
            })));
    private static async Task<FieldNavigationAcceptance> SeedAsync(MesDbContext db, string? condition = null)
    {
        var item = new FieldNavigationAcceptance
        {
            Id = Guid.NewGuid(), AgvId = "AGV-01", SourceStationId = "LM7", TargetStationId = "LM1",
            PlannedPathJson = "[\"LM7\",\"LM6\",\"LM1\"]", Status = condition == "draft" ? "draft" : "unknown",
            PermitConsumedAtUtc = condition == "not-consumed" ? null : DateTimeOffset.UtcNow,
            WorkflowRunId = condition == "workflow" ? Guid.NewGuid() : null,
            LastError = "write-reset"
        };
        db.FieldNavigationAcceptances.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    private sealed class ClosureGateway : IAgvGateway, IFieldNavigationManualClosureGateway
    {
        public int Calls { get; private set; }
        public int Dispositions { get; private set; }
        public int Mutations { get; private set; }
        public bool LoseFirstAcknowledgement { get; init; }
        public string? Defect { get; init; }
        private FieldNavigationManualClosureResult? _result;
        public Task<FieldNavigationManualClosureResult> CloseUnconfirmedNavigationAsync(
            Guid id, FieldNavigationManualCloseCommand command, CancellationToken ct)
        {
            Calls++;
            if (_result is null)
            {
                Dispositions++;
                var map = command.Replacement is null ? null : new ControllerMapEvidenceResponse(
                    Defect != "map-authority", "test", Defect == "map" ? "wrong" : command.Replacement.MapName,
                    "1", command.Replacement.MapMd5, command.PlannedPath, [], DateTimeOffset.UtcNow);
                var evidence = new AgvTaskAbsenceEvidence(id,
                    command.Replacement is null || Defect == "station" ? command.SourceStationId : command.TargetStationId,
                    Defect == "path" ? ["LM7", "LM2", "LM1"] : command.PlannedPath,
                    Defect == "segment" ? [] : [new(id.ToString("N"), Defect == "active" ? 2 : 404), new("second-segment", 404)],
                    DateTimeOffset.UtcNow.AddMinutes(Defect == "stale" ? -1 : 0), map,
                    command.Replacement is null ? null : DateTimeOffset.UtcNow.AddMinutes(Defect == "stale-map" ? -1 : 0));
                _result = new(Defect == "task" ? Guid.NewGuid() : id, command.RequestId,
                    Defect == "actor" ? "guest" : command.Actor, command.Reason, command.AgvId,
                    command.SourceStationId, command.TargetStationId, "write-reset", evidence, DateTimeOffset.UtcNow,
                    Defect == "replacement" ? null : command.Replacement);
                if (LoseFirstAcknowledgement) throw new HttpRequestException("Response lost after durable Adapter closure.");
            }
            return Task.FromResult(_result);
        }
        private T Write<T>() { Mutations++; throw new InvalidOperationException("Unexpected physical mutation"); }
        public Task<AgvTaskResponse> DispatchAsync(Guid id, string target, CancellationToken ct) => Write<Task<AgvTaskResponse>>();
        public Task<AgvTaskResponse?> CancelAsync(Guid id, CancellationToken ct) => Write<Task<AgvTaskResponse?>>();
        public Task<AgvTaskResponse?> ExecuteAgvCommandAsync(string agvId, string command, Guid? taskId, CancellationToken ct) => Write<Task<AgvTaskResponse?>>();
        public Task<AgvTaskResponse?> GetTaskAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
