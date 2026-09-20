using MesControlAgv.Contracts;

namespace MesControlAgv.Wpf.DigitalTwin;

public interface IDigitalTwinPoseSource
{
    Task<AgvPoseResponse> ReadPoseAsync(CancellationToken cancellationToken);
}

public static class TwinPoseValidation
{
    public static string? Rejection(AgvPoseResponse pose, string agvId, DateTimeOffset now)
    {
        if (pose.AgvId != agvId) return "位置设备编号不符";
        if (pose.X is not { } x || pose.Y is not { } y || pose.Angle is not { } angle ||
            !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(angle) ||
            Math.Abs(x) > 1000 || Math.Abs(y) > 1000) return "位置字段缺失或无效";
        if (pose.Confidence is not { } c || !double.IsFinite(c) || c < .8 || c > 1) return "定位置信度不足";
        if (pose.ReceivedAt > now.AddSeconds(1) || now - pose.ReceivedAt > TimeSpan.FromSeconds(2)) return "位置数据过期／时钟异常";
        if (pose.MapObservedAt is not { } observed || observed > now.AddSeconds(1) ||
            now - observed > TimeSpan.FromSeconds(15)) return "地图身份未确认或已过期";
        if (!string.Equals(pose.MapMd5, TwinCalibration.MapMd5, StringComparison.OrdinalIgnoreCase) ||
            pose.MapName is not ("guangzhou606" or "guangzhou606.smap")) return "导航地图与标定地图不一致";
        return null;
    }
}

/// <summary>Page-scoped serial polling; failures never fabricate an offline pose.</summary>
public sealed class DigitalTwinPoseSession(IDigitalTwinPoseSource source, TimeSpan? interval = null,
    TimeSpan? timeout = null) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private bool _started, _disposed;
    public event Action<AgvPoseResponse?, string?>? Reading;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _started = true;
        Completion = PollAsync();
    }
    private async Task PollAsync()
    {
        var token = _stop.Token;
        var failures = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
                request.CancelAfter(timeout ?? TimeSpan.FromSeconds(2));
                Task<AgvPoseResponse>? pending = null;
                try
                {
                    pending = source.ReadPoseAsync(request.Token);
                    var pose = await pending.WaitAsync(request.Token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested) Reading?.Invoke(pose, null);
                    failures = 0;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    failures = Math.Min(4, failures + 1);
                    if (!token.IsCancellationRequested)
                        Reading?.Invoke(null, error is NotSupportedException ? "位置接口未就绪／非物理运行链" : "位置读取失败或超时，已停止同步");
                    if (pending is { IsCompleted: false })
                    {
                        try { await pending.WaitAsync(token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                        catch (Exception ex) when (ex is not OutOfMemoryException) { }
                    }
                }
                finally
                {
                    if (pending is { IsCompleted: false })
                        _ = pending.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                await Task.Delay(TimeSpan.FromMilliseconds((interval ?? TimeSpan.FromMilliseconds(500)).TotalMilliseconds * Math.Pow(2, failures)), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        Reading = null;
        _ = Completion.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
