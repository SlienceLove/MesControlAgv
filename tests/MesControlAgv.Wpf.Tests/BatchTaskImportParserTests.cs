using System.IO.Compression;
using System.Text;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class BatchTaskImportParserTests
{
    [Fact]
    public void Csv_parser_reads_rows_and_sorts_by_priority_then_planned_time()
    {
        const string csv = "Task ID,Start,End,Description,Priority,Planned Time\n" +
                           "T-LOW,S01,S02,Normal task,1,2026-08-04 08:00\n" +
                           "T-HIGH-LATE,S03,S04,\"High, later\",10,2026-08-04 10:00\n" +
                           "T-HIGH-EARLY,S05,S06,High task,10,2026-08-04 09:00\n";
        var parser = new BatchTaskImportParser();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var result = parser.Parse(stream, "tasks.csv");

        Assert.False(result.HasErrors);
        Assert.Equal(["T-HIGH-EARLY", "T-HIGH-LATE", "T-LOW"], result.Tasks.Select(task => task.TaskId));
        Assert.Equal("High, later", result.Tasks[1].Description);
        Assert.Equal(new DateTime(2026, 8, 4, 9, 0, 0), result.Tasks[0].PlannedTime);
    }

    [Fact]
    public void Xlsx_parser_reads_shared_strings_and_numeric_cells()
    {
        using var stream = CreateWorkbook();
        var parser = new BatchTaskImportParser();

        var result = parser.Parse(stream, "tasks.xlsx");

        var task = Assert.Single(result.Tasks);
        Assert.False(result.HasErrors);
        Assert.Equal("XLSX-001", task.TaskId);
        Assert.Equal("ST-START", task.SourceStation);
        Assert.Equal("ST-END", task.TargetStation);
        Assert.Equal("From Excel", task.Description);
        Assert.Equal(7, task.Priority);
        Assert.Equal(new DateTime(2026, 8, 4, 12, 30, 0), task.PlannedTime);
    }

    [Fact]
    public void Invalid_rows_are_reported_without_discarding_valid_rows()
    {
        const string csv = "\u4EFB\u52A1 ID,\u8D77\u70B9,\u7EC8\u70B9,\u4EFB\u52A1\u63CF\u8FF0,\u4F18\u5148\u7EA7,\u8BA1\u5212\u65F6\u95F4\n" +
                           "OK-001,A,B,valid,3,\n" +
                           ",A,B,missing id,2,\n" +
                           "BAD-PRIORITY,A,B,invalid,urgent-not-number,\n";
        var parser = new BatchTaskImportParser();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var result = parser.Parse(stream, "tasks.csv");

        Assert.Single(result.Tasks);
        Assert.Equal("OK-001", result.Tasks[0].TaskId);
        Assert.Equal(2, result.Issues.Count);
    }

    private static MemoryStream CreateWorkbook()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Tasks\" sheetId=\"1\" r:id=\"rId1\" /></sheets></workbook>");
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"worksheets/sheet1.xml\" /></Relationships>");
            WriteEntry(archive, "xl/sharedStrings.xml", "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>Task ID</t></si><si><t>Start</t></si><si><t>End</t></si><si><t>Description</t></si><si><t>Priority</t></si><si><t>Planned Time</t></si><si><t>XLSX-001</t></si><si><t>ST-START</t></si><si><t>ST-END</t></si><si><t>From Excel</t></si></sst>");
            WriteEntry(archive, "xl/worksheets/sheet1.xml", "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c><c r=\"D1\" t=\"s\"><v>3</v></c><c r=\"E1\" t=\"s\"><v>4</v></c><c r=\"F1\" t=\"s\"><v>5</v></c></row><row r=\"2\"><c r=\"A2\" t=\"s\"><v>6</v></c><c r=\"B2\" t=\"s\"><v>7</v></c><c r=\"C2\" t=\"s\"><v>8</v></c><c r=\"D2\" t=\"s\"><v>9</v></c><c r=\"E2\"><v>7</v></c><c r=\"F2\"><v>46238.5208333333</v></c></row></sheetData></worksheet>");
        }
        stream.Position = 0;
        return stream;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
