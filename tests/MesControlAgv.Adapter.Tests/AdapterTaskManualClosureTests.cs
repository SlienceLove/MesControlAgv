using System.Text.Json;
using MesControlAgv.Adapter.Data;
using MesControlAgv.Adapter.Entities;
using MesControlAgv.Adapter.Modules;
using MesControlAgv.Adapter.Services;
using MesControlAgv.Contracts;
using MesControlAgv.Domain.Profiles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Adapter.Tests;

public sealed partial class AdapterTaskManualClosureTests
{
    [Fact]
    public async Task Startup_upgrades_existing_database_without_losing_tasks_and_can_run_twice()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AdapterDbContext>(options => options.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        Guid id;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AdapterDbContext>();
            await db.Database.EnsureCreatedAsync();
            id = await SeedAsync(db);
            // Recreate the deployed schema before manual closure was introduced.
            await db.Database.ExecuteSqlRawAsync("DROP TABLE TaskManualClosures");
        }
        var module = new AgvAdapterModule();
        await module.InitializeAsync(provider, default);
        await module.InitializeAsync(provider, default);
        await using var upgradedScope = provider.CreateAsyncScope();
        var upgraded = upgradedScope.ServiceProvider.GetRequiredService<AdapterDbContext>();
        Assert.Equal("unknown", (await upgraded.Tasks.FindAsync(id))!.State);
        var result = await new AdapterService(upgraded, new AbsenceDriver(), profile: Profile)
            .CloseUnconfirmedNavigationAsync(id, Request(), default);
        Assert.Equal(FieldNavigationAcceptanceStatuses.ManuallyClosed, result.State);
        Assert.Single(await upgraded.TaskManualClosures.ToListAsync());
    }

    private static readonly string[] Route = ["LM7", "LM6", "LM1"];
    private static ProfileConfiguration Profile => ProfileConfiguration.Default with
    {
        PhysicalAcceptance = new PhysicalAcceptanceProfile(),
        Features = ProfileConfiguration.Default.Features with { EnableTaskCancellation = true }
    };

    [Fact]
    public async Task Closure_is_durable_idempotent_and_old_id_cannot_write_or_be_resurrected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AdapterDbContext>().UseSqlite(connection).Options;
        await using var db = new AdapterDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var id = await SeedAsync(db);
        var driver = new AbsenceDriver();
        var request = Request();
        var service = new AdapterService(db, driver, profile: Profile);
        var first = await service.CloseUnconfirmedNavigationAsync(id, request, default);
        Assert.Equal("write-reset", first.PreviousError);
        Assert.Equal(FieldNavigationAcceptanceStatuses.ManuallyClosed, (await service.GetAsync(id, default))!.State);
        Assert.Equal("write-reset", (await db.Tasks.FindAsync(id))!.LastError);

        await using var restarted = new AdapterDbContext(options);
        var afterRestart = new AdapterService(restarted, driver, profile: Profile);
        var replay = await afterRestart.CloseUnconfirmedNavigationAsync(id, request, default);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(replay));
        await Assert.ThrowsAsync<InvalidOperationException>(() => afterRestart.DispatchAsync(id, "LM1", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => afterRestart.CancelAsync(id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => afterRestart.CloseUnconfirmedNavigationAsync(
            id, request with { Reason = "different" }, default));
        var secondId = await SeedAsync(restarted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => afterRestart.CloseUnconfirmedNavigationAsync(secondId, request, default));
        Assert.Single(await db.TaskManualClosures.ToListAsync());
        Assert.Equal(1, driver.Reads);
        Assert.Equal(0, driver.Mutations);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("path")]
    [InlineData("task")]
    [InlineData("ids")]
    [InlineData("moving")]
    [InlineData("completed")]
    [InlineData("partial")]
    [InlineData("stale")]
    [InlineData("timeout")]
    public async Task Insufficient_evidence_never_closes_or_sends_commands(string defect)
    {
        await using var db = NewMemoryDb();
        var id = await SeedAsync(db);
        var driver = new AbsenceDriver { Defect = defect };
        var service = new AdapterService(db, driver, profile: Profile);
        await Assert.ThrowsAnyAsync<Exception>(() => service.CloseUnconfirmedNavigationAsync(id, Request(), default));
        Assert.Equal("unknown", (await db.Tasks.FindAsync(id))!.State);
        Assert.Empty(await db.TaskManualClosures.ToListAsync());
        Assert.Equal(0, driver.Mutations);
    }

    [Fact]
    public async Task Concurrent_scopes_and_stale_tracking_cannot_overwrite_closure()
    {
        var options = new DbContextOptionsBuilder<AdapterDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var one = new AdapterDbContext(options);
        await using var two = new AdapterDbContext(options);
        var id = await SeedAsync(one);
        await two.Tasks.FindAsync(id); // Deliberately track the pre-closure Unknown state.
        var driver = new AbsenceDriver();
        var gate = new PhysicalAgvSessionGate();
        var a = new AdapterService(one, driver, profile: Profile, physicalSessionGate: gate);
        var b = new AdapterService(two, driver, profile: Profile, physicalSessionGate: gate);
        var request = Request();
        await Task.WhenAll(a.CloseUnconfirmedNavigationAsync(id, request, default), b.CloseUnconfirmedNavigationAsync(id, request, default));
        Assert.Equal(FieldNavigationAcceptanceStatuses.ManuallyClosed, (await b.GetAsync(id, default))!.State);
        Assert.Equal(1, driver.Reads);
        Assert.Equal(0, driver.Mutations);
    }

    private static AdapterDbContext NewMemoryDb() => new(new DbContextOptionsBuilder<AdapterDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static async Task<Guid> SeedAsync(AdapterDbContext db)
    {
        var id = Guid.NewGuid();
        db.Tasks.Add(new AdapterTask { TaskId = id, DeviceTaskId = id.ToString("N"), AgvId = "AGV-01",
            TargetStationId = "LM1", PathJson = JsonSerializer.Serialize(Route), State = "unknown", LastError = "write-reset" });
        await db.SaveChangesAsync();
        return id;
    }
    private static FieldNavigationManualCloseCommand Request() => new(Guid.NewGuid(), "admin", "operator-approved closure",
        "AGV-01", Route[0], Route[^1], Route);

    private sealed class AbsenceDriver : IAgvDeviceClient, IAgvTaskAbsenceEvidenceClient, IControllerMapEvidenceDeviceClient
    {
        public string? Defect { get; init; }
        public int Reads { get; private set; }
        public int Mutations { get; private set; }
        public async Task<AgvTaskAbsenceEvidence> ReadTaskAbsenceAtDestinationAsync(Guid id, IReadOnlyList<string> path, CancellationToken ct) =>
            (await ReadTaskAbsenceAsync(id, path, ct)) with { CurrentStationId = Defect == "source" ? "LM2" : path[^1] };
        public Task<ControllerMapEvidenceResponse?> GetControllerMapEvidenceAsync(CancellationToken ct) =>
            Task.FromResult<ControllerMapEvidenceResponse?>(Defect == "no-map" ? null : new(
                Defect != "map-authority", "test", Defect == "map" ? "other-map" : "map", "1", "hash",
                Route, Defect == "map-path" ? [] : [new("LM7", "LM6"), new("LM6", "LM1")], DateTimeOffset.UtcNow));
        public Task<AgvTaskAbsenceEvidence> ReadTaskAbsenceAsync(Guid id, IReadOnlyList<string> path, CancellationToken ct)
        {
            Reads++;
            if (Defect == "timeout") throw new TimeoutException();
            var segments = path.Zip(path.Skip(1), (from, to) => (from, to)).Select((edge, index) =>
            {
                if (index == 0) return new AgvAbsentSegment(id.ToString("N"), 404);
                var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                    $"{id:N}|{index}|{edge.from}|{edge.to}"))[..16];
                bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
                bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
                return new AgvAbsentSegment(new Guid(bytes).ToString("N"), 404);
            }).ToArray();
            if (Defect == "ids") segments[0] = new AgvAbsentSegment(Guid.NewGuid().ToString("N"), 404);
            if (Defect is "moving" or "completed") segments[0] = segments[0] with { VendorStatus = Defect == "moving" ? 2 : 4 };
            return Task.FromResult(new AgvTaskAbsenceEvidence(Defect == "task" ? Guid.NewGuid() : id,
                Defect == "source" ? "LM2" : path[0], Defect == "path" ? ["LM7", "LM2", "LM1"] : path,
                Defect == "partial" ? segments[..1] : segments,
                DateTimeOffset.UtcNow.AddMinutes(Defect == "stale" ? -5 : 0)));
        }
        private T Write<T>() { Mutations++; throw new InvalidOperationException("Unexpected physical write"); }
        public Task EnsureControlAsync(CancellationToken ct) => Write<Task>();
        public Task<bool> ReleaseControlAsync(CancellationToken ct) => Write<Task<bool>>();
        public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken ct) => throw new InvalidOperationException("Must use fresh absence evidence");
        public Task<AgvTaskResponse?> GetTaskAsync(Guid id, CancellationToken ct) => throw new InvalidOperationException("Terminal task must not be polled");
        public Task<AgvTaskResponse> NavigateAsync(Guid id, string? source, string target, CancellationToken ct) => Write<Task<AgvTaskResponse>>();
        public Task<AgvTaskResponse?> PauseAsync(Guid id, CancellationToken ct) => Write<Task<AgvTaskResponse?>>();
        public Task<AgvTaskResponse?> ResumeAsync(Guid id, CancellationToken ct) => Write<Task<AgvTaskResponse?>>();
        public Task<AgvTaskResponse?> CancelAsync(Guid id, CancellationToken ct) => Write<Task<AgvTaskResponse?>>();
    }
}
