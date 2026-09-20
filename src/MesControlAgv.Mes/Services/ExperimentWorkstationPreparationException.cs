namespace MesControlAgv.Mes.Services;

public sealed class ExperimentWorkstationPreparationException(string message, string code)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
