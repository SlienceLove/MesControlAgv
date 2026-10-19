using System.Text.Json;

namespace MesControlAgv.Contracts;

/// <summary>
/// Builds Rike/DCSeq Config bodies. Only the explicit vendor Standard value is
/// sent as standard; local task and method metadata are not inferred as aliases.
/// </summary>
public static class ShineLabConfigPayloadBuilder
{
    public static JsonElement Build(
        ShineLabConfigRequest request,
        ShineLabPayloadProfile profile = ShineLabPayloadProfile.Rike)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(profile))
            throw new ArgumentOutOfRangeException(nameof(profile));
        if (string.IsNullOrWhiteSpace(request.TaskUuid))
            throw new ArgumentException("TaskUuid is required.", nameof(request));
        if (request.SampleData is null || request.SampleData.Count == 0)
            throw new ArgumentException("At least one sampleData row is required.", nameof(request));
        if (request.Standard is not null &&
            (profile == ShineLabPayloadProfile.Rike || string.IsNullOrWhiteSpace(request.Standard)))
            throw new ArgumentException("standard requires DCSeq and an explicit nonblank vendor value; omit it to preserve existing behavior.", nameof(request));

        var firstChannel = request.SampleData[0].Channel;
        var singleChannel = profile == ShineLabPayloadProfile.DcSeqSingleChannel;
        if (!singleChannel && string.IsNullOrWhiteSpace(firstChannel))
            throw new ArgumentException("At least one sampleData row must specify Channel.", nameof(request));

        if (profile is ShineLabPayloadProfile.DcSeq or ShineLabPayloadProfile.DcSeqSingleChannel)
        {
            // The vendor's 2026-09-21 sequence contract has one channel per
            // Config. Reject mixed channels instead of silently routing them
            // all through the first sample's channel.
            var sampleIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sample in request.SampleData)
            {
                if (singleChannel && !string.IsNullOrWhiteSpace(sample.Channel))
                    throw new ArgumentException("Single-channel DCSeq must not specify Channel.", nameof(request));
                if (!singleChannel && !string.Equals(sample.Channel, firstChannel, StringComparison.Ordinal))
                    throw new ArgumentException("DCSeq Config must contain a single channel.", nameof(request));
                if (string.IsNullOrWhiteSpace(sample.SampleId) || !sampleIds.Add(sample.SampleId))
                    throw new ArgumentException("DCSeq sampleId must be nonempty and unique within the sequence.", nameof(request));
                if (!int.TryParse(sample.Type, out var type) || type is < 0 or > 5)
                    throw new ArgumentException("DCSeq sample type must be an integer from 0 to 5.", nameof(request));
            }

            var samples = request.SampleData.Select(sample => new
                {
                    sampleId = sample.SampleId,
                    sampleName = sample.SampleName,
                    type = int.Parse(sample.Type),
                    position = sample.Position,
                    mPos = sample.MPos
                }).ToArray();
            // Vendor confirmation: for a single instrument omit channel entirely,
            // rather than writing an empty string or a placeholder "A".
            var body = new Dictionary<string, object>();
            if (!singleChannel) body["channel"] = firstChannel;
            if (request.Standard is not null) body["standard"] = request.Standard;
            body["sampleData"] = samples;
            return JsonSerializer.SerializeToElement(body);
        }

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
