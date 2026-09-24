using MesControlAgv.Contracts;

namespace MesControlAgv.Wpf.Tests;

public sealed class ShineLabResultReviewTests
{
    [Theory]
    [InlineData("{\"compounds\":[{\"name\":\"TEST\",\"area\":0,\"height\":0,\"concent\":0}]}", "AllZeroQuantitation")]
    [InlineData("{\"compounds\":[{\"area\":\"0\",\"height\":\"0.0\",\"concent\":\"0\"}]}", "AllZeroQuantitation")]
    [InlineData("{\"compounds\":[{\"area\":\"ND\",\"height\":0,\"concent\":0}]}", "NonQuantified")]
    [InlineData("{\"compounds\":[{\"area\":null,\"height\":0,\"concent\":0}]}", "IncompleteQuantitation")]
    [InlineData("{\"compounds\":[{\"height\":0,\"concent\":0}]}", "IncompleteQuantitation")]
    [InlineData("{\"compounds\":[{\"area\":\"NaN\",\"height\":0,\"concent\":0}]}", "IncompleteQuantitation")]
    [InlineData("{\"compounds\":[null]}", "IncompleteQuantitation")]
    [InlineData("{\"compounds\":[]}", "EmptyCompounds")]
    [InlineData("{}", "InvalidResultStructure")]
    [InlineData("null", "InvalidResultStructure")]
    [InlineData("invalid", "MalformedResult")]
    public void Advisory_preserves_raw_value_and_is_scoped_to_registered_sample(string raw, string code)
    {
        var sample = Sample(raw);
        var advice = Assert.Single(ShineLabResultReview.Assess(sample));
        Assert.Equal(code, advice.Code);
        Assert.Equal("964398530", advice.SampleId);
        Assert.Equal(raw, sample.ResultJson);
        if (code == "AllZeroQuantitation")
        {
            Assert.Contains("未确认", advice.Suggestions);
            Assert.Contains("不能直接判为", advice.Suggestions);
        }
    }

    [Fact]
    public void Valid_nonzero_or_single_zero_does_not_trigger_all_zero_rule()
    {
        foreach (var raw in new[] {
            "{\"compounds\":[{\"area\":4.4,\"height\":36,\"concent\":1.36}]}",
            "{\"compounds\":[{\"area\":4.4,\"height\":36,\"concent\":0}]}" })
            Assert.Empty(ShineLabResultReview.Assess(Sample(raw)));
    }

    [Fact]
    public void Missing_and_conflicting_evidence_are_distinct()
    {
        Assert.Empty(ShineLabResultReview.Assess(Sample(null) with { TaskFinishObserved = false }));
        Assert.Equal("MissingResultAfterFinish", Assert.Single(ShineLabResultReview.Assess(Sample(null))).Code);
        var warnings = ShineLabResultReview.Assess(Sample("{\"compounds\":[]}") with { ResultConflict = true });
        Assert.Contains(warnings, w => w.Code == "ResultConflict");
        Assert.Contains(warnings, w => w.Code == "EmptyCompounds");
    }

    private static ShineLabTaskSampleResponse Sample(string? raw) =>
        new(1, "source", "964398530", "test", true, true, true, false, raw, false, null);
}
