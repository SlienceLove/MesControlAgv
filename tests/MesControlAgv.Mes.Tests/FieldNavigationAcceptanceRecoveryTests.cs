using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace MesControlAgv.Mes.Tests;

public sealed class FieldNavigationAcceptanceRecoveryTests
{
    [Theory]
    [InlineData("task")]
    [InlineData("agv")]
    [InlineData("target")]
    [InlineData("vendor")]
    [InlineData("missing-vendor")]
    public async Task Mismatched_arrival_cannot_overwrite_the_bound_task_or_complete_acceptance(string mismatch)
    {
        await using var fixture = await Fixture.CreateAsync();
        var expected = fixture.Gateway.Response!;
        fixture.Gateway.Response = mismatch switch
        {
            "task" => expected with { TaskId = Guid.NewGuid() },
            "agv" => expected with { AgvId = "AGV-OTHER" },
            "target" => expected with { TargetStationId = "LM2" },
            "vendor" => expected with { DeviceTaskId = "another-vendor-task" },
            "missing-vendor" => expected with { DeviceTaskId = "" },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };

        await fixture.Worker.ReconcileOnceAsync(CancellationToken.None);
        await fixture.Worker.ReconcileOnceAsync(CancellationToken.None);
        var mismatchedResponse = fixture.Gateway.Response;
        fixture.Gateway.Response = expected;
        await fixture.Worker.ReconcileOnceAsync(CancellationToken.None);

        await using var scope = fixture.Provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<FieldNavigationAcceptanceRepository>();
        var stored = (await repository.GetAsync(expected.TaskId, CancellationToken.None))!;
        Assert.Equal(FieldNavigationAcceptanceStatuses.Unknown, stored.Status);
        Assert.Equal(expected.DeviceTaskId, stored.DeviceTaskId);
        Assert.Contains("adapter_task_identity_mismatch", stored.LastError);
        var expectedMismatch = mismatch switch
        {
            "task" => "task_id", "agv" => "agv_id", "target" => "target_station",
            _ => "vendor_task_id"
        };
        Assert.Contains(expectedMismatch, stored.LastError);
        var audit = Assert.Single(await repository.ListAuditsAsync(expected.TaskId, CancellationToken.None));
        Assert.Equal("AdapterTaskIdentityMismatch", audit.EventType);
        using var details = JsonDocument.Parse(audit.DetailsJson);
        Assert.Equal(expectedMismatch, Assert.Single(details.RootElement.GetProperty("mismatches").EnumerateArray()).GetString());
        Assert.Equal(expected.TaskId, details.RootElement.GetProperty("expected").GetProperty("taskId").GetGuid());
        Assert.Equal(expected.DeviceTaskId, details.RootElement.GetProperty("expected").GetProperty("DeviceTaskId").GetString());
        var observed = details.RootElement.GetProperty("observed");
        Assert.Equal(mismatchedResponse.TaskId, observed.GetProperty("TaskId").GetGuid());
        Assert.Equal(mismatchedResponse.AgvId, observed.GetProperty("AgvId").GetString());
        Assert.Equal(mismatchedResponse.TargetStationId, observed.GetProperty("TargetStationId").GetString());
        Assert.Equal(mismatchedResponse.DeviceTaskId, observed.GetProperty("DeviceTaskId").GetString());
        Assert.Equal(expected.TaskId, Assert.Single(fixture.Gateway.Reads));
    }

