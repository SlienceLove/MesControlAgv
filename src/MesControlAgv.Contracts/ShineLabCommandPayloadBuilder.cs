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

    public static JsonElement Build(ShineLabCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TaskUuid))
            throw new ArgumentException("TaskUuid is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Channel))
            throw new ArgumentException("Channel is required for a ShineLab Command.", nameof(request));
        if (!SupportedActions.Contains(request.Action))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.Action),
                request.Action,
                "Supported ShineLab Command actions are 0 (inject), 2 (stop), and 7 (clean).");
        }

        return JsonSerializer.SerializeToElement(new
        {
            chan = request.Channel,
            action = request.Action
        });
    }
}
