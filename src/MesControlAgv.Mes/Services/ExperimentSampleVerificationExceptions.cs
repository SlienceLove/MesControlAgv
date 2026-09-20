namespace MesControlAgv.Mes.Services;

public sealed class ExperimentSampleVerificationException(string message, string code)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
