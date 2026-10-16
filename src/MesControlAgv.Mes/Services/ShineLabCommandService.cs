using MesControlAgv.Contracts;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// High-level Config/Command downlink.  It sends business JSON through the
/// active ShineLab Client connection; it does not expose arbitrary frames.
/// </summary>
public sealed class ShineLabCommandService(
    ShineLabConnectionManager connectionManager,
    IOptions<ShineLabTcpOptions> configuredOptions,
    ShineLabStatusHub? statusHub = null)
{
    private readonly ShineLabTcpOptions _options = configuredOptions.Value;
    private readonly SemaphoreSlim _trayGate = new(1, 1);

    public async Task<ShineLabCommandResponse> SendConfigAsync(
        string equipmentCode,
        ShineLabConfigRequest request,
        CancellationToken cancellationToken,
        bool trackTask = false)
    {
        ValidateEquipment(equipmentCode);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TaskUuid))
            throw new ArgumentException("TaskUuid is required.", nameof(request));
        if (request.SampleData is null || request.SampleData.Count == 0)
            throw new ArgumentException("At least one sampleData row is required.", nameof(request));

        var body = ShineLabConfigPayloadBuilder.Build(request, _options.PayloadProfile);
        var result = await connectionManager.SendAsync(
            equipmentCode,
            "Config",
            body,
            TimeSpan.FromMilliseconds(_options.CommandTimeoutMs),
            cancellationToken, trackTask ? request.TaskUuid : null);
        return ToResponse(result);
    }

    public async Task<ShineLabCommandResponse> SendCommandAsync(
        string equipmentCode,
        ShineLabCommandRequest request,
        CancellationToken cancellationToken,
        bool trackTask = false)
    {
        ValidateEquipment(equipmentCode);
        ArgumentNullException.ThrowIfNull(request);
        var body = ShineLabCommandPayloadBuilder.Build(request, _options.PayloadProfile);
        var tray = !string.IsNullOrEmpty(request.StrAction);
        if (tray)
        {
            if (trackTask)
                throw new ArgumentException("Tray motion is a device command, not a sample-task start/stop command.", nameof(request));
            if (!_options.EnableTrayCommands)
                throw new InvalidOperationException("Tray commands are disabled until the updated vendor module is confirmed.");
            if (!await _trayGate.WaitAsync(0, cancellationToken))
                throw new InvalidOperationException("Another tray command is awaiting its reply; no additional command was sent.");
        }
        try
        {
            if (tray)
            {
                if (statusHub?.HasFreshIdleDeviceStatus(equipmentCode, TimeSpan.FromSeconds(_options.StaleAfterSeconds)) != true)
                    throw new InvalidOperationException("Tray motion requires a fresh online Idle status and no active sample.");
            }
            var result = await connectionManager.SendAsync(
                equipmentCode, "Command", body, TimeSpan.FromMilliseconds(_options.CommandTimeoutMs),
                cancellationToken, trackTask ? request.TaskUuid : null, tray ? null : request.Action);
            return ToResponse(result);
        }
        finally { if (tray) _trayGate.Release(); }
    }

    private static ShineLabCommandResponse ToResponse(ShineLabCommandResult result) => new(
        result.StrId,
        result.StrMethod,
        result.EquipmentCode,
        result.IsSuccess,
        result.Message,
        result.Body);

    private static void ValidateEquipment(string equipmentCode)
    {
        if (string.IsNullOrWhiteSpace(equipmentCode))
            throw new ArgumentException("EquipmentCode is required.", nameof(equipmentCode));
    }
}