    [Theory]
    [InlineData("arrived", "arrived")]
    [InlineData("completed", "arrived")]
    [InlineData("moving", "moving")]
    [InlineData("paused", "moving")]
    [InlineData("unknown", "moving")]
    [InlineData("cancelled", "cancelled")]
    [InlineData("failed", "failed")]
    public async Task Matching_task_preserves_normal_status_mapping_and_deduplicates_audit(string state, string expectedStatus)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Gateway.Response = fixture.Gateway.Response! with { State = state };
        await fixture.Worker.ReconcileOnceAsync(CancellationToken.None);
        await fixture.Worker.ReconcileOnceAsync(CancellationToken.None);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<FieldNavigationAcceptanceRepository>();
        var stored = (await repository.GetAsync(fixture.AcceptanceId, CancellationToken.None))!;
        Assert.Equal(expectedStatus, stored.Status);
        Assert.Equal("vendor-task", stored.DeviceTaskId);
        Assert.True((await repository.ListAuditsAsync(stored.Id, CancellationToken.None)).Count <= 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_failed_read_keeps_confirmed_motion_and_can_reconcile_later(bool throws)
    {
        await using var fixture = await Fixture.CreateAsync();
        var arrival = fixture.Gateway.Response;
        fixture.Gateway.Response = null;
        fixture.Gateway.ReadException = throws ? new TimeoutException("offline test read timeout") : null;
        await fixture.Worker.ReconcileOnceAsync(CancellationToken.None);
        await fixture.Worker.ReconcileOnceAsync(CancellationToken.None);
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<FieldNavigationAcceptanceRepository>();
            var stored = (await repository.GetAsync(fixture.AcceptanceId, CancellationToken.None))!;
            Assert.Equal(FieldNavigationAcceptanceStatuses.Moving, stored.Status);
            Assert.Equal("vendor-task", stored.DeviceTaskId);
            Assert.Single(await repository.ListAuditsAsync(stored.Id, CancellationToken.None));
        }
        fixture.Gateway.Response = arrival;
        fixture.Gateway.ReadException = null;
        await fixture.Worker.ReconcileOnceAsync(CancellationToken.None);
        await using var finalScope = fixture.Provider.CreateAsyncScope();
        var finalRepository = finalScope.ServiceProvider.GetRequiredService<FieldNavigationAcceptanceRepository>();
        var arrived = (await finalRepository.GetAsync(fixture.AcceptanceId, CancellationToken.None))!;
        Assert.Equal(FieldNavigationAcceptanceStatuses.Arrived, arrived.Status);
        Assert.Null(arrived.LastError);
        Assert.Equal(2, (await finalRepository.ListAuditsAsync(arrived.Id, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Disabled_reconciliation_does_not_read_gateway()
    {
        await using var fixture = await Fixture.CreateAsync(enabled: false);
        await fixture.Worker.ReconcileOnceAsync(CancellationToken.None);
        Assert.Empty(fixture.Gateway.Reads);
    }

    [Fact]
    public async Task Unconsumed_permit_cannot_be_completed_by_an_adapter_read()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var record = await database.FieldNavigationAcceptances.SingleAsync();
            record.Status = FieldNavigationAcceptanceStatuses.Authorized;
            record.PermitConsumedAtUtc = null;
            await database.SaveChangesAsync();
        }
        await fixture.Worker.ReconcileOnceAsync(CancellationToken.None);
        Assert.Empty(fixture.Gateway.Reads);
        await using var finalScope = fixture.Provider.CreateAsyncScope();
        var stored = await finalScope.ServiceProvider.GetRequiredService<MesDbContext>().FieldNavigationAcceptances.SingleAsync();
        Assert.Equal(FieldNavigationAcceptanceStatuses.Authorized, stored.Status);
    }

    [Fact]
    public async Task Consumed_unbound_dispatch_can_learn_vendor_task_from_matching_response()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var record = await database.FieldNavigationAcceptances.SingleAsync();
            record.Status = FieldNavigationAcceptanceStatuses.Unknown;
            record.DeviceTaskId = null;
            record.LastError = "initial dispatch response lost";
            await database.SaveChangesAsync();
        }
        await fixture.Worker.ReconcileOnceAsync(CancellationToken.None);
        await using var finalScope = fixture.Provider.CreateAsyncScope();
        var stored = await finalScope.ServiceProvider.GetRequiredService<MesDbContext>().FieldNavigationAcceptances.SingleAsync();
        Assert.Equal(FieldNavigationAcceptanceStatuses.Arrived, stored.Status);
        Assert.Equal("vendor-task", stored.DeviceTaskId);
        Assert.Null(stored.LastError);
    }

    [Fact]
    public async Task Cancellation_propagates_without_persisting_a_device_warning()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Worker.ReconcileOnceAsync(cancellation.Token));
        Assert.Empty(fixture.Gateway.Reads);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Null((await database.FieldNavigationAcceptances.SingleAsync()).LastError);
        Assert.Empty(await database.FieldNavigationAcceptanceAudits.ToListAsync());
    }

    private sealed class Fixture(SqliteConnection connection, ServiceProvider provider,
        ReadOnlyGateway gateway, Guid acceptanceId, FieldNavigationAcceptanceRecoveryService worker) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;
        public ReadOnlyGateway Gateway { get; } = gateway;
        public Guid AcceptanceId { get; } = acceptanceId;
        public FieldNavigationAcceptanceRecoveryService Worker { get; } = worker;

        public static async Task<Fixture> CreateAsync(bool enabled = true)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var id = Guid.NewGuid();
            var gateway = new ReadOnlyGateway
            {
                Response = new AgvTaskResponse(id, "vendor-task", "LM7", "arrived", null, "AGV-01")
            };
            var services = new ServiceCollection();
            services.AddDbContext<MesDbContext>(options => options.UseSqlite(connection));
            services.AddScoped<FieldNavigationAcceptanceRepository>();
            services.AddSingleton<IAgvGateway>(gateway);
            var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
                await database.Database.EnsureCreatedAsync();
                database.FieldNavigationAcceptances.Add(new FieldNavigationAcceptance
                {
                    Id = id, AgvId = "AGV-01", SourceStationId = "LM1", TargetStationId = "LM7",
                    DeviceTaskId = "vendor-task", Status = FieldNavigationAcceptanceStatuses.Moving,
                    PermitConsumedAtUtc = DateTimeOffset.UtcNow
                });
                await database.SaveChangesAsync();
            }
            var worker = new FieldNavigationAcceptanceRecoveryService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                ProfileConfiguration.Default with
                { Features = new FeatureFlags { EnableFieldNavigationAcceptance = enabled, UseSimulator = false } },
                NullLogger<FieldNavigationAcceptanceRecoveryService>.Instance);
            return new Fixture(connection, provider, gateway, id, worker);
        }

        public async ValueTask DisposeAsync()
        {
            Worker.Dispose();
            await Provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class ReadOnlyGateway : IAgvGateway
    {
        public AgvTaskResponse? Response { get; set; }
        public Exception? ReadException { get; set; }
        public List<Guid> Reads { get; } = [];
        public Task<AgvTaskResponse?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads.Add(operationId);
            return ReadException is null ? Task.FromResult(Response) : Task.FromException<AgvTaskResponse?>(ReadException);
        }
        public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AgvTaskResponse> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken) => throw new NotSupportedException("No writes");
        public Task<AgvTaskResponse?> CancelAsync(Guid operationId, CancellationToken cancellationToken) => throw new NotSupportedException("No writes");
        public Task<AgvTaskResponse?> ExecuteAgvCommandAsync(string agvId, string command, Guid? taskId, CancellationToken cancellationToken) => throw new NotSupportedException("No writes");
    }
}
