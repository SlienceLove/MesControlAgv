namespace MesControlAgv.InstrumentGateway;

public static class CicD160PlusPressureScale
{
    public const decimal RawUnitsPerMpa = 10m;

    public static decimal ToMpa(ushort rawValue) => rawValue / RawUnitsPerMpa;

    public static ushort ToRawExact(decimal pressureMpa)
    {
        if (pressureMpa < 0 || pressureMpa > ushort.MaxValue / RawUnitsPerMpa)
            throw new ArgumentOutOfRangeException(nameof(pressureMpa));

        var raw = pressureMpa * RawUnitsPerMpa;
        if (raw != decimal.Truncate(raw))
        {
            throw new ArgumentOutOfRangeException(
                nameof(pressureMpa),
                pressureMpa,
                "Pressure must map exactly to a D160+ raw tenth of an MPa.");
        }
        return (ushort)raw;
    }
}

public enum CicD160PlusPressureSafetyDecisionCode
{
    PressureSafetyLimitNotConfigured,
    WithinConfiguredHardStop,
    ConfiguredHardStopExceeded
}

public sealed record CicD160PlusPressureSafetyDecision(
    CicD160PlusPressureSafetyDecisionCode Code,
    ushort PressureRaw,
    decimal PressureMpa,
    ushort? HardStopRaw,
    decimal? HardStopMpa)
{
    public bool Permitted => Code == CicD160PlusPressureSafetyDecisionCode.WithinConfiguredHardStop;
}

/// <summary>
/// Fail-closed pressure policy for a future activation gate. The observed
/// 9.8 MPa field value is deliberately not used as a limit.
/// </summary>
public sealed class CicD160PlusPressureSafetyPolicy
{
    public CicD160PlusPressureSafetyPolicy(
        bool enabled = false,
        decimal? hardStopPressureMpa = null,
        string? evidenceReference = null)
    {
        if (hardStopPressureMpa is not null)
            HardStopRaw = CicD160PlusPressureScale.ToRawExact(hardStopPressureMpa.Value);
        if (enabled && string.IsNullOrWhiteSpace(evidenceReference))
            throw new ArgumentException(
                "An enabled pressure safety policy requires a vendor/site evidence reference.",
                nameof(evidenceReference));

        Enabled = enabled;
        HardStopPressureMpa = hardStopPressureMpa;
        EvidenceReference = evidenceReference;
    }

    public bool Enabled { get; }
    public decimal? HardStopPressureMpa { get; }
    public ushort? HardStopRaw { get; }
    public string? EvidenceReference { get; }

    public bool IsConfigured =>
        Enabled &&
        HardStopPressureMpa is > 0 &&
        HardStopRaw is > 0 &&
        !string.IsNullOrWhiteSpace(EvidenceReference);

    public CicD160PlusPressureSafetyDecision Assess(ushort pressureRaw)
    {
        var pressureMpa = CicD160PlusPressureScale.ToMpa(pressureRaw);
        if (!IsConfigured)
        {
            return new(
                CicD160PlusPressureSafetyDecisionCode.PressureSafetyLimitNotConfigured,
                pressureRaw,
                pressureMpa,
                HardStopRaw,
                HardStopPressureMpa);
        }

        var code = pressureRaw <= HardStopRaw
            ? CicD160PlusPressureSafetyDecisionCode.WithinConfiguredHardStop
            : CicD160PlusPressureSafetyDecisionCode.ConfiguredHardStopExceeded;
        return new(code, pressureRaw, pressureMpa, HardStopRaw, HardStopPressureMpa);
    }

    public void EnsureWithinConfiguredHardStop(ushort pressureRaw)
    {
        var decision = Assess(pressureRaw);
        if (!decision.Permitted)
        {
            throw new InvalidOperationException(
                decision.Code == CicD160PlusPressureSafetyDecisionCode.PressureSafetyLimitNotConfigured
                    ? "D160+ pressure safety hard-stop policy is not configured; activation is blocked."
                    : $"D160+ pressure {decision.PressureMpa:0.###} MPa exceeds the configured hard stop " +
                      $"{decision.HardStopMpa:0.###} MPa; activation is blocked.");
        }
    }
}
