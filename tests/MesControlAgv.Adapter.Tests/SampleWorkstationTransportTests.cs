using System.Net;
using MesControlAgv.Adapter.Modules.SampleWorkstation;

namespace MesControlAgv.Adapter.Tests;

public sealed class SampleWorkstationTransportTests
{
    [Fact]
    public async Task Timeout_covers_a_stalled_body_after_successful_headers()
    {
        using var handler = new StalledBodyHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://vendor.invalid/Service/"),
            Timeout = TimeSpan.FromMilliseconds(150)
        };
        var vendor = new VendorSampleWorkstationHttpClient(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            vendor.GetAsync("GetInstrumentStatus", null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, handler.Calls);
    }

    private sealed class StalledBodyHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledBody() });
        }
    }

    private sealed class StalledBody : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("Expected the cancellable overload.");
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
