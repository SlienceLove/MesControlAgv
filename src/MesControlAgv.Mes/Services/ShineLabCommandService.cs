using System.Text.Json;
using MesControlAgv.Contracts;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// High-level Config/Command downlink.  It sends business JSON through the
/// active ShineLab Client connection; it does not expose arbitrary frames.
/// </summary>
public sealed class ShineLabCommandService(
    ShineLabConnectionManager connectionManager,
    IOptions<ShineLabTcpOptions> configuredOptions)
{
    private readonly ShineLabTcpOptions _options = configuredOptions.Value;

    public async Task<ShineLabCommandResponse> SendConfigAsync(
        string equipmentCode,
        ShineLabConfigRequest request,
        CancellationToken cancellationToken)
    {
        ValidateEquipment(equipmentCode);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TaskUuid))
            throw new ArgumentException("TaskUuid is required.", nameof(request));
        if (request.SampleData is null || request.SampleData.Count == 0)
            throw new ArgumentException("At least one sampleData row is required.", nameof(request));

        var firstChannel = request.SampleData.FirstOrDefault()?.Channel;
        var body = JsonSerializer.SerializeToElement(new
        {
            task_uuid = request.TaskUuid,
            chan = firstChannel,
            sampleData = request.SampleData.Select(sample => new
            {
                sampleID = sample.SampleId,
                sampleName = sample.SampleName,
                type = int.TryParse(sample.Type, out var sampleType) ? sampleType : 0,
                position = sample.Position,
                mPos = sample.MPos,
                Channel = sample.Channel,
                instrumentMethod = sample.InstrumentMethod,
                processingMethod = sample.ProcessingMethod,
                detectionMethod = sample.DetectionMethod,
                injectionVolume = sample.InjectionVolume,
                injectionVolumeUnit = sample.InjectionVolumeUnit
            }),
            instrumentMethod = request.InstrumentMethod,
            processingMethod = request.ProcessingMethod,
            detectionMethod = request.DetectionMethod
        });
        var result = await connectionManager.SendAsync(
            equipmentCode,
            "Config",
            body,
            TimeSpan.FromMilliseconds(_options.CommandTimeoutMs),
            cancellationToken);
        return ToResponse(result);
    }

    public async Task<ShineLabCommandResponse> SendCommandAsync(
        string equipmentCode,
        ShineLabCommandRequest request,
        CancellationToken cancellationToken)
    {
        ValidateEquipment(equipmentCode);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TaskUuid))
            throw new ArgumentException("TaskUuid is required.", nameof(request));

        var body = JsonSerializer.SerializeToElement(new
        {
            task_uuid = request.TaskUuid,
            chan = request.Channel,
            action = request.Action,
            sampleID = request.SampleId,
            sampleName = request.SampleName,
            detectionMethod = request.DetectionMethod,
            cleanTime = request.CleanTime
        });
        var result = await connectionManager.SendAsync(
            equipmentCode,
            "Command",
            body,
            TimeSpan.FromMilliseconds(_options.CommandTimeoutMs),
            cancellationToken);
        return ToResponse(result);
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
