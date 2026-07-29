namespace MesControlAgv.Domain.Tests;

public class AssemblySmokeTests
{
    [Fact]
    public void Domain_assembly_loads()
    {
        Assert.Equal("MesControlAgv.Domain", typeof(SolutionMarker).Assembly.GetName().Name);
    }
}
