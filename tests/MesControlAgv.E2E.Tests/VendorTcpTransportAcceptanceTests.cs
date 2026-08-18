extern alias AdapterApp;

using System.Net.Http.Json;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AdapterProgram = AdapterApp::Program;
using AdapterRunMode = AdapterApp::MesControlAgv.Adapter.AdapterRunMode;
using AdapterProtocol = AdapterApp::MesControlAgv.Adapter.Services.AgvTcpProtocol;
using AdapterPacket = AdapterApp::MesControlAgv.Adapter.Services.AgvTcpPacket;
using MesControlAgv.Contracts;
using MesControlAgv.Domain;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.E2E.Tests;

public sealed class VendorTcpTransportAcceptanceTests
{
    [Fact]
    public async Task Read_only_preflight_allows_reads_and_rejects_every_http_write()
    {
        await using var controller = new FakeVendorTcpController();
        using var adapterFactory = new VendorTcpAdapterFactory(
            controller,
            CreatePhysicalAcceptanceProfile(),
            AdapterRunMode.ReadOnlyPreflightValue,
            acquireControl: false,
            enablePush: false,
            minimumConfidence: 0.98);
        using var client = adapterFactory.CreateClient();

        var health = await client.GetFromJsonAsync<JsonElement>("health");
        Assert.Equal(AdapterRunMode.ReadOnlyPreflightValue, health.GetProperty("runMode").GetString());
        var modules = health.GetProperty("modules").EnumerateArray().ToArray();
        Assert.Equal(2, modules.Length);
        var module = Assert.Single(modules.Where(item => item.GetProperty("moduleId").GetString() == "agv"));
        Assert.Equal("agv", module.GetProperty("moduleId").GetString());
        Assert.Equal("agv", module.GetProperty("deviceType").GetString());
        Assert.Equal(
            ["Simulator", "Tcp"],
            module.GetProperty("supportedTransports")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray());
        var workstationModule = Assert.Single(modules.Where(item =>
            item.GetProperty("moduleId").GetString() == "sample-workstation"));
        Assert.Equal(
            ["Http"],
            workstationModule.GetProperty("supportedTransports")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray());
        var workstation = Assert.Single(health.GetProperty("devices").EnumerateArray().Where(item =>
            item.GetProperty("deviceId").GetString() == "SAMPLE-WORKSTATION-01"));
        Assert.False(workstation.GetProperty("enabled").GetBoolean());
        Assert.False(workstation.GetProperty("controlEnabled").GetBoolean());

        using var workstationStatus = await client.GetAsync(
            "api/workstations/SAMPLE-WORKSTATION-01/status");
        using var workstationWrite = await client.PostAsync(
            "api/workstations/SAMPLE-WORKSTATION-01/status",
            content: null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, workstationStatus.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, workstationWrite.StatusCode);

        using var preflight = await client.GetAsync("physical/preflight");
        Assert.Equal(HttpStatusCode.OK, preflight.StatusCode);
        var result = await preflight.Content.ReadFromJsonAsync<PhysicalAgvPreflightResponse>();
        Assert.NotNull(result);
        Assert.False(result.DispatchPermitted);
        Assert.NotNull(result.MapEvidence);
        Assert.True(result.MapEvidence.IsControllerAuthoritative);
        Assert.DoesNotContain("controller_map_evidence_unavailable", result.BlockingReasons);

        using var dispatch = await client.PostAsJsonAsync(
            $"tasks/{Guid.NewGuid():D}/dispatch",
            new { targetStationId = "LM2", sourceStationId = "LM1" });
        using var command = await client.PostAsJsonAsync(
            "agvs/AGV-01/command",
            new { command = "pause" });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, dispatch.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, command.StatusCode);

