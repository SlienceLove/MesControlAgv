using System.Net.Http;
using MesControlAgv.Adapter;
using MesControlAgv.Adapter.Services;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.DigitalTwin;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>Temporary preview composition only. Never starts the WPF App, MES or Adapter hosts.</summary>
internal sealed class PreviewSource : IDigitalTwinStatusSource, IDigitalTwinPoseSource, IAsyncDisposable
{
    private readonly HttpClient _http = new(new GetOnlyHandler()) {
        BaseAddress = new Uri("http://127.0.0.1:15445/"), Timeout = TimeSpan.FromSeconds(5) };
    private readonly TcpAgvClient _pose = new(Options.Create(new TcpAgvOptions {
        Host = "192.168.1.2", StatusPort = 19204, CommandPort = 1, ControlPort = 1, OtherPort = 1,
        EnablePush = false, AcquireControl = false, RequestTimeoutMs = 2000, ConnectTimeoutMs = 1500
    }), NullLogger<TcpAgvClient>.Instance, AdapterRunMode.ReadOnlyPreflight);
    private readonly MesDigitalTwinStatusSource _status;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    public PreviewSource() => _status = new(new MesClient(_http), RuntimeConnectionSource.PhysicalDevice);
    public string SourceDisplay => "标定预览 · 状态：现有 MES 15445 · 位置：AGV 192.168.1.2:19204 直读 · 无调度器";
    public TwinBindings Bindings { get; } = new();
    public AgvPoseResponse? LastPose { get; private set; }
    public int PoseReads { get; private set; }
    public async Task<TwinReading> ReadAsync(TwinChannel channel, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        return await _status.ReadAsync(channel, linked.Token).ConfigureAwait(false);
    }
    public async Task<AgvPoseResponse> ReadPoseAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            // TCP continuations must not depend on the closing WPF dispatcher.
            var pose = (await Task.Run(() => _pose.GetPoseAsync(linked.Token), linked.Token).ConfigureAwait(false))
                with { AgvId = Bindings.AgvId };
            LastPose = pose; PoseReads++;
            return pose;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _http.Dispose();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _pose.Dispose(); }
        finally { _gate.Release(); }
        _stop.Dispose();
    }
    private sealed class GetOnlyHandler : DelegatingHandler
    {
        public GetOnlyHandler() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri is not { } uri ||
                uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.Port != 15445)
                throw new InvalidOperationException("Calibration preview allows only GET to the existing local MES.");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
