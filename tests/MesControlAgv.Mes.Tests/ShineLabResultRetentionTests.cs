using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Tests;

public sealed class ShineLabResultRetentionTests
{
    private const string Equipment = "STN61_01";
    private const string EvidenceUrl = "/api/shinelab/devices/STN61_01/push-evidence?limit=100";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tcp_results_beyond_three_survive_restart_with_exact_envelopes(bool captureSamples)
    {
        // Synthetic fixture, NOT a reconstruction of the missing field Result.
        // DA screenshot numbers are merely offline parser test inputs.
        var body = """{"sampleId":"6","sampleName":"SYNTHETIC_TEST","testDate":"2026-09-23 09:50:34","compounds":[{"name":"TEST_ION_01","rt":5.055000,"area":4.386941,"concentration":1.354831}]}""";
        var frames = Enumerable.Range(1, 6).Select(index =>
            "{ \"strID\":\"synthetic-" + index + "\",\"strMethod\":\"Result\",\"equipmentCode\":\"STN61_01\",\"strCode\":\"\",\"vendorExtra\":\"retain me\",\"body\":" +
            (index == 6 ? """{"sampleId":"7","testDate":"2026-09-23 09:51:34","compounds":[]}""" : body) + " }").ToArray();
        string path;
        List<ShineLabPushEvidence> received;
        using (var factory = new MesWebApplicationFactory())
        using (var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<ShineLabTcpOptions>(options =>
            {
                options.Enabled = false; // The application must not bind any field port.
                options.PayloadProfile = ShineLabPayloadProfile.DcSeqSingleChannel;
            }))))
        using (var http = app.CreateClient())
        {
            path = factory.DatabasePath;
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            var options = Options.Create(new ShineLabTcpOptions
            {
                Enabled = true, ListenAddress = "127.0.0.1", Port = port,
                PayloadProfile = ShineLabPayloadProfile.DcSeqSingleChannel,
                CaptureProtocolSamples = captureSamples, SendCertificationOnConnect = false,
                StaleAfterSeconds = 60
            });
            var logger = new RecordingLogger();
            using var manager = new ShineLabConnectionManager();
            using var server = new ShineLabTcpServer(options, new ShineLabStatusHub(options), manager,
                logger, app.Services.GetRequiredService<IServiceScopeFactory>());
            await server.StartAsync(default);
            try
            {
                using var tcp = await ConnectAsync(port);
                await using var stream = tcp.GetStream();
                var before = DateTimeOffset.UtcNow.UtcTicks;
                foreach (var frame in frames)
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(frame + "\n"));

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                do
                {
                    received = (await http.GetFromJsonAsync<List<ShineLabPushEvidence>>(EvidenceUrl, timeout.Token))!;
                    if (received.Count < frames.Length) await Task.Delay(30, timeout.Token);
                } while (received.Count < frames.Length);

                Assert.Equal(frames.Length, received.Count);
                Assert.Single(received.Select(item => item.ConnectionId).Distinct());
                Assert.All(received, item =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(item.ConnectionId));
                    Assert.Equal("LineJson", item.WireFormat);
                    Assert.Equal("MissingTaskId", item.Correlation);
                    Assert.Null(item.TaskUuid);
                    Assert.InRange(item.ReceivedAtUtcTicks, before, DateTimeOffset.UtcNow.UtcTicks);
                    var expected = frames.Single(frame => frame.Contains("\"" + item.StrId + "\""));
                    Assert.Equal(expected, item.RawJson); // Includes whitespace and unknown envelope fields.
                    using var json = JsonDocument.Parse(expected);
                    Assert.Equal(json.RootElement.GetProperty("body").GetRawText(), item.BodyJson);
                });
                Assert.Equal(4, received.Count(item => item.IsDuplicate));
                Assert.False(received.Single(item => item.StrId == "synthetic-6").IsDuplicate);
                foreach (var frame in frames)
                    Assert.Equal(captureSamples, logger.Messages.Any(message =>
                        message.Contains("diagnostic RX") && message.Contains(frame)));

                // No unsolicited Config/Command/Init or other outbound frames.
                using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await stream.ReadAsync(new byte[1], quiet.Token));
            }
            finally { await server.StopAsync(default); }
        }

        using var restarted = new MesWebApplicationFactory(path);
        using var restartedHttp = restarted.CreateClient();
        var persisted = (await restartedHttp.GetFromJsonAsync<List<ShineLabPushEvidence>>(EvidenceUrl))!;
        Assert.Equal(received.OrderBy(item => item.StrId).Select(item => JsonSerializer.Serialize(item)),
            persisted.OrderBy(item => item.StrId).Select(item => JsonSerializer.Serialize(item)));
    }

    [Fact]
    public async Task Existing_body_only_rows_gain_nullable_columns_without_fabricating_envelopes()
    {
        string path;
        Guid id;
        using (var previous = new MesWebApplicationFactory())
        {
            using var http = previous.CreateClient();
            path = previous.DatabasePath;
            using var scope = previous.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var legacy = new ShineLabPushEvidence { EquipmentCode = Equipment, StrId = "legacy",
                Method = "Result", BodyJson = "{\"compounds\":[]}", Correlation = "MissingTaskId" };
            db.ShineLabPushEvidence.Add(legacy);
            await db.SaveChangesAsync();
            id = legacy.Id;
            // Test-only temp SQLite: simulate the intermediate body-only schema.
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE ShineLabPushEvidence DROP COLUMN RawJson;");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE ShineLabPushEvidence DROP COLUMN ConnectionId;");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE ShineLabPushEvidence DROP COLUMN WireFormat;");
        }
        using var upgraded = new MesWebApplicationFactory(path);
        using var client = upgraded.CreateClient();
        var row = Assert.Single((await client.GetFromJsonAsync<List<ShineLabPushEvidence>>(EvidenceUrl))!);
        Assert.Equal(id, row.Id);
        Assert.Equal("{\"compounds\":[]}", row.BodyJson);
        Assert.Null(row.RawJson);
        Assert.Null(row.ConnectionId);
        Assert.Null(row.WireFormat);
    }

    [Theory]
    [InlineData("{\"sampleId\":\"6\",\"testDate\":\"t1\",\"compounds\":[{\"value\":1}]}", true)]
    [InlineData("{\"sampleId\":\"6\",\"testDate\":\"t1\",\"compounds\":[{\"value\":2}]}", false)]
    [InlineData("{\"sampleId\":\"6\",\"testDate\":\"t2\",\"compounds\":[{\"value\":1}]}", false)]
    [InlineData("{\"sampleId\":\"7\",\"testDate\":\"t1\",\"compounds\":[{\"value\":1}]}", false)]
    [InlineData("{\"sampleId\":\"6\",\"compounds\":[{\"value\":1}]}", false)]
    [InlineData("{\"testDate\":\"t1\",\"compounds\":[{\"value\":1}]}", false)]
    public async Task Result_dedup_preserves_changed_values_runs_and_missing_identifiers(string secondBody, bool duplicate)
    {
        await using var db = new MesDbContext(new DbContextOptionsBuilder<MesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var options = Options.Create(new ShineLabTcpOptions { PayloadProfile = ShineLabPayloadProfile.DcSeqSingleChannel });
        var repository = new ShineLabTaskRepository(db, options);
        var firstBody = JsonSerializer.Deserialize<JsonElement>("""{"compounds":[{"value":1}],"testDate":"t1","sampleId":"6"}""");
        await repository.ApplyPushAsync("Result", Equipment, firstBody, default, "first");
        var second = JsonSerializer.Deserialize<JsonElement>(secondBody);
        await repository.ApplyPushAsync("Result", Equipment, second, default, "second");
        await repository.ApplyPushAsync("Result", Equipment, second, default, "third");
        var evidence = await repository.GetPushEvidenceAsync(Equipment, 100, default);
        Assert.Equal(3, evidence.Count);
        Assert.Equal(duplicate, evidence.Single(item => item.StrId == "second").IsDuplicate);
        Assert.Equal(second.TryGetProperty("sampleId", out _) && second.TryGetProperty("testDate", out _),
            evidence.Single(item => item.StrId == "third").IsDuplicate);
    }

    [Fact]
    public async Task Explicit_task_keeps_original_receipt_context_and_deduplicates_only_observation()
    {
        await using var db = new MesDbContext(new DbContextOptionsBuilder<MesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var options = Options.Create(new ShineLabTcpOptions { PayloadProfile = ShineLabPayloadProfile.DcSeqSingleChannel });
        var repository = new ShineLabTaskRepository(db, options);
        await repository.CreateOrGetAsync(new(Equipment, "task-test",
            [new("6", "synthetic", "0", 1, "1", "")]), default);
        var body = JsonSerializer.Deserialize<JsonElement>("""{"task_uuid":"task-test","sampleId":"6","testDate":"t1","compounds":[]}""");
        var received = DateTimeOffset.Parse("2026-09-23T01:50:26.951Z");
        foreach (var id in new[] { "first", "second" })
        {
            var raw = JsonSerializer.Serialize(new { strID = id, strMethod = "Result", equipmentCode = Equipment, body });
            await repository.ApplyPushAsync("Result", Equipment, body, default, id,
                new ShineLabPushContext(raw, "test-connection", "LineJson", received));
        }
        var evidence = await repository.GetPushEvidenceAsync(Equipment, 100, default);
        Assert.Equal(2, evidence.Count);
        Assert.All(evidence, item =>
        {
            Assert.Equal(received.UtcTicks, item.ReceivedAtUtcTicks);
            Assert.Equal("test-connection", item.ConnectionId);
            Assert.Equal("ExplicitTaskId", item.Correlation);
            Assert.Equal("task-test", item.TaskUuid);
        });
        Assert.Single(evidence.Where(item => item.IsDuplicate));
        Assert.Single((await repository.GetEventsAsync("task-test", default))
            .Where(item => item.EventType == "ResultObserved"));
        var task = (await repository.GetAsync("task-test", default))!;
        Assert.Equal(body.GetRawText(), task.ResultJson);
        Assert.Null(task.CompletedAtUtc);
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var client = new TcpClient();
            try { await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token); return client; }
            catch (SocketException) { client.Dispose(); await Task.Delay(20, timeout.Token); }
            catch { client.Dispose(); throw; }
        }
    }

    private sealed class RecordingLogger : ILogger<ShineLabTcpServer>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
}
