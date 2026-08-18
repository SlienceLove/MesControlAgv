using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Modules.SampleWorkstation;

public sealed class SampleWorkstationReadOnlyDriver(
    VendorSampleWorkstationHttpClient vendor,
    SampleWorkstationOptions options,
    TimeProvider timeProvider) : ISampleWorkstationDriver
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<SampleWorkstationStatusResponse> GetStatusAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        EnsureDeviceId(deviceId);
        var response = await vendor.GetAsync(
            "GetInstrumentStatus",
            new Dictionary<string, string?> { ["EquipmentNo"] = options.EquipmentNo },
            cancellationToken);
        if (response.Data.ValueKind != JsonValueKind.Number || !response.Data.TryGetInt32(out var rawState))
            throw new SampleWorkstationProtocolException("Instrument status Data must be an integer.");

        var state = rawState switch
        {
            0 => SampleWorkstationDeviceState.Idle,
            1 => SampleWorkstationDeviceState.Running,
            2 => SampleWorkstationDeviceState.Paused,
            3 => SampleWorkstationDeviceState.Faulted,
            4 => SampleWorkstationDeviceState.Initializing,
            5 => SampleWorkstationDeviceState.Offline,
            _ => SampleWorkstationDeviceState.Unknown
        };
        return new SampleWorkstationStatusResponse(
            options.DeviceId,
            options.EquipmentNo,
            state != SampleWorkstationDeviceState.Offline,
            state,
            rawState,
            timeProvider.GetUtcNow());
    }

    public async Task<SampleWorkstationErrorResponse> GetErrorsAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        EnsureDeviceId(deviceId);
        var response = await vendor.GetAsync(
            "GetErrorInformation",
            new Dictionary<string, string?> { ["EquipmentNo"] = options.EquipmentNo },
            cancellationToken);
        var errorCode = response.Data.ValueKind == JsonValueKind.Number
            && response.Data.TryGetInt32(out var parsedCode)
            ? parsedCode
            : (int?)null;
        string? description = null;
        var recognized = errorCode is not null
            && TryDescribeError(errorCode.Value, out description);
        return recognized
            ? new SampleWorkstationErrorResponse(
                options.DeviceId,
                errorCode,
                description!,
                true,
                timeProvider.GetUtcNow())
            : new SampleWorkstationErrorResponse(
                options.DeviceId,
                null,
                "UndocumentedVendorErrorPayload",
                false,
                timeProvider.GetUtcNow());
    }

    public async Task<IReadOnlyList<SampleWorkstationTaskSummaryResponse>> GetTasksAsync(
        string deviceId,
        SampleWorkstationTaskQuery query,
        CancellationToken cancellationToken)
    {
        EnsureDeviceId(deviceId);
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query);
        var response = await vendor.GetAsync(
            "GetTaskInformationList",
            new Dictionary<string, string?>
            {
                ["State"] = ToVendorTaskFilter(query.State),
                ["StartTime"] = query.StartDate ?? string.Empty,
                ["EndTime"] = query.EndDate ?? string.Empty,
                ["StartNo"] = query.StartNo.ToString(CultureInfo.InvariantCulture),
                ["RecordNum"] = query.RecordNum.ToString(CultureInfo.InvariantCulture)
            },
            cancellationToken);
        if (response.Data.ValueKind != JsonValueKind.Array)
            throw new SampleWorkstationProtocolException("Task list Data must be an array.");

        var tasks = response.Data.Deserialize<List<VendorTaskSummary>>(SerializerOptions)
            ?? throw new SampleWorkstationProtocolException("Task list Data is empty or invalid.");
        return tasks.Select(ToSummary).ToArray();
    }

    public async Task<SampleWorkstationTaskDetailsResponse> GetTaskDetailsAsync(
        string deviceId,
        string taskNo,
        CancellationToken cancellationToken)
    {
        EnsureDeviceId(deviceId);
        taskNo = RequireTaskNo(taskNo);
        var response = await vendor.GetAsync(
            "GetTaskDetails",
            new Dictionary<string, string?> { ["TaskNo"] = taskNo },
            cancellationToken);
        var details = response.Data.ValueKind switch
        {
            JsonValueKind.Array => response.Data.Deserialize<List<VendorTaskDetails>>(SerializerOptions),
            JsonValueKind.Object =>
                [response.Data.Deserialize<VendorTaskDetails>(SerializerOptions)
                    ?? throw new SampleWorkstationProtocolException("Task details Data is invalid.")],
            _ => throw new SampleWorkstationProtocolException("Task details Data must be an object or array.")
        };
        var detail = details?.SingleOrDefault(item =>
            string.Equals(item.TaskNo?.Trim(), taskNo, StringComparison.Ordinal));
        if (detail is null)
            throw new KeyNotFoundException($"Sample workstation task '{taskNo}' was not returned by the device.");
        return ToDetails(detail);
    }

    public async Task<SampleWorkstationTaskStateResponse> GetTaskStateAsync(
        string deviceId,
        string taskNo,
        CancellationToken cancellationToken)
    {
        EnsureDeviceId(deviceId);
        taskNo = RequireTaskNo(taskNo);
        var response = await vendor.GetAsync(
            "GetTaskState",
            new Dictionary<string, string?> { ["TaskNo"] = taskNo },
            cancellationToken);
        if (response.Data.ValueKind != JsonValueKind.String)
            throw new SampleWorkstationProtocolException("Task state Data must be a string.");
        var rawState = response.Data.GetString()?.Trim() ?? string.Empty;
        return new SampleWorkstationTaskStateResponse(
            options.DeviceId,
            taskNo,
            NormalizeTaskState(rawState),
            rawState,
            timeProvider.GetUtcNow());
    }

    private static SampleWorkstationTaskSummaryResponse ToSummary(VendorTaskSummary task)
    {
        var taskNo = RequireVendorValue(task.TaskNo, "TaskNo");
        var rawState = RequireVendorValue(task.State, "State");
        return new SampleWorkstationTaskSummaryResponse(
            task.RecordNumber,
            taskNo,
            RequireVendorValue(task.TaskName, "TaskName"),
            NormalizeTaskState(rawState),
            rawState,
            RequireVendorValue(task.MakeTime, "MakeTime"),
            NullIfWhiteSpace(task.Remark));
    }

    private static SampleWorkstationTaskDetailsResponse ToDetails(VendorTaskDetails task)
    {
        var rawState = RequireVendorValue(task.State, "State");
        return new SampleWorkstationTaskDetailsResponse(
            task.RecordNumber,
            RequireVendorValue(task.TaskNo, "TaskNo"),
            RequireVendorValue(task.TaskName, "TaskName"),
            NormalizeTaskState(rawState),
            rawState,
            RequireVendorValue(task.RequestTime, "RequestTime"),
            NullIfWhiteSpace(task.ProductionTime),
            NullIfWhiteSpace(task.CompletionTime),
            RequireVendorValue(task.OperatorAccount, "OperatorAccount"),
            RequireVendorValue(task.MakeTime, "MakeTime"),
            NullIfWhiteSpace(task.Remark));
    }

    private void ValidateQuery(SampleWorkstationTaskQuery query)
    {
        if (query.StartNo < 1) throw new ArgumentOutOfRangeException(nameof(query), "StartNo must be positive.");
        if (query.RecordNum is < 1 || query.RecordNum > options.MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(query), $"RecordNum must be between 1 and {options.MaximumPageSize}.");
        var start = ParseDate(query.StartDate, nameof(query.StartDate));
        var end = ParseDate(query.EndDate, nameof(query.EndDate));
        if (start is not null && end is not null && end < start)
            throw new ArgumentException("EndDate cannot be before StartDate.", nameof(query));
        _ = ToVendorTaskFilter(query.State);
    }

    private static DateOnly? ParseDate(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ArgumentException($"{parameterName} must use yyyy-MM-dd.", parameterName);
    }

    private static string ToVendorTaskFilter(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return string.Empty;
        return state.Trim() switch
        {
            "Waiting" or "等待运行" => "等待运行",
            "Running" or "正在运行" => "正在运行",
            "Completed" or "任务完成" => "任务完成",
            _ => throw new ArgumentException("State must be Waiting, Running, or Completed.", nameof(state))
        };
    }

    private static SampleWorkstationTaskState NormalizeTaskState(string state) => state.Trim() switch
    {
        "等待运行" => SampleWorkstationTaskState.Waiting,
        "正在运行" => SampleWorkstationTaskState.Running,
        "任务完成" => SampleWorkstationTaskState.Completed,
        _ => SampleWorkstationTaskState.Unknown
    };

    private static bool TryDescribeError(int code, out string? description)
    {
        description = code switch
        {
            0 => "TaskCompleted",
            1 => "ManuallyEnded",
            2 => "AbnormallyEnded",
            3 => "ExperimentStarted",
            -1 => "ActionGenerationError",
            -2 => "ActionExecutionError",
            -3 => "InvalidTaskNumber",
            -4 => "OverallActionError",
            _ => null
        };
        return description is not null;
    }

    private void EnsureDeviceId(string deviceId)
    {
        if (!string.Equals(deviceId?.Trim(), options.DeviceId, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Sample workstation '{deviceId}' is not configured.");
    }

    private static string RequireTaskNo(string taskNo) =>
        string.IsNullOrWhiteSpace(taskNo)
            ? throw new ArgumentException("TaskNo is required.", nameof(taskNo))
            : taskNo.Trim();

    private static string RequireVendorValue(string? value, string fieldName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new SampleWorkstationProtocolException($"Task data field '{fieldName}' is required.")
            : value.Trim();

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record VendorTaskSummary(
        [property: JsonPropertyName("R_No")] int RecordNumber,
        string? TaskNo,
        string? TaskName,
        string? State,
        string? MakeTime,
        string? Remark);

    private sealed record VendorTaskDetails(
        [property: JsonPropertyName("R_No")] int RecordNumber,
        string? TaskNo,
        string? TaskName,
        string? State,
        string? RequestTime,
        string? ProductionTime,
        string? CompletionTime,
        string? OperatorAccount,
        string? MakeTime,
        string? Remark);
}