        Assert.Equal([1060, 1110, 1101, 1021, 1000, 1300, 1301, 1302, 4011], controller.ApiIds);
        Assert.DoesNotContain((ushort)4005, controller.ApiIds);
        Assert.DoesNotContain((ushort)9300, controller.ApiIds);
        Assert.DoesNotContain((ushort)3066, controller.ApiIds);
        Assert.DoesNotContain((ushort)3067, controller.ApiIds);
    }

    [Fact]
    public async Task Mes_dispatches_through_adapter_and_vendor_tcp_until_both_operator_confirmations()
    {
        await using var controller = new FakeVendorTcpController();
        var profile = CreateProfile();
        using var adapterFactory = new VendorTcpAdapterFactory(controller, profile);
        using var adapterHttpClient = adapterFactory.CreateClient();
        var configuredAdapterProfile = adapterFactory.Services.GetRequiredService<ProfileConfiguration>();
        Assert.Contains("SAMPLE_01", configuredAdapterProfile.Map.StationIds);
        var gateway = new AdapterClient(adapterHttpClient);

        var databasePath = Path.Combine(Path.GetTempPath(), $"mes-vendor-tcp-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<MesDbContext>()
                .UseInMemoryDatabase(databasePath)
                .Options;
            await using (var database = new MesDbContext(options))
            {
                await database.Database.EnsureCreatedAsync();
                var service = new TaskService(
                    new TaskRepository(database),
                    gateway,
                    profile,
                    new PathPlanner(AgvMap.FromProfile(profile.Map)));

            var created = await service.CreateAsync(new CreateTaskRequest(2, 4), CancellationToken.None);
            Assert.Equal("Created", created.Status);
            var dispatched = await service.DispatchAsync(created.Id, CancellationToken.None);
            Assert.Equal("MovingToPickup", dispatched.Status);
            Assert.Equal(["CHARGE_01", "PICK_01", "SAMPLE_01"], dispatched.ActivePath);

            await service.ReconcileActiveAsync(CancellationToken.None);
            var waitingForPickup = await service.GetDetailAsync(created.Id, CancellationToken.None);
            Assert.Equal("WaitingPickupConfirmation", waitingForPickup!.Task.Status);

            var movingToDropoff = await service.ConfirmPickupAsync(created.Id, "operator-a", CancellationToken.None);
            Assert.Equal("MovingToDropoff", movingToDropoff.Status);
            Assert.Equal(["SAMPLE_01", "ST_PREP_01"], movingToDropoff.ActivePath);

            await service.ReconcileActiveAsync(CancellationToken.None);
            var waitingForDropoff = await service.GetDetailAsync(created.Id, CancellationToken.None);
            Assert.Equal("WaitingDropoffConfirmation", waitingForDropoff!.Task.Status);

            var completed = await service.ConfirmDropoffAsync(created.Id, "operator-a", CancellationToken.None);
            Assert.Equal("Completed", completed.Status);

            var eventTypes = completed is not null
                ? (await service.GetDetailAsync(created.Id, CancellationToken.None))!.Events.Select(item => item.EventType).ToArray()
                : [];
            Assert.Contains("PickupConfirmed", eventTypes);
            Assert.Contains("DropoffConfirmed", eventTypes);
            Assert.Equal(2, controller.NavigateRequests.Count);
            Assert.Equal(
                [("CHARGE_01", "PICK_01"), ("PICK_01", "SAMPLE_01")],
                controller.NavigateRequests[0].Select(item => (item.Source, item.Target)).ToArray());
            Assert.Equal(
                [("SAMPLE_01", "ST_PREP_01")],
                controller.NavigateRequests[1].Select(item => (item.Source, item.Target)).ToArray());
            Assert.Contains(controller.ApiIds, apiId => apiId == 3066);
            Assert.Contains(controller.ApiIds, apiId => apiId == 1110);
            Assert.Contains(controller.ApiIds, apiId => apiId == 1101);
            Assert.Contains(controller.ApiIds, apiId => apiId == 4005);
                Assert.Equal("adapter", controller.ControlOwner);
            }
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static ProfileConfiguration CreateProfile() => ProfileConfiguration.Default;

    private static ProfileConfiguration CreatePhysicalAcceptanceProfile()
    {
        // WebApplicationFactory overlays indexed configuration keys onto the
        // default JSON profile. Keep every indexed collection the same length
        // so no simulator stations or edges survive the overlay.
        var defaults = ProfileConfiguration.Default;
        var stationIds = Enumerable.Range(1, defaults.Stations.Count)
            .Select(index => $"LM{index}")
            .ToArray();
        var stations = defaults.Stations
            .Select((station, index) => station with
            {
                Code = index + 1,
                StationId = stationIds[index],
                AgvStationId = stationIds[index],
                Name = stationIds[index],
                Type = "PhysicalAcceptance"
            })
            .ToArray();
        var edges = defaults.Map.Edges
            .Select((_, index) => new MapEdgeProfile
            {
                From = stationIds[index],
                To = stationIds[(index + 1) % stationIds.Length],
                Cost = 1,
                Bidirectional = false
            })
            .ToArray();

        return defaults with
        {
            Product = new ProductProfile { ProductId = "MES-AGV", DisplayName = "Physical acceptance", Version = "1.0" },
            Agvs =
            [
                defaults.Agvs[0] with
                {
                    Model = "Vendor-AMR",
                    Driver = "vendor-tcp",
                    Endpoint = "tcp://127.0.0.1",
                    MaxSpeedMetersPerSecond = 0.3,
                    HomeStationId = stationIds[0]
                }
            ],
            Stations = stations,
            Map = new MapProfile { StationIds = stationIds, Edges = edges },
            PhysicalAcceptance = new PhysicalAcceptanceProfile
            {
                ExpectedControlOwner = "MesControlAgv.Adapter",
                MapSnapshot = new ControllerMapSnapshot
                {
                    MapName = "acceptance-map",
                    Version = "1.0",
                    Md5 = "e1b8d6b2b24362c1d44f1884c0abd8fb",
                    CapturedAtUtc = new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero),
                    StationIds = stationIds,
                    DirectedEdges = edges
                        .Select(edge => new DirectedMapEdgeProfile { From = edge.From, To = edge.To })
                        .ToArray()
                },
                Safety = new PhysicalAgvSafetyProfile
                {
                    MinimumLocalizationConfidence = 0.98,
                    MaximumDispatchSpeedMetersPerSecond = 0.3,
                    RequireControlOwnership = true,
                    RequireNoEmergency = true,
                    RequireNoBlocked = true,
                    RequireNoFaults = true,
                    RequireAutomaticMode = true
                }
            },
            Features = defaults.Features with
            {
                UseSimulator = false,
                EnableAutomaticDispatch = false,
                EnableFieldNavigationAcceptance = false,
                EnableTaskCancellation = false
            }
        };
    }
}

