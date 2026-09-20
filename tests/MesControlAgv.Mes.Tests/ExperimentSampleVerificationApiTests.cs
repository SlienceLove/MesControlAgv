using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        // Existing successful command fingerprints must remain replayable across this upgrade.
        using var scope = _factory.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<MesDbContext>().ExperimentSchedulingAudits.AsNoTracking()
            .SingleAsync(item => item.RequestId == verifyRequest.RequestId);
        var legacyPayload = JsonSerializer.Serialize(new
        {
            Action = "VerifyExperimentSampleVerification", verifyRequest.Actor, verifyRequest.Reason,
            Payload = new { ExperimentJobId = jobId, revision = saved.Revision, expectedHash = saved.SnapshotHash, verifyRequest.VerificationNote }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacyPayload))), audit.RequestFingerprint);
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
    public async Task Business_sample_identifier_registration_drift_invalidates_verified_snapshot_and_gate_rejects_it()
    {
        var jobId = await AddJobAsync("B-BUSINESS-DRIFT");
        var sample = await RegisterAsync("B-BUSINESS-DRIFT", "BUSINESS-1");
        var saved = await SaveRowsAsync(jobId, [new ExperimentSampleTaskRow
        {
            RowId = Guid.NewGuid(), SampleId = sample.SampleId, BusinessSampleId = sample.BusinessSampleId,
            SampleBarcode = sample.Barcode, Position = "A1", Order = 1
        }]);
        await VerifyAsync(jobId, saved);
        var changed = new SaveExperimentSampleRequest
        {
            RequestId = Guid.NewGuid(), Actor = "operator", Reason = "correct business id",
            Sample = sample with { BusinessSampleId = sample.BusinessSampleId + "-CORRECTED" }
        };
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
                "business" => row,
                "barcode" => row with { SampleBarcode = row.SampleBarcode + "-changed" },
                "position" => row with { Position = "B1" },
                _ => row with { Order = 2 }
            };
            if (field == "business")
            {
                var registered = (await _client.GetFromJsonAsync<ExperimentSample[]>("/api/experiment-samples?batchId=B-SEM-business"))!.Single();
                (await _client.PutAsJsonAsync($"/api/experiment-samples/{registered.SampleId}", new SaveExperimentSampleRequest
                {
                    RequestId = Guid.NewGuid(), Actor = "operator", Reason = "correct identity",
                    Sample = registered with { BusinessSampleId = registered.BusinessSampleId + "-changed" }
                })).EnsureSuccessStatusCode();
            }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_freezes_central_business_identity_when_client_omits_or_forges_it(bool forged)
    {
        var batch = Guid.NewGuid().ToString("N");
        var jobId = await AddJobAsync(batch);
        var sample = await RegisterAsync(batch, "BC-" + batch);
        var rowId = Guid.NewGuid();
        var row = new JsonObject { ["rowId"] = rowId, ["sampleId"] = sample.SampleId, ["sampleBarcode"] = sample.Barcode, ["position"] = "A1", ["order"] = 1 };
        if (forged) row["businessSampleId"] = "FORGED";
        var response = await _client.PutAsJsonAsync($"/api/experiment-jobs/{jobId}/sample-verifications/current", new
        {
            requestId = Guid.NewGuid(), actor = "operator", reason = "prepare", rows = new[] { row }
        });
        response.EnsureSuccessStatusCode();
        var saved = (await response.Content.ReadFromJsonAsync<ExperimentSampleVerification>())!;
        Assert.Equal(sample.BusinessSampleId, Assert.Single(saved.Rows).BusinessSampleId);
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        var record = await database.ExperimentSampleVerifications.SingleAsync(item => item.VerificationId == saved.VerificationId);
        Assert.Equal(sample.BusinessSampleId, JsonDocument.Parse(record.RowsJson).RootElement[0].GetProperty("businessSampleId").GetString());
        var same = await SaveRowsAsync(jobId, saved.Rows);
        Assert.Equal(saved.SnapshotHash, same.SnapshotHash);
        await VerifyAsync(jobId, same);
    }

    [Fact]
    public async Task Legacy_missing_business_identity_is_readable_but_requires_a_new_revision_before_verification()
    {
        var batch = Guid.NewGuid().ToString("N");
        var job = await AddJobAsync(batch);
        var sample = await RegisterAsync(batch, "LEGACY-" + batch);
        var saved = await SaveRowsAsync(job, [new() { RowId = Guid.NewGuid(), SampleId = sample.SampleId, SampleBarcode = sample.Barcode, Position = "A1", Order = 1 }]);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            var record = await db.ExperimentSampleVerifications.SingleAsync(item => item.VerificationId == saved.VerificationId);
            var json = JsonNode.Parse(record.RowsJson)!.AsArray();
            json[0]!.AsObject().Remove("businessSampleId");
            // Seed pre-feature JSON as it would exist on disk; normal application writes remain append-only.
            var legacyJson = json.ToJsonString();
            await db.ExperimentSampleVerifications.Where(item => item.VerificationId == saved.VerificationId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.RowsJson, legacyJson));
        }
        var legacy = (await _client.GetFromJsonAsync<ExperimentSampleVerification>($"/api/experiment-jobs/{job}/sample-verifications/current"))!;
        Assert.Empty(legacy.Rows[0].BusinessSampleId);
        Assert.Contains(legacy.ValidationIssues, issue => issue.Code == "EXP-SAMPLE-BUSINESS-ID-MISMATCH");
        var request = new CompleteExperimentSampleVerificationRequest { RequestId = Guid.NewGuid(), Actor = "operator", Reason = "legacy", Revision = legacy.Revision, SnapshotHash = legacy.SnapshotHash };
        var failed = await _client.PostAsJsonAsync($"/api/experiment-jobs/{job}/sample-verifications/{legacy.Revision}/verify", request);
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        var next = await SaveRowsAsync(job, legacy.Rows);
        Assert.Equal(legacy.Revision + 1, next.Revision);
        Assert.Equal(sample.BusinessSampleId, next.Rows[0].BusinessSampleId);
        await VerifyAsync(job, next);
    }

    [Theory]
    [InlineData(ExperimentJobStatus.Admitted)]
    [InlineData(ExperimentJobStatus.Running)]
    [InlineData(ExperimentJobStatus.Completed)]
    [InlineData(ExperimentJobStatus.Failed)]
    [InlineData(ExperimentJobStatus.Cancelled)]
    public async Task Registration_update_for_protected_task_is_rejected_without_any_partial_write(ExperimentJobStatus status)
    {
        var batch = Guid.NewGuid().ToString("N");
        var job = await AddJobAsync(batch);
        var sample = await RegisterAsync(batch, "PROTECTED-" + batch);
        var saved = await SaveRowsAsync(job, [new() { RowId = Guid.NewGuid(), SampleId = sample.SampleId, SampleBarcode = sample.Barcode, Position = "A1", Order = 1 }]);
        await VerifyAsync(job, saved);
        var next = await SaveRowsAsync(job, [saved.Rows[0] with { Position = "A2" }]);
        await VerifyAsync(job, next);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        (await db.ExperimentJobs.SingleAsync(item => item.JobId == job)).Status = status.ToString();
        await db.SaveChangesAsync();
        var beforeSample = JsonSerializer.Serialize(await db.ExperimentSamples.AsNoTracking().SingleAsync(item => item.SampleId == sample.SampleId));
        var beforeHistory = JsonSerializer.Serialize(await db.ExperimentSampleVerifications.AsNoTracking().Where(item => item.ExperimentJobId == job).OrderBy(item => item.Revision).ToArrayAsync());
        var beforeAudits = await db.ExperimentSchedulingAudits.CountAsync(item => item.ExperimentJobId == job);
        var response = await _client.PutAsJsonAsync($"/api/experiment-samples/{sample.SampleId}", new SaveExperimentSampleRequest
        {
            RequestId = Guid.NewGuid(), Actor = "operator", Reason = "too late", Sample = sample with { Barcode = sample.Barcode + "-changed" }
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ExperimentSampleVerificationIssueCodes.VersionConflict, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Equal(beforeSample, JsonSerializer.Serialize(await db.ExperimentSamples.AsNoTracking().SingleAsync(item => item.SampleId == sample.SampleId)));
        Assert.Equal(beforeHistory, JsonSerializer.Serialize(await db.ExperimentSampleVerifications.AsNoTracking().Where(item => item.ExperimentJobId == job).OrderBy(item => item.Revision).ToArrayAsync()));
        Assert.Equal(beforeAudits, await db.ExperimentSchedulingAudits.CountAsync(item => item.ExperimentJobId == job));
    }

    [Theory]
    [InlineData("hash", ExperimentSampleVerificationIssueCodes.VersionConflict)]
    [InlineData("revision", ExperimentSampleVerificationIssueCodes.VersionConflict)]
    [InlineData("route", ExperimentSampleVerificationIssueCodes.VersionConflict)]
    [InlineData("draft", ExperimentSampleVerificationIssueCodes.VerificationRequired)]
    [InlineData("drift", ExperimentSampleVerificationIssueCodes.VerificationInvalidated)]
    public async Task Verification_rejection_is_audited_and_replayed_with_exact_failure(string scenario, string code)
    {
        var batch = Guid.NewGuid().ToString("N");
        var job = await AddJobAsync(batch);
        var sample = await RegisterAsync(batch, "AUDIT-" + batch);
        var saved = await SaveRowsAsync(job, [new() { RowId = Guid.NewGuid(), SampleId = sample.SampleId, SampleBarcode = scenario == "draft" ? "wrong" : sample.Barcode, Position = "A1", Order = 1 }]);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        if (scenario == "drift")
        {
            (await db.ExperimentSamples.SingleAsync(item => item.SampleId == sample.SampleId)).BusinessSampleId += "-drift";
            await db.SaveChangesAsync();
        }
        var routeRevision = scenario == "revision" ? saved.Revision + 1 : saved.Revision;
        var request = new CompleteExperimentSampleVerificationRequest
        {
            RequestId = Guid.NewGuid(), Actor = "audit operator", Reason = "check labels", Revision = scenario == "route" ? 9 : routeRevision,
            SnapshotHash = scenario == "hash" ? "stale-hash" : saved.SnapshotHash
        };
        var path = $"/api/experiment-jobs/{job}/sample-verifications/{routeRevision}/verify";
        var response = await _client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal(new[] { "code", "detail" }, problem.EnumerateObject().Select(item => item.Name).Order().ToArray());
        Assert.Equal(code, problem.GetProperty("code").GetString());
        var audit = await db.ExperimentSchedulingAudits.AsNoTracking().SingleAsync(item => item.RequestId == request.RequestId);
        Assert.Equal(job, audit.ExperimentJobId);
        Assert.Equal("ExperimentSampleVerificationVerified", audit.EventType);
        Assert.Equal("Rejected", audit.Outcome);
        Assert.Equal(code, audit.Code);
        Assert.Equal(request.Actor, audit.Actor);
        Assert.Equal(request.Reason, audit.Reason);
        var details = JsonDocument.Parse(audit.DetailsJson).RootElement;
        Assert.Equal(saved.VerificationId.ToString("D"), details.GetProperty("verificationId").GetString());
        Assert.Equal(saved.Revision.ToString(), details.GetProperty("revision").GetString());
        Assert.Equal(saved.SnapshotHash, details.GetProperty("snapshotHash").GetString());
        Assert.Equal(code, details.GetProperty("code").GetString());
        Assert.Equal(problem.GetProperty("detail").GetString(), details.GetProperty("reason").GetString());
        var result = JsonDocument.Parse(audit.ResultJson).RootElement;
        Assert.Equal(new[] { "code", "message" }, result.EnumerateObject().Select(item => item.Name).Order().ToArray());
        Assert.Equal(code, result.GetProperty("code").GetString());
        Assert.Equal(problem.GetProperty("detail").GetString(), result.GetProperty("message").GetString());
        var count = await db.ExperimentSchedulingAudits.CountAsync(item => item.ExperimentJobId == job);
        var replay = await _client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal(body, await replay.Content.ReadAsStringAsync());
        Assert.Equal(count, await db.ExperimentSchedulingAudits.CountAsync(item => item.ExperimentJobId == job));
        var reused = await _client.PostAsJsonAsync(path, request with { VerificationNote = "different payload" });
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal(ExperimentSampleVerificationIssueCodes.VersionConflict, JsonDocument.Parse(await reused.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Equal(count, await db.ExperimentSchedulingAudits.CountAsync(item => item.ExperimentJobId == job));
        var invalidations = await db.ExperimentSchedulingAudits.Where(item => item.ExperimentJobId == job && item.EventType == "ExperimentSampleVerificationInvalidated").ToArrayAsync();
        Assert.Equal(scenario == "drift" ? 1 : 0, invalidations.Length);
        if (scenario == "drift") Assert.Equal(request.RequestId.ToString("D"), JsonDocument.Parse(invalidations[0].DetailsJson).RootElement.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task Registration_drift_audits_each_affected_task_once_and_replay_does_not_repeat_invalidation()
    {
        var batch = Guid.NewGuid().ToString("N");
        var sample = await RegisterAsync(batch, "MULTI-" + batch);
        var snapshots = new List<ExperimentSampleVerification>();
        for (var i = 0; i < 2; i++)
        {
            var job = await AddJobAsync(batch);
            snapshots.Add(await VerifyAsync(job, await SaveRowsAsync(job, [new() { RowId = Guid.NewGuid(), SampleId = sample.SampleId, SampleBarcode = sample.Barcode, Position = "A1", Order = 1 }])));
        }
        var request = new SaveExperimentSampleRequest { RequestId = Guid.NewGuid(), Actor = "registration operator", Reason = "correct barcode", Sample = sample with { Barcode = sample.Barcode + "-new" } };
        for (var i = 0; i < 2; i++) (await _client.PutAsJsonAsync($"/api/experiment-samples/{sample.SampleId}", request)).EnsureSuccessStatusCode();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        foreach (var snapshot in snapshots)
        {
            var audit = await db.ExperimentSchedulingAudits.SingleAsync(item => item.ExperimentJobId == snapshot.ExperimentJobId && item.EventType == "ExperimentSampleVerificationInvalidated");
            Assert.Equal(request.Actor, audit.Actor);
            Assert.Equal(request.Reason, audit.Reason);
            Assert.Equal(ExperimentSampleVerificationIssueCodes.VerificationInvalidated, audit.Code);
            var details = JsonDocument.Parse(audit.DetailsJson).RootElement;
            Assert.Equal(new[] { "code", "reason", "requestId", "revision", "snapshotHash", "verificationId" }, details.EnumerateObject().Select(item => item.Name).Order().ToArray());
            Assert.Equal(snapshot.VerificationId.ToString("D"), details.GetProperty("verificationId").GetString());
            Assert.Equal(snapshot.Revision.ToString(), details.GetProperty("revision").GetString());
            Assert.Equal(snapshot.SnapshotHash, details.GetProperty("snapshotHash").GetString());
            Assert.Equal(request.RequestId.ToString("D"), details.GetProperty("requestId").GetString());
            Assert.Equal("Invalidated", (await db.ExperimentSampleVerifications.SingleAsync(item => item.VerificationId == snapshot.VerificationId)).Status);
        }
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
