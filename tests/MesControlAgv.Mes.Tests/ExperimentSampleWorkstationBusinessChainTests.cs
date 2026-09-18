using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MesControlAgv.Mes.Tests;

public sealed class ExperimentSampleWorkstationBusinessChainTests
{
    [Fact]
    public async Task Import_rejects_changed_verified_snapshot_before_device_io()
    {
        var gateway = new RecordingWorkstation();
        using var factory = ConfigureGateway(new PhysicalMesWebApplicationFactory(PhysicalProfile()), gateway);
        using var client = factory.CreateClient();
        var workflow = await PublishWorkflowAsync(client);
        var plan = await CreatePublishedPlanAsync(client, workflow);
        var scheduled = await CreateScheduledJobAsync(client, plan);
        var prepared = await PrepareTwoSourceTaskAsync(client, scheduled.JobId);
        var first = prepared.Payload.BottleBindings.Single(binding => binding.BottleNumber == 1);
        var drift = await client.PutAsJsonAsync($"/api/experiment-samples/{first.SampleId}", new SaveExperimentSampleRequest
        {
            RequestId = Guid.NewGuid(), Actor = "test", Reason = "Change source after preparation",
            Sample = new ExperimentSample
            {
                SampleId = first.SampleId, BusinessSampleId = first.BusinessSampleId, BatchId = "TEST-001",
                Barcode = first.SampleBarcode + "-CHANGED", Status = ExperimentSampleStatus.Active
            }
        });
        drift.EnsureSuccessStatusCode();

        var import = await client.PostAsJsonAsync(
            $"/api/experiment-jobs/{scheduled.JobId}/workstation-preparations/{prepared.PreparationId}/import",
            new ImportExperimentWorkstationTaskRequest { RequestId = Guid.NewGuid(), Actor = "test", Reason = "Reject stale preparation" });
        Assert.Equal(HttpStatusCode.Conflict, import.StatusCode);
        Assert.Equal(0, gateway.ImportCalls);
        Assert.Equal(0, gateway.BarcodeUpdateCalls);
        Assert.Equal(0, gateway.StartCalls + gateway.BarcodeStartCalls);
    }

