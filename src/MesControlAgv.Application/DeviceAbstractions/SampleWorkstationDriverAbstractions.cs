using MesControlAgv.Contracts;

namespace MesControlAgv.Application;

/// <summary>
/// Typed read-only boundary for the cap-opening and dispensing workstation.
/// Vendor paths, envelopes, and inconsistent Data shapes remain in Adapter.
/// </summary>
public interface ISampleWorkstationReader
{
    Task<SampleWorkstationStatusResponse> GetStatusAsync(
        string deviceId,
        CancellationToken cancellationToken);

    Task<SampleWorkstationErrorResponse> GetErrorsAsync(
        string deviceId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SampleWorkstationTaskSummaryResponse>> GetTasksAsync(
        string deviceId,
        SampleWorkstationTaskQuery query,
        CancellationToken cancellationToken);

    Task<SampleWorkstationTaskDetailsResponse> GetTaskDetailsAsync(
        string deviceId,
        string taskNo,
        CancellationToken cancellationToken);

    Task<SampleWorkstationTaskStateResponse> GetTaskStateAsync(
        string deviceId,
        string taskNo,
        CancellationToken cancellationToken);
}

public interface ISampleWorkstationDriver : ISampleWorkstationReader;
