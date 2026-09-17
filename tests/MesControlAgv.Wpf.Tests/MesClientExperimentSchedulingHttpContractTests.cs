using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class MesClientExperimentSchedulingHttpContractTests
{
    [Fact]
    public async Task Plan_lifecycle_uses_existing_G5_routes_and_typed_payloads()
    {
        var planId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var request = new SaveExperimentPlanDraftRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "planner api",
            Reason = "Prepare acceptance batch",
            Draft = new ExperimentPlanDraft
            {
                Name = "Anion plan",
                Description = "Pinned workflow plan",
                WorkflowId = workflowId,
                WorkflowVersion = 3,
                MaterialRequirements =
                [
                    new ExperimentMaterialRequirement
                    {
                        MaterialId = "sample",
                        Name = "Sample vial",
                        Quantity = 1,
                        Unit = "vial"
                    }
                ],
                DefaultParameters = new Dictionary<string, string?> { ["batch"] = "B-42" },
                ResourceRequirements =
                [
                    new ExperimentResourceRequirement
                    {
                        ResourceType = ExperimentResourceTypeIds.Instrument,
                        ResourceId = "D160_01"
                    }
                ]
            }
        };
        var draft = new ExperimentPlan
        {
            PlanId = planId,
            Version = 1,
            Name = request.Draft.Name,
            WorkflowId = workflowId,
            WorkflowVersion = 3,
            Status = ExperimentPlanStatus.Draft
        };
        var validated = draft with
        {
            Status = ExperimentPlanStatus.Validated,
            Validation = new ExperimentPlanValidationResult
            {
                ValidatorVersion = "1.0",
                ValidatedAt = DateTimeOffset.Parse("2026-08-24T03:00:00Z"),
                ValidatedBy = "planner api"
            }
        };
        var published = validated with { Status = ExperimentPlanStatus.Published };
        var nextDraft = draft with { Version = 2 };

        var handler = new RecordingHandler(message =>
        {
            var path = message.RequestUri!.AbsolutePath;
            if (message.Method == HttpMethod.Get && path == "/api/experiment-plans")
                return JsonResponse(new[] { draft });
            if (message.Method == HttpMethod.Get && path == $"/api/experiment-plans/{planId}/versions")
                return JsonResponse(new[] { draft });
            if (message.Method == HttpMethod.Get && path == $"/api/experiment-plans/{planId}/versions/1")
                return JsonResponse(draft);
            if (message.Method == HttpMethod.Post && path == "/api/experiment-plans")
                return JsonResponse(draft, HttpStatusCode.Created);
            if (message.Method == HttpMethod.Put && path.EndsWith("/draft", StringComparison.Ordinal))
                return JsonResponse(draft);
            if (path.EndsWith("/validate", StringComparison.Ordinal))
                return JsonResponse(validated);
            if (path.EndsWith("/publish", StringComparison.Ordinal))
                return JsonResponse(published);
            if (path.EndsWith("/next-draft", StringComparison.Ordinal))
                return JsonResponse(nextDraft, HttpStatusCode.Created);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://mes.local/") };
        var client = new MesClient(httpClient);
        var action = new ExperimentSchedulingActionRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "planner api",
            Reason = "Lifecycle acceptance"
        };

        Assert.Equal(planId, Assert.Single(await client.GetExperimentPlansAsync(CancellationToken.None)).PlanId);
        Assert.Equal(1, Assert.Single(await client.GetExperimentPlanVersionsAsync(planId, CancellationToken.None)).Version);
        Assert.Equal(planId, (await client.GetExperimentPlanAsync(planId, 1, CancellationToken.None))!.PlanId);
        Assert.Equal(1, (await client.CreateExperimentPlanDraftAsync(request, CancellationToken.None)).Version);
        Assert.Equal(ExperimentPlanStatus.Draft, (await client.UpdateExperimentPlanDraftAsync(planId, 1, request, CancellationToken.None)).Status);
        Assert.Equal(ExperimentPlanStatus.Validated, (await client.ValidateExperimentPlanAsync(planId, 1, action, CancellationToken.None)).Status);
        Assert.Equal(ExperimentPlanStatus.Published, (await client.PublishExperimentPlanAsync(planId, 1, action, CancellationToken.None)).Status);
        Assert.Equal(2, (await client.CreateNextExperimentPlanDraftAsync(planId, 1, action, CancellationToken.None)).Version);

        Assert.Equal(8, handler.Requests.Count);
        Assert.Equal(HttpMethod.Put, handler.Requests[4].Method);
        Assert.Equal($"/api/experiment-plans/{planId}/versions/1/draft", handler.Requests[4].Uri.AbsolutePath);
        Assert.Equal($"/api/experiment-plans/{planId}/versions/1/validate", handler.Requests[5].Uri.AbsolutePath);
        Assert.Equal($"/api/experiment-plans/{planId}/versions/1/publish", handler.Requests[6].Uri.AbsolutePath);
        Assert.Equal($"/api/experiment-plans/{planId}/versions/1/next-draft", handler.Requests[7].Uri.AbsolutePath);

        using var createBody = JsonDocument.Parse(handler.Requests[3].Body!);
        Assert.Equal(request.RequestId, createBody.RootElement.GetProperty("requestId").GetGuid());
        Assert.Equal("planner api", createBody.RootElement.GetProperty("actor").GetString());
        var draftBody = createBody.RootElement.GetProperty("draft");
        Assert.Equal(workflowId, draftBody.GetProperty("workflowId").GetGuid());
        Assert.Equal("D160_01", draftBody.GetProperty("resourceRequirements")[0].GetProperty("resourceId").GetString());
        Assert.Equal("B-42", draftBody.GetProperty("defaultParameters").GetProperty("batch").GetString());
    }

    [Fact]
    public async Task Scheduling_board_uses_window_filters_manual_commands_and_admission_route()
    {
        var planId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var resource = new ExperimentResourceReference
        {
            ResourceType = ExperimentResourceTypeIds.Instrument,
            ResourceId = "D160_01"
        };
        var job = new ExperimentJob
        {
            JobId = jobId,
            PlanId = planId,
            PlanVersion = 2,
            WorkflowId = Guid.NewGuid(),
            WorkflowVersion = 4,
            SampleBatchId = "B-1042",
            Status = ExperimentJobStatus.Ready
        };
        var from = DateTimeOffset.Parse("2026-08-24T08:00:00+08:00");
        var to = from.AddHours(12);
        var scheduleRequest = new ScheduleExperimentJobRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "scheduler",
            Reason = "Place acceptance task",
            PlannedStart = from.AddHours(1),
            PlannedEnd = from.AddHours(2),
            Priority = 80,
            Resources = [resource]
        };
        var schedule = new ScheduleEntry
        {
            ScheduleEntryId = scheduleId,
            ExperimentJobId = jobId,
            PlannedStart = scheduleRequest.PlannedStart,
            PlannedEnd = scheduleRequest.PlannedEnd,
            Priority = 80,
            Status = ScheduleEntryStatus.Scheduled,
            RequestedResources = [resource]
        };
        var snapshot = new ExperimentScheduleSnapshot { Entries = [schedule] };
        var availability = new ExperimentResourceAvailability
        {
            Resource = resource,
            DisplayName = "D160 #1",
            Enabled = true,
            Capacity = 1,
            AvailableCapacity = 1
        };
        var audit = new ExperimentSchedulingAuditEntry
        {
            Id = Guid.NewGuid(),
            EventType = "ExperimentJobScheduled",
            RequestId = scheduleRequest.RequestId,
            Actor = scheduleRequest.Actor,
            Reason = scheduleRequest.Reason,
            ExperimentJobId = jobId,
            ScheduleEntryId = scheduleId
        };
        var admitted = new ExperimentJobAdmissionResult
        {
            RequestId = Guid.NewGuid(),
            Status = ExperimentJobAdmissionStatus.Admitted,
            WorkflowRunId = Guid.NewGuid(),
            Job = job with { Status = ExperimentJobStatus.Admitted }
        };

        var handler = new RecordingHandler(message =>
        {
            var path = message.RequestUri!.AbsolutePath;
            if (message.Method == HttpMethod.Get && path == "/api/experiment-jobs")
                return JsonResponse(new[] { job });
            if (message.Method == HttpMethod.Get && path == $"/api/experiment-jobs/{jobId}")
                return JsonResponse(job);
            if (message.Method == HttpMethod.Post && path == "/api/experiment-jobs")
                return JsonResponse(job, HttpStatusCode.Created);
            if (message.Method == HttpMethod.Put && path.EndsWith("/schedule", StringComparison.Ordinal))
                return JsonResponse(schedule);
            if (path.EndsWith("/unschedule", StringComparison.Ordinal))
                return JsonResponse(job);
            if (path.EndsWith("/cancel", StringComparison.Ordinal))
                return JsonResponse(job with { Status = ExperimentJobStatus.Cancelled });
            if (path.EndsWith("/admit", StringComparison.Ordinal))
                return JsonResponse(admitted, HttpStatusCode.Accepted);
            if (path == "/api/schedule") return JsonResponse(snapshot);
            if (path == "/api/resources/availability") return JsonResponse(new[] { availability });
            if (path == "/api/experiment-scheduling/audits") return JsonResponse(new[] { audit });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://mes.local/") };
        var client = new MesClient(httpClient);
        var action = new ExperimentSchedulingActionRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "scheduler",
            Reason = "Manual action"
        };

        Assert.Equal(jobId, Assert.Single(await client.GetExperimentJobsAsync(ExperimentJobStatus.Ready, CancellationToken.None)).JobId);
        Assert.Equal(jobId, (await client.GetExperimentJobAsync(jobId, CancellationToken.None))!.JobId);
        Assert.Equal(jobId, (await client.CreateExperimentJobAsync(new CreateExperimentJobRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "scheduler",
            Reason = "Create task",
            PlanId = planId,
            PlanVersion = 2,
            SampleBatchId = "B-1042"
        }, CancellationToken.None)).JobId);
        Assert.Equal(scheduleId, (await client.ScheduleExperimentJobAsync(jobId, scheduleRequest, CancellationToken.None)).ScheduleEntryId);
        await client.UnscheduleExperimentJobAsync(jobId, action, CancellationToken.None);
        Assert.Equal(ExperimentJobStatus.Cancelled, (await client.CancelExperimentJobAsync(jobId, action, CancellationToken.None)).Status);
        Assert.Equal(scheduleId, Assert.Single((await client.GetExperimentScheduleAsync(from, to, CancellationToken.None)).Entries).ScheduleEntryId);
        Assert.Equal("D160_01", Assert.Single(await client.GetExperimentResourceAvailabilityAsync(from, to, CancellationToken.None)).Resource.ResourceId);
        Assert.Equal(audit.Id, Assert.Single(await client.GetExperimentSchedulingAuditsAsync(planId, jobId, scheduleId, 2500, CancellationToken.None)).Id);
        Assert.True((await client.AdmitExperimentJobAsync(jobId, new AdmitExperimentJobRequest
        {
            RequestId = admitted.RequestId,
            Actor = "scheduler",
            Reason = "Explicit admission"
        }, CancellationToken.None)).IsAdmitted);

        Assert.Equal("Ready", ParseQuery(handler.Requests[0].Uri)["status"]);
        Assert.Equal(HttpMethod.Put, handler.Requests[3].Method);
        using var scheduleBody = JsonDocument.Parse(handler.Requests[3].Body!);
        Assert.Equal(80, scheduleBody.RootElement.GetProperty("priority").GetInt32());
        Assert.Equal("D160_01", scheduleBody.RootElement.GetProperty("resources")[0].GetProperty("resourceId").GetString());
        var scheduleQuery = ParseQuery(handler.Requests[6].Uri);
        Assert.Equal(from, DateTimeOffset.Parse(scheduleQuery["from"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        Assert.Equal(to, DateTimeOffset.Parse(scheduleQuery["to"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        Assert.Equal("1000", ParseQuery(handler.Requests[8].Uri)["limit"]);
        Assert.Equal($"/api/experiment-jobs/{jobId}/admit", handler.Requests[9].Uri.AbsolutePath);
    }

    [Fact]
    public async Task Admission_business_rejection_is_returned_and_command_errors_keep_stable_code()
    {
        var jobId = Guid.NewGuid();
        var rejected = new ExperimentJobAdmissionResult
        {
            RequestId = Guid.NewGuid(),
            Status = ExperimentJobAdmissionStatus.Rejected,
            RejectionCode = ExperimentSchedulingIssueCodes.ResourceLeaseActive,
            RejectionReason = "Resource is already leased."
        };
        var calls = 0;
        var handler = new RecordingHandler(_ =>
        {
            calls++;
            return calls == 1
                ? JsonResponse(rejected, HttpStatusCode.Conflict)
                : JsonResponse(
                    new { detail = "Resource has no capacity.", code = ExperimentSchedulingIssueCodes.ResourceCapacityInsufficient },
                    HttpStatusCode.Conflict);
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://mes.local/") };
        var client = new MesClient(httpClient);

        var result = await client.AdmitExperimentJobAsync(jobId, new AdmitExperimentJobRequest
        {
            RequestId = rejected.RequestId,
            Actor = "scheduler",
            Reason = "Admission check"
        }, CancellationToken.None);
        Assert.True(result.IsRejected);
        Assert.Equal(ExperimentSchedulingIssueCodes.ResourceLeaseActive, result.RejectionCode);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ScheduleExperimentJobAsync(jobId, new ScheduleExperimentJobRequest
            {
                RequestId = Guid.NewGuid(),
                Actor = "scheduler",
                Reason = "Conflict check",
                PlannedStart = DateTimeOffset.UtcNow,
                PlannedEnd = DateTimeOffset.UtcNow.AddHours(1)
            }, CancellationToken.None));
        Assert.Contains(ExperimentSchedulingIssueCodes.ResourceCapacityInsufficient, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Resource has no capacity", exception.Message, StringComparison.Ordinal);

        var admissionException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.AdmitExperimentJobAsync(jobId, new AdmitExperimentJobRequest
            {
                RequestId = Guid.NewGuid(),
                Actor = "scheduler",
                Reason = "Request id conflict"
            }, CancellationToken.None));
        Assert.Contains(ExperimentSchedulingIssueCodes.ResourceCapacityInsufficient, admissionException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sample_verification_uses_mes_routes_and_serializes_snapshot_metadata()
    {
        var jobId = Guid.NewGuid();
        var sampleId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var sample = new ExperimentSample
        {
            SampleId = sampleId,
            BusinessSampleId = "S-1042",
            BatchId = "B-1042",
            Barcode = "BC-1042",
            DisplayName = "样品 1042",
            Status = ExperimentSampleStatus.Active
        };
        var row = new ExperimentSampleTaskRow
        {
            RowId = rowId,
            SampleId = sampleId,
            SampleBarcode = sample.Barcode,
            Position = "A01",
            DisplayName = sample.DisplayName,
            Order = 1
        };
        var verification = new ExperimentSampleVerification
        {
            VerificationId = Guid.NewGuid(),
            ExperimentJobId = jobId,
            Revision = 3,
            Status = ExperimentSampleVerificationStatus.ReadyForVerification,
            Rows = [row],
            SnapshotHash = "AABBCCDDEEFF"
        };
        var handler = new RecordingHandler(message =>
        {
            var path = message.RequestUri!.AbsolutePath;
            if (message.Method == HttpMethod.Get && path == "/api/experiment-samples") return JsonResponse(new[] { sample });
            if (message.Method == HttpMethod.Put && path == $"/api/experiment-samples/{sampleId}") return JsonResponse(sample);
            if (message.Method == HttpMethod.Get && path.EndsWith("/current", StringComparison.Ordinal)) return JsonResponse(verification);
            if (message.Method == HttpMethod.Put && path.EndsWith("/current", StringComparison.Ordinal)) return JsonResponse(verification);
            if (message.Method == HttpMethod.Post && path.EndsWith("/verify", StringComparison.Ordinal))
                return JsonResponse(verification with { Status = ExperimentSampleVerificationStatus.Verified });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://mes.local/") };
        var client = new MesClient(httpClient);

        Assert.Equal(sampleId, Assert.Single(await client.GetExperimentSamplesAsync("B-1042", CancellationToken.None)).SampleId);
        await client.SaveExperimentSampleAsync(sampleId, new SaveExperimentSampleRequest
        {
            RequestId = Guid.NewGuid(), Actor = "operator", Reason = "register", Sample = sample
        }, CancellationToken.None);
        Assert.Equal(3, (await client.GetCurrentExperimentSampleVerificationAsync(jobId, CancellationToken.None))!.Revision);
        await client.SaveCurrentExperimentSampleVerificationAsync(jobId, new SaveExperimentSampleVerificationRequest
        {
            RequestId = Guid.NewGuid(), Actor = "operator", Reason = "rows", Rows = [row]
        }, CancellationToken.None);
        await client.CompleteExperimentSampleVerificationAsync(jobId, 3, new CompleteExperimentSampleVerificationRequest
        {
            RequestId = Guid.NewGuid(), Actor = "operator", Reason = "visual", Revision = 3, SnapshotHash = verification.SnapshotHash
        }, CancellationToken.None);

        Assert.Equal("B-1042", ParseQuery(handler.Requests[0].Uri)["batchId"]);
        Assert.Equal(HttpMethod.Put, handler.Requests[3].Method);
        using var saveRowsBody = JsonDocument.Parse(handler.Requests[3].Body!);
        Assert.Equal("A01", saveRowsBody.RootElement.GetProperty("rows")[0].GetProperty("position").GetString());
        Assert.Equal($"/api/experiment-jobs/{jobId}/sample-verifications/3/verify", handler.Requests[4].Uri.AbsolutePath);
        using var verifyBody = JsonDocument.Parse(handler.Requests[4].Body!);
        Assert.Equal(3, verifyBody.RootElement.GetProperty("revision").GetInt32());
        Assert.Equal(verification.SnapshotHash, verifyBody.RootElement.GetProperty("snapshotHash").GetString());
    }

    private static Dictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => Uri.UnescapeDataString(part[1]),
                StringComparer.OrdinalIgnoreCase);

    private static HttpResponseMessage JsonResponse(object value, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = JsonContent.Create(value) };

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Body, HttpStatusCode ResponseStatus);

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
            var response = responseFactory(request);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, body, response.StatusCode));
            return response;
        }
    }
}
