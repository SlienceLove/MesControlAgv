using System.IO.Compression;
using System.Xml.Linq;
using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.WorkflowContract.Tests;

public sealed class WorkstationBatchWorkbookTests
{
    private static SampleWorkstationTaskTemplate Task(string id = "20260920000100000001") => new(id, "测试任务", [
        new("LIQ", "TIP", 0, 0, "SRC", 1, 1, "DST", 1, 1, 50),
        new("LIQ", "TIP", 0, 0, "SRC", 1, 1, "DST", 1, 2, 80)]);

    [Fact]
    public void Excel_groups_multiple_transfers_per_task_and_preserves_long_text_ids()
    {
        var tasks = new[] { Task(), Task("20260920000100000002") };
        var result = WorkstationBatchWorkbook.Read(WorkstationBatchWorkbook.Write(tasks));
        Assert.Equal(2, result.Count);
        for (var i = 0; i < 2; i++) Assert.True(SampleWorkstationTemplateFile.SameContent(tasks[i], result[i]));
    }

    [Theory]
    [InlineData("formula")]
    [InlineData("numeric-id")]
    [InlineData("name-conflict")]
    [InlineData("negative-volume")]
    [InlineData("missing-module")]
    public void Invalid_excel_fails_before_any_import(string kind)
    {
        using var memory = new MemoryStream(); memory.Write(WorkstationBatchWorkbook.Write([Task()])); memory.Position = 0;
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Update, true))
        {
            const string path = "xl/worksheets/sheet1.xml";
            XDocument sheet; using (var input = zip.GetEntry(path)!.Open()) sheet = XDocument.Load(input);
            XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var address = kind switch { "name-conflict" => "B3", "negative-volume" => "M2", "missing-module" => "G2", _ => "A2" };
            var cell = sheet.Descendants(s + "c").Single(c => (string?)c.Attribute("r") == address);
            if (kind == "formula") cell.Add(new XElement(s + "f", "1+1"));
            else if (kind == "numeric-id") { cell.Attribute("t")!.Remove(); cell.RemoveNodes(); cell.Add(new XElement(s + "v", "20260920000100000001")); }
            else cell.Descendants(s + "t").Single().Value = kind switch { "name-conflict" => "不同名称", "negative-volume" => "-1", _ => "" };
            zip.GetEntry(path)!.Delete(); using var output = zip.CreateEntry(path).Open(); sheet.Save(output);
        }
        Assert.Throws<ArgumentException>(() => WorkstationBatchWorkbook.Read(memory.ToArray()));
    }

    [Fact]
    public void Duplicate_case_variants_and_overlong_task_numbers_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => WorkstationBatchWorkbook.Validate([Task("ABC"), Task("abc")]));
        Assert.Throws<ArgumentException>(() => WorkstationBatchWorkbook.Validate([Task(new string('1', 21))]));
        Assert.Throws<ArgumentException>(() => WorkstationBatchWorkbook.Validate([]));
    }
}
