using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Tests;

public sealed class SampleWorkstationZeroTipTemplateTests
{
    internal static byte[] CapturedBytes()
    {
        using var response = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "workstation-test-zero-tip-response.json")));
        Assert.Equal(200, response.RootElement.GetProperty("code").GetInt32());
        var data = response.RootElement.GetProperty("data");
        Assert.Equal("实验任务模板_test[test].xlsx", data.GetProperty("FileName").GetString());
        return Convert.FromBase64String(data.GetProperty("FileData").GetString()!);
    }

    [Fact]
    public void Captured_two_source_template_keeps_default_tips_and_clones_all_sixteen_transfers_at_50ul()
    {
        var original = SampleWorkstationTemplateFile.Read(CapturedBytes());
        Assert.Equal("test", original.TaskNo);
        Assert.Equal(16, original.Transfers.Count);
        Assert.All(original.Transfers, row =>
        {
            Assert.Equal("QT-001", row.TipModule);
            Assert.Equal((0, 0), (row.TipX, row.TipY));
            Assert.Equal((1, 1), (row.SourceX, row.SourceY));
            Assert.Equal(1000, row.VolumeMicroliters);
        });
        var expectedPositions = new[] { (1, 1), (1, 2), (1, 3), (1, 4), (2, 1), (2, 2), (2, 3), (2, 4) };
        for (var bottle = 1; bottle <= 2; bottle++)
        {
            var rows = original.Transfers.Skip((bottle - 1) * 8).Take(8).ToArray();
            Assert.All(rows, row =>
            {
                Assert.Equal($"CYC-00{bottle}-1000", row.SourceModule);
                Assert.Equal($"FYB-00{bottle}", row.TargetModule);
            });
            Assert.Equal(expectedPositions, rows.Select(row => (row.TargetX, row.TargetY)).ToArray());
        }

        var preview = original with
        {
            TaskNo = "LOCAL-test-50ul-preview",
            TaskName = "两来源50uL本地预览",
            Transfers = original.Transfers.Select(row => row with { VolumeMicroliters = 50 }).ToArray()
        };
        var generated = SampleWorkstationTemplateFile.Write(preview);
        var readback = SampleWorkstationTemplateFile.Read(generated);
        Assert.True(SampleWorkstationTemplateFile.SameContent(preview, readback));
        Assert.NotEqual(original.TaskNo, readback.TaskNo);
        Assert.Equal(original.Transfers.Select(row => row with { VolumeMicroliters = 50 }), readback.Transfers);
        Assert.Equal(800, readback.Transfers.Sum(row => row.VolumeMicroliters));
        Assert.All(readback.Transfers.GroupBy(row => row.SourceModule), group => Assert.Equal(400, group.Sum(row => row.VolumeMicroliters)));
        Assert.All(original.Transfers, row => Assert.Equal(1000, row.VolumeMicroliters));
        Assert.Equal(generated, SampleWorkstationTemplateFile.Write(preview));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    public void Complete_default_or_positive_tip_pair_roundtrips_without_reinterpretation(int x, int y)
    {
        var template = Template(Row() with { TipX = x, TipY = y });
        var readback = SampleWorkstationTemplateFile.Read(SampleWorkstationTemplateFile.Write(template));
        Assert.Equal((x, y), (readback.Transfers[0].TipX, readback.Transfers[0].TipY));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void Invalid_tip_pairs_are_rejected_on_both_read_and_write(int x, int y)
    {
        Assert.Throws<ArgumentException>(() => SampleWorkstationTemplateFile.Write(Template(Row() with { TipX = x, TipY = y })));
        var malformed = ChangeNumericCells(CapturedBytes(), ("C2", x.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("D2", y.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Throws<ArgumentException>(() => SampleWorkstationTemplateFile.Read(malformed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.0")]
    [InlineData("default")]
    [InlineData("-0")]
    public void Missing_or_non_integer_tip_values_are_not_treated_as_default(string value) =>
        Assert.Throws<ArgumentException>(() => SampleWorkstationTemplateFile.Read(ChangeNumericCells(CapturedBytes(), ("C2", value))));

    [Theory]
    [InlineData("F2", "source-x")]
    [InlineData("G2", "source-y")]
    [InlineData("I2", "target-x")]
    [InlineData("J2", "target-y")]
    [InlineData("K2", "volume")]
    public void Default_tip_does_not_relax_source_target_or_volume_validation(string cell, string field)
    {
        Assert.Throws<ArgumentException>(() => SampleWorkstationTemplateFile.Read(ChangeNumericCells(CapturedBytes(), (cell, "0"))));
        var row = Row() with { TipX = 0, TipY = 0 };
        row = field switch
        {
            "source-x" => row with { SourceX = 0 },
            "source-y" => row with { SourceY = 0 },
            "target-x" => row with { TargetX = 0 },
            "target-y" => row with { TargetY = 0 },
            _ => row with { VolumeMicroliters = 0 }
        };
        Assert.Throws<ArgumentException>(() => SampleWorkstationTemplateFile.Write(Template(row)));
    }

    private static SampleWorkstationTransferRow Row() => new("CGRJ-001", "QT-001", 1, 1, "CYC-001-1000", 1, 1, "FYB-001", 1, 1, 50);
    private static SampleWorkstationTaskTemplate Template(SampleWorkstationTransferRow row) => new("LOCAL-test", "Local test", [row]);

    private static byte[] ChangeNumericCells(byte[] bytes, params (string Cell, string Value)[] changes)
    {
        using var stream = new MemoryStream();
        stream.Write(bytes);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry("xl/worksheets/sheet2.xml")!;
            XDocument document;
            using (var source = entry.Open()) document = XDocument.Load(source);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            foreach (var (cell, value) in changes)
                document.Descendants(ns + "c").Single(item => (string?)item.Attribute("r") == cell).Element(ns + "v")!.Value = value;
            entry.Delete();
            using var output = zip.CreateEntry("xl/worksheets/sheet2.xml").Open();
            document.Save(output);
        }
        return stream.ToArray();
    }
}
