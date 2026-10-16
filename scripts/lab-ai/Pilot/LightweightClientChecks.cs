using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Contracts.Experiments;

internal static class LightweightClientChecks
{
    public static async Task RunAsync()
    {
        var catalog = new AiPlanningCatalog { Templates = [new() { Key = "T1", Description = "运送到分液位" }] };
        var request = new AiPlanningRequest { Message = "送到分液位" };
        static HttpResponseMessage Response(string content, string reason = "stop") => new(HttpStatusCode.OK)
        { Content = JsonContent.Create(new { done = true, done_reason = reason, message = new { content } }) };
        static HttpClient Http(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
            new(new Handler(handler)) { BaseAddress = new("http://127.0.0.1:11434/") };
        static async Task Reject<T>(Func<Task> action) where T : Exception
        {
            try { await action(); }
            catch (T) { return; }
            throw new InvalidOperationException("Negative self-check did not reject: " + typeof(T).Name);
        }

        const string valid = "{\"summary\":\"选择运输模板\",\"steps\":[\"T1\"],\"question\":\"\"}";
        using (var http = Http((_, _) => Task.FromResult(Response(valid, "length"))))
            await Reject<InvalidOperationException>(() => new LightweightModelClient(http).SelectAsync(request, catalog, CancellationToken.None));
        using (var http = Http((_, _) => Task.FromResult(Response(valid.Replace("T1", "T999")))))
            await Reject<InvalidOperationException>(() => new LightweightModelClient(http).SelectAsync(request, catalog, CancellationToken.None));
        using (var http = Http((_, _) => Task.FromResult(Response("{\"summary\":\"缺字段\"}"))))
            await Reject<JsonException>(() => new LightweightModelClient(http).SelectAsync(request, catalog, CancellationToken.None));
        using (var http = Http((_, _) => throw new Exception("Oversized input must not reach HTTP.")))
            await Reject<InvalidOperationException>(() => new LightweightModelClient(http).SelectAsync(
                request with { Message = new string('字', 2000) }, catalog, CancellationToken.None));
        using (var http = Http(async (_, token) =>
        { await Task.Delay(Timeout.Infinite, token); return Response(valid); }))
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Reject<OperationCanceledException>(() => new LightweightModelClient(http).SelectAsync(request, catalog, cancel.Token));
        }
        Console.WriteLine("PASS: 5 negative light-client checks (truncation, unknown template, missing fields, input budget, cancellation).");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token); }
}
