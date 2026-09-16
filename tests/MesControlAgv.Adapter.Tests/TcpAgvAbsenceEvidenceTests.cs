using System.Text;
using System.Text.Json;
using MesControlAgv.Adapter.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Adapter.Tests;

public sealed class TcpAgvAbsenceEvidenceTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("completed")]
    [InlineData("malformed")]
    [InlineData("active")]
    [InlineData("active-scalar")]
    [InlineData("active-id")]
    [InlineData("active-id-package")]
    [InlineData("active-task-id")]
    [InlineData("wrong-station")]
    [InlineData("destination-valid")]
    [InlineData("destination-empty")]
    [InlineData("destination-missing")]
    [InlineData("destination-duplicate")]
    [InlineData("destination-foreign")]
    [InlineData("destination-completed")]
    [InlineData("destination-malformed")]
    [InlineData("destination-active")]
    [InlineData("destination-active-scalar")]
    [InlineData("destination-active-id")]
    [InlineData("destination-active-id-package")]
    [InlineData("destination-active-task-id")]
    [InlineData("destination-wrong-station")]
    [InlineData("scalar-zero")]
    [InlineData("scalar-unknown")]
    [InlineData("scalar-404")]
    [InlineData("scalar-null")]
    [InlineData("scalar-malformed")]
    [InlineData("missing-scalar")]
    [InlineData("unfiltered-404")]
    [InlineData("list-only-terminal")]
    [InlineData("destination-scalar-zero")]
    [InlineData("destination-scalar-unknown")]
    [InlineData("destination-scalar-404")]
    [InlineData("destination-scalar-null")]
    [InlineData("destination-scalar-malformed")]
    [InlineData("destination-missing-scalar")]
    [InlineData("destination-unfiltered-404")]
    [InlineData("destination-list-only-terminal")]
    public async Task Only_explicit_absence_of_every_segment_plus_fresh_idle_at_required_station_is_accepted(string shape)
    {
        var atDestination = shape.StartsWith("destination-", StringComparison.Ordinal);
        if (atDestination) shape = shape["destination-".Length..];
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taskId = Guid.NewGuid();
        string[]? queriedIds = null;
        await using var server = new TcpApiTestServer(shape is "empty" or "missing" or "duplicate" or "foreign" or "completed" or "malformed" ? 1 : 2,
            packet =>
            {
                Assert.Equal((ushort)1110, packet.ApiId);
                if (packet.Payload.Length == 0)
                {
                    var reply = new Dictionary<string, object?>
                    {
                        ["current_station"] = shape == "wrong-station" ? "LM2" : atDestination ? "LM1" : "LM7",
                        ["task_status"] = shape switch { "active-scalar" => 2, "scalar-zero" => 0,
                            "scalar-unknown" => 7, "scalar-404" => 404, "scalar-null" => null,
                            "scalar-malformed" => "invalid", _ => 4 },
                        ["current_task_id"] = shape == "active-id" ? "opaque-active-task" : null,
                        ["task_id"] = shape == "active-task-id" ? "opaque-active-task" : null,
                        ["task_status_package"] = shape == "active-id-package" ? new { current_task_id = "opaque-active-task", task_status_list = Array.Empty<object>() } : null,
                        ["task_status_list"] = shape is "active" or "list-only-terminal" or "unfiltered-404"
                            ? new[] { new { task_id = "opaque-task", status = shape == "active" ? 2 : shape == "unfiltered-404" ? 404 : 4 } } : []
                    };
                    if (shape is "missing-scalar" or "list-only-terminal") reply.Remove("task_status");
                    return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(reply));
                }
                using var payload = JsonDocument.Parse(packet.Payload);
                queriedIds = payload.RootElement.GetProperty("task_ids").EnumerateArray().Select(item => item.GetString()!).ToArray();
                Assert.Equal(2, queriedIds.Length);
                Assert.Equal(taskId.ToString("N"), queriedIds[0]);
                if (shape == "missing") return Task.FromResult(Encoding.UTF8.GetBytes("{}"));
                if (shape == "malformed") return Task.FromResult(Encoding.UTF8.GetBytes("{\"task_status_list\":[{}]}"));
                var ids = shape == "empty" ? [] : queriedIds.ToArray();
                if (shape == "duplicate") ids[1] = ids[0];
                if (shape == "foreign") ids[1] = Guid.NewGuid().ToString("N");
                return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    task_status_list = ids.Select(id => new { task_id = id, status = shape == "completed" ? 4 : 404 }).ToArray()
                }));
            });
        using var client = new TcpAgvClient(Options.Create(new TcpAgvOptions
        {
            Host = "127.0.0.1", StatusPort = server.Port, EnablePush = false, RequestTimeoutMs = 1000
        }), NullLogger<TcpAgvClient>.Instance);
        Task<MesControlAgv.Contracts.AgvTaskAbsenceEvidence> ReadAsync() => atDestination
            ? client.ReadTaskAbsenceAtDestinationAsync(taskId, ["LM7", "LM6", "LM1"], cancellation.Token)
            : client.ReadTaskAbsenceAsync(taskId, ["LM7", "LM6", "LM1"], cancellation.Token);
        if (shape is "valid" or "list-only-terminal")
        {
            var evidence = await ReadAsync();
            Assert.Equal(queriedIds, evidence.Segments.Select(item => item.DeviceTaskId));
            Assert.Equal(atDestination ? "LM1" : "LM7", evidence.CurrentStationId);
        }
        else
            await Assert.ThrowsAsync<InvalidOperationException>(ReadAsync);
        await server.Completion;
        Assert.All(server.ApiIds, id => Assert.Equal((ushort)1110, id));
    }
}
