using System.Text.Json;

namespace MesControlAgv.Contracts;

/// <summary>
/// Builds the strict Command body confirmed for the Rike ShineLab module.
/// Task and sample metadata remain local to MES and are deliberately not
/// serialized into the device command body.
/// </summary>
public static class ShineLabCommandPayloadBuilder
{
    private static readonly HashSet<int> SupportedActions = [0, 2, 7];

    public static JsonElement Build(
        ShineLabCommandRequest request,
        ShineLabPayloadProfile profile = ShineLabPayloadProfile.Rike)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(profile))
            throw new ArgumentOutOfRangeException(nameof(profile));
        if (string.IsNullOrWhiteSpace(request.TaskUuid))
            throw new ArgumentException("TaskUuid is required.", nameof(request));
        var singleChannel = profile == ShineLabPayloadProfile.DcSeqSingleChannel;
        if (singleChannel && !string.IsNullOrWhiteSpace(request.Channel))
            throw new ArgumentException("Single-channel DCSeq must not specify Channel.", nameof(request));
        if (!singleChannel && string.IsNullOrWhiteSpace(request.Channel))
            throw new ArgumentException("Channel is required for a ShineLab Command.", nameof(request));
        if (!string.IsNullOrEmpty(request.StrAction))
        {
            if (profile == ShineLabPayloadProfile.Rike)
                throw new ArgumentException("String tray commands require the updated DCSeq module.", nameof(request));
            // Vendor screenshot + user spelling confirmation, 2026-09-23.
            // Do not auto-correct/try aliases or fall back to numeric Start.
            if (request.StrAction is not ("TrayPopup" or "TrayReset"))
                throw new ArgumentException("Supported strAction values are exactly TrayPopup and TrayReset.", nameof(request));
            if (!string.IsNullOrEmpty(request.Param))
                throw new ArgumentException("TrayPopup/TrayReset use an empty param.", nameof(request));
            return singleChannel
                ? JsonSerializer.SerializeToElement(new { strAction = request.StrAction, param = "" })
                : JsonSerializer.SerializeToElement(new { channel = request.Channel, strAction = request.StrAction, param = "" });
        }
        if (!string.IsNullOrEmpty(request.Param))
            throw new ArgumentException("param is not supported for numeric actions.", nameof(request));
        if (!SupportedActions.Contains(request.Action))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.Action),
                request.Action,
                "Supported ShineLab Command actions are 0 (inject), 2 (stop), and 7 (clean).");
        }

        if (singleChannel)
            return JsonSerializer.SerializeToElement(new { action = request.Action });

        if (profile == ShineLabPayloadProfile.DcSeq)
        {
            // Action 1 remains unsupported until the vendor clarifies its
            // difference from action 0. No Init or start is sent implicitly.
            return JsonSerializer.SerializeToElement(new
            {
                channel = request.Channel,
                action = request.Action
            });
        }

        return JsonSerializer.SerializeToElement(new
        {
            chan = request.Channel,
            action = request.Action
        });
    }
}
