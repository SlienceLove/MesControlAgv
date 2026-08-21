using MesControlAgv.Contracts.Experiments;

namespace MesControlAgv.Mes.Services;

public sealed class ExperimentSchedulingConflictException(string message)
    : InvalidOperationException(message);

public sealed class ExperimentPlanValidationException(
    string message,
    ExperimentPlanValidationResult validation) : InvalidOperationException(message)
{
    public ExperimentPlanValidationResult Validation { get; } = validation;
}
