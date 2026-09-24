using MesControlAgv.Application;

namespace MesControlAgv.WorkflowContract.Tests;

public sealed class WorkstationTaskNumberTests
{
    [Fact]
    public void Generated_id_is_twenty_text_characters_and_sample_has_no_duplicates()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => WorkstationTaskNumber.Create()).ToArray();
        Assert.All(ids, id => { Assert.Matches("^WS[0-9A-F]{18}$", id); WorkstationTaskNumber.EnsureSafeForImport(id); });
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("1234567890123456")]
    [InlineData("20260924143809983251")]
    [InlineData("00000000000000000001")]
    public void New_import_rejects_long_numeric_identity(string id) =>
        Assert.Throws<ArgumentException>(() => WorkstationTaskNumber.EnsureSafeForImport(id));

    [Theory]
    [InlineData("TEST-001")]
    [InlineData("123456789012345")]
    [InlineData("WS0123456789ABCDEF01")]
    [InlineData("W123456789012345")]
    public void Text_ids_and_existing_short_numeric_policy_are_preserved(string id) =>
        WorkstationTaskNumber.EnsureSafeForImport(id);
}
