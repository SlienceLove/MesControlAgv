using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using MesControlAgv.Reporting;

namespace MesControlAgv.Reporting.Tests;

public sealed class ReportTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "mes-report-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-21T10:00:00+08:00");
    public ReportTests() => Directory.CreateDirectory(_temp);
    private string FileName(string name) => Path.Combine(_temp, name);
    private static ReportJob Job(string id = "1", string status = "Completed", DateTimeOffset? start = null, DateTimeOffset? end = null) =>
        new(id, "00001", "B1", status, Now, start, end, null);
    public static ReportSnapshot Example() => new("snapshot-1", Now, new(Now.AddDays(-1), Now.AddDays(1)),
        [Job(start: Now, end: Now.AddSeconds(30)), Job("2", "Failed")],
        [new("0001", "00001", "B1", "氯离子", 12.3m, "mg/L", Now, "实验室", "ABC", 2)], []);

    [Fact] public void StatisticsExcludeCancelledAndInvalidTimesAndDeduplicate()
    {
        var s = ReportRules.Calculate([Job(), Job(), Job("2", "Failed"), Job("3", "Cancelled"),
            Job("4", start: Now, end: Now.AddSeconds(20)), Job("5", "Running"), Job("6", start: Now, end: Now.AddSeconds(-1))]);
        Assert.Equal(6, s.Total); Assert.Equal(75m, s.SuccessPercent); Assert.Equal(20, s.AverageSeconds);
        Assert.Equal(2, s.InvalidCompletedTimes); Assert.Equal(1, s.Other);
    }
    [Fact] public void EmptyAndCancelledHaveNoSuccessRate() => Assert.Null(ReportRules.Calculate([Job(status: "Cancelled")]).SuccessPercent);
    [Fact] public void UnitsRemainSeparate() => Assert.Equal(2, ReportRules.Aggregate([
        Example().Measurements[0], Example().Measurements[0] with { Unit = "ug/L" }]).Length);
    [Fact] public void QueryRejectsReversedOrTooLongRanges()
    {
        Assert.Throws<ArgumentException>(() => ReportRules.ValidateQuery(new(Now, Now)));
        Assert.Throws<ArgumentException>(() => ReportRules.ValidateQuery(new(Now, Now.AddDays(367))));
    }
    [Fact] public void TemplateHasTextIdentifierColumnsAndFrozenHeader()
    {
        var path = FileName("template.xlsx"); MeasurementFiles.WriteTemplate(path);
        using var book = new XLWorkbook(path);
        Assert.Equal("@", book.Worksheet("原始记录").Column(2).Style.NumberFormat.Format);
        Assert.Equal(1, book.Worksheet("原始记录").SheetView.SplitRow);
        Assert.False(MeasurementFiles.Read(path).CanAccept);
    }
    [Fact] public void ExcelRoundtripPreservesLeadingZeroAndProvenance()
    {
        var path = FileName("report.xlsx"); ReportFiles.WriteExcel(path, Example(), null);
        var preview = MeasurementFiles.Read(path);
        Assert.True(preview.CanAccept); Assert.Equal("00001", preview.Records[0].SampleId);
        Assert.Equal(12.3m, preview.Records[0].Value); Assert.Equal(64, preview.FileSha256.Length);
        using var book = new XLWorkbook(path);
        Assert.Equal(new[] { "原始记录", "MES任务明细", "统计汇总", "AI摘要与建议" }, book.Worksheets.Select(w => w.Name));
        Assert.Equal(XLDataType.Text, book.Worksheet("原始记录").Cell(2, 2).DataType);
    }
    [Fact] public void FormulaLikeTextIsNotExecutableInExcel()
    {
        var snapshot = Example() with { Measurements = [Example().Measurements[0] with { Source = "=HYPERLINK(\"http://invalid\")" }] };
        var path = FileName("safe.xlsx"); ReportFiles.WriteExcel(path, snapshot, null);
        using var book = new XLWorkbook(path); Assert.False(book.Worksheet("原始记录").Cell(2, 8).HasFormula);
    }
    [Fact] public void FormulaCellsAreRejectedBeforePreview()
    {
        var path = FileName("formula.xlsx"); ReportFiles.WriteExcel(path, Example(), null);
        using (var book = new XLWorkbook(path)) { book.Worksheet("原始记录").Cell(2, 5).FormulaA1 = "1+1"; book.Save(); }
        Assert.Throws<InvalidDataException>(() => MeasurementFiles.Read(path));
    }
    [Fact] public void NumericIdentifiersAreRejectedRatherThanLosingZeros()
    {
        var path = FileName("numeric.xlsx"); ReportFiles.WriteExcel(path, Example(), null);
        using (var book = new XLWorkbook(path)) { book.Worksheet("原始记录").Cell(2, 2).Value = 1; book.Save(); }
        Assert.Throws<InvalidDataException>(() => MeasurementFiles.Read(path));
    }
    [Fact] public void DuplicateAndMissingTimezoneProduceIssues()
    {
        var row = ReportFiles.MeasurementValues(Example().Measurements[0]);
        var preview = MeasurementFiles.Validate([MeasurementFiles.Headers, row, row], "hash");
        Assert.False(preview.CanAccept); Assert.Contains(preview.Issues, i => i.Message.Contains("重复"));
        row[6] = "2026-09-21 10:00:00";
        Assert.False(MeasurementFiles.Validate([MeasurementFiles.Headers, row], "hash").CanAccept);
    }
    [Fact] public void MalformedHeadersRejected() => Assert.Throws<InvalidDataException>(() => MeasurementFiles.Validate([["wrong"]], "hash"));
    [Fact] public void DecimalMinValueReportedNotThrown()
    {
        var row = ReportFiles.MeasurementValues(Example().Measurements[0]); row[4] = decimal.MinValue.ToString();
        Assert.False(MeasurementFiles.Validate([MeasurementFiles.Headers, row], "hash").CanAccept);
    }
    [Fact] public void CsvHandlesQuotesCommaAndMultiline()
    {
        var snapshot = Example() with { Measurements = [Example().Measurements[0] with { Source = "a,b\n\"c\"" }] };
        var path = FileName("roundtrip.csv"); ReportFiles.WriteCsv(path, snapshot);
        Assert.Equal(snapshot.Measurements[0].Source, MeasurementFiles.Read(path).Records[0].Source);
    }
    [Theory][InlineData("=0001")][InlineData("'0001")][InlineData("+0001")]
    public void CsvRoundtripPreservesNegativeValuesAndSpecialIds(string id)
    {
        var path = FileName("negative.csv");
        ReportFiles.WriteCsv(path, Example() with { Measurements = [Example().Measurements[0] with { RecordId = id, Value = -1.2m }] });
        var m = Assert.Single(MeasurementFiles.Read(path).Records); Assert.Equal(id, m.RecordId); Assert.Equal(-1.2m, m.Value);
    }
    [Fact] public void HighPrecisionExcelIsExactText()
    {
        var path = FileName("precise.xlsx"); const decimal value = 0.123456789012345678901m;
        ReportFiles.WriteExcel(path, Example() with { Measurements = [Example().Measurements[0] with { Value = value }] }, null);
        Assert.Equal(value, Assert.Single(MeasurementFiles.Read(path).Records).Value);
    }
    [Theory][InlineData("=1+1")][InlineData(" @test")][InlineData("\tCMD")][InlineData("-cmd")]
    public void CsvFormulaProtection(string value) => Assert.StartsWith("\"'", ReportFiles.CsvCell(value));
    [Fact] public void HtmlEscapesRawAndAiContent()
    {
        var path = FileName("safe.html"); ReportFiles.WriteHtml(path, Example(), new(true, "", "test", "<script>alert(1)</script>", ["S1"], Now));
        var html = File.ReadAllText(path); Assert.DoesNotContain("<script>", html); Assert.Contains("&lt;script&gt;", html);
    }
    [Fact] public void OversizedFileRejected()
    {
        var path = FileName("large.xlsx"); using (var file = File.Create(path)) file.SetLength(ReportRules.MaxFileBytes + 1);
        Assert.Throws<InvalidDataException>(() => MeasurementFiles.Read(path));
    }
    [Fact] public void ExpandedZipLimitRejected()
    {
        var path = FileName("compressed.xlsx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry("xl/workbook.xml").Open()) stream.Write(new byte[31 * 1024 * 1024]);
        Assert.Throws<InvalidDataException>(() => MeasurementFiles.Read(path));
    }
    [Fact] public async Task ModelRequestUsesBoundedCpuOptionsAndValidatedReferences()
    {
        string? body = null;
        using var client = new OllamaReportClient(new HttpClient(new Handler(async (request, token) =>
        { body = await request.Content!.ReadAsStringAsync(token); return Reply("stop", "{\"text\":\"建议核查失败任务。\",\"references\":[\"S1\"]}"); })) { BaseAddress = new("http://127.0.0.1:11434/") });
        var result = await client.SummarizeAsync(Example() with { Measurements = [] }, default);
        Assert.True(result.Available); using var json = JsonDocument.Parse(body!);
        Assert.False(json.RootElement.GetProperty("think").GetBoolean());
        Assert.Equal(0, json.RootElement.GetProperty("options").GetProperty("num_gpu").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("keep_alive").GetInt32());
    }
    [Theory][InlineData("length", "S1")][InlineData("stop", "invented")]
    public async Task ModelRejectsTruncatedAndUnrelatedReference(string reason, string reference)
    {
        using var client = Model((_, _) => Task.FromResult(Reply(reason, JsonSerializer.Serialize(new { text = "文字", references = new[] { reference } }))));
        Assert.False((await client.SummarizeAsync(Example(), default)).Available);
    }
    [Fact] public async Task RemoteEndpointIsRejectedWithoutNetwork()
    {
        using var client = new OllamaReportClient(new HttpClient(new Handler((_, _) => throw new Exception("Must not call"))) { BaseAddress = new("http://example.com/") });
        Assert.False((await client.SummarizeAsync(Example(), default)).Available);
    }
    [Fact] public async Task NumericClaimsRejectedEvenWithValidReference()
    {
        using var client = Model((_, _) => Task.FromResult(Reply("stop", "{\"text\":\"成功率为99%\",\"references\":[\"S1\"]}")));
        Assert.False((await client.SummarizeAsync(Example(), default)).Available);
    }
    [Fact] public async Task ConcurrentRequestDoesNotQueueAndCancellationPropagates()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Model(async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new Exception(); });
        using var cancel = new CancellationTokenSource(); var pending = client.SummarizeAsync(Example(), cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("另一份", (await client.SummarizeAsync(Example(), default)).Message);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
    [Fact] public async Task OfflineReturnsUnavailable()
    {
        using var client = Model((_, _) => throw new HttpRequestException());
        Assert.False((await client.SummarizeAsync(Example(), default)).Available);
    }
    private static OllamaReportClient Model(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> handler) =>
        new(new HttpClient(new Handler(handler)) { BaseAddress = new("http://127.0.0.1:11434/") });
    private static HttpResponseMessage Reply(string reason, string content) => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(new { done = true, done_reason = reason, message = new { content } }) };
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> handler) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handler(request, token); }
    public void Dispose() => Directory.Delete(_temp, true);
}
