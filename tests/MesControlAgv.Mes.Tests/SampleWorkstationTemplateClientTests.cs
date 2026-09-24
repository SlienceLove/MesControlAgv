using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Services;

namespace MesControlAgv.Mes.Tests;

public sealed class SampleWorkstationTemplateClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Milliliter_file_unit_is_sent_and_requires_matching_ack(bool wrongUnit)
    {
        var template = Example();
        var unit = SampleWorkstationTemplateVolumeUnit.Milliliters;
        var file = SampleWorkstationTemplateFile.Write(template, unit);
        using var handler = new Handler(JsonContent.Create(new SampleWorkstationTaskImportResponse("WS-01", [template.TaskNo], DateTimeOffset.UtcNow)
        {
            FileVolumeUnit = wrongUnit ? SampleWorkstationTemplateVolumeUnit.Microliters : unit,
            ReadbackVerified = true, Template = template, FileSha256 = Convert.ToHexString(SHA256.HashData(file))
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://adapter.invalid/") };
        using var source = new MemoryStream(file);
        var client = new SampleWorkstationAdapterClient(http);
        if (wrongUnit)
            await Assert.ThrowsAsync<SampleWorkstationGatewayException>(() => client.ImportTasksAsync("WS-01", "task.xlsx", source, CancellationToken.None, unit));
        else
            Assert.Equal(50, (await client.ImportTasksAsync("WS-01", "task.xlsx", source, CancellationToken.None, unit)).Template!.Transfers.Single().VolumeMicroliters);
        Assert.Equal("Milliliters", Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(handler.Uri!.Query)["fileVolumeUnit"]);
        Assert.Equal(file, handler.Bytes);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Legacy_payload_serialization_does_not_add_a_default_unit()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var original = System.Text.Json.JsonSerializer.Serialize(new MesControlAgv.Contracts.Experiments.ExperimentWorkstationPreparationPayload(), options);
        Assert.DoesNotContain("fileVolumeUnit", original);
        var restored = System.Text.Json.JsonSerializer.Deserialize<MesControlAgv.Contracts.Experiments.ExperimentWorkstationPreparationPayload>(original, options)!;
        Assert.Equal(SampleWorkstationTemplateVolumeUnit.Microliters, restored.FileVolumeUnit);
        Assert.Equal(original, System.Text.Json.JsonSerializer.Serialize(restored, options));
    }

    private static SampleWorkstationTaskTemplate Example() => new("MES-TASK-001", "中控任务",
        [new("CGRJ-001", "QT-001", 1, 1, "CYC-001-1000", 1, 1, "FYB-001", 1, 1, 50)]);

    [Theory]
    [InlineData("valid")]
    [InlineData("unverified")]
    [InlineData("wrong-task")]
    [InlineData("wrong-transfers")]
    [InlineData("wrong-hash")]
    [InlineData("missing-transfers")]
    public async Task Import_sends_raw_xlsx_to_adapter_and_requires_matching_verified_response(string mode)
    {
        var template = Example();
        var file = SampleWorkstationTemplateFile.Write(template);
        var ack = new SampleWorkstationTaskImportResponse("WS-01", [template.TaskNo], DateTimeOffset.UtcNow)
        {
            ReadbackVerified = mode != "unverified", FileSha256 = mode == "wrong-hash" ? "other" : Convert.ToHexString(SHA256.HashData(file)),
            Template = mode switch
            {
                "wrong-task" => template with { TaskNo = "other" },
                "wrong-transfers" => template with { Transfers = [template.Transfers[0] with { TargetX = 2 }] },
                "missing-transfers" => template with { Transfers = null! },
                _ => template
            }
        };
        using var handler = new Handler(JsonContent.Create(ack));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://adapter.invalid/") };
        var client = new SampleWorkstationAdapterClient(http);
        using var source = new MemoryStream(file);
        if (mode == "valid")
        {
            var result = await client.ImportTasksAsync("WS-01", "任务&1.xlsx", source, CancellationToken.None);
            Assert.True(result.ReadbackVerified);
        }
        else
        {
            var error = await Assert.ThrowsAsync<SampleWorkstationGatewayException>(() => client.ImportTasksAsync("WS-01", "任务&1.xlsx", source, CancellationToken.None));
            Assert.Equal(SampleWorkstationErrorCodes.TemplateMismatch, error.ErrorCode);
            Assert.True(error.OutcomeUnknown);
        }
        Assert.True(source.CanRead);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/api/workstations/WS-01/tasks/import", handler.Uri!.AbsolutePath);
        Assert.Equal("任务&1.xlsx", Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(handler.Uri.Query)["fileName"]);
        Assert.Equal(file, handler.Bytes);
        Assert.Equal("application/octet-stream", handler.ContentType);
    }

    [Fact]
    public async Task Local_invalid_file_does_not_contact_adapter()
    {
        using var handler = new Handler(JsonContent.Create(new { }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://adapter.invalid/") };
        using var source = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<ArgumentException>(() => new SampleWorkstationAdapterClient(http).ImportTasksAsync("WS-01", "task.xlsx", source, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    private sealed class Handler(HttpContent response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public byte[]? Bytes { get; private set; }
        public string? ContentType { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Uri = request.RequestUri; Method = request.Method;
            Bytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            return new(HttpStatusCode.OK) { Content = response };
        }
    }
}
