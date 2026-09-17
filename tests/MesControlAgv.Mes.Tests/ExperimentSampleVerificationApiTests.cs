using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Application;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Mes.Tests;

public sealed class ExperimentSampleVerificationApiTests : IClassFixture<MesWebApplicationFactory>
{
    private readonly MesWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ExperimentSampleVerificationApiTests(MesWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Registered_rows_are_snapshotted_verified_and_replayed()
    {
        var jobId = await AddJobAsync("B-42");
        var sampleId = Guid.NewGuid();
        var registration = new SaveExperimentSampleRequest
        {
            RequestId = Guid.NewGuid(), Actor = "operator", Reason = "Register bottle",
            Sample = new ExperimentSample { SampleId = sampleId, BusinessSampleId = "S-42", BatchId = "B-42", Barcode = " BC-42 ", DisplayName = "Bottle 42", Status = ExperimentSampleStatus.Active }
        };
        var register = await _client.PutAsJsonAsync($"/api/experiment-samples/{sampleId}", registration);
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync($"/api/experiment-samples/{sampleId}", registration)).StatusCode);

        var save = new SaveExperimentSampleVerificationRequest
        {
            RequestId = Guid.NewGuid(), Actor = "operator", Reason = "Prepare task rows",
            Rows = [new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = sampleId, SampleBarcode = "BC-42", Position = "A1", DisplayName = "Bottle 42", Order = 1 }]
        };
        var savedResponse = await _client.PutAsJsonAsync($"/api/experiment-jobs/{jobId}/sample-verifications/current", save);
        Assert.Equal(HttpStatusCode.OK, savedResponse.StatusCode);
        var saved = (await savedResponse.Content.ReadFromJsonAsync<ExperimentSampleVerification>())!;
        Assert.Equal(ExperimentSampleVerificationStatus.ReadyForVerification, saved.Status);
        Assert.Equal(1, saved.Revision);

        var verifyRequest = new CompleteExperimentSampleVerificationRequest { RequestId = Guid.NewGuid(), Actor = "operator", Reason = "Labels visually checked", Revision = saved.Revision, SnapshotHash = saved.SnapshotHash, VerificationNote = "clear" };
        var verifiedResponse = await _client.PostAsJsonAsync($"/api/experiment-jobs/{jobId}/sample-verifications/{saved.Revision}/verify", verifyRequest);
        Assert.Equal(HttpStatusCode.OK, verifiedResponse.StatusCode);
        var verified = (await verifiedResponse.Content.ReadFromJsonAsync<ExperimentSampleVerification>())!;
        Assert.Equal(ExperimentSampleVerificationStatus.Verified, verified.Status);
        Assert.Equal("operator", verified.VerifiedBy);
        Assert.NotNull(verified.VerifiedAt);
        var replay = (await (await _client.PostAsJsonAsync($"/api/experiment-jobs/{jobId}/sample-verifications/{saved.Revision}/verify", verifyRequest)).Content.ReadFromJsonAsync<ExperimentSampleVerification>())!;
        Assert.Equal(verified.VerificationId, replay.VerificationId);
        Assert.Equal(verified.VerifiedAt, replay.VerifiedAt);
    }

    [Fact]
    public async Task Identical_snapshot_reuses_revision_and_changed_rows_invalidate_verified_history()
    {
        var jobId = await AddJobAsync("B-REV");
        var first = await RegisterAsync("B-REV", "REV-1");
        var second = await RegisterAsync("B-REV", "REV-2");
        var row = new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = first.SampleId, SampleBarcode = "REV-1", Position = "A1", Order = 1 };
        var saved = await SaveRowsAsync(jobId, [row]);
        var same = await SaveRowsAsync(jobId, [row]);
        Assert.Equal(saved.Revision, same.Revision);
        var verified = await VerifyAsync(jobId, saved);
        Assert.Equal(ExperimentSampleVerificationStatus.Verified, verified.Status);

        var changed = await SaveRowsAsync(jobId, [row with { SampleId = second.SampleId, SampleBarcode = "REV-2", Position = "A2" }]);
        Assert.Equal(2, changed.Revision);
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Equal(ExperimentSampleVerificationStatus.Invalidated.ToString(), (await database.ExperimentSampleVerifications.SingleAsync(item => item.VerificationId == verified.VerificationId)).Status);
    }

    [Fact]
    public async Task Invalid_rows_stay_draft_and_version_drift_or_admitted_jobs_are_rejected()
    {
        var jobId = await AddJobAsync("B-CONFLICT");
        var sample = await RegisterAsync("B-CONFLICT", "CONFLICT-1");
        var draft = await SaveRowsAsync(jobId, [new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = sample.SampleId, SampleBarcode = "wrong", Position = "A1", Order = 1 }]);
        Assert.Equal(ExperimentSampleVerificationStatus.Draft, draft.Status);
        Assert.Contains(draft.ValidationIssues, issue => issue.Code == "EXP-SAMPLE-BARCODE-MISMATCH" && issue.Order == 1);
        var readDraft = (await (await _client.GetAsync($"/api/experiment-jobs/{jobId}/sample-verifications/current")).Content.ReadFromJsonAsync<ExperimentSampleVerification>())!;
        Assert.Contains(readDraft.ValidationIssues, issue => issue.Code == "EXP-SAMPLE-BARCODE-MISMATCH" && issue.RowId == draft.Rows[0].RowId);
        var stale = await _client.PostAsJsonAsync($"/api/experiment-jobs/{jobId}/sample-verifications/{draft.Revision}/verify", new CompleteExperimentSampleVerificationRequest { RequestId = Guid.NewGuid(), Actor = "operator", Reason = "try stale", Revision = draft.Revision, SnapshotHash = "wrong" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            (await database.ExperimentJobs.SingleAsync(item => item.JobId == jobId)).Status = ExperimentJobStatus.Admitted.ToString();
            await database.SaveChangesAsync();
        }
        var rejected = await _client.PutAsJsonAsync($"/api/experiment-jobs/{jobId}/sample-verifications/current", new SaveExperimentSampleVerificationRequest { RequestId = Guid.NewGuid(), Actor = "operator", Reason = "late edit", Rows = draft.Rows });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
    }

    [Fact]
    public async Task Registration_drift_invalidates_verified_snapshot_and_gate_rejects_it()
    {
        var jobId = await AddJobAsync("B-DRIFT");
        var sample = await RegisterAsync("B-DRIFT", "DRIFT-1");
        var saved = await SaveRowsAsync(jobId, [new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = sample.SampleId, SampleBarcode = sample.Barcode, Position = "A1", Order = 1 }]);
        await VerifyAsync(jobId, saved);
        var changed = new SaveExperimentSampleRequest { RequestId = Guid.NewGuid(), Actor = "operator", Reason = "correct label", Sample = sample with { Barcode = "DRIFT-2" } };
        (await _client.PutAsJsonAsync($"/api/experiment-samples/{sample.SampleId}", changed)).EnsureSuccessStatusCode();
        var current = (await (await _client.GetAsync($"/api/experiment-jobs/{jobId}/sample-verifications/current")).Content.ReadFromJsonAsync<ExperimentSampleVerification>())!;
        Assert.Equal(ExperimentSampleVerificationStatus.Invalidated, current.Status);
        using var scope = _factory.Services.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<IExperimentSampleVerificationGate>();
        var exception = await Assert.ThrowsAsync<ExperimentSampleVerificationException>(() => gate.RequireVerifiedCurrentAsync(jobId, saved.Revision, saved.SnapshotHash, CancellationToken.None));
        Assert.Equal(ExperimentSampleVerificationIssueCodes.VerificationInvalidated, exception.Code);
    }

    [Fact]
    public async Task Verification_identity_tracks_business_id_barcode_position_and_order_but_not_display_name()
    {
        async Task<ExperimentSampleVerification> VerifiedAsync(string suffix)
        {
            var job = await AddJobAsync("B-SEM-" + suffix);
            var sample = await RegisterAsync("B-SEM-" + suffix, "SEM-" + suffix);
            var saved = await SaveRowsAsync(job, [new ExperimentSampleTaskRow
            {
                RowId = Guid.NewGuid(), SampleId = sample.SampleId, BusinessSampleId = sample.BusinessSampleId,
                SampleBarcode = sample.Barcode, Position = "A1", DisplayName = "first", Order = 1
            }]);
            return await VerifyAsync(job, saved);
        }

        var display = await VerifiedAsync("DISPLAY");
        var displayReplay = await SaveRowsAsync(display.ExperimentJobId, [display.Rows[0] with { DisplayName = "renamed only" }]);
        Assert.Equal(display.Revision, displayReplay.Revision);
        Assert.Equal(ExperimentSampleVerificationStatus.Verified, displayReplay.Status);

        foreach (var field in new[] { "business", "barcode", "position", "order" })
        {
            var verified = await VerifiedAsync(field);
            var row = verified.Rows[0];
            var changed = field switch
            {
                "business" => row with { BusinessSampleId = row.BusinessSampleId + "-changed" },
                "barcode" => row with { SampleBarcode = row.SampleBarcode + "-changed" },
                "position" => row with { Position = "B1" },
                _ => row with { Order = 2 }
            };
            var next = await SaveRowsAsync(verified.ExperimentJobId, [changed]);
            Assert.Equal(verified.Revision + 1, next.Revision);
            using var scope = _factory.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            Assert.Equal(ExperimentSampleVerificationStatus.Invalidated.ToString(),
                (await database.ExperimentSampleVerifications.SingleAsync(item => item.VerificationId == verified.VerificationId)).Status);
        }
    }

    [Fact]
    public async Task Empty_unknown_cross_batch_disabled_and_duplicate_rows_remain_draft()
    {
        var jobId = await AddJobAsync("B-VALIDATE");
        var otherBatch = await RegisterAsync("B-OTHER", "OTHER-1");
        var disabledId = Guid.NewGuid();
        var disabled = new SaveExperimentSampleRequest { RequestId = Guid.NewGuid(), Actor = "operator", Reason = "register disabled", Sample = new ExperimentSample { SampleId = disabledId, BusinessSampleId = "DISABLED-" + disabledId.ToString("N"), BatchId = "B-VALIDATE", Barcode = "DISABLED-1", Status = ExperimentSampleStatus.Disabled } };
        (await _client.PutAsJsonAsync($"/api/experiment-samples/{disabledId}", disabled)).EnsureSuccessStatusCode();
        var draft = await SaveRowsAsync(jobId,
        [
            new ExperimentSampleTaskRow { RowId = Guid.Empty, SampleId = Guid.NewGuid(), SampleBarcode = "", Position = "P1", Order = 1 },
            new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = otherBatch.SampleId, SampleBarcode = otherBatch.Barcode, Position = "P1", Order = 2 },
            new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = disabledId, SampleBarcode = "DISABLED-1", Position = "P3", Order = 3 }
        ]);
        Assert.Equal(ExperimentSampleVerificationStatus.Draft, draft.Status);

        var duplicate = new SaveExperimentSampleRequest { RequestId = Guid.NewGuid(), Actor = "operator", Reason = "duplicate", Sample = new ExperimentSample { SampleId = Guid.NewGuid(), BusinessSampleId = "DUPLICATE-" + Guid.NewGuid().ToString("N"), BatchId = "B-VALIDATE", Barcode = "OTHER-1", Status = ExperimentSampleStatus.Active } };
        var duplicateResponse = await _client.PutAsJsonAsync($"/api/experiment-samples/{duplicate.Sample.SampleId}", duplicate);
        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);

        var valid = await RegisterAsync("B-VALIDATE", "DUP-ROW-1");
        var duplicateRows = await SaveRowsAsync(jobId,
        [
            new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = valid.SampleId, SampleBarcode = valid.Barcode, Position = "P4", Order = 4 },
            new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = valid.SampleId, SampleBarcode = valid.Barcode, Position = "P5", Order = 5 }
        ]);
        Assert.Equal(ExperimentSampleVerificationStatus.Draft, duplicateRows.Status);
        Assert.Contains(duplicateRows.ValidationIssues, issue => issue.Code == ExperimentSampleVerificationIssueCodes.BarcodeDuplicate && issue.Order == 5);
    }

    [Fact]
    public async Task Request_replay_is_scoped_to_the_route_job_for_save_and_verify()
    {
        var firstJobId = await AddJobAsync("B-REPLAY");
        var secondJobId = await AddJobAsync("B-REPLAY");
        var sample = await RegisterAsync("B-REPLAY", "REPLAY-1");
        var rows = new[] { new ExperimentSampleTaskRow { RowId = Guid.NewGuid(), SampleId = sample.SampleId, SampleBarcode = sample.Barcode, Position = "A1", Order = 1 } };
        var saveRequest = new SaveExperimentSampleVerificationRequest { RequestId = Guid.NewGuid(), Actor = "operator", Reason = "same save", Rows = rows };
        var firstSave = await _client.PutAsJsonAsync($"/api/experiment-jobs/{firstJobId}/sample-verifications/current", saveRequest);
        firstSave.EnsureSuccessStatusCode();
        var crossJobSave = await _client.PutAsJsonAsync($"/api/experiment-jobs/{secondJobId}/sample-verifications/current", saveRequest);
        Assert.Equal(HttpStatusCode.Conflict, crossJobSave.StatusCode);

        var first = (await firstSave.Content.ReadFromJsonAsync<ExperimentSampleVerification>())!;
        var second = await SaveRowsAsync(secondJobId, rows);
        var verifyRequest = new CompleteExperimentSampleVerificationRequest { RequestId = Guid.NewGuid(), Actor = "operator", Reason = "same verify", Revision = first.Revision, SnapshotHash = first.SnapshotHash };
        (await _client.PostAsJsonAsync($"/api/experiment-jobs/{firstJobId}/sample-verifications/{first.Revision}/verify", verifyRequest)).EnsureSuccessStatusCode();
        var crossJobVerify = await _client.PostAsJsonAsync($"/api/experiment-jobs/{secondJobId}/sample-verifications/{second.Revision}/verify", verifyRequest);
        Assert.Equal(HttpStatusCode.Conflict, crossJobVerify.StatusCode);
    }

    private async Task<Guid> AddJobAsync(string batchId)
    {
        var id = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        database.ExperimentJobs.Add(new ExperimentJobRecord { JobId = id, PlanId = Guid.NewGuid(), PlanVersion = 1, WorkflowId = Guid.NewGuid(), WorkflowVersion = 1, SampleBatchId = batchId, Status = ExperimentJobStatus.Ready.ToString(), CreatedBy = "test", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
        await database.SaveChangesAsync();
        return id;
    }

    private async Task<ExperimentSample> RegisterAsync(string batchId, string barcode)
    {
        var id = Guid.NewGuid();
        var response = await _client.PutAsJsonAsync($"/api/experiment-samples/{id}", new SaveExperimentSampleRequest { RequestId = Guid.NewGuid(), Actor = "operator", Reason = "register", Sample = new ExperimentSample { SampleId = id, BusinessSampleId = Guid.NewGuid().ToString("N"), BatchId = batchId, Barcode = barcode, Status = ExperimentSampleStatus.Active } });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExperimentSample>())!;
    }

    private async Task<ExperimentSampleVerification> SaveRowsAsync(Guid jobId, IReadOnlyList<ExperimentSampleTaskRow> rows)
    {
        var response = await _client.PutAsJsonAsync($"/api/experiment-jobs/{jobId}/sample-verifications/current", new SaveExperimentSampleVerificationRequest { RequestId = Guid.NewGuid(), Actor = "operator", Reason = "save rows", Rows = rows });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExperimentSampleVerification>())!;
    }

    private async Task<ExperimentSampleVerification> VerifyAsync(Guid jobId, ExperimentSampleVerification verification)
    {
        var response = await _client.PostAsJsonAsync($"/api/experiment-jobs/{jobId}/sample-verifications/{verification.Revision}/verify", new CompleteExperimentSampleVerificationRequest { RequestId = Guid.NewGuid(), Actor = "operator", Reason = "verify", Revision = verification.Revision, SnapshotHash = verification.SnapshotHash });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExperimentSampleVerification>())!;
    }
}