    [Fact]
    public async Task Partial_barcode_update_failure_becomes_unknown_and_blocks_import_reissue_and_admission()
    {
        var gateway = new RecordingWorkstation { FailBarcodeUpdate = true };
        using var factory = ConfigureGateway(new PhysicalMesWebApplicationFactory(PhysicalProfile()), gateway);
        using var client = factory.CreateClient();
        var workflow = await PublishWorkflowAsync(client);
        var plan = await CreatePublishedPlanAsync(client, workflow);
        var scheduled = await CreateScheduledJobAsync(client, plan);
        var prepared = await PrepareTwoSourceTaskAsync(client, scheduled.JobId);

        var firstImport = await client.PostAsJsonAsync(
            $"/api/experiment-jobs/{scheduled.JobId}/workstation-preparations/{prepared.PreparationId}/import",
            new ImportExperimentWorkstationTaskRequest { RequestId = Guid.NewGuid(), Actor = "test", Reason = "Exercise partial failure" });
        firstImport.EnsureSuccessStatusCode();
        Assert.Equal(ExperimentWorkstationPreparationStatus.Unknown,
            (await firstImport.Content.ReadFromJsonAsync<ExperimentWorkstationPreparation>())!.Status);
        Assert.Equal(1, gateway.ImportCalls);
        Assert.Equal(1, gateway.BarcodeUpdateCalls);

        var bypassPrepare = await client.PostAsJsonAsync(
            $"/api/experiment-jobs/{scheduled.JobId}/workstation-preparations/prepare",
            new PrepareExperimentWorkstationTaskRequest
            {
                RequestId = Guid.NewGuid(), Actor = "test", Reason = "Must not bypass unknown on another device",
                DeviceId = "SAMPLE-WORKSTATION-02", SourceTaskNo = prepared.Payload.SourceTemplate.TaskNo,
                VerificationRevision = prepared.VerificationRevision,
                VerificationSnapshotHash = prepared.VerificationSnapshotHash,
                BottleBindings = prepared.Payload.BottleBindings.Select(binding => new PrepareWorkstationBottleBinding
                {
                    BottleNumber = binding.BottleNumber,
                    SampleId = binding.SampleId,
                    TemplateSource = binding.TemplateSource
                }).ToArray()
            });
        Assert.Equal(HttpStatusCode.Conflict, bypassPrepare.StatusCode);
        using (var problem = JsonDocument.Parse(await bypassPrepare.Content.ReadAsStringAsync()))
            Assert.Equal(ExperimentWorkstationPreparationIssueCodes.ImportOutcomeUnknown, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, gateway.TemplateReadCalls);

        var secondImport = await client.PostAsJsonAsync(
            $"/api/experiment-jobs/{scheduled.JobId}/workstation-preparations/{prepared.PreparationId}/import",
            new ImportExperimentWorkstationTaskRequest { RequestId = Guid.NewGuid(), Actor = "test", Reason = "Must not retry unknown write" });
        Assert.Equal(HttpStatusCode.Conflict, secondImport.StatusCode);
        Assert.Equal(1, gateway.ImportCalls);
        Assert.Equal(1, gateway.BarcodeUpdateCalls);

        var admission = await client.PostAsJsonAsync($"/api/experiment-jobs/{scheduled.JobId}/admit", Action("Reject unknown preparation"));
        Assert.Equal(HttpStatusCode.Conflict, admission.StatusCode);
        var rejected = (await admission.Content.ReadFromJsonAsync<ExperimentJobAdmissionResult>())!;
        Assert.Equal(ExperimentWorkstationPreparationIssueCodes.ImportOutcomeUnknown, rejected.RejectionCode);
        Assert.Equal(0, gateway.StartCalls + gateway.BarcodeStartCalls);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<MesDbContext>().WorkflowExecutions.ToListAsync());
    }

    [Fact]
    public async Task Preparation_scope_is_pinned_and_drift_rejects_before_adapter_io()
    {
        var gateway = new RecordingWorkstation();
        using var factory = ConfigureGateway(new PhysicalMesWebApplicationFactory(PhysicalProfile()), gateway);
        using var client = factory.CreateClient();
        var workflow = await PublishWorkflowAsync(client);
        var plan = await CreatePublishedPlanAsync(client, workflow);
        var scheduled = await CreateScheduledJobAsync(client, plan);
        var sample1 = await RegisterSampleAsync(client, "TEST-001", "PIN-1", "PIN-BC-1");
        var sample2 = await RegisterSampleAsync(client, "TEST-001", "PIN-2", "PIN-BC-2");
        var saved = await SaveRowsAsync(client, scheduled.JobId,
        [
            new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = sample1.SampleId, SampleBarcode = sample1.Barcode, Position = "A1", Order = 1 },
            new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = sample2.SampleId, SampleBarcode = sample2.Barcode, Position = "A2", Order = 2 }
        ], "Snapshot pin test sources");
        var verified = await VerifyAsync(client, scheduled.JobId, saved, "test", "Verify pin test sources");
        PrepareExperimentWorkstationTaskRequest Request(string deviceId, string reason) => new()
        {
            RequestId = Guid.NewGuid(), Actor = "test", Reason = reason, DeviceId = deviceId, SourceTaskNo = "TEST-001",
            VerificationRevision = verified.Revision, VerificationSnapshotHash = verified.SnapshotHash,
            BottleBindings =
            [
                new PrepareWorkstationBottleBinding { BottleNumber = 1, SampleId = sample1.SampleId, TemplateSource = new WorkstationTemplateSourceKey { Module = "SOURCE-A", X = 1, Y = 1 } },
                new PrepareWorkstationBottleBinding { BottleNumber = 2, SampleId = sample2.SampleId, TemplateSource = new WorkstationTemplateSourceKey { Module = "SOURCE-B", X = 2, Y = 1 } }
            ]
        };

        var wrongDevice = await client.PostAsJsonAsync(
            $"/api/experiment-jobs/{scheduled.JobId}/workstation-preparations/prepare",
            Request("SAMPLE-WORKSTATION-02", "Reject non-workflow device"));
        Assert.Equal(HttpStatusCode.Conflict, wrongDevice.StatusCode);
        Assert.Equal(0, gateway.TemplateReadCalls);

        var prepare = await client.PostAsJsonAsync(
            $"/api/experiment-jobs/{scheduled.JobId}/workstation-preparations/prepare",
            Request("SAMPLE-WORKSTATION-01", "Pin workflow and schedule"));
        Assert.Equal(HttpStatusCode.Created, prepare.StatusCode);
        var prepared = (await prepare.Content.ReadFromJsonAsync<ExperimentWorkstationPreparation>())!;
        Assert.Equal(1, prepared.Revision);
        Assert.Equal(workflow.WorkflowId, prepared.WorkflowId);
        Assert.Equal(workflow.Version, prepared.WorkflowVersion);
        Assert.Equal(scheduled.ScheduleId, prepared.ScheduleEntryId);
        Assert.Equal(1, gateway.TemplateReadCalls);

        var driftedResourcesJson = JsonSerializer.Serialize(new[]
        {
            new ExperimentResourceReference { ResourceType = ExperimentResourceTypeIds.Workstation, ResourceId = "SAMPLE-WORKSTATION-02" }
        });
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            await database.ScheduleEntries.Where(entry => entry.ScheduleEntryId == scheduled.ScheduleId)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    entry => entry.RequestedResourcesJson,
                    driftedResourcesJson));
        }
        var scheduleDrift = await client.PostAsJsonAsync(
            $"/api/experiment-jobs/{scheduled.JobId}/workstation-preparations/{prepared.PreparationId}/import",
            new ImportExperimentWorkstationTaskRequest { RequestId = Guid.NewGuid(), Actor = "test", Reason = "Reject schedule drift" });
        Assert.Equal(HttpStatusCode.Conflict, scheduleDrift.StatusCode);
        Assert.Equal(0, gateway.ImportCalls);
        Assert.Equal(0, gateway.BarcodeUpdateCalls);
    }

    [Fact]
    public async Task Fixed_clock_consecutive_preparations_use_monotonic_revision_for_current()
    {
        var gateway = new RecordingWorkstation();
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero));
        using var factory = ConfigureGateway(new PhysicalMesWebApplicationFactory(PhysicalProfile()), gateway, timeProvider: clock);
        using var client = factory.CreateClient();
        var workflow = await PublishWorkflowAsync(client);
        var plan = await CreatePublishedPlanAsync(client, workflow);
        var scheduled = await CreateScheduledJobAsync(client, plan);
        var sample1 = await RegisterSampleAsync(client, "TEST-001", "REV-1", "REV-BC-1");
        var sample2 = await RegisterSampleAsync(client, "TEST-001", "REV-2", "REV-BC-2");
        var saved = await SaveRowsAsync(client, scheduled.JobId,
        [
            new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = sample1.SampleId, SampleBarcode = sample1.Barcode, Position = "A1", Order = 1 },
            new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = sample2.SampleId, SampleBarcode = sample2.Barcode, Position = "A2", Order = 2 }
        ], "Snapshot fixed-clock sources");
        var verified = await VerifyAsync(client, scheduled.JobId, saved, "test", "Verify fixed-clock sources");
        PrepareExperimentWorkstationTaskRequest Request(string reason) => new()
        {
            RequestId = Guid.NewGuid(), Actor = "test", Reason = reason,
            DeviceId = "SAMPLE-WORKSTATION-01", SourceTaskNo = "TEST-001",
            VerificationRevision = verified.Revision, VerificationSnapshotHash = verified.SnapshotHash,
            BottleBindings =
            [
                new PrepareWorkstationBottleBinding { BottleNumber = 1, SampleId = sample1.SampleId, TemplateSource = new WorkstationTemplateSourceKey { Module = "SOURCE-A", X = 1, Y = 1 } },
                new PrepareWorkstationBottleBinding { BottleNumber = 2, SampleId = sample2.SampleId, TemplateSource = new WorkstationTemplateSourceKey { Module = "SOURCE-B", X = 2, Y = 1 } }
            ]
        };
        var firstResponse = await client.PostAsJsonAsync($"/api/experiment-jobs/{scheduled.JobId}/workstation-preparations/prepare", Request("Prepare revision one"));
        var secondResponse = await client.PostAsJsonAsync($"/api/experiment-jobs/{scheduled.JobId}/workstation-preparations/prepare", Request("Prepare revision two"));
        firstResponse.EnsureSuccessStatusCode();
        secondResponse.EnsureSuccessStatusCode();
        var first = (await firstResponse.Content.ReadFromJsonAsync<ExperimentWorkstationPreparation>())!;
        var second = (await secondResponse.Content.ReadFromJsonAsync<ExperimentWorkstationPreparation>())!;
        var current = (await client.GetFromJsonAsync<ExperimentWorkstationPreparation>($"/api/experiment-jobs/{scheduled.JobId}/workstation-preparations/current"))!;
        Assert.Equal(first.PreparedAt, second.PreparedAt);
        Assert.Equal(1, first.Revision);
        Assert.Equal(2, second.Revision);
        Assert.Equal(second.PreparationId, current.PreparationId);
        Assert.Equal(2, current.Revision);
    }

    [Fact]
    public async Task Prepared_task_import_is_idempotent_and_worker_uses_frozen_task_and_barcodes_once()
    {
        var gateway = new RecordingWorkstation();
        using var factory = ConfigureGateway(new PhysicalMesWebApplicationFactory(PhysicalProfile()), gateway);
        using var client = factory.CreateClient();
        var workflow = await PublishWorkflowAsync(client);
        var plan = await CreatePublishedPlanAsync(client, workflow);
        var scheduled = await CreateScheduledJobAsync(client, plan);
        var sample1 = await RegisterSampleAsync(client, "TEST-001", "SOURCE-1", "BARCODE-1");
        var sample2 = await RegisterSampleAsync(client, "TEST-001", "SOURCE-2", "BARCODE-2");
        var saved = await SaveRowsAsync(client, scheduled.JobId,
        [
            new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = sample1.SampleId, SampleBarcode = sample1.Barcode, Position = "A1", Order = 1 },
            new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = sample2.SampleId, SampleBarcode = sample2.Barcode, Position = "A2", Order = 2 }
        ], "Snapshot two workstation sources");
        var verified = await VerifyAsync(client, scheduled.JobId, saved, "test", "Verify two workstation sources");
        var prepareRequest = new PrepareExperimentWorkstationTaskRequest
        {
            RequestId = Guid.NewGuid(), Actor = "test", Reason = "Prepare generated workstation task",
            DeviceId = "SAMPLE-WORKSTATION-01", SourceTaskNo = "TEST-001",
            VerificationRevision = verified.Revision, VerificationSnapshotHash = verified.SnapshotHash,
            BottleBindings =
            [
                new PrepareWorkstationBottleBinding { BottleNumber = 1, SampleId = sample1.SampleId, TemplateSource = new WorkstationTemplateSourceKey { Module = "SOURCE-A", X = 1, Y = 1 } },
                new PrepareWorkstationBottleBinding { BottleNumber = 2, SampleId = sample2.SampleId, TemplateSource = new WorkstationTemplateSourceKey { Module = "SOURCE-B", X = 2, Y = 1 } }
            ]
        };

        var prepareResponse = await client.PostAsJsonAsync($"/api/experiment-jobs/{scheduled.JobId}/workstation-preparations/prepare", prepareRequest);
        Assert.Equal(HttpStatusCode.Created, prepareResponse.StatusCode);
        var prepared = (await prepareResponse.Content.ReadFromJsonAsync<ExperimentWorkstationPreparation>())!;
        Assert.Equal(ExperimentWorkstationPreparationStatus.Prepared, prepared.Status);
        Assert.NotEqual("TEST-001", prepared.VendorTaskNo);
        Assert.Equal([sample1.SampleId, sample1.SampleId, sample2.SampleId], prepared.Payload.Transfers.Select(row => row.SourceSampleId).ToArray());
        Assert.Equal(1, gateway.TemplateReadCalls);
        Assert.Equal(0, gateway.ImportCalls);
        Assert.Equal(0, gateway.BarcodeUpdateCalls);
        Assert.Equal(0, gateway.StartCalls + gateway.BarcodeStartCalls);

        var prepareReplay = await client.PostAsJsonAsync($"/api/experiment-jobs/{scheduled.JobId}/workstation-preparations/prepare", prepareRequest);
        Assert.Equal(HttpStatusCode.Created, prepareReplay.StatusCode);
        Assert.True((await prepareReplay.Content.ReadFromJsonAsync<ExperimentWorkstationPreparation>())!.IsIdempotentReplay);
        Assert.Equal(1, gateway.TemplateReadCalls);

        var importRequest = new ImportExperimentWorkstationTaskRequest { RequestId = Guid.NewGuid(), Actor = "test", Reason = "Import generated task" };
        var importResponse = await client.PostAsJsonAsync($"/api/experiment-jobs/{scheduled.JobId}/workstation-preparations/{prepared.PreparationId}/import", importRequest);
        importResponse.EnsureSuccessStatusCode();
        var imported = (await importResponse.Content.ReadFromJsonAsync<ExperimentWorkstationPreparation>())!;
        Assert.Equal(ExperimentWorkstationPreparationStatus.Imported, imported.Status);
        Assert.Equal(1, gateway.ImportCalls);
        Assert.Equal(1, gateway.BarcodeUpdateCalls);
        Assert.Equal(0, gateway.StartCalls + gateway.BarcodeStartCalls);

        var importReplay = await client.PostAsJsonAsync($"/api/experiment-jobs/{scheduled.JobId}/workstation-preparations/{prepared.PreparationId}/import", importRequest);
        importReplay.EnsureSuccessStatusCode();
        Assert.True((await importReplay.Content.ReadFromJsonAsync<ExperimentWorkstationPreparation>())!.IsIdempotentReplay);
        Assert.Equal(1, gateway.ImportCalls);
        Assert.Equal(1, gateway.BarcodeUpdateCalls);

        var admission = await client.PostAsJsonAsync($"/api/experiment-jobs/{scheduled.JobId}/admit", Action("Admit prepared workstation task"));
        Assert.Equal(HttpStatusCode.Accepted, admission.StatusCode);
        var admitted = (await admission.Content.ReadFromJsonAsync<ExperimentJobAdmissionResult>())!;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var node = await db.WorkflowNodeExecutions.SingleAsync(item => item.WorkflowRunId == admitted.WorkflowRunId);
            var inputs = JsonSerializer.Deserialize<Dictionary<string, string?>>(node.InputJson)!;
            Assert.Equal(prepared.VendorTaskNo, inputs[WorkflowNodeConfigurationKeys.TaskNo]);
            Assert.Equal(prepared.PreparationId.ToString("D"), inputs[WorkflowTrustedWorkstationInputKeys.PreparationId]);
            Assert.Equal("BARCODE-1", inputs[WorkflowTrustedWorkstationInputKeys.SampleBarcode1]);
            Assert.Equal("BARCODE-2", inputs[WorkflowTrustedWorkstationInputKeys.SampleBarcode2]);

            var dispatcher = new WorkflowSampleWorkstationDispatcher(
                scope.ServiceProvider.GetRequiredService<IWorkflowApplicationService>(), gateway, gateway,
                PhysicalProfile(), new WorkflowSampleWorkstationWorkerOptions
                { Enabled = true, PollIntervalMs = 1, ReadinessRetryIntervalMs = 1, StartObservationTimeoutMs = 5000, CompletionTimeoutMs = 5000 },
                barcodeCommands: gateway,
                runtimeBindingValidator: scope.ServiceProvider.GetRequiredService<IExperimentWorkstationRuntimeBindingValidator>());
            await dispatcher.ProcessAsync(CancellationToken.None);
        }

        Assert.Equal(0, gateway.StartCalls);
        Assert.Equal(1, gateway.BarcodeStartCalls);
        Assert.Equal(prepared.VendorTaskNo, gateway.StartTaskNo);
        Assert.Equal(new SampleWorkstationTaskBarcodes { SampleBarcode1 = "BARCODE-1", SampleBarcode2 = "BARCODE-2" }, gateway.StartBarcodes);
    }

    [Fact]
    public async Task Formal_experiment_admission_dispatches_and_closes_the_sample_workstation_business_chain()
    {
        var gateway = new RecordingWorkstation();
        using var factory = ConfigureGateway(new PhysicalMesWebApplicationFactory(PhysicalProfile()), gateway);
        using var client = factory.CreateClient();
        var workflow = await PublishWorkflowAsync(client);
        var plan = await CreatePublishedPlanAsync(client, workflow);
        var scheduled = await CreateScheduledJobAsync(client, plan);
        var verification = await VerifyCurrentSampleAsync(client, scheduled.JobId, "TEST-001");

        var admissionResponse = await client.PostAsJsonAsync($"/api/experiment-jobs/{scheduled.JobId}/admit", Action("Admit TEST-001"));
        Assert.Equal(HttpStatusCode.Accepted, admissionResponse.StatusCode);
        var admitted = (await admissionResponse.Content.ReadFromJsonAsync<ExperimentJobAdmissionResult>())!;
        Assert.True(admitted.IsAdmitted);
        Assert.NotNull(admitted.WorkflowRunId);
        Assert.Equal(ExperimentJobStatus.Admitted, admitted.Job!.Status);
        Assert.Equal(ScheduleEntryStatus.Admitted, admitted.ScheduleEntry!.Status);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            Assert.Single(await db.WorkflowExecutions.AsNoTracking().ToListAsync());
            var lease = Assert.Single(await db.WorkflowResourceLeases.AsNoTracking().Where(x => x.ActiveResourceKey != null).ToListAsync());
            Assert.Equal(admitted.WorkflowRunId, lease.WorkflowRunId);
            var admissionAudit = Assert.Single(await db.ExperimentSchedulingAudits.AsNoTracking()
                .Where(audit => audit.RequestId == admitted.RequestId).ToListAsync());
            Assert.Contains(verification.VerificationId.ToString("D"), admissionAudit.DetailsJson);
            Assert.Contains(verification.Revision.ToString(), admissionAudit.DetailsJson);
            Assert.Contains(verification.SnapshotHash, admissionAudit.DetailsJson);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var dispatcher = new WorkflowSampleWorkstationDispatcher(
                scope.ServiceProvider.GetRequiredService<IWorkflowApplicationService>(), gateway, gateway,
                PhysicalProfile(), new WorkflowSampleWorkstationWorkerOptions
                { Enabled = true, PollIntervalMs = 1, ReadinessRetryIntervalMs = 1, StartObservationTimeoutMs = 5000, CompletionTimeoutMs = 5000 });
            await dispatcher.ProcessAsync(CancellationToken.None);
        }

        Assert.Equal(1, gateway.StartCalls);
        Assert.Equal(0, gateway.InitializeCalls);
        Assert.Equal("SAMPLE-WORKSTATION-01", gateway.StartDeviceId);
        Assert.Equal("TEST-001", gateway.StartTaskNo);
        Assert.Equal(
        [
            new RecordingWorkstation.ConsumedObservation("SAMPLE-WORKSTATION-01", "EQ-01", SampleWorkstationDeviceState.Idle, 0, 0, "TEST-001", SampleWorkstationTaskState.Completed, "Completed"),
            new RecordingWorkstation.ConsumedObservation("SAMPLE-WORKSTATION-01", "EQ-01", SampleWorkstationDeviceState.Running, 1, 3, "TEST-001", SampleWorkstationTaskState.Running, "Running"),
            new RecordingWorkstation.ConsumedObservation("SAMPLE-WORKSTATION-01", "EQ-01", SampleWorkstationDeviceState.Idle, 0, 0, "TEST-001", SampleWorkstationTaskState.Completed, "Completed")
        ],
        gateway.ConsumedObservations);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var run = Assert.Single(await db.WorkflowExecutions.AsNoTracking().ToListAsync());
            Assert.Equal(WorkflowRuntimeStatus.Completed.ToString(), run.RuntimeStatus);
            Assert.Equal(WorkflowNodeExecutionStatus.Succeeded.ToString(), (await db.WorkflowNodeExecutions.SingleAsync(x => x.WorkflowRunId == run.ExecutionId && x.NodeTypeId == WorkflowGraphNodeTypeIds.SampleWorkstationExecuteExistingTask)).Status);
            var operation = Assert.Single(await db.WorkflowDeviceOperations.AsNoTracking().Where(x => x.WorkflowRunId == run.ExecutionId).ToListAsync());
            Assert.Equal(WorkflowDeviceOperationStatus.Succeeded.ToString(), operation.Status);
            Assert.Equal(ExperimentJobStatus.Completed.ToString(), (await db.ExperimentJobs.SingleAsync(x => x.JobId == scheduled.JobId)).Status);
            Assert.Equal(ScheduleEntryStatus.Completed.ToString(), (await db.ScheduleEntries.SingleAsync(x => x.ScheduleEntryId == scheduled.ScheduleId)).Status);
            var lease = await db.WorkflowResourceLeases.SingleAsync(x => x.WorkflowRunId == run.ExecutionId);
            Assert.Equal(ResourceLeaseStatus.Released.ToString(), lease.Status);
            Assert.Null(lease.ActiveResourceKey);
        }

        var schedule = (await (await client.GetAsync("/api/schedule")).Content.ReadFromJsonAsync<ExperimentScheduleSnapshot>())!;
        var activity = Assert.Single(schedule.Activities);
        Assert.Equal(scheduled.JobId, activity.ExperimentJobId);
        Assert.Equal(admitted.WorkflowRunId, activity.WorkflowRunId);
        Assert.Equal(ExperimentResourceTypeIds.Workstation, activity.Resource.ResourceType);
        Assert.Equal("SAMPLE-WORKSTATION-01", activity.Resource.ResourceId);
        Assert.Equal(WorkflowDeviceOperationStatus.Succeeded.ToString(), activity.Status);
        Assert.NotNull(activity.ActualEnd);
    }

    [Fact]
    public async Task Workstation_admission_without_a_verified_snapshot_is_rejected_before_runtime_side_effects()
    {
        var gateway = new RecordingWorkstation();
        using var factory = ConfigureGateway(new PhysicalMesWebApplicationFactory(PhysicalProfile()), gateway);
        using var client = factory.CreateClient();
        var workflow = await PublishWorkflowAsync(client);
        var plan = await CreatePublishedPlanAsync(client, workflow);
        var scheduled = await CreateScheduledJobAsync(client, plan);
        var request = Action("Reject missing sample verification");

        var response = await client.PostAsJsonAsync($"/api/experiment-jobs/{scheduled.JobId}/admit", request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var rejected = (await response.Content.ReadFromJsonAsync<ExperimentJobAdmissionResult>())!;
        Assert.True(rejected.IsRejected);
        Assert.Equal(ExperimentSampleVerificationIssueCodes.VerificationRequired, rejected.RejectionCode);
        Assert.Null(rejected.WorkflowRunId);
        Assert.Equal(0, gateway.StartCalls);
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Empty(await database.WorkflowExecutions.ToListAsync());
        Assert.Empty(await database.WorkflowResourceLeases.ToListAsync());
        Assert.Empty(await database.WorkflowDeviceOperations.ToListAsync());
        Assert.Single(await database.ExperimentSchedulingAudits.Where(audit => audit.RequestId == request.RequestId && audit.EventType == "ExperimentJobAdmissionRejected").ToListAsync());
    }

    [Fact]
    public async Task Workstation_admission_rejects_verification_revision_and_hash_drift_without_runtime_side_effects()
    {
        var gateway = new RecordingWorkstation();
        using var factory = ConfigureGateway(new PhysicalMesWebApplicationFactory(PhysicalProfile()), gateway, introduceVersionDrift: true);
        using var client = factory.CreateClient();
        var workflow = await PublishWorkflowAsync(client);
        var plan = await CreatePublishedPlanAsync(client, workflow);
        var scheduled = await CreateScheduledJobAsync(client, plan);
        await VerifyCurrentSampleAsync(client, scheduled.JobId, "TEST-001");

        var response = await client.PostAsJsonAsync($"/api/experiment-jobs/{scheduled.JobId}/admit", Action("Reject verification revision and hash drift"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var rejected = (await response.Content.ReadFromJsonAsync<ExperimentJobAdmissionResult>())!;
        Assert.Equal(ExperimentSampleVerificationIssueCodes.VersionConflict, rejected.RejectionCode);
        Assert.Null(rejected.WorkflowRunId);
        Assert.Equal(0, gateway.StartCalls);
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Empty(await database.WorkflowExecutions.ToListAsync());
        Assert.Empty(await database.WorkflowResourceLeases.ToListAsync());
        Assert.Empty(await database.WorkflowDeviceOperations.ToListAsync());
    }

    [Theory]
    [InlineData(ExperimentSampleVerificationStatus.Draft, ExperimentSampleVerificationIssueCodes.VerificationRequired)]
    [InlineData(ExperimentSampleVerificationStatus.ReadyForVerification, ExperimentSampleVerificationIssueCodes.VerificationRequired)]
    [InlineData(ExperimentSampleVerificationStatus.Invalidated, ExperimentSampleVerificationIssueCodes.VerificationInvalidated)]
    public async Task Workstation_admission_rejects_non_verified_snapshot_states_without_runtime_side_effects(
        ExperimentSampleVerificationStatus status,
        string expectedCode)
    {
        var gateway = new RecordingWorkstation();
        using var factory = ConfigureGateway(new PhysicalMesWebApplicationFactory(PhysicalProfile()), gateway);
        using var client = factory.CreateClient();
        var workflow = await PublishWorkflowAsync(client);
        var plan = await CreatePublishedPlanAsync(client, workflow);
        var scheduled = await CreateScheduledJobAsync(client, plan);
        await VerifyCurrentSampleAsync(client, scheduled.JobId, "TEST-001");
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            (await database.ExperimentSampleVerifications.SingleAsync(item => item.ExperimentJobId == scheduled.JobId)).Status = status.ToString();
            await database.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync($"/api/experiment-jobs/{scheduled.JobId}/admit", Action("Reject non-verified snapshot"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var rejected = (await response.Content.ReadFromJsonAsync<ExperimentJobAdmissionResult>())!;
        Assert.Equal(expectedCode, rejected.RejectionCode);
        Assert.Null(rejected.WorkflowRunId);
        Assert.Equal(0, gateway.StartCalls);
        using var verifyScope = factory.Services.CreateScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Empty(await verifyDatabase.WorkflowExecutions.ToListAsync());
        Assert.Empty(await verifyDatabase.WorkflowResourceLeases.ToListAsync());
        Assert.Empty(await verifyDatabase.WorkflowDeviceOperations.ToListAsync());
    }

    [Fact]
    public async Task Workstation_admission_rejects_registered_sample_drift_without_runtime_side_effects()
    {
        var gateway = new RecordingWorkstation();
        using var factory = ConfigureGateway(new PhysicalMesWebApplicationFactory(PhysicalProfile()), gateway);
        using var client = factory.CreateClient();
        var workflow = await PublishWorkflowAsync(client);
        var plan = await CreatePublishedPlanAsync(client, workflow);
        var scheduled = await CreateScheduledJobAsync(client, plan);
        await VerifyCurrentSampleAsync(client, scheduled.JobId, "TEST-001");
        ExperimentSample sample;
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var record = await database.ExperimentSamples.SingleAsync();
            sample = new ExperimentSample { SampleId = record.SampleId, BusinessSampleId = record.BusinessSampleId, BatchId = record.BatchId, Barcode = "DRIFTED", DisplayName = record.DisplayName, Status = ExperimentSampleStatus.Active };
        }
        (await client.PutAsJsonAsync($"/api/experiment-samples/{sample.SampleId}", new SaveExperimentSampleRequest
        {
            RequestId = Guid.NewGuid(), Actor = "test", Reason = "Cause registered sample drift", Sample = sample
        })).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync($"/api/experiment-jobs/{scheduled.JobId}/admit", Action("Reject registered sample drift"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var rejected = (await response.Content.ReadFromJsonAsync<ExperimentJobAdmissionResult>())!;
        Assert.Equal(ExperimentSampleVerificationIssueCodes.VerificationInvalidated, rejected.RejectionCode);
        Assert.Equal(0, gateway.StartCalls);
        using var verifyScope = factory.Services.CreateScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Empty(await verifyDatabase.WorkflowExecutions.ToListAsync());
        Assert.Empty(await verifyDatabase.WorkflowResourceLeases.ToListAsync());
        Assert.Empty(await verifyDatabase.WorkflowDeviceOperations.ToListAsync());
    }

    [Fact]
    public async Task Unverified_drifted_and_reverified_sample_snapshot_gates_workstation_admission_without_gateway_start()
    {
        var gateway = new RecordingWorkstation();
        using var factory = ConfigureGateway(new PhysicalMesWebApplicationFactory(PhysicalProfile()), gateway);
        using var client = factory.CreateClient();
        var workflow = await PublishWorkflowAsync(client);
        var plan = await CreatePublishedPlanAsync(client, workflow);
        var scheduled = await CreateScheduledJobAsync(client, plan);

        var firstSample = await RegisterSampleAsync(client, "TEST-001", "S-CHAIN-01", "BC-CHAIN-01");
        var secondSample = await RegisterSampleAsync(client, "TEST-001", "S-CHAIN-02", "BC-CHAIN-02");
        var rows = new[]
        {
            new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = firstSample.SampleId, BusinessSampleId = firstSample.BusinessSampleId, SampleBarcode = firstSample.Barcode, Position = "A01", Order = 1 },
            new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = secondSample.SampleId, BusinessSampleId = secondSample.BusinessSampleId, SampleBarcode = secondSample.Barcode, Position = "B01", Order = 2 }
        };
        var saved = await SaveRowsAsync(client, scheduled.JobId, rows, "Save two-row snapshot");
        Assert.Equal(ExperimentSampleVerificationStatus.ReadyForVerification, saved.Status);
        Assert.Equal(["S-CHAIN-01", "S-CHAIN-02"], saved.Rows.Select(row => row.BusinessSampleId));
        Assert.Equal(["A01", "B01"], saved.Rows.Select(row => row.Position));
        Assert.Equal([1, 2], saved.Rows.Select(row => row.Order));

        var firstAdmissionRequest = Action("Reject unverified two-row snapshot");
        var firstAdmissionResponse = await client.PostAsJsonAsync($"/api/experiment-jobs/{scheduled.JobId}/admit", firstAdmissionRequest);
        Assert.Equal(HttpStatusCode.Conflict, firstAdmissionResponse.StatusCode);
        var firstRejected = (await firstAdmissionResponse.Content.ReadFromJsonAsync<ExperimentJobAdmissionResult>())!;
        Assert.Equal(ExperimentSampleVerificationIssueCodes.VerificationRequired, firstRejected.RejectionCode);
        Assert.Null(firstRejected.WorkflowRunId);
        await AssertNoRuntimeSideEffectsAsync(factory, gateway);

        var firstVerified = await VerifyAsync(client, scheduled.JobId, saved, "sample-operator", "Visually verify two rows");
        Assert.Equal(ExperimentSampleVerificationStatus.Verified, firstVerified.Status);
        Assert.Equal("sample-operator", firstVerified.VerifiedBy);
        Assert.NotNull(firstVerified.VerifiedAt);
        Assert.Equal(saved.Revision, firstVerified.Revision);
        Assert.Equal(saved.SnapshotHash, firstVerified.SnapshotHash);

        var drifted = await SaveRowsAsync(
            client,
            scheduled.JobId,
            rows.Select(row => row.Position == "B01" ? row with { Position = "C01" } : row).ToArray(),
            "Correct second sample position");
        Assert.Equal(firstVerified.Revision + 1, drifted.Revision);
        Assert.Equal(ExperimentSampleVerificationStatus.ReadyForVerification, drifted.Status);
        Assert.Equal("C01", drifted.Rows.Single(row => row.Order == 2).Position);
        using (var invalidationScope = factory.Services.CreateScope())
        {
            var database = invalidationScope.ServiceProvider.GetRequiredService<MesDbContext>();
            Assert.Equal(
                ExperimentSampleVerificationStatus.Invalidated.ToString(),
                (await database.ExperimentSampleVerifications.SingleAsync(item => item.VerificationId == firstVerified.VerificationId)).Status);
        }

        var secondAdmissionRequest = Action("Reject corrected but unverified snapshot");
        var secondAdmissionResponse = await client.PostAsJsonAsync($"/api/experiment-jobs/{scheduled.JobId}/admit", secondAdmissionRequest);
        Assert.Equal(HttpStatusCode.Conflict, secondAdmissionResponse.StatusCode);
        var secondRejected = (await secondAdmissionResponse.Content.ReadFromJsonAsync<ExperimentJobAdmissionResult>())!;
        Assert.Equal(ExperimentSampleVerificationIssueCodes.VerificationRequired, secondRejected.RejectionCode);
        Assert.Null(secondRejected.WorkflowRunId);
        await AssertNoRuntimeSideEffectsAsync(factory, gateway);

        var reverified = await VerifyAsync(client, scheduled.JobId, drifted, "sample-operator", "Re-verify corrected two-row snapshot");
        Assert.Equal(ExperimentSampleVerificationStatus.Verified, reverified.Status);
        Assert.Equal("sample-operator", reverified.VerifiedBy);
        Assert.NotNull(reverified.VerifiedAt);
        Assert.Equal(drifted.Revision, reverified.Revision);
        Assert.Equal(drifted.SnapshotHash, reverified.SnapshotHash);

        var admittedResponse = await client.PostAsJsonAsync($"/api/experiment-jobs/{scheduled.JobId}/admit", Action("Admit re-verified snapshot"));
        Assert.Equal(HttpStatusCode.Accepted, admittedResponse.StatusCode);
        var admitted = (await admittedResponse.Content.ReadFromJsonAsync<ExperimentJobAdmissionResult>())!;
        Assert.True(admitted.IsAdmitted);
        Assert.NotNull(admitted.WorkflowRunId);
        Assert.Equal(0, gateway.StartCalls);
        using var admissionScope = factory.Services.CreateScope();
        var admissionDatabase = admissionScope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Single(await admissionDatabase.WorkflowExecutions.Where(run => run.ExecutionId == admitted.WorkflowRunId).ToListAsync());
        var lease = Assert.Single(await admissionDatabase.WorkflowResourceLeases.Where(item => item.WorkflowRunId == admitted.WorkflowRunId).ToListAsync());
        Assert.NotNull(lease.ActiveResourceKey);
        Assert.Empty(await admissionDatabase.WorkflowDeviceOperations.Where(item => item.WorkflowRunId == admitted.WorkflowRunId).ToListAsync());
        var audit = Assert.Single(await admissionDatabase.ExperimentSchedulingAudits.Where(item => item.RequestId == admitted.RequestId).ToListAsync());
        var details = JsonSerializer.Deserialize<Dictionary<string, string?>>(audit.DetailsJson)!;
        Assert.Equal(reverified.VerificationId.ToString("D"), details["verificationId"]);
        Assert.Equal(reverified.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), details["verificationRevision"]);
        Assert.Equal(reverified.SnapshotHash, details["verificationSnapshotHash"]);
    }

    private static WebApplicationFactory<Program> ConfigureGateway(
        WebApplicationFactory<Program> factory,
        RecordingWorkstation gateway,
        bool introduceVersionDrift = false,
        TimeProvider? timeProvider = null) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISampleWorkstationReader>(); services.RemoveAll<ISampleWorkstationCommands>();
            services.RemoveAll<ISampleWorkstationTemplateReader>(); services.RemoveAll<ISampleWorkstationTaskImporter>();
            services.RemoveAll<ISampleWorkstationBarcodeCommands>();
            services.AddSingleton<ISampleWorkstationReader>(gateway); services.AddSingleton<ISampleWorkstationCommands>(gateway);
            services.AddSingleton<ISampleWorkstationTemplateReader>(gateway); services.AddSingleton<ISampleWorkstationTaskImporter>(gateway);
            services.AddSingleton<ISampleWorkstationBarcodeCommands>(gateway);
            if (timeProvider is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(timeProvider);
            }
            if (introduceVersionDrift)
            {
                services.RemoveAll<IExperimentSampleVerificationService>();
                services.AddScoped<IExperimentSampleVerificationService>(serviceProvider => new VersionDriftVerificationService(
                    serviceProvider.GetRequiredService<ExperimentSampleVerificationService>(),
                    serviceProvider.GetRequiredService<MesDbContext>()));
            }
        }));

    private static ProfileConfiguration PhysicalProfile() => ProfileConfiguration.Default with
    {
        Features = ProfileConfiguration.Default.Features with { UseSimulator = false },
        WorkflowDevices = ProfileConfiguration.Default.WorkflowDevices.Concat([new WorkflowDeviceProfile
        { DeviceId = "SAMPLE-WORKSTATION-01", DeviceFamily = WorkflowDeviceFamilyIds.SampleWorkstation, Enabled = true, ControlEnabled = true, CapabilityIds = [WorkflowCapabilityIds.SampleWorkstationStartExistingTask] }]).ToArray()
    };

    private static async Task<WorkflowVersion> PublishWorkflowAsync(HttpClient client)
    {
        var create = await client.PostAsJsonAsync("/api/workflows?actor=test", WorkstationWorkflow()); create.EnsureSuccessStatusCode();
        var draft = (await create.Content.ReadFromJsonAsync<WorkflowVersion>())!;
        (await client.PostAsync($"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/validate", null)).EnsureSuccessStatusCode();
        var publish = await client.PostAsync($"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/publish?actor=test", null); publish.EnsureSuccessStatusCode();
        return (await publish.Content.ReadFromJsonAsync<WorkflowVersion>())!;
    }

    private static async Task<ExperimentPlan> CreatePublishedPlanAsync(HttpClient client, WorkflowVersion workflow)
    {
        var response = await client.PostAsJsonAsync("/api/experiment-plans", new SaveExperimentPlanDraftRequest { RequestId = Guid.NewGuid(), Actor = "test", Reason = "Create", Draft = new ExperimentPlanDraft { Name = "Sample workstation plan", WorkflowId = workflow.WorkflowId, WorkflowVersion = workflow.Version, ProfileProductId = "MES-AGV", ProfileVersion = "1.0", ResourceRequirements = [new ExperimentResourceRequirement { ResourceType = ExperimentResourceTypeIds.Workstation, ResourceId = "SAMPLE-WORKSTATION-01" }] } }); response.EnsureSuccessStatusCode();
        var draft = (await response.Content.ReadFromJsonAsync<ExperimentPlan>())!;
        (await client.PostAsJsonAsync($"/api/experiment-plans/{draft.PlanId}/versions/{draft.Version}/validate", Action("Validate"))).EnsureSuccessStatusCode();
        var publish = await client.PostAsJsonAsync($"/api/experiment-plans/{draft.PlanId}/versions/{draft.Version}/publish", Action("Publish")); publish.EnsureSuccessStatusCode();
        return (await publish.Content.ReadFromJsonAsync<ExperimentPlan>())!;
    }

    private static async Task<(Guid JobId, Guid ScheduleId)> CreateScheduledJobAsync(HttpClient client, ExperimentPlan plan)
    {
        var create = await client.PostAsJsonAsync("/api/experiment-jobs", new CreateExperimentJobRequest { RequestId = Guid.NewGuid(), Actor = "test", Reason = "Create", PlanId = plan.PlanId, PlanVersion = plan.Version, SampleBatchId = "TEST-001" }); create.EnsureSuccessStatusCode();
        var job = (await create.Content.ReadFromJsonAsync<ExperimentJob>())!;
        var start = new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);
        var schedule = await client.PutAsJsonAsync($"/api/experiment-jobs/{job.JobId}/schedule", new ScheduleExperimentJobRequest { RequestId = Guid.NewGuid(), Actor = "test", Reason = "Schedule", PlannedStart = start, PlannedEnd = start.AddHours(1), Resources = [new ExperimentResourceReference { ResourceType = ExperimentResourceTypeIds.Workstation, ResourceId = "SAMPLE-WORKSTATION-01" }] }); schedule.EnsureSuccessStatusCode();
        return (job.JobId, (await schedule.Content.ReadFromJsonAsync<ScheduleEntry>())!.ScheduleEntryId);
    }

    private static async Task<ExperimentSampleVerification> VerifyCurrentSampleAsync(HttpClient client, Guid jobId, string batchId)
    {
        var sampleId = Guid.NewGuid();
        (await client.PutAsJsonAsync($"/api/experiment-samples/{sampleId}", new SaveExperimentSampleRequest
        {
            RequestId = Guid.NewGuid(), Actor = "test", Reason = "Register workstation sample",
            Sample = new ExperimentSample { SampleId = sampleId, BusinessSampleId = "S-" + sampleId.ToString("N"), BatchId = batchId, Barcode = "BC-" + sampleId.ToString("N"), Status = ExperimentSampleStatus.Active }
        })).EnsureSuccessStatusCode();
        var savedResponse = await client.PutAsJsonAsync($"/api/experiment-jobs/{jobId}/sample-verifications/current", new SaveExperimentSampleVerificationRequest
        {
            RequestId = Guid.NewGuid(), Actor = "test", Reason = "Snapshot workstation sample",
            Rows = [new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = sampleId, SampleBarcode = "BC-" + sampleId.ToString("N"), Position = "A1", Order = 1 }]
        });
        savedResponse.EnsureSuccessStatusCode();
        var saved = (await savedResponse.Content.ReadFromJsonAsync<ExperimentSampleVerification>())!;
        var verifiedResponse = await client.PostAsJsonAsync($"/api/experiment-jobs/{jobId}/sample-verifications/{saved.Revision}/verify", new CompleteExperimentSampleVerificationRequest
        {
            RequestId = Guid.NewGuid(), Actor = "test", Reason = "Verify workstation sample", Revision = saved.Revision, SnapshotHash = saved.SnapshotHash
        });
        verifiedResponse.EnsureSuccessStatusCode();
        return (await verifiedResponse.Content.ReadFromJsonAsync<ExperimentSampleVerification>())!;
    }

    private static async Task<ExperimentWorkstationPreparation> PrepareTwoSourceTaskAsync(HttpClient client, Guid jobId)
    {
        var sample1 = await RegisterSampleAsync(client, "TEST-001", "P1-" + Guid.NewGuid().ToString("N"), "B1-" + Guid.NewGuid().ToString("N"));
        var sample2 = await RegisterSampleAsync(client, "TEST-001", "P2-" + Guid.NewGuid().ToString("N"), "B2-" + Guid.NewGuid().ToString("N"));
        var saved = await SaveRowsAsync(client, jobId,
        [
            new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = sample1.SampleId, SampleBarcode = sample1.Barcode, Position = "A1", Order = 1 },
            new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = sample2.SampleId, SampleBarcode = sample2.Barcode, Position = "A2", Order = 2 }
        ], "Snapshot two sources");
        var verified = await VerifyAsync(client, jobId, saved, "test", "Verify two sources");
        var response = await client.PostAsJsonAsync($"/api/experiment-jobs/{jobId}/workstation-preparations/prepare",
            new PrepareExperimentWorkstationTaskRequest
            {
                RequestId = Guid.NewGuid(), Actor = "test", Reason = "Prepare two-source task",
                DeviceId = "SAMPLE-WORKSTATION-01", SourceTaskNo = "TEST-001",
                VerificationRevision = verified.Revision, VerificationSnapshotHash = verified.SnapshotHash,
                BottleBindings =
                [
                    new PrepareWorkstationBottleBinding { BottleNumber = 1, SampleId = sample1.SampleId, TemplateSource = new WorkstationTemplateSourceKey { Module = "SOURCE-A", X = 1, Y = 1 } },
                    new PrepareWorkstationBottleBinding { BottleNumber = 2, SampleId = sample2.SampleId, TemplateSource = new WorkstationTemplateSourceKey { Module = "SOURCE-B", X = 2, Y = 1 } }
                ]
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExperimentWorkstationPreparation>())!;
    }

    private static async Task<ExperimentSample> RegisterSampleAsync(HttpClient client, string batchId, string businessSampleId, string barcode)
    {
        var sampleId = Guid.NewGuid();
        var response = await client.PutAsJsonAsync($"/api/experiment-samples/{sampleId}", new SaveExperimentSampleRequest
        {
            RequestId = Guid.NewGuid(), Actor = "sample-operator", Reason = "Register two-row chain sample",
            Sample = new ExperimentSample { SampleId = sampleId, BusinessSampleId = businessSampleId, BatchId = batchId, Barcode = barcode, Status = ExperimentSampleStatus.Active }
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExperimentSample>())!;
    }

    private static async Task<ExperimentSampleVerification> SaveRowsAsync(HttpClient client, Guid jobId, IReadOnlyList<ExperimentSampleTaskRow> rows, string reason)
    {
        var response = await client.PutAsJsonAsync($"/api/experiment-jobs/{jobId}/sample-verifications/current", new SaveExperimentSampleVerificationRequest
        {
            RequestId = Guid.NewGuid(), Actor = "sample-operator", Reason = reason, Rows = rows
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExperimentSampleVerification>())!;
    }

    private static async Task<ExperimentSampleVerification> VerifyAsync(HttpClient client, Guid jobId, ExperimentSampleVerification verification, string actor, string reason)
    {
        var response = await client.PostAsJsonAsync($"/api/experiment-jobs/{jobId}/sample-verifications/{verification.Revision}/verify", new CompleteExperimentSampleVerificationRequest
        {
            RequestId = Guid.NewGuid(), Actor = actor, Reason = reason, Revision = verification.Revision, SnapshotHash = verification.SnapshotHash
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExperimentSampleVerification>())!;
    }

    private static async Task AssertNoRuntimeSideEffectsAsync(WebApplicationFactory<Program> factory, RecordingWorkstation gateway)
    {
        Assert.Equal(0, gateway.StartCalls);
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Empty(await database.WorkflowExecutions.ToListAsync());
        Assert.Empty(await database.WorkflowResourceLeases.ToListAsync());
        Assert.Empty(await database.WorkflowDeviceOperations.ToListAsync());
    }

    private static ExperimentSchedulingActionRequest Action(string reason) => new() { RequestId = Guid.NewGuid(), Actor = "test", Reason = reason };

    private static WorkflowDefinition WorkstationWorkflow()
    {
        var catalog = BuiltInWorkflowCatalog.Create();
        WorkflowNode Node(string type, int order, IReadOnlyDictionary<string, string?>? config = null) { var d = catalog.NodeTypes.GetLatest(type)!; return new WorkflowNode { Id = Guid.NewGuid(), Type = WorkflowGraphNodeTypeIds.ToContractType(type), NodeTypeId = type, SchemaVersion = d.SchemaVersion, Name = type, Order = order, Ports = d.Ports, Configuration = config ?? new Dictionary<string, string?>() }; }
        var start = Node(WorkflowGraphNodeTypeIds.Start, 1); var station = Node(WorkflowGraphNodeTypeIds.SampleWorkstationExecuteExistingTask, 2, new Dictionary<string, string?> { [WorkflowNodeConfigurationKeys.DeviceId] = "SAMPLE-WORKSTATION-01", [WorkflowNodeConfigurationKeys.TaskNo] = "TEST-001" }); var end = Node(WorkflowGraphNodeTypeIds.End, 3);
        start = start with { NextNodeIds = [station.Id] }; station = station with { NextNodeIds = [end.Id] };
        return new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Sample workstation", SchemaVersion = WorkflowGraphDocument.CurrentSchemaVersion, Nodes = [start, station, end], Edges = [Edge(start, station), Edge(station, end)] };
    }
    private static WorkflowEdgeDefinition Edge(WorkflowNode source, WorkflowNode target) => new() { SourceNodeId = source.Id, SourcePort = "success", TargetNodeId = target.Id, TargetPort = "in", Kind = WorkflowEdgeKind.Success };

    private sealed class RecordingWorkstation : ISampleWorkstationReader, ISampleWorkstationCommands,
        ISampleWorkstationTemplateReader, ISampleWorkstationTaskImporter, ISampleWorkstationBarcodeCommands
    {
        private int _observation; public int StartCalls { get; private set; } public int BarcodeStartCalls { get; private set; } public int InitializeCalls { get; private set; }
        public int TemplateReadCalls { get; private set; } public int ImportCalls { get; private set; } public int BarcodeUpdateCalls { get; private set; }
        public string? StartDeviceId { get; private set; } public string? StartTaskNo { get; private set; }
        public SampleWorkstationTaskBarcodes? StartBarcodes { get; private set; }
        public bool FailBarcodeUpdate { get; init; }
        public List<ConsumedObservation> ConsumedObservations { get; } = [];
        private (SampleWorkstationDeviceState State, int Raw, int Error, SampleWorkstationTaskState Task) Current => _observation++ switch { 0 => (SampleWorkstationDeviceState.Idle, 0, 0, SampleWorkstationTaskState.Completed), 1 => (SampleWorkstationDeviceState.Running, 1, 3, SampleWorkstationTaskState.Running), _ => (SampleWorkstationDeviceState.Idle, 0, 0, SampleWorkstationTaskState.Completed) };
        private (SampleWorkstationDeviceState State, int Raw, int Error, SampleWorkstationTaskState Task)? _snapshot;
        private (SampleWorkstationDeviceState State, int Raw, int Error, SampleWorkstationTaskState Task) Snapshot => _snapshot ??= Current;
        public Task<SampleWorkstationStatusResponse> GetStatusAsync(string id, CancellationToken ct) { _snapshot = Current; return Task.FromResult(new SampleWorkstationStatusResponse(id, "EQ-01", true, Snapshot.State, Snapshot.Raw, DateTimeOffset.UtcNow)); }
        public Task<SampleWorkstationErrorResponse> GetErrorsAsync(string id, CancellationToken ct) => Task.FromResult(new SampleWorkstationErrorResponse(id, Snapshot.Error, "result", true, DateTimeOffset.UtcNow));
        public Task<SampleWorkstationTaskStateResponse> GetTaskStateAsync(string id, string task, CancellationToken ct)
        {
            ConsumedObservations.Add(new ConsumedObservation(id, "EQ-01", Snapshot.State, Snapshot.Raw, Snapshot.Error, task, Snapshot.Task, Snapshot.Task.ToString()));
            return Task.FromResult(new SampleWorkstationTaskStateResponse(id, task, Snapshot.Task, Snapshot.Task.ToString(), DateTimeOffset.UtcNow));
        }
        public Task<SampleWorkstationCommandResponse> InitializeAsync(string id, CancellationToken ct) { InitializeCalls++; throw new InvalidOperationException(); }
        public Task<SampleWorkstationCommandResponse> StartTaskAsync(string id, string task, CancellationToken ct) { StartCalls++; StartDeviceId = id; StartTaskNo = task; using var json = JsonDocument.Parse("null"); return Task.FromResult(new SampleWorkstationCommandResponse(id, SampleWorkstationCommandOperation.StartTask, 0, json.RootElement.Clone(), DateTimeOffset.UtcNow) { TaskNo = task, Acknowledged = true }); }
        public Task<SampleWorkstationCommandResponse> StartTaskAsync(string id, string task, SampleWorkstationTaskBarcodes barcodes, CancellationToken ct) { BarcodeStartCalls++; StartDeviceId = id; StartTaskNo = task; StartBarcodes = barcodes; return Task.FromResult(Command(id, task, SampleWorkstationCommandOperation.StartTask)); }
        public Task<SampleWorkstationCommandResponse> UpdateTaskBarcodesAsync(string id, string task, SampleWorkstationTaskBarcodes barcodes, CancellationToken ct) { BarcodeUpdateCalls++; return FailBarcodeUpdate ? throw new SampleWorkstationGatewayException(504, SampleWorkstationErrorCodes.Timeout, "Uncertain barcode update", true) : Task.FromResult(Command(id, task, SampleWorkstationCommandOperation.UpdateTaskBarcodes)); }
        public Task<SampleWorkstationTemplateResponse> GetTaskTemplateAsync(string id, string task, CancellationToken ct)
        {
            TemplateReadCalls++;
            var template = Template(task);
            var bytes = SampleWorkstationTemplateFile.Write(template);
            return Task.FromResult(new SampleWorkstationTemplateResponse(id, task + ".xlsx", bytes, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), template, DateTimeOffset.UtcNow));
        }
        public async Task<SampleWorkstationTaskImportResponse> ImportTasksAsync(string id, string fileName, Stream content, CancellationToken ct)
        {
            ImportCalls++;
            var bytes = await SampleWorkstationTemplateFile.ReadBytesAsync(content, ct);
            var template = SampleWorkstationTemplateFile.Read(bytes);
            return new SampleWorkstationTaskImportResponse(id, [template.TaskNo], DateTimeOffset.UtcNow)
            { ReadbackVerified = true, FileSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), Template = template };
        }
        private static SampleWorkstationTaskTemplate Template(string taskNo) => new(taskNo, "Prepared task",
        [
            new("L1", "TIP", 1, 1, "SOURCE-A", 1, 1, "TARGET", 1, 1, 100),
            new("L2", "TIP", 1, 2, "SOURCE-A", 1, 1, "TARGET", 1, 2, 100),
            new("L3", "TIP", 1, 3, "SOURCE-B", 2, 1, "TARGET", 1, 3, 100)
        ]);
        private static SampleWorkstationCommandResponse Command(string id, string task, SampleWorkstationCommandOperation operation) { using var json = JsonDocument.Parse("null"); return new SampleWorkstationCommandResponse(id, operation, 0, json.RootElement.Clone(), DateTimeOffset.UtcNow) { TaskNo = task, Acknowledged = true }; }
        public Task<IReadOnlyList<SampleWorkstationTaskSummaryResponse>> GetTasksAsync(string id, SampleWorkstationTaskQuery query, CancellationToken ct) => throw new NotSupportedException(); public Task<SampleWorkstationTaskDetailsResponse> GetTaskDetailsAsync(string id, string task, CancellationToken ct) => throw new NotSupportedException(); public Task<SampleWorkstationProtocolResponse> GetProtocolReadAsync(string id, SampleWorkstationProtocolOperation op, SampleWorkstationProtocolReadQuery query, CancellationToken ct) => throw new NotSupportedException();

        public sealed record ConsumedObservation(
            string DeviceId,
            string EquipmentId,
            SampleWorkstationDeviceState DeviceState,
            int RawDeviceState,
            int ErrorCode,
            string TaskNo,
            SampleWorkstationTaskState TaskState,
            string RawTaskState);
    }

    private sealed class VersionDriftVerificationService(
        ExperimentSampleVerificationService inner,
        MesDbContext database) : IExperimentSampleVerificationService
    {
        public Task<IReadOnlyList<ExperimentSample>> ListSamplesAsync(QueryExperimentSamplesRequest request, CancellationToken cancellationToken) => inner.ListSamplesAsync(request, cancellationToken);
        public Task<ExperimentSample> SaveSampleAsync(Guid sampleId, SaveExperimentSampleRequest request, CancellationToken cancellationToken) => inner.SaveSampleAsync(sampleId, request, cancellationToken);
        public async Task<ExperimentSampleVerification?> GetCurrentAsync(Guid experimentJobId, CancellationToken cancellationToken)
        {
            var current = await inner.GetCurrentAsync(experimentJobId, cancellationToken);
            if (current is null) return null;
            database.ExperimentSampleVerifications.Add(new ExperimentSampleVerificationRecord
            {
                VerificationId = Guid.NewGuid(), ExperimentJobId = current.ExperimentJobId, Revision = current.Revision + 1,
                Status = ExperimentSampleVerificationStatus.ReadyForVerification.ToString(), RowsJson = JsonSerializer.Serialize(current.Rows),
                SnapshotHash = "DRIFTED-" + current.SnapshotHash, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
            });
            await database.SaveChangesAsync(cancellationToken);
            return current;
        }
        public Task<ExperimentSampleVerification> SaveCurrentAsync(Guid experimentJobId, SaveExperimentSampleVerificationRequest request, CancellationToken cancellationToken) => inner.SaveCurrentAsync(experimentJobId, request, cancellationToken);
        public Task<ExperimentSampleVerification> VerifyAsync(Guid experimentJobId, int revision, CompleteExperimentSampleVerificationRequest request, CancellationToken cancellationToken) => inner.VerifyAsync(experimentJobId, revision, request, cancellationToken);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class PhysicalMesWebApplicationFactory(ProfileConfiguration profile) : WebApplicationFactory<Program>
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"mes-control-agv-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<DbContextOptions<MesDbContext>>(); services.RemoveAll<MesDbContext>(); services.RemoveAll<IAgvGateway>();
                services.AddDbContext<MesDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
                services.AddSingleton<IAgvGateway, TestAdapterClient>();
            });
        }
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration =>
            {
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
                configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Profile = profile }, options))));
            });
            return base.CreateHost(builder);
        }
    }
}
