extern alias AdapterApp;

using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AdapterProgram = AdapterApp::Program;
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
            Assert.Equal("MovingToPickup", created.Status);
            Assert.Equal(["CHARGE_01", "PICK_01", "SAMPLE_01"], created.ActivePath);

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
}

internal sealed class VendorTcpAdapterFactory : WebApplicationFactory<AdapterProgram>
{
    private readonly FakeVendorTcpController _controller;
    private readonly ProfileConfiguration _profile;
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"adapter-vendor-tcp-{Guid.NewGuid():N}.db");

    public VendorTcpAdapterFactory(FakeVendorTcpController controller, ProfileConfiguration profile)
    {
        _controller = controller;
        _profile = profile;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.Sources.Clear();
            var settings = new
            {
                Profile = _profile,
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
                        AcquireControl = true,
                        EnablePush = false,
                        MinimumConfidence = 0.0,
                        RequestTimeoutMs = 1000,
                        ConnectTimeoutMs = 1000
                    }
                }
            };
            configuration.AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(settings)));
        });
        builder.UseSetting("ConnectionStrings:Adapter", $"Data Source={_databasePath}");
        builder.UseSetting("Agv:Driver", "vendor-tcp");
        builder.UseSetting("Agv:Tcp:Host", "127.0.0.1");
        builder.UseSetting("Agv:Tcp:StatusPort", _controller.StatusPort.ToString());
        builder.UseSetting("Agv:Tcp:CommandPort", _controller.CommandPort.ToString());
        builder.UseSetting("Agv:Tcp:ControlPort", _controller.ControlPort.ToString());
        builder.UseSetting("Agv:Tcp:PushPort", _controller.PushPort.ToString());
        builder.UseSetting("Agv:Tcp:NickName", "MesControlAgv.Adapter");
        builder.UseSetting("Agv:Tcp:AcquireControl", "true");
        builder.UseSetting("Agv:Tcp:EnablePush", "false");
        builder.UseSetting("Agv:Tcp:MinimumConfidence", "0");
        builder.UseSetting("Agv:Tcp:RequestTimeoutMs", "1000");
        builder.UseSetting("Agv:Tcp:ConnectTimeoutMs", "1000");
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
            1101 => Json("{\"ret_code\":0,\"reloc_status\":1,\"confidence\":1.0,\"emergency\":false,\"blocked\":false,\"fatals\":[],\"errors\":[],\"fork_auto_flag\":true}"),
            1110 => TaskStatusResponse(packet),
            _ => throw new InvalidOperationException($"Unexpected status API {packet.ApiId}.")
        });
    }

    private Task<byte[]> HandleControlAsync(AdapterPacket packet)
    {
        RecordApi(packet.ApiId);
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
