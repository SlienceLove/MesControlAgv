using MesControlAgv.Domain.Map;

namespace MesControlAgv.Domain.Tests.Map;

public sealed class SmapMapIdentityTests
{
    [Fact]
    public void Builds_directed_edges_from_curve_start_and_end_marks()
    {
        var document = new SmapDocument(
            new SmapHeader("2D-Map", "test-map", default, default, 0.02, "1.0.6"),
            [],
            [new SmapLocationMark("LM1", default), new SmapLocationMark("LM2", default)],
            [],
            [
                new SmapCurve("forward", "LM1", default, "LM2", default, default, default, 1, 0),
                new SmapCurve("reverse", "LM2", default, "LM1", default, default, default, 0, 0)
            ]);

        var identity = SmapMapIdentity.FromDocument(document, "AABBCC");

        Assert.Equal("test-map", identity.MapName);
        Assert.Equal("1.0.6", identity.MapVersion);
        Assert.Equal("aabbcc", identity.Md5);
        Assert.Equal(["LM1", "LM2"], identity.StationMarks);
        Assert.Equal(
            [new SmapDirectedEdge("LM1", "LM2"), new SmapDirectedEdge("LM2", "LM1")],
            identity.DirectedEdges);
    }
}
