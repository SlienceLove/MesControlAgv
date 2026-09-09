using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain.Profiles;
using Microsoft.Extensions.Logging;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Shared admission boundary for operations that could enter a physical
/// execution path. This policy deliberately has no Adapter dependency.
/// </summary>
public sealed class PhysicalExecutionAdmissionPolicy
{
    private readonly ProfileConfiguration _profile;
    private readonly IPhysicalReadinessState _readiness;
    private readonly ILogger<PhysicalExecutionAdmissionPolicy> _logger;

    public PhysicalExecutionAdmissionPolicy(
        ProfileConfiguration profile,
        IPhysicalReadinessState readiness,
        ILogger<PhysicalExecutionAdmissionPolicy> logger)
    {
        _profile = profile;
        _readiness = readiness;
        _logger = logger;
    }

    /// <summary>Requires the physical readiness supervisor for execution.</summary>
    public void RequireSupervisedExecution(string operation)
    {
        if (_profile.Features.UseSimulator)
        {
            return;
        }

        if (_readiness.Enabled)
        {
            return;
        }

        Reject(
            operation,
            PhysicalReadinessReasonCodes.SupervisorDisabled,
            "Physical execution requires the readiness supervisor, which is disabled.");
    }

    /// <summary>
    /// Rejects physical direct writes that do not carry a workflow or other
    /// current epoch authorization. Simulator behavior remains unchanged.
    /// </summary>
    public void RejectUnboundPhysicalWrite(string operation)
    {
        if (_profile.Features.UseSimulator)
        {
            return;
        }

        if (!_readiness.Enabled)
        {
            Reject(
                operation,
                PhysicalReadinessReasonCodes.SupervisorDisabled,
                "Physical execution requires the readiness supervisor, which is disabled.");
        }

        Reject(
            operation,
            PhysicalReadinessReasonCodes.EpochAuthorizationRequired,
            "Physical direct writes require a current supervised execution authorization.");
    }

    private void Reject(string operation, string code, string detail)
    {
        var safeOperation = string.IsNullOrWhiteSpace(operation) ? "unspecified operation" : operation.Trim();
        _logger.LogWarning(
            "Physical execution admission rejected for operation {Operation}; reason {ReasonCode}.",
            safeOperation,
            code);
        throw new PhysicalExecutionAdmissionException(code, detail);
    }
}

/// <summary>Stable exception raised by the physical execution admission policy.</summary>
public sealed class PhysicalExecutionAdmissionException : InvalidOperationException
{
    public PhysicalExecutionAdmissionException(string code, string detail)
        : base(detail)
    {
        Code = string.IsNullOrWhiteSpace(code) ? throw new ArgumentException("A rejection code is required.", nameof(code)) : code;
        Detail = string.IsNullOrWhiteSpace(detail) ? throw new ArgumentException("A rejection detail is required.", nameof(detail)) : detail;
    }

    public string Code { get; }

    public string Detail { get; }
}
