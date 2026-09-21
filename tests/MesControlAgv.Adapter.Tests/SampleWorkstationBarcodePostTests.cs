using System.Net;
using System.Text;
using System.Text.Json;
using MesControlAgv.Adapter.Modules.SampleWorkstation;
using MesControlAgv.Contracts;
using Microsoft.AspNetCore.WebUtilities;

namespace MesControlAgv.Adapter.Tests;

public sealed class SampleWorkstationBarcodePostTests
{
    [Theory]
    [InlineData("更新成功")]
    [InlineData("更新任务信息完成!任务编号:'TASK&/01',1号样品瓶条码:'来源'A&+?#/%001',2号样品瓶条码:'B/002')")]
    public async Task Barcode_post_encodes_query_and_accepts_only_matching_success(string ack)
    {
        using var handler = new Handler(200, ack);
        var result = await Driver(handler).UpdateTaskBarcodesAsync("WS", " TASK&/01 ",
            new() { SampleBarcode1 = " 来源'A&+?#/%001 ", SampleBarcode2 = " B/002 " }, CancellationToken.None);
        Assert.True(result.Acknowledged);
        Assert.Equal(SampleWorkstationCommandOperation.UpdateTaskBarcodes, result.Operation);
        Assert.Equal("TASK&/01", result.TaskNo);
        Assert.Equal(ack, result.Data.GetString());
        Assert.Single(handler.Calls);
        var sent = handler.Calls[0];
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/Service/UpdateTaskBarcode", sent.Uri.AbsolutePath);
        Assert.Empty(sent.Body);
        Assert.Null(sent.ContentType);
        var query = QueryHelpers.ParseQuery(sent.Uri.Query);
        Assert.Equal(3, query.Count);
        Assert.Equal("TASK&/01", query["TaskNo"]);
        Assert.Equal("来源'A&+?#/%001", query["SampleBarcode1"]);
        Assert.Equal("B/002", query["SampleBarcode2"]);
    }

    [Theory]
    [InlineData("更新任务信息完成!任务编号:'OTHER',1号样品瓶条码:'A',2号样品瓶条码:'B')")]
    [InlineData("更新任务信息完成!任务编号:'TASK',1号样品瓶条码:'OTHER',2号样品瓶条码:'B')")]
    [InlineData("更新任务信息完成!任务编号:'TASK',1号样品瓶条码:'A',2号样品瓶条码:'OTHER')")]
    [InlineData("更新任务信息完成!任务编号:'TASK',1号样品瓶条码:'B',2号样品瓶条码:'A')")]
    [InlineData("更新任务信息完成!任务编号:'TASK',1号样品瓶条码:'',2号样品瓶条码:'B')")]
    [InlineData("更新任务信息完成!")]
    [InlineData("更新任务信息完成!任务编号:'TASK',1号样品瓶条码:'A',2号样品瓶条码:'B')更新失败")]
    [InlineData("启动成功")]
    public async Task Incomplete_or_mismatched_echo_is_unknown_and_not_retried(string ack)
    {
        using var handler = new Handler(200, ack);
        var error = await Assert.ThrowsAsync<SampleWorkstationProtocolException>(() => Update(Driver(handler)));
        Assert.Equal(SampleWorkstationErrorCodes.CommandUnconfirmed, error.ErrorCode);
        Assert.Equal(ack, error.VendorData!.Value.GetString());
        Assert.Equal(HttpMethod.Post, Assert.Single(handler.Calls).Method);
    }

    [Fact]
    public async Task Matching_new_echo_succeeds_but_business_rejection_does_not()
    {
        const string ack = "更新任务信息完成!任务编号:'TASK',1号样品瓶条码:'A',2号样品瓶条码:'B')";
        using var good = new Handler(200, ack);
        Assert.True((await Update(Driver(good))).Acknowledged);
        using var rejected = new Handler(201, ack);
        var error = await Assert.ThrowsAsync<SampleWorkstationProtocolException>(() => Update(Driver(rejected)));
        Assert.Equal(SampleWorkstationErrorCodes.CommandRejected, error.ErrorCode);
        Assert.Single(rejected.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Barcode_transport_failure_never_falls_back_or_retries(bool timeout)
    {
        using var handler = new Handler(200, "") { Failure = timeout ? new TaskCanceledException("timeout") : new HttpRequestException("disconnect") };
        await Assert.ThrowsAnyAsync<Exception>(() => Update(Driver(handler)));
        Assert.Equal(HttpMethod.Post, Assert.Single(handler.Calls).Method);
    }

    private static Task<SampleWorkstationCommandResponse> Update(SampleWorkstationDriver driver) =>
        driver.UpdateTaskBarcodesAsync("WS", "TASK", new() { SampleBarcode1 = "A", SampleBarcode2 = "B" }, CancellationToken.None);

    [Fact]
    public async Task Method_rejection_does_not_trigger_get_fallback()
    {
        using var handler = new Handler(200, "更新成功") { Status = HttpStatusCode.MethodNotAllowed };
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Update(Driver(handler)));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, error.StatusCode);
        Assert.Equal(HttpMethod.Post, Assert.Single(handler.Calls).Method);
    }

    private static SampleWorkstationDriver Driver(Handler handler) => new(
        new VendorSampleWorkstationHttpClient(new HttpClient(handler) { BaseAddress = new Uri("http://vendor.invalid/Service/") }),
        new SampleWorkstationOptions { DeviceId = "WS", EquipmentNo = "EQ", Enabled = true, ControlEnabled = true }, TimeProvider.System);

    private sealed class Handler(int code, string data) : HttpMessageHandler
    {
        public Exception? Failure { get; init; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public List<(HttpMethod Method, Uri Uri, byte[] Body, string? ContentType)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Method, request.RequestUri!, request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken), request.Content?.Headers.ContentType?.MediaType));
            if (Failure is not null) throw Failure;
            return new(Status) { Content = new StringContent(JsonSerializer.Serialize(new { Code = code, Data = data }), Encoding.UTF8, "application/octet-stream") };
        }
    }
}
