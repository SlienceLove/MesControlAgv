using System.Globalization;
using System.Text.Json;
using MesControlAgv.Contracts;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// In-memory projection of status pushed by ShineLab.  It contains no serial
/// transport and is intentionally replaceable with a persisted/event-backed
/// store when the production protocol is finalized.
/// </summary>
public sealed class ShineLabStatusHub(IOptions<ShineLabTcpOptions> configuredOptions)
{
    private readonly ShineLabTcpOptions _options = configuredOptions.Value;
    private readonly object _sync = new();
    private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.OrdinalIgnoreCase);

    public void Apply(
        string strId,
        string strMethod,
        string equipmentCode,
        JsonElement body,
        string? connectionId = null)
    {
        if (string.IsNullOrWhiteSpace(equipmentCode)) return;
        var code = equipmentCode.Trim();
        var method = strMethod?.Trim() ?? string.Empty;
        lock (_sync)
        {
            if (!_devices.TryGetValue(code, out var state))
            {
                state = new DeviceState(code);
                _devices[code] = state;
            }

            state.ConnectionActive = true;
            state.ConnectionId = connectionId ?? state.ConnectionId;
            state.LastSeenAtUtc = DateTimeOffset.UtcNow;
            state.LastStrId = strId;

            switch (method)
            {
                case "Certification":
                    state.State = "Connected";
                    state.Status = 0;
                    state.HasActiveTask = false;
                    state.TaskUuid = null;
                    state.SampleId = null;
                    state.SampleName = null;
                    state.Channel = null;
                    state.Position = null;
                    state.Stage = null;
                    state.Progress = null;
                    state.TaskStartedAtUtc = null;
                    state.TaskFinishedAtUtc = null;
                    break;

                case "UpdateInfo":
                    ApplyUpdate(state, body);
                    break;

                case "AlarmInfo":
                    state.Status = 2;
                    state.State = "Error";
                    state.AlarmCode = ReadString(body, "errorCode", "alarmCode");
                    state.AlarmMessage = ReadString(body, "errorMsg", "errorMessage", "errorMsgText")
                        ?? ReadString(body, "msg");
                    break;

                case "SampleFinish":
                    ApplyCommonTaskFields(state, body);
                    state.State = "SampleFinished";
                    state.Stage = "SampleFinished";
                    state.TaskFinishedAtUtc = ParseDate(body, "finishDate") ?? DateTimeOffset.UtcNow;
                    break;

                case "TaskFinish":
                    ApplyCommonTaskFields(state, body);
                    state.Status = 0;
                    state.State = "Completed";
                    state.Stage = "Completed";
                    state.Progress = 100;
                    state.HasActiveTask = false;
                    state.TaskFinishedAtUtc = ParseDate(body, "finishDate") ?? DateTimeOffset.UtcNow;
                    break;

                case "TaskError":
                    ApplyCommonTaskFields(state, body);
                    state.Status = 2;
                    state.State = "Error";
                    state.Stage = ReadString(body, "lastKnownStage", "stage") ?? "Error";
                    state.HasActiveTask = false;
                    state.AlarmCode = ReadString(body, "errorCode", "alarmCode");
                    state.AlarmMessage = ReadString(body, "errorMsg", "errorMessage", "msg");
                    state.TaskFinishedAtUtc = ParseDate(body, "finishDate") ?? DateTimeOffset.UtcNow;
                    break;

                case "Result":
                    ApplyCommonTaskFields(state, body);
                    state.Stage = "ResultAvailable";
                    break;

                default:
                    ApplyCommonTaskFields(state, body);
                    break;
            }
        }
    }

    public void MarkDisconnected(string? equipmentCode, string? connectionId = null)
    {
        if (string.IsNullOrWhiteSpace(equipmentCode)) return;
        lock (_sync)
        {
            if (_devices.TryGetValue(equipmentCode.Trim(), out var state) &&
                (string.IsNullOrWhiteSpace(connectionId) ||
                 string.Equals(state.ConnectionId, connectionId, StringComparison.Ordinal)))
                state.ConnectionActive = false;
        }
    }

    public IReadOnlyList<ShineLabDeviceStatusResponse> GetStatuses(DateTimeOffset? now = null)
    {
        var observedAt = now ?? DateTimeOffset.UtcNow;
        lock (_sync)
        {
            return _devices.Values
                .OrderBy(item => item.EquipmentCode, StringComparer.OrdinalIgnoreCase)
                .Select(item => ToResponse(item, observedAt))
                .ToList();
        }
    }

    public ShineLabDeviceStatusResponse? GetStatus(string equipmentCode, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(equipmentCode)) return null;
        var observedAt = now ?? DateTimeOffset.UtcNow;
        lock (_sync)
        {
            return _devices.TryGetValue(equipmentCode.Trim(), out var item)
                ? ToResponse(item, observedAt)
                : null;
        }
    }

    private void ApplyUpdate(DeviceState state, JsonElement body)
    {
        var status = ReadInt(body, "status");
        if (status is { } value) state.Status = value;
        ApplyCommonTaskFields(state, body);
        state.AlarmCode = ReadString(body, "errorCode", "alarmCode") ?? state.AlarmCode;
        state.AlarmMessage = ReadString(body, "errorMsg", "errorMessage", "alarmMessage") ?? state.AlarmMessage;
        if (state.Status == 0)
        {
            state.AlarmCode = null;
            state.AlarmMessage = null;
        }
        state.State = state.Status switch
        {
            1 => "Running",
            2 => "Error",
            _ => string.IsNullOrWhiteSpace(state.Stage) ? "Idle" : state.Stage!
        };
    }

    private static void ApplyCommonTaskFields(DeviceState state, JsonElement body)
    {
        var taskUuid = ReadString(body, "task_uuid", "taskUuid", "taskId");
        if (!string.IsNullOrWhiteSpace(taskUuid) &&
            !string.Equals(taskUuid, state.TaskUuid, StringComparison.OrdinalIgnoreCase))
        {
            state.TaskStartedAtUtc = null;
            state.TaskFinishedAtUtc = null;
            state.AlarmCode = null;
            state.AlarmMessage = null;
        }
        state.TaskUuid = taskUuid ?? state.TaskUuid;
        state.SampleId = ReadString(body, "sampleID", "sampleId") ?? state.SampleId;
        state.SampleName = ReadString(body, "sampleName") ?? state.SampleName;
        state.Channel = ReadString(body, "channel", "Channel") ?? state.Channel;
        state.Position = ReadInt(body, "position", "Position") ?? state.Position;
        state.Stage = ReadString(body, "stage", "currentStage", "lastKnownStage") ?? state.Stage;
        state.Progress = ReadInt(body, "progress", "percent") ?? state.Progress;
        state.TaskStartedAtUtc ??= ParseDate(body, "startDate", "startTime", "taskStartDate");

        if (state.Status == 1)
        {
            state.HasActiveTask = !IsTerminalStage(state.Stage);
        }
        else if (!string.IsNullOrWhiteSpace(state.TaskUuid))
        {
            state.HasActiveTask = false;
        }
        else if (state.Status == 0)
        {
            state.HasActiveTask = false;
        }
    }

    private ShineLabDeviceStatusResponse ToResponse(DeviceState state, DateTimeOffset now)
    {
        var staleAfter = TimeSpan.FromSeconds(Math.Max(1, _options.StaleAfterSeconds));
        var online = state.ConnectionActive && now - state.LastSeenAtUtc <= staleAfter;
        var displayState = online ? state.State : "Offline";
        return new(
            state.EquipmentCode,
            DisplayName(state.EquipmentCode),
            online,
            state.Status,
            displayState,
            online && state.HasActiveTask,
            state.TaskUuid,
            state.SampleId,
            state.SampleName,
            state.Channel,
            state.Position,
            state.Stage,
            state.Progress,
            state.AlarmCode,
            state.AlarmMessage,
            state.LastSeenAtUtc,
            state.TaskStartedAtUtc,
            state.TaskFinishedAtUtc);
    }

    private static string DisplayName(string code) => code.ToUpperInvariant() switch
    {
        "SHA18I" or "SHA18IA" or "AS18" or "SHA-18IA" => "SHA-18i 自动进样器",
        "IC_A" or "IC_C" => $"离子色谱仪 {code}",
        _ => code
    };

    private static bool IsTerminalStage(string? stage) => stage is not null &&
        (stage.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
         stage.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
         stage.Equals("Cancelled", StringComparison.OrdinalIgnoreCase));

    private static string? ReadString(JsonElement body, params string[] names)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!body.TryGetProperty(name, out var value))
            {
                var found = false;
                foreach (var property in body.EnumerateObject())
                {
                    if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    value = property.Value;
                    found = true;
                    break;
                }
                if (!found) continue;
            }

            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return value.ToString();
        }

        return null;
    }

    private static int? ReadInt(JsonElement body, params string[] names)
    {
        var value = ReadString(body, names);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    private static DateTimeOffset? ParseDate(JsonElement body, params string[] names)
    {
        var value = ReadString(body, names);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)
            ? date.ToUniversalTime()
            : null;
    }

    private sealed class DeviceState(string equipmentCode)
    {
        public string EquipmentCode { get; } = equipmentCode;
        public bool ConnectionActive { get; set; }
        public string? ConnectionId { get; set; }
        public DateTimeOffset LastSeenAtUtc { get; set; }
        public string? LastStrId { get; set; }
        public int Status { get; set; }
        public string State { get; set; } = "Offline";
        public bool HasActiveTask { get; set; }
        public string? TaskUuid { get; set; }
        public string? SampleId { get; set; }
        public string? SampleName { get; set; }
        public string? Channel { get; set; }
        public int? Position { get; set; }
        public string? Stage { get; set; }
        public int? Progress { get; set; }
        public string? AlarmCode { get; set; }
        public string? AlarmMessage { get; set; }
        public DateTimeOffset? TaskStartedAtUtc { get; set; }
        public DateTimeOffset? TaskFinishedAtUtc { get; set; }
    }
}
