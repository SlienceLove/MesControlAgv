using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Endpoints;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MesControlAgv.Mes.Tests;

public sealed class WorkstationBatchReconciliationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "workstation-review-" + Guid.NewGuid().ToString("N"));
    private readonly Gateway _gateway = new();
    private WorkstationBatchImportService Service() => new(_gateway, _gateway, _gateway,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["WorkstationBatch:JournalDirectory"] = _directory }).Build());
    private string Journal(Guid id) => Path.Combine(_directory, id.ToString("N") + ".json");
    private static SampleWorkstationTaskTemplate Template(string id) => new(id, "test", [new("L", "T", 0, 0, "S", 1, 1, "D", 1, 1, 50)]);
    private WorkstationBatchRequest Seed(params string[] states)
    {
        var request = new WorkstationBatchRequest(Guid.NewGuid(), "original", states.Select((_, i) => Template(i == 0 ? "20260924143809983251" : "WSOTHER" + i)).ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { DeviceId = "WS-01", request.Actor, request.Tasks })));
        var batch = new WorkstationBatchResult(request.RequestId, "WS-01", hash, states.Select((s, i) =>
            new WorkstationBatchItem(request.Tasks[i].TaskNo, "test", s, "original readback error")).ToArray(), DateTimeOffset.UtcNow, request.Actor);
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Journal(request.RequestId), JsonSerializer.SerializeToUtf8Bytes(batch));
        return request;
    }
    private async Task<WorkstationBatchReconcileRequest> Confirmation(Guid id, string? task = null)
    {
        var row = (await Service().GetReviewItemsAsync("WS-01", default)).Single(x => x.RequestId == id && (task is null || x.TaskNo == task));
        return new(Guid.NewGuid(), row.TaskNo, row.PayloadHash, row.JournalSha256, "reviewer", "Checked exact task and all transfers; workbook number rounded", true, true);
    }

    [Fact]
    public async Task Review_list_is_local_and_confirmation_preserves_original_bytes_and_replay()
    {
        var request = Seed("Unknown");
        var original = File.ReadAllBytes(Journal(request.RequestId));
        var confirmation = await Confirmation(request.RequestId);
        Assert.Equal(0, _gateway.Reads);
        var audit = await Service().ReconcileCreatedAsync("WS-01", request.RequestId, confirmation, default);
        Assert.Equal("CreatedWithReadbackException", audit.Outcome);
        Assert.Equal("Unknown", audit.OriginalStatus);
        Assert.Equal(original, File.ReadAllBytes(Journal(request.RequestId)));
        Assert.Equal(0, _gateway.Writes);
        Assert.Equal(3, _gateway.Reads);
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "reconciliations"), "*.json"));
        Assert.NotNull(Assert.Single(await Service().GetReviewItemsAsync("WS-01", default)).Reconciliation);
        var replay = await Service().ImportAsync("WS-01", request, default);
        Assert.Equal("Unknown", Assert.Single(replay.Items).Status);
        Assert.Equal(audit, Assert.Single(replay.Reconciliations!));
        Assert.Equal(3, _gateway.Reads);
        Assert.Equal(0, _gateway.Writes);
    }

    [Fact]
    public async Task Confirmed_item_unblocks_only_new_batches_and_never_resends_original()
    {
        var old = Seed("Unknown");
        var next = new WorkstationBatchRequest(Guid.NewGuid(), "operator", [Template("WSNEW01")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ImportAsync("WS-01", next, default));
        await Service().ReconcileCreatedAsync("WS-01", old.RequestId, await Confirmation(old.RequestId), default);
        var result = await Service().ImportAsync("WS-01", next, default);
        Assert.Equal("Imported", Assert.Single(result.Items).Status);
        Assert.Equal(1, _gateway.Writes);
        await Service().ImportAsync("WS-01", old, default);
        Assert.Equal(1, _gateway.Writes);
    }

    [Fact]
    public async Task Other_unresolved_item_continues_to_block_and_pending_item_cannot_be_confirmed()
    {
        var request = Seed("Unknown", "Importing", "Pending");
        await Service().ReconcileCreatedAsync("WS-01", request.RequestId, await Confirmation(request.RequestId, request.Tasks[0].TaskNo), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ImportAsync("WS-01",
            new(Guid.NewGuid(), "operator", [Template("WSNEW01")]), default));
        var second = await Confirmation(request.RequestId, request.Tasks[1].TaskNo);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ReconcileCreatedAsync("WS-01", request.RequestId,
            second with { TaskNo = request.Tasks[2].TaskNo }, default));
        Assert.Equal(0, _gateway.Writes);
    }

    [Theory]
    [InlineData("no-created")]
    [InlineData("no-transfers")]
    [InlineData("actor")]
    [InlineData("reason")]
    [InlineData("confirmation-id")]
    [InlineData("hash")]
    public async Task Missing_confirmation_or_identity_is_rejected_before_device_reads(string mode)
    {
        var request = Seed("Unknown");
        var c = await Confirmation(request.RequestId);
        c = mode switch {
            "no-created" => c with { ConfirmTaskCreated = false }, "no-transfers" => c with { ConfirmTransfersChecked = false },
            "actor" => c with { Actor = " " }, "reason" => c with { Reason = " " },
            "confirmation-id" => c with { ConfirmationId = Guid.Empty }, _ => c with { ExpectedJournalSha256 = "bad" } };
        await Assert.ThrowsAsync<ArgumentException>(() => Service().ReconcileCreatedAsync("WS-01", request.RequestId, c, default));
        Assert.Equal(0, _gateway.Reads); Assert.Equal(0, _gateway.Writes);
    }

    [Theory]
    [InlineData("device")]
    [InlineData("task")]
    [InlineData("payload")]
    [InlineData("journal")]
    public async Task Cross_identity_and_stale_revision_cannot_unlock(string mode)
    {
        var request = Seed("Unknown");
        var c = await Confirmation(request.RequestId);
        c = mode switch { "task" => c with { TaskNo = "OTHER" }, "payload" => c with { ExpectedPayloadHash = new('A', 64) },
            "journal" => c with { ExpectedJournalSha256 = new('A', 64) }, _ => c };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ReconcileCreatedAsync(mode == "device" ? "WS-02" : "WS-01", request.RequestId, c, default));
        Assert.Equal(0, _gateway.Reads); Assert.Equal(0, _gateway.Writes);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("running")]
    [InlineData("stale")]
    [InlineData("task-number")]
    [InlineData("task-name")]
    [InlineData("task-running")]
    [InlineData("missing")]
    [InlineData("started-during-details")]
    public async Task Device_must_be_idle_with_exact_waiting_task(string mode)
    {
        var request = Seed("Unknown");
        var c = await Confirmation(request.RequestId);
        _gateway.Mode = mode;
        await Assert.ThrowsAnyAsync<Exception>(() => Service().ReconcileCreatedAsync("WS-01", request.RequestId, c, default));
        Assert.False(Directory.Exists(Path.Combine(_directory, "reconciliations")));
        Assert.Equal(0, _gateway.Writes);
    }

    [Fact]
    public async Task Concurrent_same_confirmation_is_idempotent_and_different_content_conflicts()
    {
        var request = Seed("Unknown");
        var c = await Confirmation(request.RequestId);
        var results = await Task.WhenAll(Service().ReconcileCreatedAsync("WS-01", request.RequestId, c, default),
            Service().ReconcileCreatedAsync("WS-01", request.RequestId, c, default));
        Assert.Equal(results[0], results[1]); Assert.Equal(3, _gateway.Reads);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ReconcileCreatedAsync("WS-01", request.RequestId, c with { Reason = "different" }, default));
        var other = Seed("Unknown");
        var otherConfirmation = (await Confirmation(other.RequestId)) with { ConfirmationId = c.ConfirmationId };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ReconcileCreatedAsync("WS-01", other.RequestId, otherConfirmation, default));
        Assert.Equal(3, _gateway.Reads); Assert.Equal(0, _gateway.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Changed_original_or_corrupt_audit_keeps_new_imports_blocked(bool originalChanged)
    {
        var request = Seed("Unknown");
        await Service().ReconcileCreatedAsync("WS-01", request.RequestId, await Confirmation(request.RequestId), default);
        if (originalChanged) File.AppendAllText(Journal(request.RequestId), " ");
        else File.WriteAllText(Directory.GetFiles(Path.Combine(_directory, "reconciliations"), "*.json").Single(), "broken");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ImportAsync("WS-01",
            new(Guid.NewGuid(), "operator", [Template("WSNEW01")]), default));
        Assert.Equal(0, _gateway.Writes);
    }

    [Fact]
    public async Task External_journal_lock_prevents_reconciliation_before_any_device_read()
    {
        var request = Seed("Unknown"); var c = await Confirmation(request.RequestId);
        using var held = new FileStream(Path.Combine(_directory, ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => Service().ReconcileCreatedAsync("WS-01", request.RequestId, c, default));
        Assert.Equal(0, _gateway.Reads); Assert.Equal(0, _gateway.Writes);
    }

    [Theory]
    [InlineData("missing-device")]
    [InlineData("empty-items")]
    [InlineData("duplicate-items")]
    public async Task Malformed_original_record_cannot_be_silently_skipped(string mode)
    {
        var request = Seed("Unknown");
        var batch = JsonSerializer.Deserialize<WorkstationBatchResult>(File.ReadAllBytes(Journal(request.RequestId)))!;
        batch = mode switch { "missing-device" => batch with { DeviceId = "" },
            "empty-items" => batch with { Items = [] }, _ => batch with { Items = [batch.Items[0], batch.Items[0]] } };
        File.WriteAllBytes(Journal(request.RequestId), JsonSerializer.SerializeToUtf8Bytes(batch));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ImportAsync("WS-01", new(Guid.NewGuid(), "operator", [Template("WSNEW01")]), default));
        Assert.Equal(0, _gateway.Reads); Assert.Equal(0, _gateway.Writes);
    }

    [Theory]
    [InlineData("old-state")]
    [InlineData("future-state")]
    [InlineData("future-confirmation")]
    public async Task Invalid_audit_time_relationship_keeps_import_blocked(string mode)
    {
        var request = Seed("Unknown");
        var audit = await Service().ReconcileCreatedAsync("WS-01", request.RequestId, await Confirmation(request.RequestId), default);
        audit = mode switch {
            "old-state" => audit with { DeviceStatus = audit.DeviceStatus with { ObservedAtUtc = audit.ConfirmedAtUtc.AddMinutes(-5) } },
            "future-state" => audit with { DeviceStatus = audit.DeviceStatus with { ObservedAtUtc = audit.ConfirmedAtUtc.AddMinutes(5) } },
            _ => audit with { ConfirmedAtUtc = DateTimeOffset.UtcNow.AddHours(1), DeviceStatus = audit.DeviceStatus with { ObservedAtUtc = DateTimeOffset.UtcNow.AddHours(1) } } };
        File.WriteAllBytes(Directory.GetFiles(Path.Combine(_directory, "reconciliations"), "*.json").Single(), JsonSerializer.SerializeToUtf8Bytes(audit));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ImportAsync("WS-01", new(Guid.NewGuid(), "operator", [Template("WSNEW01")]), default));
        Assert.Equal(0, _gateway.Writes);
    }

    [Fact]
    public async Task Historically_fresh_confirmation_does_not_expire_just_because_it_is_old()
    {
        var request = Seed("Unknown");
        var audit = await Service().ReconcileCreatedAsync("WS-01", request.RequestId, await Confirmation(request.RequestId), default);
        var past = DateTimeOffset.UtcNow.AddYears(-1);
        audit = audit with { ConfirmedAtUtc = past, DeviceStatus = audit.DeviceStatus with { ObservedAtUtc = past.AddSeconds(-1) } };
        File.WriteAllBytes(Directory.GetFiles(Path.Combine(_directory, "reconciliations"), "*.json").Single(), JsonSerializer.SerializeToUtf8Bytes(audit));
        var result = await Service().ImportAsync("WS-01", new(Guid.NewGuid(), "operator", [Template("WSNEW01")]), default);
        Assert.Equal("Imported", Assert.Single(result.Items).Status);
        Assert.Equal(1, _gateway.Writes);
    }

    [Fact]
    public async Task Http_review_and_confirm_return_audited_manual_outcome_without_device_commands()
    {
        var request = Seed("Unknown");
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseTestServer();
        builder.Services.AddSampleWorkstationGateway(builder.Configuration);
        builder.Services.AddScoped<WorkstationBatchImportService>(_ => Service());
        await using var app = builder.Build(); app.MapSampleWorkstationEndpoints(); await app.StartAsync();
        using var client = app.GetTestClient();
        var rows = (await client.GetFromJsonAsync<WorkstationBatchReviewItem[]>("/api/workstations/WS-01/task-batches/reviews"))!;
        Assert.Single(rows);
        var confirmation = await Confirmation(request.RequestId);
        using var missing = await client.PostAsJsonAsync($"/api/workstations/WS-01/task-batches/{Guid.NewGuid()}/reconcile-created", confirmation);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var invalid = await client.PostAsJsonAsync($"/api/workstations/WS-01/task-batches/{request.RequestId}/reconcile-created", confirmation with { ConfirmTransfersChecked = false });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var success = await client.PostAsJsonAsync($"/api/workstations/WS-01/task-batches/{request.RequestId}/reconcile-created", confirmation);
        success.EnsureSuccessStatusCode();
        var audit = (await success.Content.ReadFromJsonAsync<WorkstationBatchReconciliation>())!;
        Assert.Equal(confirmation.ConfirmationId, audit.Confirmation.ConfirmationId);
        Assert.Equal("CreatedWithReadbackException", audit.Outcome);
        Assert.Equal(0, _gateway.Writes);
    }

    private sealed class Gateway : ISampleWorkstationReader, ISampleWorkstationCapabilityReader, ISampleWorkstationTaskImporter
    {
        public int Reads; public int Writes; public string Mode = "";
        public Task<SampleWorkstationStatusResponse> GetStatusAsync(string id, CancellationToken token)
        { Reads++; return Task.FromResult(new SampleWorkstationStatusResponse(id, "EQ", Mode != "offline",
            Mode == "running" ? SampleWorkstationDeviceState.Running : SampleWorkstationDeviceState.Idle, 0,
            DateTimeOffset.UtcNow.AddMinutes(Mode == "stale" ? -5 : 0))); }
        public Task<SampleWorkstationTaskDetailsResponse> GetTaskDetailsAsync(string id, string task, CancellationToken token)
        {
            Reads++;
            if (Mode == "missing") throw new KeyNotFoundException("Not found");
            if (Mode == "started-during-details") Mode = "running";
            return Task.FromResult(new SampleWorkstationTaskDetailsResponse(1, Mode == "task-number" ? "OTHER" : task,
                Mode == "task-name" ? "OTHER" : "test", Mode == "task-running" ? SampleWorkstationTaskState.Running : SampleWorkstationTaskState.Waiting,
                "waiting", null, null, null, "operator", "2026-09-24", null));
        }
        public Task<SampleWorkstationCapabilitiesResponse> GetCapabilitiesAsync(string id, CancellationToken token) => Task.FromResult(
            new SampleWorkstationCapabilitiesResponse(id, true, true, true, [SampleWorkstationCommandOperation.ImportTasks], [])
            { TemplateVolumeUnit = SampleWorkstationTemplateVolumeUnit.Milliliters });
        public Task<IReadOnlyList<SampleWorkstationTaskSummaryResponse>> GetTasksAsync(string id, SampleWorkstationTaskQuery query, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SampleWorkstationTaskSummaryResponse>>([]);
        public async Task<SampleWorkstationTaskImportResponse> ImportTasksAsync(string id, string fileName, Stream content, CancellationToken token,
            SampleWorkstationTemplateVolumeUnit fileVolumeUnit = SampleWorkstationTemplateVolumeUnit.Microliters)
        {
            Writes++;
            var template = SampleWorkstationTemplateFile.Read(await SampleWorkstationTemplateFile.ReadBytesAsync(content, token), fileVolumeUnit);
            return new(id, [template.TaskNo], DateTimeOffset.UtcNow) { Template = template, FileVolumeUnit = fileVolumeUnit, ReadbackVerified = true };
        }
        public Task<SampleWorkstationErrorResponse> GetErrorsAsync(string id, CancellationToken token) => throw new NotSupportedException();
        public Task<SampleWorkstationTaskStateResponse> GetTaskStateAsync(string id, string task, CancellationToken token) => throw new NotSupportedException();
        public Task<SampleWorkstationProtocolResponse> GetProtocolReadAsync(string id, SampleWorkstationProtocolOperation operation, SampleWorkstationProtocolReadQuery query, CancellationToken token) => throw new NotSupportedException();
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
