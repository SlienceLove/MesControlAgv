using System.Text;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class SampleImportParserTests
{
    [Fact]
    public void Csv_template_supports_chinese_headers_and_optional_run()
    {
        const string csv = "样品编号,样品条码,样品批次,来源库位,容器位置,流程RunId\n" +
                           "S-001,BC-001,B-001,WH-A-01,A1,00000000-0000-0000-0000-000000000001\n" +
                           "S-002,BC-002,B-001,WH-A-01,A2,\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = new SampleImportParser().Parse(stream, "samples.csv");

        Assert.Empty(result.Issues);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("S-001", result.Rows[0].SampleId);
        Assert.Equal("BC-002", result.Rows[1].Barcode);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), result.Rows[0].RunId);
        Assert.Null(result.Rows[1].RunId);
    }

    [Fact]
    public void Parser_reports_duplicate_and_invalid_run_rows_without_dropping_valid_rows()
    {
        const string csv = "SampleId,Barcode,SampleBatchId,SourceLocation,RunId\n" +
                           "S-001,BC-001,B-001,WH-A-01,not-a-guid\n" +
                           "S-001,BC-002,B-001,WH-A-01,\n" +
                           "S-003,BC-003,B-001,WH-A-01,\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = new SampleImportParser().Parse(stream, "samples.csv");

        Assert.Single(result.Rows);
        Assert.Equal("S-003", result.Rows[0].SampleId);
        Assert.Equal(2, result.Issues.Count);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("GUID", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Issues, issue => issue.Message.Contains("重复", StringComparison.Ordinal));
    }
}
