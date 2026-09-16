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

/// <summary>
/// Controlled workstation boundary. Implementations must serialize writes and
/// never retry an operation whose vendor outcome is unknown.
/// </summary>
public interface ISampleWorkstationController
{
    Task<SampleWorkstationOperationResponse> InitializeAsync(
        string deviceId,
        SampleWorkstationOperationRequest request,
        CancellationToken cancellationToken);

    Task<SampleWorkstationOperationResponse> CreateTaskAsync(
        string deviceId,
        SampleWorkstationTaskCreateRequest request,
        CancellationToken cancellationToken);

    Task<SampleWorkstationOperationResponse> AddTrajectoryAsync(
        string deviceId,
        SampleWorkstationTrajectoryRequest request,
        CancellationToken cancellationToken);

    Task<SampleWorkstationOperationResponse> StartExperimentAsync(
        string deviceId,
        SampleWorkstationStartRequest request,
        CancellationToken cancellationToken);
}
