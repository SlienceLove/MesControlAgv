using System.Text.Json;

namespace MesControlAgv.Contracts;

public static class ShineLabAutoCurvePayloadBuilder
{
    public static JsonElement Build(ShineLabAutoCurveRequest request, ShineLabPayloadProfile profile)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (profile == ShineLabPayloadProfile.DcSeqSingleChannel)
        {
            if (!string.IsNullOrWhiteSpace(request.Channel))
                throw new ArgumentException("Single-channel DCSeq must not specify Channel.", nameof(request));
            return JsonSerializer.SerializeToElement(new { });
        }
        if (profile != ShineLabPayloadProfile.DcSeq)
            throw new ArgumentException("AutoCurve requires a DCSeq payload profile.", nameof(profile));
        if (string.IsNullOrWhiteSpace(request.Channel))
            throw new ArgumentException("Multi-system DCSeq requires Channel.", nameof(request));
        return JsonSerializer.SerializeToElement(new { channel = request.Channel });
    }
}
