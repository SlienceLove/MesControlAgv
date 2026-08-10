using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class MesClientHttpContractTests
{
    [Fact]
    public async Task Get_stations_maps_collection_and_preserves_enabled_flag()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new StationResponse[] {
            new StationResponse(2, "Sample", "SAMPLE_CUSTOM", true),
            new StationResponse(9, "Disabled", "DISABLED_CUSTOM", false) }));
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var stations = await client.GetStationsAsync(CancellationToken.None);

        Assert.Collection(
            stations,
            station =>
            {
                Assert.Equal(2, station.Code);
                Assert.Equal("Sample", station.Name);
                Assert.Equal("SAMPLE_CUSTOM", station.AgvStationId);
                Assert.True(station.Enabled);
            },
            station =>
            {
                Assert.Equal(9, station.Code);
                Assert.Equal("Disabled", station.Name);
                Assert.Equal("DISABLED_CUSTOM", station.AgvStationId);
                Assert.False(station.Enabled);
            });

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/stations", request.Uri.AbsolutePath);
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task Readiness_clients_map_runtime_and_physical_evidence()
    {
        var runtime = new RuntimeReadinessResponse(
            "CUSTOM",
            "Custom MES",
            "2.0",
            UseSimulator: false,
            AutomaticDispatchEnabled: true,
            TaskCancellationEnabled: true,
            ProfileFingerprint: new string('a', 64),
            MapFingerprint: new string('b', 64),
            StationIds: ["FROM", "TO"],
            DirectedEdges: [new DirectedMapEdgeResponse("FROM", "TO", 2.5)],
            ExpectedPhysicalMapName: "plant-map",
            ExpectedPhysicalMapVersion: "v7",
            ExpectedPhysicalMapMd5: "map-md5");
        var safety = new AgvSafetyReadinessResponse(
            "automatic",
            "controller",
            "plant-map",
            "map-md5",
            true,
            1,
            false,
            true,
            false,
            false,
            0,
            0,
            1,
            0.99,
            DateTimeOffset.UtcNow);
        var preflight = new PhysicalAgvPreflightResponse(
            new AgvSnapshotResponse(true, "adapter", "FROM", null, SafetyReadiness: safety),
            safety,
            true,
            []);
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/runtime/readiness" => JsonResponse(runtime),
            "/api/physical/preflight" => JsonResponse(preflight),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var actualRuntime = await client.GetRuntimeReadinessAsync(CancellationToken.None);
        var actualPreflight = await client.GetPhysicalPreflightAsync(CancellationToken.None);

        Assert.NotNull(actualRuntime);
        Assert.Equal(runtime.ProductId, actualRuntime.ProductId);
        Assert.Equal(runtime.ProfileFingerprint, actualRuntime.ProfileFingerprint);
        Assert.Equal(runtime.MapFingerprint, actualRuntime.MapFingerprint);
        Assert.Equal(runtime.StationIds, actualRuntime.StationIds);
        Assert.Equal(runtime.DirectedEdges, actualRuntime.DirectedEdges);
        Assert.NotNull(actualPreflight);
        Assert.True(actualPreflight.DispatchPermitted);
        Assert.Equal("adapter", actualPreflight.Snapshot.ControlOwner);
        Assert.Equal("plant-map", actualPreflight.Readiness?.MapName);
        Assert.Empty(actualPreflight.BlockingReasons);
        Assert.Equal(
            ["/api/runtime/readiness", "/api/physical/preflight"],
            handler.Requests.Select(request => request.Uri.AbsolutePath).ToArray());
    }

    [Fact]
    public async Task Plan_path_posts_station_ids_and_blocked_collection()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            new PlannedPathResponse(
                ["SAMPLE_CUSTOM", "PREP_CUSTOM", "DROP_CUSTOM"],
                12.5,
                "SAMPLE_CUSTOM",
                "DROP_CUSTOM")));
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var path = await client.PlanPathAsync(
            "SAMPLE_CUSTOM",
            "DROP_CUSTOM",
            ["BLOCKED_CUSTOM"],
            CancellationToken.None);

        Assert.Equal(new[] { "SAMPLE_CUSTOM", "PREP_CUSTOM", "DROP_CUSTOM" }, path.Stations);
        Assert.Equal(12.5, path.Cost);
        Assert.Equal("SAMPLE_CUSTOM", path.SourceStationId);
        Assert.Equal("DROP_CUSTOM", path.TargetStationId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/planning/path", request.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("SAMPLE_CUSTOM", root.GetProperty("fromStationId").GetString());
        Assert.Equal("DROP_CUSTOM", root.GetProperty("toStationId").GetString());
        Assert.Equal(
            new[] { "BLOCKED_CUSTOM" },
            root.GetProperty("blockedStations").EnumerateArray().Select(item => item.GetString()).ToArray());
    }

    [Fact]
    public async Task Get_tasks_uses_date_query_and_maps_assignment_fields()
    {
        var taskId = Guid.NewGuid();
        var createdAt = new DateTime(2026, 8, 7, 1, 2, 3, DateTimeKind.Utc);
        var endedAt = createdAt.AddMinutes(8);
        var handler = new RecordingHandler(_ => JsonResponse(new TaskResponse[] {
            new TaskResponse(
                taskId,
                2,
                9,
                "Completed",
                1,
                null,
                Priority: 7,
                Description: "custom transport",
                ExternalId: "external-42",
                CreatedAt: createdAt,
                EndedAt: endedAt,
                ActiveAgvId: "AGV-02",
                ActiveDeviceTaskId: "device-42",
                ActivePath: ["SAMPLE_CUSTOM", "DROP_CUSTOM"]) }));
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var tasks = await client.GetTasksAsync(new DateOnly(2026, 8, 7), CancellationToken.None);

        var task = Assert.Single(tasks);
        Assert.Equal(taskId, task.Id);
        Assert.Equal(2, task.SourceStationCode);
        Assert.Equal(9, task.TargetStationCode);
        Assert.Equal("Completed", task.Status);
        Assert.Equal(1, task.RetryCount);
        Assert.Equal(7, task.Priority);
        Assert.Equal("custom transport", task.Description);
        Assert.Equal("external-42", task.ExternalId);
        Assert.Equal(createdAt, task.CreatedAt);
        Assert.Equal(endedAt, task.EndedAt);
        Assert.Equal("AGV-02", task.ActiveAgvId);
        Assert.Equal("device-42", task.ActiveDeviceTaskId);
        Assert.Equal(new[] { "SAMPLE_CUSTOM", "DROP_CUSTOM" }, task.ActivePath);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/tasks", request.Uri.AbsolutePath);
        Assert.Equal("date=2026-08-07", request.Uri.Query.TrimStart('?'));
    }

    [Fact]
    public async Task Create_and_dispatch_post_configured_task_and_map_task_responses()
    {
        var taskId = Guid.NewGuid();
        var created = new TaskResponse(
            taskId,
            2,
            9,
            "Created",
            0,
            null,
            Priority: 3,
            Description: "configured task",
            ExternalId: "external-99");
        var dispatched = created with
        {
            Status = "MovingToPickup",
            ActiveAgvId = "AGV-02",
            ActiveDeviceTaskId = "device-99",
            ActivePath = ["SAMPLE_CUSTOM", "PREP_CUSTOM", "DROP_CUSTOM"]
        };
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/tasks")
            {
                return JsonResponse(created, HttpStatusCode.Created);
            }

            if (request.RequestUri.AbsolutePath == $"/api/tasks/{taskId}/dispatch")
            {
                return JsonResponse(dispatched);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var createdTask = await client.CreateTaskAsync(
            2,
            9,
            3,
            "configured task",
            "external-99",
            CancellationToken.None);
        var dispatchedTask = await client.DispatchTaskAsync(taskId, CancellationToken.None);

        Assert.Equal(created.Id, createdTask.Id);
        Assert.Equal("Created", createdTask.Status);
        Assert.Equal("external-99", createdTask.ExternalId);
        Assert.Equal("MovingToPickup", dispatchedTask.Status);
        Assert.Equal("AGV-02", dispatchedTask.ActiveAgvId);
        Assert.Equal(new[] { "SAMPLE_CUSTOM", "PREP_CUSTOM", "DROP_CUSTOM" }, dispatchedTask.ActivePath);

        Assert.Equal(2, handler.Requests.Count);
        var createRequest = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, createRequest.Method);
        Assert.Equal("/api/tasks", createRequest.Uri.AbsolutePath);
        using (var createBody = JsonDocument.Parse(createRequest.Body!))
        {
            var root = createBody.RootElement;
            Assert.Equal(2, root.GetProperty("sourceStationCode").GetInt32());
            Assert.Equal(9, root.GetProperty("targetStationCode").GetInt32());
            Assert.Equal(3, root.GetProperty("priority").GetInt32());
            Assert.Equal("configured task", root.GetProperty("description").GetString());
            Assert.Equal("external-99", root.GetProperty("externalId").GetString());
        }

        var dispatchRequest = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, dispatchRequest.Method);
        Assert.Equal($"/api/tasks/{taskId}/dispatch", dispatchRequest.Uri.AbsolutePath);
        Assert.Equal("null", dispatchRequest.Body);
    }

    [Fact]
    public async Task Get_fleet_status_maps_nested_snapshot_active_task_and_capabilities()
    {
        var transportTaskId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var handler = new RecordingHandler(_ => JsonResponse(new AgvFleetStatusResponse[] {
            new AgvFleetStatusResponse(
                new AgvSnapshotResponse(
                    true,
                    "adapter",
                    "SAMPLE_CUSTOM",
                    transportTaskId,
                    "AGV-02",
                    new AgvCapabilitiesResponse(
                        SupportsPause: false,
                        SupportsResume: true,
                        SupportsCancel: true,
                        SupportsEmergencyStop: true,
                        SupportsLift: true,
                        SupportsBarcode: false,
                        SupportsStationConfirmation: true)),
                new AgvActiveTaskStatusResponse(
                    transportTaskId,
                    operationId,
                    "MovingToTarget",
                    "device-42",
                    "moving",
                    "DROP_CUSTOM",
                    "last warning",
                    ["SAMPLE_CUSTOM", "DROP_CUSTOM"])),
            new AgvFleetStatusResponse(
                new AgvSnapshotResponse(false, "none", null, null, "AGV-03"),
                null) }));
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var statuses = await client.GetAgvFleetStatusAsync(CancellationToken.None);

        Assert.Equal(2, statuses.Count);
        var active = statuses[0];
        Assert.Equal("AGV-02", active.Snapshot.AgvId);
        Assert.True(active.Snapshot.Online);
        Assert.Equal("adapter", active.Snapshot.ControlOwner);
        Assert.Equal("SAMPLE_CUSTOM", active.Snapshot.CurrentStationId);
        Assert.Equal(transportTaskId, active.Snapshot.CurrentTaskId);
        Assert.NotNull(active.Snapshot.Capabilities);
        Assert.False(active.Snapshot.Capabilities!.SupportsPause);
        Assert.True(active.Snapshot.Capabilities.SupportsEmergencyStop);
        Assert.NotNull(active.ActiveTask);
        Assert.Equal(operationId, active.ActiveTask!.OperationId);
        Assert.Equal(transportTaskId, active.ActiveTask.TransportTaskId);
        Assert.Equal("MovingToTarget", active.ActiveTask.MesStatus);
        Assert.Equal("device-42", active.ActiveTask.DeviceTaskId);
        Assert.Equal("moving", active.ActiveTask.DeviceState);
        Assert.Equal("DROP_CUSTOM", active.ActiveTask.TargetStationId);
        Assert.Equal("last warning", active.ActiveTask.LastError);
        Assert.Equal(new[] { "SAMPLE_CUSTOM", "DROP_CUSTOM" }, active.ActiveTask.Path);

        var offline = statuses[1];
        Assert.Equal("AGV-03", offline.Snapshot.AgvId);
        Assert.False(offline.Snapshot.Online);
        Assert.Null(offline.ActiveTask);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/agvs/fleet/status", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task Execute_agv_command_posts_command_and_maps_device_response()
    {
        var taskId = Guid.NewGuid();
        var handler = new RecordingHandler(_ => JsonResponse(new AgvTaskResponse(
            taskId,
            "device-42",
            "DROP_CUSTOM",
            "paused",
            "operator pause",
            "AGV-02",
            ["SAMPLE_CUSTOM", "DROP_CUSTOM"])));
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var result = await client.ExecuteAgvCommandAsync(
            "AGV-02",
            "pause",
            taskId,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(taskId, result!.TaskId);
        Assert.Equal("device-42", result.DeviceTaskId);
        Assert.Equal("DROP_CUSTOM", result.TargetStationId);
        Assert.Equal("paused", result.State);
        Assert.Equal("operator pause", result.LastError);
        Assert.Equal("AGV-02", result.AgvId);
        Assert.Equal(new[] { "SAMPLE_CUSTOM", "DROP_CUSTOM" }, result.Path);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/agvs/AGV-02/command", request.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("pause", body.RootElement.GetProperty("command").GetString());
        Assert.Equal(taskId, body.RootElement.GetProperty("taskId").GetGuid());
    }

    [Fact]
    public async Task Get_task_detail_maps_task_and_event_collection()
    {
        var taskId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var createdAt = new DateTime(2026, 8, 7, 3, 4, 5, DateTimeKind.Utc);
        var handler = new RecordingHandler(_ => JsonResponse(new TaskDetailResponse(
            new TaskResponse(taskId, 2, 9, "WaitingPickupConfirmation", 0, null, CreatedAt: createdAt),
            [new TaskEventResponse(eventId, "PickupArrived", "{\"station\":\"SAMPLE_CUSTOM\"}", createdAt)])));
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var detail = await client.GetTaskDetailAsync(taskId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(taskId, detail!.Task.Id);
        Assert.Equal("WaitingPickupConfirmation", detail.Task.Status);
        var item = Assert.Single(detail.Events);
        Assert.Equal(eventId, item.Id);
        Assert.Equal("PickupArrived", item.EventType);
        Assert.Equal("{\"station\":\"SAMPLE_CUSTOM\"}", item.Payload);
        Assert.Equal(createdAt, item.CreatedAt);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/api/tasks/{taskId}", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task Not_found_task_detail_is_mapped_to_null()
    {
        var taskId = Guid.NewGuid();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var detail = await client.GetTaskDetailAsync(taskId, CancellationToken.None);

        Assert.Null(detail);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/api/tasks/{taskId}", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task Dispatch_conflict_exposes_http_status_and_json_detail()
    {
        var taskId = Guid.NewGuid();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new { detail = "task cannot be dispatched" })
        });
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var exception = await Assert.ThrowsAsync<MesApiException>(
            () => client.DispatchTaskAsync(taskId, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal("task cannot be dispatched", exception.Detail);
        Assert.Contains("task cannot be dispatched", exception.Message, StringComparison.Ordinal);
        Assert.Equal($"/api/tasks/{taskId}/dispatch", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "Task was not found.")]
    [InlineData(HttpStatusCode.Conflict, "Device task cannot be reconciled.")]
    public async Task Recover_failure_exposes_http_status_and_json_detail(
        HttpStatusCode statusCode,
        string detail)
    {
        var taskId = Guid.NewGuid();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = JsonContent.Create(new { detail })
        });
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var exception = await Assert.ThrowsAsync<MesApiException>(
            () => client.RecoverAsync(taskId, CancellationToken.None));

        Assert.Equal(statusCode, exception.ResponseStatusCode);
        Assert.Equal(detail, exception.Detail);
        Assert.Contains(detail, exception.Message, StringComparison.Ordinal);
        Assert.Equal($"/api/tasks/{taskId}/recover", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task Agv_command_conflict_exposes_json_detail()
    {
        var operationId = Guid.NewGuid();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new { detail = "Cancel the MES transport task instead." })
        });
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var exception = await Assert.ThrowsAsync<MesApiException>(() => client.ExecuteAgvCommandAsync(
            "AGV-01",
            "cancel",
            operationId,
            CancellationToken.None));

        Assert.Equal(HttpStatusCode.Conflict, exception.ResponseStatusCode);
        Assert.Equal("Cancel the MES transport task instead.", exception.Detail);
        Assert.Contains("Cancel the MES transport task instead.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stations_service_unavailable_exposes_http_status_code()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetStationsAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
    }

    private static HttpClient CreateClient(RecordingHandler handler) => new(handler)
    {
        BaseAddress = new Uri("http://mes.local/")
    };

    private static HttpResponseMessage JsonResponse(object value, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = JsonContent.Create(value) };

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, body));
            return responseFactory(request);
        }
    }
}
