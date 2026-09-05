using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class MesClientCompositeRuntimeHttpContractTests
{
    [Fact]
    public async Task Composite_runtime_client_uses_prepare_and_business_lookup_routes()
    {
        var runId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var run = new ExperimentRun
        {
            ExperimentRunId = runId,
            ExperimentJobId = jobId,
            PlanId = Guid.NewGuid(),
            PlanVersion = 3,
            AdmissionRequestId = Guid.NewGuid(),
            Status = ExperimentRunStatus.Prepared,
            CurrentStepOrder = 1,
            Steps =
            [
                new ExperimentStepRun
                {
                    ExperimentRunId = runId,
                    StepRunId = Guid.NewGuid(),
                    StepId = Guid.NewGuid(),
                    Order = 1,
                    WorkflowId = Guid.NewGuid(),
                    WorkflowVersion = 2,
                    Name = "搬运"
                }
            ]
        };
        var request = new PrepareExperimentRunRequest
        {
            RequestId = Guid.NewGuid(),
            ExperimentJobId = jobId,
            Actor = "planner",
            Reason = "Prepare composite run"
        };
        var reconcileRequest = new ReconcileExperimentChildRequest
        {
            RequestId = Guid.NewGuid(),
            ChildWorkflowRunId = Guid.NewGuid(),
            Actor = "coordinator",
            Reason = "Reconcile child"
        };
        var handler = new RecordingHandler(message =>
        {
            var path = message.RequestUri!.AbsolutePath;
            if (message.Method == HttpMethod.Post && path == "/api/experiment-runs/prepare")
                return JsonResponse(run, HttpStatusCode.Created);
            if (message.Method == HttpMethod.Get && path == $"/api/experiment-runs/{runId}")
                return JsonResponse(run);
            if (message.Method == HttpMethod.Get && path == $"/api/experiment-jobs/{jobId}/experiment-run")
                return JsonResponse(run);
            if (message.Method == HttpMethod.Post && path == $"/api/experiment-runs/{runId}/reconcile-child")
                return JsonResponse(run);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://mes.local/") };
        var client = new MesClient(httpClient);

        var prepared = await client.PrepareExperimentRunAsync(request, CancellationToken.None);
        var byRun = await client.GetExperimentRunAsync(runId, CancellationToken.None);
        var byJob = await client.GetExperimentRunForJobAsync(jobId, CancellationToken.None);
        AssertRunIdentity(run, prepared);
        AssertRunIdentity(run, byRun);
        AssertRunIdentity(run, byJob);
        var reconciled = await client.ReconcileExperimentChildAsync(
            runId,
            reconcileRequest,
            CancellationToken.None);
        AssertRunIdentity(run, reconciled);

        Assert.Equal(
            [
                "/api/experiment-runs/prepare",
                $"/api/experiment-runs/{runId}",
                $"/api/experiment-jobs/{jobId}/experiment-run",
                $"/api/experiment-runs/{runId}/reconcile-child"
            ],
            handler.Requests.Select(item => item.Uri.AbsolutePath).ToArray());
    }

    private static HttpResponseMessage JsonResponse(object value, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = JsonContent.Create(value) };

    private static void AssertRunIdentity(ExperimentRun expected, ExperimentRun? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.ExperimentRunId, actual!.ExperimentRunId);
        Assert.Equal(expected.ExperimentJobId, actual.ExperimentJobId);
        Assert.Equal(expected.PlanId, actual.PlanId);
        Assert.Equal(expected.PlanVersion, actual.PlanVersion);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Steps.Select(step => step.StepRunId), actual.Steps.Select(step => step.StepRunId));
        Assert.Equal(expected.Steps.Select(step => step.Status), actual.Steps.Select(step => step.Status));
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = responseFactory(request);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!));
            return Task.FromResult(response);
        }
    }
}
