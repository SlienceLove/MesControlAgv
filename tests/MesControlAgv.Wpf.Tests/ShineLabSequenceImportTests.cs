using System.IO;
using System.Text;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class ShineLabSequenceImportTests
{
    private const string ValidCsv =
        "样品名称,样品类型,样品等级,处理方法,清除校正,循环次数,进样体积,进样单位,空白,色谱方法\n" +
        "水样-01,未知样品,1,阴离子标准法,否,1,25,μL,否,阴离子常规\n" +
        "水样-02,未知样品,1,阴离子标准法,否,1,25,μL,否,阴离子常规\n";

    private static ShineLabSequenceImportResult ParseCsv(string csv)
    {
        var parser = new ShineLabSequenceParser();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        return parser.Parse(stream, "sequence.csv");
    }

    [Fact]
    public void Parser_reads_sample_rows_and_preserves_string_semantics()
    {
        var result = ParseCsv(ValidCsv);

        Assert.False(result.HasIssues);
        Assert.True(result.CanGenerateSequence);
        Assert.Equal(2, result.Tasks.Count);
        Assert.Equal("水样-01", result.Tasks[0].SampleName);
        Assert.Equal("阴离子常规", result.Tasks[0].ChromatographyMethod);
        Assert.Equal(2, result.Tasks[0].SourceRowNumber);
    }

    [Fact]
    public void Parser_preserves_leading_zero_in_cycle_and_volume()
    {
        var csv =
            "样品名称,样品类型,样品等级,处理方法,清除校正,进样体积,循环次数\n" +
            "水样-07,未知样品,07,阴离子标准法,否,07,01\n";

        var result = ParseCsv(csv);

        var task = Assert.Single(result.Tasks);
        Assert.Equal("07", task.InjectionVolume);
        Assert.Equal("01", task.CycleCount);
    }

    [Fact]
    public void Parser_defaults_missing_sample_name()
    {
        var csv =
            "样品名称,样品类型,样品等级,处理方法,清除校正,进样体积\n" +
            ",未知样品,1,阴离子标准法,否,25\n";

        var result = ParseCsv(csv);

        Assert.False(result.HasIssues);
        var task = Assert.Single(result.Tasks);
        Assert.Equal(ShineLabSequenceParser.DefaultSampleName, task.SampleName);
    }

    [Fact]
    public void Parser_rejects_sample_type_outside_shinelab_whitelist()
    {
        var csv =
            "样品名称,样品类型,进样体积\n" +
            "水样-09,未知,25\n";

        var result = ParseCsv(csv);

        Assert.True(result.HasIssues);
        Assert.False(result.CanGenerateSequence);
        Assert.Contains("未知", Assert.Single(result.Issues).Message);
    }

    [Fact]
    public void Parser_reports_empty_input_instead_of_generating_empty_sequence()
    {
        var result = ParseCsv("样品名称,样品类型,进样体积\n");

        Assert.False(result.CanGenerateSequence);
        Assert.Empty(result.Tasks);
    }

    [Fact]
    public void Parser_rejects_fields_that_shinelab_would_inherit_from_the_previous_row()
    {
        var csv =
            "样品名称,样品类型,样品等级,处理方法,清除校正,进样体积\n" +
            "水样-10,未知样品,,,否,25\n";

        var result = ParseCsv(csv);

        Assert.False(result.CanGenerateSequence);
        Assert.Empty(result.Tasks);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("样品等级"));
        Assert.Contains(result.Issues, issue => issue.Message.Contains("处理方法"));
    }

    [Fact]
    public void Writer_rejects_manually_constructed_tasks_with_ambiguous_blank_fields()
    {
        var task = new ShineLabSampleTask(
            2, "水样-11", "未知样品", "", "阴离子标准法", "否", "1", "25", "μL", "否", "阴离子常规");

        var error = Assert.Throws<InvalidOperationException>(() => ShineLabCsvWriter.Build([task]));

        Assert.Contains("样品等级", error.Message);
    }

    [Fact]
    public void Writer_emits_canonical_header_trailing_comma_and_crlf()
    {
        var csv = ShineLabCsvWriter.Build(ParseCsv(ValidCsv).Tasks);

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(ShineLabCsvWriter.CanonicalHeaderLine, lines[0]);
        Assert.EndsWith(",", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.Equal(13, line.Split(',').Length - 1));
    }

    [Fact]
    public void Writer_numbers_rows_sequentially_from_one()
    {
        var csv = ShineLabCsvWriter.Build(ParseCsv(ValidCsv).Tasks);

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("1,", lines[1]);
        Assert.StartsWith("2,", lines[2]);
    }

    [Fact]
    public void Writer_produces_utf8_without_bom()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shinelab-{Guid.NewGuid():N}.csv");
        try
        {
            ShineLabCsvWriter.Write(path, ParseCsv(ValidCsv).Tasks);

            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Sha256_is_stable_for_identical_content()
    {
        var first = ShineLabCsvWriter.Build(ParseCsv(ValidCsv).Tasks);
        var second = ShineLabCsvWriter.Build(ParseCsv(ValidCsv).Tasks);

        Assert.Equal(ShineLabCsvWriter.ComputeSha256(first), ShineLabCsvWriter.ComputeSha256(second));
    }
}
