using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Services;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MesControlAgv.Mes.Endpoints;

namespace MesControlAgv.Mes.Tests;

public sealed class WorkstationBatchImportTests : IDisposable
{
    [Fact]
    public async Task Milliliter_batch_converts_vendor_cells_and_replays_the_original_unit_without_writes()
    {
        var gateway = new Gateway { Unit = SampleWorkstationTemplateVolumeUnit.Milliliters };
        var request = Request();
        var result = await Service(gateway).ImportAsync("WS-01", request, CancellationToken.None);
        Assert.All(result.Items, i => Assert.Equal("Imported", i.Status));
        Assert.Equal(SampleWorkstationTemplateVolumeUnit.Milliliters, result.FileVolumeUnit);
        Assert.Equal(3, gateway.Uploads.Count);
        foreach (var upload in gateway.Uploads)
        {
            Assert.Equal(SampleWorkstationTemplateVolumeUnit.Milliliters, upload.Unit);
            using var stream = new MemoryStream(upload.Bytes);
            using var zip = new System.IO.Compression.ZipArchive(stream);
            using var sheet = zip.GetEntry("xl/worksheets/sheet2.xml")!.Open();
            var xml = System.Xml.Linq.XDocument.Load(sheet);
            System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var cell = xml.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == "K2");
            Assert.Equal("0.05", cell.Value);
            Assert.Equal(50, SampleWorkstationTemplateFile.Read(upload.Bytes, upload.Unit).Transfers.Single().VolumeMicroliters);
        }
        gateway.Unit = SampleWorkstationTemplateVolumeUnit.Microliters;
        var replay = await Service(gateway).ImportAsync("WS-01", request, CancellationToken.None);
        Assert.Equal(result.FileVolumeUnit, replay.FileVolumeUnit);
        Assert.Equal(3, gateway.Writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData((SampleWorkstationTemplateVolumeUnit)99)]
    public async Task Unadvertised_or_invalid_unit_is_rejected_before_import(SampleWorkstationTemplateVolumeUnit? unit)
    {
        var gateway = new Gateway { Unit = unit };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(gateway).ImportAsync("WS-01", Request(), CancellationToken.None));
        Assert.Equal(0, gateway.Writes);
        Assert.Empty(gateway.PageStarts);
    }

    [Fact]
    public async Task Wrong_readback_unit_stops_following_rows_and_replay_does_not_resend()
    {
        var gateway = new Gateway { Unit = SampleWorkstationTemplateVolumeUnit.Milliliters, WrongReadbackUnit = true };
        var request = Request();
        var result = await Service(gateway).ImportAsync("WS-01", request, CancellationToken.None);
        Assert.Equal(new[] { "Unknown", "Pending", "Pending" }, result.Items.Select(i => i.Status));
        await Service(gateway).ImportAsync("WS-01", request, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(gateway).ImportAsync("WS-01", Request(), CancellationToken.None));
        Assert.Equal(1, gateway.Writes);
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mes-batch-test-" + Guid.NewGuid().ToString("N"));
    private WorkstationBatchImportService Service(Gateway gateway) => new(gateway, gateway, gateway,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["WorkstationBatch:JournalDirectory"] = _directory }).Build());
    private static WorkstationBatchRequest Request() => new(Guid.NewGuid(), "tester", [Task("T001"), Task("T002"), Task("T003")]);
    private static SampleWorkstationTaskTemplate Task(string id) => new(id, id, [new("L", "T", 0, 0, "S", 1, 1, "D", 1, 1, 50)]);

    [Fact]
    public async Task Imports_all_tasks_once_and_replays_from_disk_without_vendor_writes()
    {
        var gateway = new Gateway(); var request = Request();
        var result = await Service(gateway).ImportAsync("WS-01", request, CancellationToken.None);
        Assert.All(result.Items, i => Assert.Equal("Imported", i.Status));
        Assert.Equal(3, gateway.Writes);
        var replay = await Service(gateway).ImportAsync("WS-01", request, CancellationToken.None);
        Assert.Equal(result.PayloadHash, replay.PayloadHash); Assert.Equal(3, gateway.Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(gateway).ImportAsync("WS-01", request with { Actor = "other" }, CancellationToken.None));
    }

    [Fact]
    public async Task Batch_endpoint_binds_the_request_and_replays_without_second_import()
    {
        var gateway = new Gateway();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseTestServer();
        builder.Services.AddSampleWorkstationGateway(builder.Configuration);
        builder.Services.AddScoped<WorkstationBatchImportService>(_ => Service(gateway));
        await using var app = builder.Build(); app.MapSampleWorkstationEndpoints(); await app.StartAsync();
        using var client = app.GetTestClient(); var request = Request();
        for (var i = 0; i < 2; i++)
        {
            using var response = await client.PostAsJsonAsync("/api/workstations/WS-01/task-batches/import", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<WorkstationBatchResult>();
            Assert.Equal(request.RequestId, result!.RequestId);
            Assert.All(result.Items, item => Assert.Equal("Imported", item.Status));
        }
        Assert.Equal(3, gateway.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Partial_failure_or_bad_readback_stops_and_blocks_fresh_batches(bool mismatch)
    {
        var gateway = new Gateway { FailOn = 2, Mismatch = mismatch }; var request = Request();
        var result = await Service(gateway).ImportAsync("WS-01", request, CancellationToken.None);
        Assert.Equal(new[] { "Imported", "Unknown", "Pending" }, result.Items.Select(i => i.Status));
        Assert.Equal(2, gateway.Writes);
        await Service(gateway).ImportAsync("WS-01", request, CancellationToken.None);
        Assert.Equal(2, gateway.Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(gateway).ImportAsync("WS-01", Request(), CancellationToken.None));
        Assert.Equal(2, gateway.Writes);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("disabled")]
    [InlineData("busy")]
    public async Task Preflight_rejection_sends_no_import(string mode)
    {
        var gateway = new Gateway { Duplicate = mode == "duplicate", Enabled = mode != "disabled", Busy = mode == "busy" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(gateway).ImportAsync("WS-01", Request(), CancellationToken.None));
        Assert.Equal(0, gateway.Writes);
    }

    [Fact]
    public async Task Interrupted_write_intent_is_returned_as_unknown_without_replay()
    {
        var gateway = new Gateway(); var request = Request();
        var result = await Service(gateway).ImportAsync("WS-01", request, CancellationToken.None);
        var interrupted = result with { Items = result.Items.Select((item, i) => i == 0 ? item with { Status = "Importing" } : item).ToArray() };
        File.WriteAllText(Path.Combine(_directory, request.RequestId.ToString("N") + ".json"), System.Text.Json.JsonSerializer.Serialize(interrupted));
        var replay = await Service(gateway).ImportAsync("WS-01", request, CancellationToken.None);
        Assert.Equal("Unknown", replay.Items[0].Status);
        Assert.Equal(3, gateway.Writes);
    }

    [Fact]
    public async Task Short_vendor_page_does_not_hide_duplicate_on_next_page()
    {
        var gateway = new Gateway { DuplicateOnSecondPage = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(gateway).ImportAsync("WS-01", Request(), CancellationToken.None));
        Assert.Contains(2, gateway.PageStarts);
        Assert.Equal(0, gateway.Writes);
    }

    private sealed class Gateway : ISampleWorkstationReader, ISampleWorkstationCapabilityReader, ISampleWorkstationTaskImporter
    {
        public int Writes; public int FailOn; public bool Mismatch; public bool Duplicate; public bool Busy; public bool Enabled = true;
        public bool DuplicateOnSecondPage; public List<int> PageStarts = [];
        public SampleWorkstationTemplateVolumeUnit? Unit = SampleWorkstationTemplateVolumeUnit.Microliters;
        public bool WrongReadbackUnit;
        public List<(byte[] Bytes, SampleWorkstationTemplateVolumeUnit Unit)> Uploads = [];
        public Task<SampleWorkstationCapabilitiesResponse> GetCapabilitiesAsync(string id, CancellationToken token) =>
            System.Threading.Tasks.Task.FromResult(new SampleWorkstationCapabilitiesResponse(id, true, Enabled, true, [SampleWorkstationCommandOperation.ImportTasks], []) { TemplateVolumeUnit = Unit });
        public Task<SampleWorkstationStatusResponse> GetStatusAsync(string id, CancellationToken token) =>
            System.Threading.Tasks.Task.FromResult(new SampleWorkstationStatusResponse(id, "EQ", true, Busy ? SampleWorkstationDeviceState.Running : SampleWorkstationDeviceState.Idle, 0, DateTimeOffset.UtcNow));
        public Task<IReadOnlyList<SampleWorkstationTaskSummaryResponse>> GetTasksAsync(string id, SampleWorkstationTaskQuery query, CancellationToken token)
        {
            PageStarts.Add(query.StartNo);
            var taskNo = DuplicateOnSecondPage ? query.StartNo switch { 1 => "OTHER", 2 => "T002", _ => null }
                : Duplicate && query.StartNo == 1 ? "T002" : null;
            return System.Threading.Tasks.Task.FromResult<IReadOnlyList<SampleWorkstationTaskSummaryResponse>>(
                taskNo is null ? [] : [new(1, taskNo, taskNo, SampleWorkstationTaskState.Waiting, "0", "", null)]);
        }
        public async Task<SampleWorkstationTaskImportResponse> ImportTasksAsync(string id, string name, Stream content, CancellationToken token,
            SampleWorkstationTemplateVolumeUnit fileVolumeUnit = SampleWorkstationTemplateVolumeUnit.Microliters)
        {
            var bytes = await SampleWorkstationTemplateFile.ReadBytesAsync(content, token);
            Uploads.Add((bytes, fileVolumeUnit));
            var template = SampleWorkstationTemplateFile.Read(bytes, fileVolumeUnit); Writes++;
            if (Writes == FailOn && !Mismatch) throw new HttpRequestException("Upload response lost");
            return new(id, [template.TaskNo], DateTimeOffset.UtcNow) { Template = template, ReadbackVerified = !(Writes == FailOn && Mismatch),
                FileVolumeUnit = WrongReadbackUnit ? SampleWorkstationTemplateVolumeUnit.Microliters : fileVolumeUnit };
        }
        public Task<SampleWorkstationErrorResponse> GetErrorsAsync(string id, CancellationToken token) => throw new NotSupportedException();
        public Task<SampleWorkstationTaskDetailsResponse> GetTaskDetailsAsync(string id, string task, CancellationToken token) => throw new NotSupportedException();
        public Task<SampleWorkstationTaskStateResponse> GetTaskStateAsync(string id, string task, CancellationToken token) => throw new NotSupportedException();
        public Task<SampleWorkstationProtocolResponse> GetProtocolReadAsync(string id, SampleWorkstationProtocolOperation op, SampleWorkstationProtocolReadQuery query, CancellationToken token) => throw new NotSupportedException();
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
