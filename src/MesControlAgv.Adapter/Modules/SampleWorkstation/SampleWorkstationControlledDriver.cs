using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Devices;

namespace MesControlAgv.Adapter.Modules.SampleWorkstation;

/// <summary>
/// Minimal controlled HTTP driver for the endpoints documented by the
/// workstation V1.0 PDF. The vendor protocol uses GET for these commands; the
/// Adapter keeps that detail private and exposes only fixed DTOs to MES.
/// </summary>
public sealed class SampleWorkstationControlledDriver(
    VendorSampleWorkstationHttpClient vendor,
    ISampleWorkstationReader reader,
    SampleWorkstationOptions options,
    TimeProvider timeProvider,
    SampleWorkstationOperationJournal? journal = null) : ISampleWorkstationController
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SampleWorkstationOperationJournal _journal = journal ?? new SampleWorkstationOperationJournal(options);

    public Task<SampleWorkstationOperationResponse> InitializeAsync(
        string deviceId,
        SampleWorkstationOperationRequest request,
        CancellationToken cancellationToken)
    {
        Validate(deviceId, request);
        return ExecuteOnceAsync(
            deviceId,
            request,
            operation: "Init",
            vendorTaskId: null,
            () => vendor.ExecuteCommandAsync("Init", EmptyQuery, cancellationToken),
            DeviceOperationLifecycle.Completed,
            requireSuccessMessage: false,
            cancellationToken);
    }

    public Task<SampleWorkstationOperationResponse> CreateTaskAsync(
        string deviceId,
        SampleWorkstationTaskCreateRequest request,
        CancellationToken cancellationToken)
    {
        Validate(deviceId, request);
        var taskNo = RequireTaskNo(request.TaskNo);
        var taskName = RequireText(request.TaskName, nameof(request.TaskName));
        var query = new Dictionary<string, string?>
        {
            ["TaskNo"] = taskNo,
            ["TaskName"] = taskName
        };
        return ExecuteOnceAsync(
            deviceId,
            request,
            operation: "AddExperimentalTask",
            vendorTaskId: taskNo,
            () => vendor.ExecuteCommandAsync("AddExperimentalTask", query, cancellationToken),
            DeviceOperationLifecycle.Completed,
            requireSuccessMessage: true,
            cancellationToken);
    }

    public Task<SampleWorkstationOperationResponse> AddTrajectoryAsync(
        string deviceId,
        SampleWorkstationTrajectoryRequest request,
        CancellationToken cancellationToken)
    {
        Validate(deviceId, request);
        var taskNo = RequireTaskNo(request.TaskNo);
        if (request.RecordNumber < 1) throw new ArgumentOutOfRangeException(nameof(request.RecordNumber));
        if (request.TransferVolume <= 0) throw new ArgumentOutOfRangeException(nameof(request.TransferVolume));
        var query = new Dictionary<string, string?>
        {
            ["R_No"] = request.RecordNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["TrajectoryTaskNo"] = taskNo,
            ["LiquidCode"] = RequireText(request.LiquidCode, nameof(request.LiquidCode)),
            ["LiquidName"] = RequireText(request.LiquidName, nameof(request.LiquidName)),
            ["WarehouseLocation"] = RequireText(request.WarehouseLocation, nameof(request.WarehouseLocation)),
            ["WarehouseX"] = request.WarehouseX.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["WarehouseY"] = request.WarehouseY.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SourceBarCode"] = RequireText(request.SourceBarCode, nameof(request.SourceBarCode)),
            ["SourceX"] = request.SourceX.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SourceY"] = request.SourceY.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["TargetBarCode"] = RequireText(request.TargetBarCode, nameof(request.TargetBarCode)),
            ["TargetX"] = request.TargetX.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["TargetY"] = request.TargetY.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["TransferVolume"] = request.TransferVolume.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        return ExecuteOnceAsync(
            deviceId,
            request,
            operation: "AddTaskTrajectoryParameter",
            vendorTaskId: taskNo,
            () => vendor.ExecuteCommandAsync("AddTaskTrajectoryParameter", query, cancellationToken),
            DeviceOperationLifecycle.Completed,
            requireSuccessMessage: true,
            cancellationToken);
    }

    public Task<SampleWorkstationOperationResponse> StartExperimentAsync(
        string deviceId,
        SampleWorkstationStartRequest request,
        CancellationToken cancellationToken)
    {
        Validate(deviceId, request);
        var taskNo = RequireTaskNo(request.TaskNo);
        var query = new Dictionary<string, string?> { ["TaskNo"] = taskNo };
        return ExecuteOnceAsync(
            deviceId,
            request,
            operation: "StartExperiment",
            vendorTaskId: taskNo,
            () => vendor.ExecuteCommandAsync("StartExperiment", query, cancellationToken),
            DeviceOperationLifecycle.StartPending,
            requireSuccessMessage: true,
            cancellationToken);
    }

    private async Task<SampleWorkstationOperationResponse> ExecuteOnceAsync(
        string deviceId,
        SampleWorkstationOperationRequest request,
        string operation,
        string? vendorTaskId,
        Func<Task<VendorSampleWorkstationResponse>> command,
        DeviceOperationLifecycle successState,
        bool requireSuccessMessage,
        CancellationToken cancellationToken)
    {
        EnsureControlEnabled();
        var gate = Gates.GetOrAdd(options.DeviceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_journal.TryGet(request.OperationId, out var previous))
            {
                EnsureSameCorrelation(deviceId, request, previous);
                if (previous.Status == DeviceOperationLifecycle.Unknown ||
                    previous.Status == DeviceOperationLifecycle.ManualInterventionRequired ||
                    previous.JournalStatus == SampleWorkstationJournalStatus.Reserved)
                {
                    throw new SampleWorkstationOperationUnknownException(
                        request,
                        operation,
                        previous.UnknownReason ?? UnknownReason.ManualReconciliationRequired,
                        previous.VendorTaskId,
                        "The workstation operation was previously reserved or became ambiguous; reconcile it manually.");
                }

                return new SampleWorkstationOperationResponse(
                    previous.DeviceId,
                    previous.OperationId,
                    previous.RunId,
                    previous.NodeExecutionId,
                    previous.Status,
                    previous.UnknownReason,
                    previous.VendorTaskId,
                    previous.VendorMessage,
                    previous.UpdatedAtUtc);
            }

            // The status query is read-only and must pass before the operation
            // is admitted. No command is sent for an unsafe/unknown state.
            var status = await reader.GetStatusAsync(deviceId, cancellationToken);
            if (!status.Online || status.State is SampleWorkstationDeviceState.Faulted
                or SampleWorkstationDeviceState.Offline
                or SampleWorkstationDeviceState.Unknown ||
                !IsAllowedState(operation, status.State))
            {
                throw new InvalidOperationException($"Workstation is not safe for {operation}: {status.State}.");
            }

            // Reserve the operation immediately before the vendor write. A
            // failed read-only preflight did not mutate the device and may be
            // retried after the operator restores a safe state; once reserved,
            // every transport outcome is terminal until manual reconciliation.
            if (!_journal.TryReserve(new SampleWorkstationJournalEntry
            {
                OperationId = request.OperationId,
                RunId = request.RunId,
                NodeExecutionId = request.NodeExecutionId,
                DeviceId = options.DeviceId,
                Operation = operation,
                VendorTaskId = vendorTaskId,
                Status = DeviceOperationLifecycle.StartPending,
                JournalStatus = SampleWorkstationJournalStatus.Reserved,
                UpdatedAtUtc = timeProvider.GetUtcNow()
            }))
                throw new InvalidOperationException($"Workstation operation {request.OperationId:N} already attempted a physical write; reconcile it instead of retrying.");

            VendorSampleWorkstationResponse response;
            try
            {
                response = await command();
            }
            catch (OperationCanceledException exception)
            {
                PersistUnknown(request, operation, vendorTaskId, UnknownReason.Timeout, "The workstation command outcome is unknown after cancellation.");
                throw new SampleWorkstationOperationUnknownException(
                    request,
                    operation,
                    UnknownReason.Timeout,
                    vendorTaskId,
                    "The workstation command outcome is unknown after cancellation.",
                    exception);
            }
            catch (HttpRequestException exception)
            {
                PersistUnknown(request, operation, vendorTaskId, UnknownReason.DisconnectedAfterWrite, "The workstation command outcome is unknown after a transport failure.");
                throw new SampleWorkstationOperationUnknownException(
                    request,
                    operation,
                    UnknownReason.DisconnectedAfterWrite,
                    vendorTaskId,
                    "The workstation command outcome is unknown after a transport failure.",
                    exception);
            }
            catch (SampleWorkstationProtocolException exception)
            {
                PersistUnknown(request, operation, vendorTaskId, UnknownReason.IncompleteResponse, "The workstation command response was incomplete or malformed.");
                throw new SampleWorkstationOperationUnknownException(
                    request,
                    operation,
                    UnknownReason.IncompleteResponse,
                    vendorTaskId,
                    "The workstation command response was incomplete or malformed.",
                    exception);
            }

            var message = ReadMessage(response.Data);
            if (requireSuccessMessage && !IsSuccessMessage(message))
            {
                var failed = CreateResponse(request, DeviceOperationLifecycle.Failed, null, vendorTaskId, message);
                Persist(failed, operation, SampleWorkstationJournalStatus.Failed);
                return failed;
            }

            var completed = CreateResponse(request, successState, null, vendorTaskId, message);
            Persist(completed, operation, SampleWorkstationJournalStatus.Completed);
            return completed;
        }
        finally
        {
            gate.Release();
        }
    }

    private void EnsureControlEnabled()
    {
        if (!options.Enabled || !options.ControlEnabled || !options.ProtocolConfirmed)
            throw new DeviceControlDisabledException(options.DeviceId);
    }

    private static void ValidateRequest(SampleWorkstationOperationRequest request, string deviceId)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (!request.IsValid(out var error)) throw new ArgumentException(error, nameof(request));
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("DeviceId is required.", nameof(deviceId));
    }

    private void Validate(string deviceId, SampleWorkstationOperationRequest request)
    {
        ValidateRequest(request, deviceId);
        if (!string.Equals(deviceId.Trim(), options.DeviceId, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Sample workstation '{deviceId}' is not configured.");
    }

    private static string RequireTaskNo(string value) => RequireText(value, nameof(value));

    private static string RequireText(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{name} is required.", name)
            : value.Trim();

    private static string? ReadMessage(JsonElement data) => data.ValueKind switch
    {
        JsonValueKind.String => data.GetString()?.Trim(),
        JsonValueKind.Object or JsonValueKind.Array => data.GetRawText(),
        _ => data.ToString()
    };

    private static bool IsSuccessMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("成功", StringComparison.OrdinalIgnoreCase)
            || message.Contains("success", StringComparison.OrdinalIgnoreCase));

    private static bool IsAllowedState(string operation, SampleWorkstationDeviceState state) =>
        operation.Equals("Init", StringComparison.OrdinalIgnoreCase)
            ? state is SampleWorkstationDeviceState.Idle or SampleWorkstationDeviceState.Initializing
            : state == SampleWorkstationDeviceState.Idle;

    private static void EnsureSameCorrelation(
        string deviceId,
        SampleWorkstationOperationRequest request,
        SampleWorkstationJournalEntry previous)
    {
        if (previous.RunId != request.RunId ||
            previous.NodeExecutionId != request.NodeExecutionId ||
            !string.Equals(previous.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The operation id is already associated with a different workflow correlation.");
    }

    private void PersistUnknown(
        SampleWorkstationOperationRequest request,
        string operation,
        string? vendorTaskId,
        UnknownReason reason,
        string message)
    {
        var response = CreateResponse(request, DeviceOperationLifecycle.Unknown, reason, vendorTaskId, message);
        Persist(response, operation, SampleWorkstationJournalStatus.Unknown);
    }

    private void Persist(
        SampleWorkstationOperationResponse response,
        string operation,
        SampleWorkstationJournalStatus journalStatus) =>
        _journal.Complete(response.OperationId, new SampleWorkstationJournalEntry
        {
            OperationId = response.OperationId,
            RunId = response.RunId,
            NodeExecutionId = response.NodeExecutionId,
            DeviceId = response.DeviceId,
            Operation = operation,
            VendorTaskId = response.VendorTaskId,
            Status = response.Status,
            JournalStatus = journalStatus,
            UnknownReason = response.UnknownReason,
            VendorMessage = response.VendorMessage,
            UpdatedAtUtc = response.ObservedAtUtc
        });

    private SampleWorkstationOperationResponse CreateResponse(
        SampleWorkstationOperationRequest request,
        DeviceOperationLifecycle state,
        UnknownReason? unknownReason,
        string? vendorTaskId,
        string? message) =>
        new(
            options.DeviceId,
            request.OperationId,
            request.RunId,
            request.NodeExecutionId,
            state,
            unknownReason,
            vendorTaskId,
            message,
            timeProvider.GetUtcNow());

    private static readonly IReadOnlyDictionary<string, string?> EmptyQuery =
        new Dictionary<string, string?>();
}

public sealed class SampleWorkstationOperationUnknownException : Exception
{
    public SampleWorkstationOperationUnknownException(
        SampleWorkstationOperationRequest request,
        string operation,
        UnknownReason reason,
        string? vendorTaskId,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Request = request;
        Operation = operation;
        Reason = reason;
        VendorTaskId = vendorTaskId;
    }

    public SampleWorkstationOperationRequest Request { get; }
    public string Operation { get; }
    public UnknownReason Reason { get; }
    public string? VendorTaskId { get; }
}
