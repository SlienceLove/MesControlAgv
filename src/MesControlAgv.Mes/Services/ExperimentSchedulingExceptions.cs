using MesControlAgv.Contracts.Experiments;

namespace MesControlAgv.Mes.Services;

public sealed class ExperimentSchedulingConflictException(
    string message,
    string code = "EXP-SCHEDULING-CONFLICT")
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class ExperimentPlanValidationException(
    string message,
    ExperimentPlanValidationResult validation) : InvalidOperationException(message)
{
    public ExperimentPlanValidationResult Validation { get; } = validation;
}
