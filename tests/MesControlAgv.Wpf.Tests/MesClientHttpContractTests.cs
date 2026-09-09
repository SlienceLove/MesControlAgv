using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class MesClientHttpContractTests
{
    [Fact]
    public async Task Aubo_catalog_fresh_flag_is_forwarded_to_mes_api()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new AuboArmProgramCatalogResponse(
            "ARM-01", true, "取料盘", ["取料盘"], ["取料盘"], true, [], DateTimeOffset.UtcNow)));
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var result = await client.GetAuboArmProgramCatalogAsync(
            "ARM-01", forceFresh: true, CancellationToken.None);

        Assert.NotNull(result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/robot-arms/ARM-01/programs", request.Uri.AbsolutePath);
        Assert.Equal("fresh=true", request.Uri.Query.TrimStart('?'));
    }

    [Fact]
    public async Task Get_map_snapshot_maps_profile_metadata_stations_and_directed_edges()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new MapSnapshotResponse(
            [
                new StationResponse(2, "Sample", "SAMPLE_CUSTOM", true, "Sample"),
                new StationResponse(4, "Dropoff", "DROP_CUSTOM", true)
            ],
            [new MapEdgeResponse("SAMPLE_CUSTOM", "DROP_CUSTOM", 12.5, false)],
            "agv-product",
            "profile-7",
            "guangzhou606",
            "2026.08",
            "e1b8d6b2b24362c1d44f1884c0abd8fb")));
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var snapshot = await client.GetMapSnapshotAsync(CancellationToken.None);

        Assert.Equal("agv-product", snapshot.ProfileProductId);
        Assert.Equal("profile-7", snapshot.ProfileVersion);
        Assert.Equal("guangzhou606", snapshot.ProfileMapName);
        Assert.Equal("2026.08", snapshot.ProfileMapVersion);
        Assert.Equal("e1b8d6b2b24362c1d44f1884c0abd8fb", snapshot.ProfileMapMd5);
        Assert.Equal("SAMPLE_CUSTOM", snapshot.Stations[0].AgvStationId);
        Assert.Equal("Sample", snapshot.Stations[0].Type);
        var edge = Assert.Single(snapshot.Edges);
        Assert.Equal("SAMPLE_CUSTOM", edge.From);
        Assert.Equal("DROP_CUSTOM", edge.To);
        Assert.Equal(12.5, edge.Cost);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/map", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task Get_physical_preflight_preserves_fail_closed_reasons_and_readiness_facts()
    {
        var readiness = new AgvSafetyReadinessResponse(
            "unknown",
            null,
            "guangzhou606",
            "816e68b9a367d9c8d5eaee9331a7ef58",
            null,
            0,
            true,
            false,
            false,
            false,
            0,
            0,
            1,
            0.9827,
            DateTimeOffset.Parse("2026-08-06T08:00:00Z"));
        var response = new PhysicalAgvPreflightResponse(
            new AgvSnapshotResponse(false, "none", "LM1", null, "AGV-01", null, readiness),
            readiness,
            false,
            ["manual_block_enabled", "automatic_mode_unknown", "map_md5_mismatch"]);
        var handler = new RecordingHandler(_ => JsonResponse(response));
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var actual = await client.GetPhysicalPreflightAsync(CancellationToken.None);

        Assert.NotNull(actual);
        Assert.False(actual!.DispatchPermitted);
        Assert.Equal(new[] { "manual_block_enabled", "automatic_mode_unknown", "map_md5_mismatch" }, actual.BlockingReasons);
        Assert.Equal("unknown", actual.Readiness!.VehicleOperatingMode);
        Assert.Equal("LM1", actual.Snapshot.CurrentStationId);
        Assert.Equal("/api/physical/preflight", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task Missing_physical_preflight_is_reported_as_unavailable_without_throwing()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        Assert.Null(await client.GetPhysicalPreflightAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Physical_readiness_refresh_uses_the_read_only_supervisor_route()
    {
        var payload = new PhysicalReadinessResponse
        {
            Enabled = true,
            SupervisorInstanceId = "supervisor-1",
            SchedulingPermitted = false,
            Devices =
            [
                new PhysicalDeviceReadinessSnapshot
                {
                    DeviceId = "AGV-01",
                    DeviceFamily = "agv",
                    State = PhysicalDeviceReadinessState.Stabilizing,
                    DeviceEpoch = 4,
                    Online = true,
                    ProbeSucceeded = true,
                    RequiresReauthorization = true,
                    BlockingReasons = [PhysicalReadinessReasonCodes.FullPreflightPending]
                }
            ],
            BlockingReasons = ["AGV-01:device_not_ready"]
        };
        var handler = new RecordingHandler(_ => JsonResponse(payload));
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var actual = await client.RefreshPhysicalReadinessAsync(
            forceFull: true,
            CancellationToken.None);

        Assert.NotNull(actual);
        Assert.Equal(4, Assert.Single(actual!.Devices).DeviceEpoch);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/physical/readiness/refresh", request.Uri.AbsolutePath);
        Assert.Equal("forceFull=true", request.Uri.Query.TrimStart('?'));
    }

    [Fact]
    public async Task Get_stations_maps_collection_and_preserves_enabled_flag()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new StationResponse[] {
            new StationResponse(2, "Sample", "SAMPLE_CUSTOM", true, "Sample"),
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
                Assert.Equal("Sample", station.Type);
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
    public async Task Get_runtime_settings_maps_profile_identity_and_refresh_interval()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new RuntimeSettingsResponse(
            "profile-a",
            "7.2",
            TimeSpan.FromSeconds(7))));
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var settings = await client.GetRuntimeSettingsAsync(CancellationToken.None);

        Assert.Equal("profile-a", settings.ProfileProductId);
        Assert.Equal("7.2", settings.ProfileVersion);
        Assert.Equal(TimeSpan.FromSeconds(7), settings.TaskRefreshInterval);
        Assert.Equal("/api/runtime-settings", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task Missing_or_invalid_runtime_settings_use_backward_compatible_default()
    {
        foreach (var response in new[]
        {
            new HttpResponseMessage(HttpStatusCode.NotFound),
            JsonResponse(new RuntimeSettingsResponse("profile-a", "7.2", TimeSpan.Zero)),
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        })
        {
            var handler = new RecordingHandler(_ => response);
            using var httpClient = CreateClient(handler);
            var client = new MesClient(httpClient);

            var settings = await client.GetRuntimeSettingsAsync(CancellationToken.None);

            Assert.Equal(DashboardRuntimeSettings.Default, settings);
        }
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
    public async Task Dispatch_conflict_exposes_http_status_code()
    {
        var taskId = Guid.NewGuid();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new { detail = "task cannot be dispatched" })
        });
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DispatchTaskAsync(taskId, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal($"/api/tasks/{taskId}/dispatch", Assert.Single(handler.Requests).Uri.AbsolutePath);
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

    [Fact]
    public async Task Ion_chromatography_status_uses_read_only_mes_route_and_maps_policy()
    {
        var response = new IonChromatographyControlCenterStatusResponse(
            new IonChromatographyStatusResponse(
                "CIC-D160-01",
                "CIC-D160+",
                "YA7261078",
                true,
                "ReadOnlyObserved",
                false,
                DateTimeOffset.Parse("2026-08-17T07:40:59Z"),
                ColumnTemperature: 31.23,
                Conductivity: 261.885712,
                TotalConductivity: 261.885712,
                Flow: 0.3,
                MappingConfidence: "CaptureCorrelatedCandidate"),
            false,
            ["Identify", "ReadStatus"],
            "ReadOnlyCaptureCorrelated");
        var handler = new RecordingHandler(_ => JsonResponse(response));
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var actual = await client.GetIonChromatographyStatusAsync(
            "CIC-D160-01",
            CancellationToken.None);

        Assert.NotNull(actual);
        Assert.Equal("YA7261078", actual.Status.SerialNumber);
        Assert.False(actual.TaskAdmissionEnabled);
        Assert.Equal(["Identify", "ReadStatus"], actual.EnabledOperations);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/instruments/CIC-D160-01/status", request.Uri.AbsolutePath);
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task Sample_workstation_snapshot_uses_only_mes_read_routes_and_maps_all_sections()
    {
        var observedAt = DateTimeOffset.Parse("2026-09-09T03:04:05Z");
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/workstations/SAMPLE-WORKSTATION-01/status" => JsonResponse(
                new SampleWorkstationStatusResponse(
                    "SAMPLE-WORKSTATION-01", "OWS-01", true,
                    SampleWorkstationDeviceState.Running, 1, observedAt)),
            "/api/workstations/SAMPLE-WORKSTATION-01/errors" => JsonResponse(
                new SampleWorkstationErrorResponse(
                    "SAMPLE-WORKSTATION-01", 17, "安全门未关闭", true, observedAt)),
            "/api/workstations/SAMPLE-WORKSTATION-01/tasks" => JsonResponse(
                new[]
                {
                    new SampleWorkstationTaskSummaryResponse(
                        9, "TASK-09", "当前任务", SampleWorkstationTaskState.Running,
                        "运行", "2026-09-09 11:00:00", "现场批次")
                }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var snapshot = await client.GetSampleWorkstationSnapshotAsync(
            "SAMPLE-WORKSTATION-01", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(SampleWorkstationDeviceState.Running, snapshot!.Status!.State);
        Assert.Equal(17, snapshot.Error!.ErrorCode);
        Assert.Equal("TASK-09", Assert.Single(snapshot.Tasks).TaskNo);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Body);
        });
        Assert.Equal(
            "/api/workstations/SAMPLE-WORKSTATION-01/status",
            handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal(
            "/api/workstations/SAMPLE-WORKSTATION-01/errors",
            handler.Requests[1].Uri.AbsolutePath);
        Assert.Equal(
            "/api/workstations/SAMPLE-WORKSTATION-01/tasks",
            handler.Requests[2].Uri.AbsolutePath);
        Assert.Equal("startNo=1&recordNum=50", handler.Requests[2].Uri.Query.TrimStart('?'));
    }

    [Fact]
    public async Task Sample_workstation_snapshot_keeps_available_sections_when_one_read_route_fails()
    {
        var observedAt = DateTimeOffset.Parse("2026-09-09T03:04:05Z");
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/workstations/SAMPLE-WORKSTATION-01/status" => JsonResponse(
                new SampleWorkstationStatusResponse(
                    "SAMPLE-WORKSTATION-01", "OWS-01", true,
                    SampleWorkstationDeviceState.Idle, 0, observedAt)),
            "/api/workstations/SAMPLE-WORKSTATION-01/errors" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "/api/workstations/SAMPLE-WORKSTATION-01/tasks" => JsonResponse(Array.Empty<SampleWorkstationTaskSummaryResponse>()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        var snapshot = await client.GetSampleWorkstationSnapshotAsync(
            "SAMPLE-WORKSTATION-01", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(SampleWorkstationDeviceState.Idle, snapshot!.Status!.State);
        Assert.Null(snapshot.Error);
        Assert.Contains("HTTP 503", snapshot.ErrorReadError);
        Assert.Empty(snapshot.Tasks);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ShineLab_config_and_command_use_high_level_mes_routes()
    {
        var response = new ShineLabCommandResponse(
            "str-001",
            "Config",
            "SHA18I",
            true,
            "accepted",
            System.Text.Json.JsonSerializer.SerializeToElement(new { result = "Success" }));
        var handler = new RecordingHandler(_ => JsonResponse(response));
        using var httpClient = CreateClient(handler);
        var client = new MesClient(httpClient);

        await client.SendShineLabConfigAsync(
            "SHA18I",
            new ShineLabConfigRequest(
                "task-001",
                [new ShineLabSampleData("S-01", "标准样", "1", 11, null, "A")]),
            CancellationToken.None);
        await client.SendShineLabCommandAsync(
            "SHA18I",
            new ShineLabCommandRequest("task-001", 0, "S-01", "标准样", "A"),
            CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/shinelab/devices/SHA18I/config", handler.Requests[0].Uri.AbsolutePath);
        Assert.Contains("task-001", handler.Requests[0].Body);
        Assert.Equal("/api/shinelab/devices/SHA18I/command", handler.Requests[1].Uri.AbsolutePath);
        Assert.Contains("\"action\":0", handler.Requests[1].Body);
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
