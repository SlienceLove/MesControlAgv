using System.Text.Json;

namespace MesControlAgv.Contracts;

/// <summary>
/// Builds the strict Config body confirmed for the Rike ShineLab module.
/// Task and method metadata remain in MES and are deliberately not serialized
/// into the device payload.
/// </summary>
public static class ShineLabConfigPayloadBuilder
{
    public static JsonElement Build(ShineLabConfigRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TaskUuid))
            throw new ArgumentException("TaskUuid is required.", nameof(request));
        if (request.SampleData is null || request.SampleData.Count == 0)
            throw new ArgumentException("At least one sampleData row is required.", nameof(request));

        var firstChannel = request.SampleData[0].Channel;
        if (string.IsNullOrWhiteSpace(firstChannel))
            throw new ArgumentException("At least one sampleData row must specify Channel.", nameof(request));

        return JsonSerializer.SerializeToElement(new
        {
            chan = firstChannel,
            sampleData = request.SampleData.Select(sample => new
            {
                sampleID = sample.SampleId,
                sampleName = sample.SampleName,
                type = int.TryParse(sample.Type, out var sampleType) ? sampleType : 0,
                position = sample.Position,
                mPos = sample.MPos,
                Channel = sample.Channel
            }).ToArray()
        });
    }
}