internal sealed class VendorTcpAdapterFactory : WebApplicationFactory<AdapterProgram>
{
    private readonly FakeVendorTcpController _controller;
    private readonly ProfileConfiguration _profile;
    private readonly string _runMode;
    private readonly bool _acquireControl;
    private readonly bool _enablePush;
    private readonly double _minimumConfidence;
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"adapter-vendor-tcp-{Guid.NewGuid():N}.db");

    public VendorTcpAdapterFactory(
        FakeVendorTcpController controller,
        ProfileConfiguration profile,
        string runMode = AdapterRunMode.StandardValue,
        bool acquireControl = true,
        bool enablePush = false,
        double minimumConfidence = 0.0)
    {
        _controller = controller;
        _profile = profile;
        _runMode = runMode;
        _acquireControl = acquireControl;
        _enablePush = enablePush;
        _minimumConfidence = minimumConfidence;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var settings = new
        {
            Profile = _profile,
            Adapter = new { RunMode = _runMode },
            ConnectionStrings = new { Adapter = $"Data Source={_databasePath}" },
            Agv = new
            {
                Driver = "vendor-tcp",
                Tcp = new
                {
                    Host = "127.0.0.1",
                    StatusPort = _controller.StatusPort,
                    CommandPort = _controller.CommandPort,
                    ControlPort = _controller.ControlPort,
                    PushPort = _controller.PushPort,
                    NickName = "MesControlAgv.Adapter",
                    AcquireControl = _acquireControl,
                    EnablePush = _enablePush,
                    MinimumConfidence = _minimumConfidence,
                    RequestTimeoutMs = 1000,
                    ConnectTimeoutMs = 1000
                }
            }
        };

        foreach (var setting in FlattenConfiguration(JsonSerializer.SerializeToElement(settings)))
        {
            builder.UseSetting(setting.Key, setting.Value);
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> FlattenConfiguration(
        JsonElement element,
        string? prefix = null)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var key = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}:{property.Name}";
                    foreach (var setting in FlattenConfiguration(property.Value, key)) yield return setting;
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var setting in FlattenConfiguration(item, $"{prefix}:{index}")) yield return setting;
                    index++;
                }
                break;
            case JsonValueKind.String:
                yield return new KeyValuePair<string, string>(prefix!, element.GetString()!);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                yield return new KeyValuePair<string, string>(prefix!, element.GetRawText());
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(_databasePath); } catch (IOException) { }
    }
}

