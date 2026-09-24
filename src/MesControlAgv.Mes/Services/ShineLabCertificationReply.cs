using System.Text.Json;

namespace MesControlAgv.Mes.Services;

/// <summary>Explicit identity-only compatibility trials, never control commands.</summary>
public enum ShineLabCertificationReplyMode
{
    Result,
    StringCode200Result,
    NumericCode200Result,
    SuccessOne,
    StringCode200SuccessOne,
    NumericCode200SuccessOne,
    StringCode200Only,
    NumericCode200Only
}

public static class ShineLabCertificationReply
{
    public static JsonElement Build(string strId, string method, string equipmentCode,
        ShineLabCertificationReplyMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var reply = new Dictionary<string, object?>
        {
            ["strID"] = strId,
            ["strMethod"] = method,
            ["equipmentCode"] = equipmentCode
        };
        if (mode is ShineLabCertificationReplyMode.StringCode200Result or
            ShineLabCertificationReplyMode.StringCode200SuccessOne or ShineLabCertificationReplyMode.StringCode200Only)
            reply["strCode"] = "200";
        if (mode is ShineLabCertificationReplyMode.NumericCode200Result or
            ShineLabCertificationReplyMode.NumericCode200SuccessOne or ShineLabCertificationReplyMode.NumericCode200Only)
            reply["strCode"] = 200;
        reply["body"] = mode switch
        {
            ShineLabCertificationReplyMode.SuccessOne or ShineLabCertificationReplyMode.StringCode200SuccessOne or
                ShineLabCertificationReplyMode.NumericCode200SuccessOne => new { success = 1 },
            ShineLabCertificationReplyMode.StringCode200Only or ShineLabCertificationReplyMode.NumericCode200Only => (object)new { },
            _ => new { result = "Success", msg = "" }
        };
        return JsonSerializer.SerializeToElement(reply);
    }
}
