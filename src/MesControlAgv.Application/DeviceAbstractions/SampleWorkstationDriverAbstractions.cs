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

    Task<SampleWorkstationProtocolResponse> GetProtocolReadAsync(
        string deviceId,
        SampleWorkstationProtocolOperation operation,
        SampleWorkstationProtocolReadQuery query,
        CancellationToken cancellationToken);
}

// Deliberately separate from the central workflow's richer operation controller.
public interface ISampleWorkstationCommands
{
    Task<SampleWorkstationCommandResponse> InitializeAsync(
        string deviceId,
        CancellationToken cancellationToken);

    Task<SampleWorkstationCommandResponse> StartTaskAsync(
        string deviceId,
        string taskNo,
        CancellationToken cancellationToken);
}

public interface ISampleWorkstationCapabilityReader
{
    Task<SampleWorkstationCapabilitiesResponse> GetCapabilitiesAsync(
        string deviceId, CancellationToken cancellationToken);
}

/// <summary>Explicit V1.02 commands; updating barcodes never starts a task.</summary>
public interface ISampleWorkstationBarcodeCommands
{
    Task<SampleWorkstationCommandResponse> UpdateTaskBarcodesAsync(
        string deviceId, string taskNo, SampleWorkstationTaskBarcodes barcodes,
        CancellationToken cancellationToken);

    Task<SampleWorkstationCommandResponse> StartTaskAsync(
        string deviceId, string taskNo, SampleWorkstationTaskBarcodes barcodes,
        CancellationToken cancellationToken);
}

/// <summary>
/// Task-table import port. The caller owns the readable stream.
/// Import must not start tasks; success requires matching downloaded task content.
/// </summary>
public interface ISampleWorkstationTaskImporter
{
    Task<SampleWorkstationTaskImportResponse> ImportTasksAsync(
        string deviceId, string fileName, Stream content, CancellationToken cancellationToken);
}

public interface ISampleWorkstationDriver
    : ISampleWorkstationReader, ISampleWorkstationCommands, ISampleWorkstationCapabilityReader,
      ISampleWorkstationBarcodeCommands, ISampleWorkstationTaskImporter, ISampleWorkstationTemplateReader;

public interface ISampleWorkstationTemplateReader
{
    Task<SampleWorkstationTemplateResponse> GetTaskTemplateAsync(
        string deviceId, string taskNo, CancellationToken cancellationToken);
}
