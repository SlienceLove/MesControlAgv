using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Mes.Services;
using Microsoft.Extensions.Configuration;

namespace MesControlAgv.Mes.Tests;

public sealed class AiPlanningModelTests
{
    private static IConfiguration Config(string? url = null) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["LabPlanning:OllamaBaseUrl"] = url ?? "http://127.0.0.1:11434/" }).Build();
    private static AiPlanningRequest Request => new() { Message = "去取料，再到分液站" };
    private static object Response(string content, bool done = true, string reason = "stop") =>
        new { done, done_reason = reason, message = new { content } };
    private static string Proposal => JsonSerializer.Serialize(new AiPlanProposal { Name = "草稿", Questions = ["请指定站点"] },
        new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public async Task LocalStructuredResponseAndConversationArePreserved()
    {
        string? body = null;
        using var http = new HttpClient(new Handler(async (r, t) =>
        {
            Assert.EndsWith("/api/chat", r.RequestUri!.AbsoluteUri);
            body = await r.Content!.ReadAsStringAsync(t);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Response(Proposal)) };
        }));
        var result = await new OllamaPlanningModel(http, Config()).ProposeAsync(
            Request with { PreviousMessages = ["批次B001"] }, new(), CancellationToken.None);
        Assert.Equal("草稿", result.Name); Assert.Single(result.Questions);
        using var json = JsonDocument.Parse(body!);
        Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
        Assert.Contains("B001", json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(false, "stop")]
    [InlineData(true, "length")]
    public async Task TruncatedOutputRejected(bool done, string reason)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Response(Proposal, done, reason)) })));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OllamaPlanningModel(http, Config()).ProposeAsync(Request, new(), CancellationToken.None));
    }

    [Fact]
    public async Task NoCloudFallbackAndNoSilentContextTruncation()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new Exception("must not call")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OllamaPlanningModel(http, Config("https://example.com")).ProposeAsync(Request, new(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OllamaPlanningModel(http, Config()).ProposeAsync(Request with { Message = new string('字', 6000) }, new(), CancellationToken.None));
    }

    [Fact]
    public async Task UnsupportedOutputFieldsRejected()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Response("{\"name\":\"x\",\"shell\":\"run\"}")) })));
        await Assert.ThrowsAsync<JsonException>(() =>
            new OllamaPlanningModel(http, Config()).ProposeAsync(Request, new(), CancellationToken.None));
    }

    [Fact]
    public async Task CancellationReleasesSingleFlightGate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            if (++count == 1) { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Response(Proposal)) };
        }));
        var model = new OllamaPlanningModel(http, Config());
        using var cancel = new CancellationTokenSource();
        var pending = model.ProposeAsync(Request, new(), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.ProposeAsync(Request, new(), CancellationToken.None));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("草稿", (await model.ProposeAsync(Request, new(), CancellationToken.None)).Name);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token);
    }
}
