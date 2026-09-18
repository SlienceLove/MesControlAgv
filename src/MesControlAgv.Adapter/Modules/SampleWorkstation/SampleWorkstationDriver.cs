using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Modules.SampleWorkstation;

public sealed class SampleWorkstationDriver(
    VendorSampleWorkstationHttpClient vendor,
    SampleWorkstationOptions options,
    TimeProvider timeProvider) : ISampleWorkstationDriver
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly IReadOnlyDictionary<SampleWorkstationProtocolOperation, ProtocolOperationSpec>
        ProtocolOperations = new Dictionary<SampleWorkstationProtocolOperation, ProtocolOperationSpec>
        {
            [SampleWorkstationProtocolOperation.WorkflowList] = new("GetWorkFlowList"),
            [SampleWorkstationProtocolOperation.WorkflowDetails] = new("GetWorkFlowDetails", "WorkflowNo", true),
            [SampleWorkstationProtocolOperation.ExperimentalTaskTemplate] = new("GetExperimentalTaskTemplate", "TaskNo"),
            [SampleWorkstationProtocolOperation.WorkflowTemplate] = new("GetWorkFlowTemplate", "WorkflowNo"),
            [SampleWorkstationProtocolOperation.MaterialTypeList] = new("GetMaterialTypeList"),
            [SampleWorkstationProtocolOperation.MaterialTypeParameterList] = new("GetMaterialTypeParameterList", null, false, true),
            [SampleWorkstationProtocolOperation.MaterialTypeParameterDetails] = new("GetMaterialTypeParameterDetails", "MaterialTypeParCode", true),
            [SampleWorkstationProtocolOperation.MaterialTemplate] = new("GeMaterialTemplate"),
            [SampleWorkstationProtocolOperation.PlatformLayoutList] = new("GetPlatformLayoutList"),
            [SampleWorkstationProtocolOperation.PlatformLayoutDetails] = new("GetPlatformLayoutDetails", "PlatformLayoutNo", true),
            [SampleWorkstationProtocolOperation.PlatformLayoutTemplate] = new("GetPlatformLayoutTemplate"),
            [SampleWorkstationProtocolOperation.TrajectoryParameterList] = new("GetTrajectoryParameterList"),
            [SampleWorkstationProtocolOperation.TrajectoryParameterDetails] = new("GetTrajectoryParameterDetails", "TrajectoryTaskNo", true),
            [SampleWorkstationProtocolOperation.TrajectoryParameterTemplate] = new("GetTrajectoryParameterTemplate"),
            [SampleWorkstationProtocolOperation.SolventParameterList] = new("GetSolventParameterList", null, false, true),
            [SampleWorkstationProtocolOperation.SolventParameterDetails] = new("GetSolventParameterDetails", "LiquidCode", true),
            [SampleWorkstationProtocolOperation.SolventParameterTemplate] = new("GetSolventParameterTemplate")
        };

    public async Task<SampleWorkstationCommandResponse> InitializeAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        EnsureControlEnabled(deviceId);
        var response = await vendor.ExecuteCommandAsync("Init", query: null, cancellationToken);
        return ToCommandResponse(SampleWorkstationCommandOperation.Initialize, response);
    }

    public Task<SampleWorkstationCommandResponse> StartTaskAsync(
        string deviceId,
        string taskNo,
        CancellationToken cancellationToken) =>
        StartTaskCoreAsync(deviceId, taskNo, null, cancellationToken);

    public Task<SampleWorkstationCommandResponse> StartTaskAsync(
        string deviceId, string taskNo, SampleWorkstationTaskBarcodes barcodes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(barcodes);
        return StartTaskCoreAsync(deviceId, taskNo, barcodes, cancellationToken);
    }

    private async Task<SampleWorkstationCommandResponse> StartTaskCoreAsync(
        string deviceId, string taskNo, SampleWorkstationTaskBarcodes? barcodes,
        CancellationToken cancellationToken)
    {
        EnsureControlEnabled(deviceId);
        taskNo = RequireTaskNo(taskNo);
        var response = await vendor.ExecuteCommandAsync(
            "StartExperiment",
            CreateTaskQuery(taskNo, barcodes),
            cancellationToken);
        if (response.Data.ValueKind == JsonValueKind.String
            && string.Equals(response.Data.GetString()?.Trim(), "启动失败", StringComparison.Ordinal))
        {
            throw new SampleWorkstationProtocolException(
                $"Sample workstation did not confirm task '{taskNo}' start: 启动失败",
                SampleWorkstationErrorCodes.CommandUnconfirmed, response.Code, response.Data);
        }

        return ToCommandResponse(SampleWorkstationCommandOperation.StartTask, response, taskNo);
    }

    public async Task<SampleWorkstationCommandResponse> UpdateTaskBarcodesAsync(
        string deviceId, string taskNo, SampleWorkstationTaskBarcodes barcodes,
        CancellationToken cancellationToken)
    {
        EnsureControlEnabled(deviceId);
        taskNo = RequireTaskNo(taskNo);
        ArgumentNullException.ThrowIfNull(barcodes);
        VendorSampleWorkstationResponse response;
        try
        {
            response = await vendor.ExecuteCommandAsync(
                "UpdateTaskBarcode", CreateTaskQuery(taskNo, barcodes), cancellationToken);
        }
        catch (SampleWorkstationProtocolException exception) when (
            exception.VendorCode == 201 ||
            exception.VendorData is { ValueKind: JsonValueKind.String } data && data.GetString()?.Trim() == "更新失败")
        {
            // A missing task or explicit update failure is a known rejection.
            throw new SampleWorkstationProtocolException(exception.Message,
                SampleWorkstationErrorCodes.CommandRejected, exception.VendorCode, exception.VendorData);
        }
        if (response.Data.ValueKind == JsonValueKind.String && response.Data.GetString()?.Trim() == "更新失败")
            throw new SampleWorkstationProtocolException("The workstation rejected the barcode update.",
                SampleWorkstationErrorCodes.CommandRejected, response.Code, response.Data);
        return ToCommandResponse(SampleWorkstationCommandOperation.UpdateTaskBarcodes, response, taskNo);
    }

    private static Dictionary<string, string?> CreateTaskQuery(
        string taskNo, SampleWorkstationTaskBarcodes? barcodes)
    {
        var query = new Dictionary<string, string?> { ["TaskNo"] = taskNo };
        if (barcodes is null) return query;
        ArgumentException.ThrowIfNullOrWhiteSpace(barcodes.SampleBarcode1);
        ArgumentException.ThrowIfNullOrWhiteSpace(barcodes.SampleBarcode2);
        query["SampleBarcode1"] = barcodes.SampleBarcode1.Trim();
        query["SampleBarcode2"] = barcodes.SampleBarcode2.Trim();
        return query;
    }

    public Task<SampleWorkstationCapabilitiesResponse> GetCapabilitiesAsync(
        string deviceId, CancellationToken cancellationToken)
    {
        EnsureDeviceId(deviceId, requireEnabled: false);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SampleWorkstationCapabilitiesResponse(
            options.DeviceId, options.Enabled, options.ControlEnabled, false,
            options.Enabled && options.ControlEnabled
                ? Enum.GetValues<SampleWorkstationCommandOperation>() : [],
            options.Enabled
                ? ProtocolOperations.Keys.Except(options.UnsupportedProtocolOperations).ToArray() : []));
    }

    public async Task<SampleWorkstationStatusResponse> GetStatusAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        EnsureDeviceId(deviceId);
        var response = await vendor.GetAsync(
            "GetInstrumentStatus",
            new Dictionary<string, string?> { ["EquipmentNo"] = options.EquipmentNo },
            cancellationToken);
        if (!TryReadInt32(response.Data, out var rawState))
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
        var errorCode = TryReadInt32(response.Data, out var parsedCode)
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
                timeProvider.GetUtcNow()) { RawData = response.Data }
            : new SampleWorkstationErrorResponse(
                options.DeviceId,
                errorCode,
                "UndocumentedVendorErrorPayload",
                false,
                timeProvider.GetUtcNow()) { RawData = response.Data };
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

    public async Task<SampleWorkstationProtocolResponse> GetProtocolReadAsync(
        string deviceId,
        SampleWorkstationProtocolOperation operation,
        SampleWorkstationProtocolReadQuery query,
        CancellationToken cancellationToken)
    {
        EnsureDeviceId(deviceId);
        ArgumentNullException.ThrowIfNull(query);
        if (!ProtocolOperations.TryGetValue(operation, out var spec))
            throw new ArgumentException($"Unsupported sample workstation operation '{operation}'.", nameof(operation));
        if (options.UnsupportedProtocolOperations.Contains(operation))
            throw new SampleWorkstationProtocolException(
                $"Operation '{operation}' is unavailable in the configured workstation version.",
                SampleWorkstationErrorCodes.UnsupportedOperation);
        if (spec.SupportsPaging)
            ValidateQuery(new SampleWorkstationTaskQuery(
                StartDate: query.StartDate, EndDate: query.EndDate,
                StartNo: query.StartNo, RecordNum: query.RecordNum));

        var response = await vendor.GetAsync(
            spec.Path,
            BuildProtocolQuery(spec, query),
            cancellationToken);
        return new SampleWorkstationProtocolResponse(
            options.DeviceId,
            operation,
            response.Code,
            response.Data,
            timeProvider.GetUtcNow());
    }

    private static Dictionary<string, string?> BuildProtocolQuery(
        ProtocolOperationSpec spec,
        SampleWorkstationProtocolReadQuery query)
    {
        var values = new Dictionary<string, string?>();
        if (spec.KeyParameter is not null && !string.IsNullOrWhiteSpace(query.Key))
            values[spec.KeyParameter] = query.Key.Trim();
        else if (spec.KeyRequired)
            throw new ArgumentException($"Key is required for '{spec.Path}'.", nameof(query));

        if (spec.SupportsPaging)
        {
            // The vendor WCF endpoint expects both date keys even when the
            // caller requests an unbounded range; omitting them returns code
            // 201 ("无数据") on the real workstation.
            values["StartTime"] = query.StartDate?.Trim() ?? string.Empty;
            values["EndTime"] = query.EndDate?.Trim() ?? string.Empty;
            values["StartNo"] = query.StartNo.ToString(CultureInfo.InvariantCulture);
            values["RecordNum"] = query.RecordNum.ToString(CultureInfo.InvariantCulture);
        }

        return values;
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
            NullIfWhiteSpace(task.RequestTime),
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

    private static bool TryReadInt32(JsonElement element, out int value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
            return true;
        if (element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;
        value = default;
        return false;
    }

    private SampleWorkstationCommandResponse ToCommandResponse(
        SampleWorkstationCommandOperation operation,
        VendorSampleWorkstationResponse response,
        string? taskNo = null)
    {
        var text = response.Data.ValueKind == JsonValueKind.String ? response.Data.GetString()?.Trim() : null;
        var acknowledged = operation switch
        {
            SampleWorkstationCommandOperation.StartTask => text == "启动成功",
            SampleWorkstationCommandOperation.UpdateTaskBarcodes => text == "更新成功",
            SampleWorkstationCommandOperation.Initialize => text is "正在进行初始化" or "初始化成功",
            _ => false
        };
        if (!acknowledged)
            throw new SampleWorkstationProtocolException(
                "The workstation returned no recognized command acknowledgement; query state before taking further action.",
                SampleWorkstationErrorCodes.CommandUnconfirmed, response.Code, response.Data);
        return new(options.DeviceId, operation, response.Code, response.Data, timeProvider.GetUtcNow())
        {
            TaskNo = taskNo,
            Acknowledged = true
        };
    }

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

    private void EnsureDeviceId(string deviceId, bool requireEnabled = true)
    {
        if (!string.Equals(deviceId?.Trim(), options.DeviceId, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Sample workstation '{deviceId}' is not configured.");
        if (requireEnabled && !options.Enabled)
            throw new DeviceDisabledException(options.DeviceId);
    }

    private void EnsureControlEnabled(string deviceId)
    {
        EnsureDeviceId(deviceId);
        if (!options.ControlEnabled)
            throw new DeviceControlDisabledException(options.DeviceId);
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

    private sealed record ProtocolOperationSpec(
        string Path,
        string? KeyParameter = null,
        bool KeyRequired = false,
        bool SupportsPaging = false);
}
