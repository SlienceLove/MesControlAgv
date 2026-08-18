using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MesControlAgv.Mes.Tests;

public sealed class SampleWorkstationApiTests(MesWebApplicationFactory factory)
    : IClassFixture<MesWebApplicationFactory>
{
    [Fact]
    public async Task Read_endpoints_proxy_normalized_adapter_contracts()
    {
        var reader = new StubReader();
        using var configuredFactory = ConfigureReader(reader);
        using var client = configuredFactory.CreateClient();

        var status = await client.GetFromJsonAsync<SampleWorkstationStatusResponse>(
            "/api/workstations/SAMPLE-WORKSTATION-01/status");
        var tasks = await client.GetFromJsonAsync<IReadOnlyList<SampleWorkstationTaskSummaryResponse>>(
            "/api/workstations/SAMPLE-WORKSTATION-01/tasks?state=Waiting&startNo=1&recordNum=20");
        var taskState = await client.GetFromJsonAsync<SampleWorkstationTaskStateResponse>(
            "/api/workstations/SAMPLE-WORKSTATION-01/tasks/TASK-01/state");

        Assert.Equal(SampleWorkstationDeviceState.Idle, status!.State);
        Assert.Single(tasks!);
        Assert.Equal(SampleWorkstationTaskState.Waiting, taskState!.State);
        Assert.Equal("Waiting", reader.LastQuery?.State);
        Assert.Equal(20, reader.LastQuery?.RecordNum);
        Assert.All(reader.Calls, call => Assert.Equal("SAMPLE-WORKSTATION-01", call.DeviceId));
    }

    [Fact]
    public async Task Workstation_api_has_no_mutating_route()
    {
        var reader = new StubReader();
        using var configuredFactory = ConfigureReader(reader);
        using var client = configuredFactory.CreateClient();

        var response = await client.PostAsync(
            "/api/workstations/SAMPLE-WORKSTATION-01/tasks/TASK-01/start",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(reader.Calls);
    }

    [Fact]
    public async Task Adapter_unavailability_is_returned_as_service_unavailable()
    {
        var reader = new StubReader(new HttpRequestException("adapter unavailable"));
        using var configuredFactory = ConfigureReader(reader);
        using var client = configuredFactory.CreateClient();

        var response = await client.GetAsync("/api/workstations/SAMPLE-WORKSTATION-01/status");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private WebApplicationFactory<Program> ConfigureReader(ISampleWorkstationReader reader) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISampleWorkstationReader>();
            services.AddSingleton(reader);
        }));

    private sealed class StubReader(Exception? exception = null) : ISampleWorkstationReader
    {
        private static readonly DateTimeOffset ObservedAt =
            new(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);

        public List<(string Operation, string DeviceId)> Calls { get; } = [];
        public SampleWorkstationTaskQuery? LastQuery { get; private set; }

        public Task<SampleWorkstationStatusResponse> GetStatusAsync(string deviceId, CancellationToken cancellationToken)
        {
            Calls.Add(("status", deviceId));
            return Result(new SampleWorkstationStatusResponse(
                deviceId, "EQ-01", true, SampleWorkstationDeviceState.Idle, 0, ObservedAt));
        }

        public Task<SampleWorkstationErrorResponse> GetErrorsAsync(string deviceId, CancellationToken cancellationToken)
        {
            Calls.Add(("errors", deviceId));
            return Result(new SampleWorkstationErrorResponse(deviceId, 0, "TaskCompleted", true, ObservedAt));
        }

        public Task<IReadOnlyList<SampleWorkstationTaskSummaryResponse>> GetTasksAsync(
            string deviceId,
            SampleWorkstationTaskQuery query,
            CancellationToken cancellationToken)
        {
            Calls.Add(("tasks", deviceId));
            LastQuery = query;
            return Result<IReadOnlyList<SampleWorkstationTaskSummaryResponse>>(
            [
                new(1, "TASK-01", "task", SampleWorkstationTaskState.Waiting, "等待运行", "2026-08-17 10:00:00", null)
            ]);
        }

        public Task<SampleWorkstationTaskDetailsResponse> GetTaskDetailsAsync(
            string deviceId,
            string taskNo,
            CancellationToken cancellationToken)
        {
            Calls.Add(("details", deviceId));
            return Result(new SampleWorkstationTaskDetailsResponse(
                1, taskNo, "task", SampleWorkstationTaskState.Waiting, "等待运行",
                "2026-08-17 10:00:00", null, null, "operator", "2026-08-17 10:00:00", null));
        }

        public Task<SampleWorkstationTaskStateResponse> GetTaskStateAsync(
            string deviceId,
            string taskNo,
            CancellationToken cancellationToken)
        {
            Calls.Add(("state", deviceId));
            return Result(new SampleWorkstationTaskStateResponse(
                deviceId, taskNo, SampleWorkstationTaskState.Waiting, "等待运行", ObservedAt));
        }

        private Task<T> Result<T>(T value) =>
            exception is null ? Task.FromResult(value) : Task.FromException<T>(exception);
    }
}
