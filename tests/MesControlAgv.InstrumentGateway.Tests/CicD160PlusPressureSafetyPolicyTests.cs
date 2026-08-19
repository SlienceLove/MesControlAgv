using MesControlAgv.InstrumentGateway;

namespace MesControlAgv.InstrumentGateway.Tests;

public sealed class CicD160PlusPressureSafetyPolicyTests
{
    [Fact]
    public void Scale_UsesFieldCorrelatedTenthOfMpaMapping()
    {
        Assert.Equal(9.8m, CicD160PlusPressureScale.ToMpa(98));
        Assert.Equal((ushort)98, CicD160PlusPressureScale.ToRawExact(9.8m));
    }

    [Fact]
    public void DefaultPolicyBlocksEveryPressure()
    {
        var policy = new CicD160PlusPressureSafetyPolicy();

        var decision = policy.Assess(98);

        Assert.False(policy.IsConfigured);
        Assert.False(decision.Permitted);
        Assert.Equal(CicD160PlusPressureSafetyDecisionCode.PressureSafetyLimitNotConfigured, decision.Code);
        Assert.Throws<InvalidOperationException>(() => policy.EnsureWithinConfiguredHardStop(0));
    }

    [Fact]
    public void ObservedMaximumIsNotAutomaticallyAConfiguredLimit()
    {
        var policy = new CicD160PlusPressureSafetyPolicy(
            enabled: false,
            hardStopPressureMpa: 9.8m,
            evidenceReference: "field-observation-20260819");

        Assert.False(policy.IsConfigured);
        Assert.False(policy.Assess(98).Permitted);
    }

    [Fact]
    public void EnabledPolicyRequiresEvidenceAndRejectsAboveHardStop()
    {
        Assert.Throws<ArgumentException>(() => new CicD160PlusPressureSafetyPolicy(
            enabled: true,
            hardStopPressureMpa: 12m));

        var policy = new CicD160PlusPressureSafetyPolicy(
            enabled: true,
            hardStopPressureMpa: 12m,
            evidenceReference: "vendor-site-approval-20260819");

        Assert.True(policy.IsConfigured);
        Assert.True(policy.Assess(98).Permitted);
        Assert.False(policy.Assess(121).Permitted);
        Assert.Throws<InvalidOperationException>(() => policy.EnsureWithinConfiguredHardStop(121));
    }

    [Fact]
    public void HardStopMustMapToAnExactRawTenth()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CicD160PlusPressureSafetyPolicy(
            hardStopPressureMpa: 12.05m));
    }
}