internal sealed class FakeVendorTcpController : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TcpApiListener _status;
    private readonly TcpApiListener _command;
    private readonly TcpApiListener _control;
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceTask> _tasks = new(StringComparer.Ordinal);
    private readonly List<ushort> _apiIds = [];
    private readonly List<IReadOnlyList<(string TaskId, string Source, string Target)>> _navigateRequests = [];
    private bool _locked;
    private string _currentStation = "CHARGE_01";

    public FakeVendorTcpController()
    {
        _status = new TcpApiListener(HandleStatusAsync);
        _command = new TcpApiListener(HandleCommandAsync);
        _control = new TcpApiListener(HandleControlAsync);
    }

    public int StatusPort => _status.Port;
    public int CommandPort => _command.Port;
    public int ControlPort => _control.Port;
    public int PushPort { get; } = 0;
    public string ControlOwner => _locked ? "adapter" : "none";
    public IReadOnlyList<ushort> ApiIds => _apiIds;
    public IReadOnlyList<IReadOnlyList<(string TaskId, string Source, string Target)>> NavigateRequests => _navigateRequests;

    private Task<byte[]> HandleStatusAsync(AdapterPacket packet)
    {
        RecordApi(packet.ApiId);
        return Task.FromResult(packet.ApiId switch
        {
            1060 => Json($"{{\"ret_code\":0,\"locked\":{(_locked ? "true" : "false")},\"nick_name\":{JsonSerializer.Serialize(_locked ? "MesControlAgv.Adapter" : null)}}}"),
            1101 => Json("{\"ret_code\":0,\"mode\":1,\"reloc_status\":1,\"confidence\":1.0,\"emergency\":false,\"blocked\":false,\"fatals\":[],\"errors\":[],\"fork_auto_flag\":true}"),
            1021 => Json("{\"ret_code\":0,\"reloc_status\":1}"),
            1000 => Json("{\"ret_code\":0,\"model\":\"Vendor-AMR\",\"version\":\"test-controller\"}"),
            1110 => TaskStatusResponse(packet),
            1300 => Json("{\"ret_code\":0,\"current_map\":\"acceptance-map\",\"maps\":[\"acceptance-map\"]}"),
            1301 => MapStationCatalogResponse(),
            1302 => Json("{\"ret_code\":0,\"map_info\":[{\"name\":\"acceptance-map.smap\",\"md5\":\"e1b8d6b2b24362c1d44f1884c0abd8fb\"}]}"),
            _ => throw new InvalidOperationException($"Unexpected status API {packet.ApiId}.")
        });
    }

    private Task<byte[]> HandleControlAsync(AdapterPacket packet)
    {
        RecordApi(packet.ApiId);
        if (packet.ApiId == 4011)
        {
            using var request = JsonDocument.Parse(packet.Payload);
            if (request.RootElement.GetProperty("map_name").GetString() != "acceptance-map")
            {
                throw new InvalidOperationException("Unexpected map download request.");
            }
            return Task.FromResult(MapDownloadResponse());
        }

        if (packet.ApiId == 4005)
        {
            lock (_gate) _locked = true;
            return Task.FromResult(Json("{\"ret_code\":0}"));
        }

        throw new InvalidOperationException($"Unexpected control API {packet.ApiId}.");
    }

    private Task<byte[]> HandleCommandAsync(AdapterPacket packet)
    {
        RecordApi(packet.ApiId);
        if (packet.ApiId == 3066)
        {
            using var document = JsonDocument.Parse(packet.Payload);
            var batch = document.RootElement.GetProperty("move_task_list")
                .EnumerateArray()
                .Select(item =>
                (
                    TaskId: item.GetProperty("task_id").GetString()!,
                    Source: item.GetProperty("source_id").GetString()!,
                    Target: item.GetProperty("id").GetString()!))
                .ToArray();
            lock (_gate)
            {
                _navigateRequests.Add(batch);
                foreach (var segment in batch)
                {
                    _tasks[segment.TaskId] = new DeviceTask(segment.Target, 4);
                }
                _currentStation = batch[^1].Target;
            }
            return Task.FromResult(Json("{\"ret_code\":0}"));
        }

        if (packet.ApiId == 3067)
        {
            lock (_gate)
            {
                foreach (var task in _tasks.Keys.ToArray()) _tasks[task] = _tasks[task] with { Status = 6 };
            }
            return Task.FromResult(Json("{\"ret_code\":0}"));
        }

        throw new InvalidOperationException($"Unexpected command API {packet.ApiId}.");
    }

    private byte[] TaskStatusResponse(AdapterPacket packet)
    {
        string[]? requestedIds = null;
        if (packet.Payload.Length > 0)
        {
            using var document = JsonDocument.Parse(packet.Payload);
            if (document.RootElement.TryGetProperty("task_ids", out var taskIds))
            {
                requestedIds = taskIds.EnumerateArray().Select(item => item.GetString()!).ToArray();
            }
        }

        lock (_gate)
        {
            var statuses = (requestedIds is null ? _tasks : _tasks.Where(item => requestedIds.Contains(item.Key, StringComparer.Ordinal)))
                .Select(item => new { task_id = item.Key, status = item.Value.Status, target_name = item.Value.Target })
                .ToArray();
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                ret_code = 0,
                current_station = _currentStation,
                task_status_list = statuses
            });
        }
    }

    private void RecordApi(ushort apiId)
    {
        lock (_gate) _apiIds.Add(apiId);
    }

    private static byte[] Json(string value) => Encoding.UTF8.GetBytes(value);

    private static byte[] MapStationCatalogResponse() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        ret_code = 0,
        stations = Enumerable.Range(1, 7).Select(index => new
        {
            id = $"LM{index}",
            type = "LocationMark"
        })
    });

    private static byte[] MapDownloadResponse()
    {
        var stationIds = Enumerable.Range(1, 7).Select(index => $"LM{index}").ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            header = new
            {
                mapType = "smap",
                mapName = "acceptance-map",
                minPos = new { x = 0, y = 0 },
                maxPos = new { x = 10, y = 10 },
                resolution = 0.05,
                version = "1.0"
            },
            advancedPointList = stationIds.Select((stationId, index) => new
            {
                instanceName = stationId,
                pos = new { x = index, y = index }
            }),
            advancedCurveList = stationIds.Select((stationId, index) => new
            {
                instanceName = $"edge-{index + 1}",
                startPos = new { instanceName = stationId, pos = new { x = index, y = index } },
                endPos = new
                {
                    instanceName = stationIds[(index + 1) % stationIds.Length],
                    pos = new { x = (index + 1) % stationIds.Length, y = (index + 1) % stationIds.Length }
                },
                controlPos1 = new { x = index, y = index },
                controlPos2 = new { x = (index + 1) % stationIds.Length, y = (index + 1) % stationIds.Length },
                property = Array.Empty<object>()
            })
        });
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _status.DisposeAsync();
        await _command.DisposeAsync();
        await _control.DisposeAsync();
        _lifetime.Dispose();
    }

    private sealed record DeviceTask(string Target, int Status);
}

internal sealed class TcpApiListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<AdapterPacket, Task<byte[]>> _handler;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _acceptLoop;

    public TcpApiListener(Func<AdapterPacket, Task<byte[]>> handler)
    {
        _handler = handler;
        _listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port => ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                _ = HandleClientAsync(client);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            await using var stream = client.GetStream();
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var packet = await AdapterProtocol.ReadPacketAsync(stream, 1024 * 1024, _lifetime.Token);
                    var response = await _handler(packet);
                    var responsePacket = AdapterProtocol.CreatePacket((ushort)(packet.ApiId + 10000), response);
                    await stream.WriteAsync(responsePacket, _lifetime.Token);
                    await stream.FlushAsync(_lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (EndOfStreamException) { }
            catch (IOException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        try { await _acceptLoop; } catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }
}
