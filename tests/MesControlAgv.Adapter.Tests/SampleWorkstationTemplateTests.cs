using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MesControlAgv.Adapter.Modules;
using MesControlAgv.Adapter.Modules.SampleWorkstation;
using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Tests;

public sealed class SampleWorkstationTemplateTests
{
    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "workstation-test-001.xlsx"));
    private static SampleWorkstationTaskTemplate Example() => SampleWorkstationTemplateFile.Read(Fixture());

    [Fact]
    public void Captured_template_matches_actual_source_and_target_and_roundtrips_with_new_identity()
    {
        var bytes = Fixture();
        Assert.Equal("1BD82711494D5BAAECB8A846E55F08601C5BA2E0FE3E06670551526B496BCD78", Convert.ToHexString(SHA256.HashData(bytes)));
        var model = SampleWorkstationTemplateFile.Read(bytes);
        Assert.Equal("TEST-001", model.TaskNo);
        Assert.Equal(new SampleWorkstationTransferRow("CGRJ-001", "QT-001", 1, 1, "CYC-001-1000", 1, 1, "FYB-001", 1, 1, 50), Assert.Single(model.Transfers));
        var generated = model with { TaskNo = "0001-来源&A", TaskName = "中控创建任务", Transfers = [model.Transfers[0], model.Transfers[0] with { TargetY = 2 }] };
        var readback = SampleWorkstationTemplateFile.Read(SampleWorkstationTemplateFile.Write(generated));
        Assert.True(SampleWorkstationTemplateFile.SameContent(generated, readback));
    }

    [Theory]
    [InlineData("task.xlsx")]
    [InlineData("任务模板.xlsx")]
    public void Upload_framing_is_decodable_by_vendor_stream_algorithm(string name)
    {
        var bytes = Fixture();
        var encoded = SampleWorkstationTemplateFile.EncodeUpload(name, bytes);
        var length = Convert.FromBase64String(Encoding.ASCII.GetString(encoded, 0, 4))[0];
        var decodedName = Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.ASCII.GetString(encoded, 4, length)));
        var decodedFile = Convert.FromBase64String(Encoding.ASCII.GetString(encoded, 4 + length, encoded.Length - 4 - length));
        Assert.Equal(name, decodedName);
        Assert.Equal(bytes, decodedFile);
    }

    [Theory]
    [InlineData("../task.xlsx")]
    [InlineData("C:\\task.xlsx")]
    [InlineData("task.xls")]
    [InlineData(" task.xlsx")]
    [InlineData("task?.xlsx")]
    [InlineData("task*.xlsx")]
    [InlineData("CON.xlsx")]
    [InlineData("LPT1.xlsx")]
    public void Invalid_vendor_filename_is_rejected(string name) =>
        Assert.Throws<ArgumentException>(() => SampleWorkstationTemplateFile.EncodeUpload(name, Fixture()));

    [Fact]
    public void Long_unicode_filename_cannot_overflow_one_byte_vendor_prefix() =>
        Assert.Throws<ArgumentException>(() => SampleWorkstationTemplateFile.EncodeUpload(new string('样', 80) + ".xlsx", Fixture()));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Whitespace_source_code_is_not_silently_normalized_on_upload_or_readback(bool readback)
    {
        var bytes = Fixture();
        using var copy = new MemoryStream();
        copy.Write(bytes);
        using (var zip = new ZipArchive(copy, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry("xl/sharedStrings.xml")!;
            XDocument document;
            using (var source = entry.Open()) document = XDocument.Load(source);
            XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            document.Descendants(s + "t").Single(e => e.Value == "CYC-001-1000").Value = "CYC-001-1000 ";
            entry.Delete();
            using var replacement = zip.CreateEntry("xl/sharedStrings.xml").Open();
            document.Save(replacement);
        }
        var malformed = copy.ToArray();
        var handler = new Handler("""{"Code":200,"Data":"导入成功"}""", Download(malformed));
        using var input = new MemoryStream(readback ? bytes : malformed);
        if (readback)
        {
            await Assert.ThrowsAsync<SampleWorkstationProtocolException>(() => Driver(handler).ImportTasksAsync("WS-01", "task.xlsx", input, CancellationToken.None));
            Assert.Equal(2, handler.Calls.Count);
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Driver(handler).ImportTasksAsync("WS-01", "task.xlsx", input, CancellationToken.None));
            Assert.Empty(handler.Calls);
        }
    }

    [Theory]
    [InlineData("task?.xlsx")]
    [InlineData("CON.xlsx")]
    public async Task Invalid_windows_filename_never_reaches_vendor(string fileName)
    {
        var handler = new Handler();
        using var input = new MemoryStream(Fixture());
        await Assert.ThrowsAsync<ArgumentException>(() => Driver(handler).ImportTasksAsync("WS-01", fileName, input, CancellationToken.None));
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("formula")]
    [InlineData("header")]
    [InlineData("multiple-tasks")]
    [InlineData("volume")]
    public void Unsupported_or_ambiguous_workbooks_fail_before_transport(string change)
    {
        var bytes = SampleWorkstationTemplateFile.Write(Example());
        using var stream = new MemoryStream();
        stream.Write(bytes);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var part = change == "multiple-tasks" ? "xl/worksheets/sheet1.xml" : "xl/worksheets/sheet2.xml";
            var entry = zip.GetEntry(part)!;
            XDocument document;
            using (var source = entry.Open()) document = XDocument.Load(source);
            XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            if (change == "multiple-tasks") document.Descendants(s + "sheetData").Single().Add(new XElement(document.Descendants(s + "row").Last()));
            else if (change == "formula") document.Descendants(s + "c").Last().Add(new XElement(s + "f", "1+1"));
            else if (change == "header") document.Descendants(s + "t").First().Value = "unknown column";
            else document.Descendants(s + "t").Last().Value = "0";
            entry.Delete();
            using var replacement = zip.CreateEntry(part).Open();
            document.Save(replacement);
        }
        Assert.Throws<ArgumentException>(() => SampleWorkstationTemplateFile.Read(stream.ToArray()));
    }

    [Fact]
    public async Task Download_checks_workbook_task_not_just_success_code_or_filename()
    {
        var handler = new Handler(Download(Fixture()));
        var driver = Driver(handler);
        var error = await Assert.ThrowsAsync<SampleWorkstationProtocolException>(() =>
            driver.GetTaskTemplateAsync("WS-01", "NOT-TEST-001", CancellationToken.None));
        Assert.Equal(SampleWorkstationErrorCodes.TemplateMismatch, error.ErrorCode);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Import_posts_once_then_verifies_all_transfers_and_does_not_close_caller_stream()
    {
        var bytes = Fixture();
        var handler = new Handler("""{"Code":200,"Data":"导入成功"}""", Download(bytes));
        var driver = Driver(handler);
        using var source = new MemoryStream(bytes);
        var result = await driver.ImportTasksAsync("WS-01", "task.xlsx", source, CancellationToken.None);
        Assert.True(source.CanRead);
        Assert.True(result.ReadbackVerified);
        Assert.Equal(["TEST-001"], result.TaskNos);
        Assert.True(SampleWorkstationTemplateFile.SameContent(Example(), result.Template!));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), result.FileSha256);
        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal(HttpMethod.Post, handler.Calls[0].Method);
        Assert.Equal("application/json", handler.ContentTypes[0]);
        Assert.Equal("/Service/ImportExperimentalTask", handler.Calls[0].Uri.AbsolutePath);
        Assert.Equal(SampleWorkstationTemplateFile.EncodeUpload("task.xlsx", bytes), handler.Calls[0].Body);
        Assert.Equal(HttpMethod.Get, handler.Calls[1].Method);
        Assert.Equal("/Service/GetExperimentalTaskTemplate?TaskNo=TEST-001", handler.Calls[1].Uri.PathAndQuery);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("volume")]
    [InlineData("task")]
    public async Task Successful_ack_with_wrong_readback_is_not_success(string changed)
    {
        var actual = Example();
        var row = actual.Transfers[0];
        actual = changed == "task" ? actual with { TaskNo = "OTHER" } : actual with { Transfers = [changed switch
        {
            "source" => row with { SourceModule = "OTHER" },
            "target" => row with { TargetY = 2 },
            _ => row with { VolumeMicroliters = 100 }
        }] };
        var handler = new Handler("""{"Code":200,"Data":"导入成功"}""", Download(SampleWorkstationTemplateFile.Write(actual)));
        using var source = new MemoryStream(Fixture());
        var error = await Assert.ThrowsAsync<SampleWorkstationProtocolException>(() =>
            Driver(handler).ImportTasksAsync("WS-01", "task.xlsx", source, CancellationToken.None));
        Assert.Equal(SampleWorkstationErrorCodes.TemplateMismatch, error.ErrorCode);
        Assert.Equal(2, handler.Calls.Count);
        Assert.Single(handler.Calls.Where(c => c.Method == HttpMethod.Post));
    }

    [Theory]
    [InlineData("{\"Code\":200,\"Data\":\"接收成功\"}")]
    [InlineData("{\"Code\":201,\"Data\":\"导入失败\"}")]
    public async Task Unconfirmed_import_is_not_retried_or_started(string response)
    {
        var handler = new Handler(response);
        using var source = new MemoryStream(Fixture());
        await Assert.ThrowsAsync<SampleWorkstationProtocolException>(() => Driver(handler).ImportTasksAsync("WS-01", "task.xlsx", source, CancellationToken.None));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Disabled_import_never_reads_or_sends_the_file()
    {
        var handler = new Handler();
        using var source = new MemoryStream(Fixture());
        await Assert.ThrowsAsync<DeviceControlDisabledException>(() => Driver(handler, false).ImportTasksAsync("WS-01", "task.xlsx", source, CancellationToken.None));
        Assert.Equal(0, source.Position);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Timeout_posts_only_once_and_never_starts()
    {
        var handler = new Handler { Failure = new TaskCanceledException("timeout") };
        using var source = new MemoryStream(Fixture());
        await Assert.ThrowsAsync<TaskCanceledException>(() => Driver(handler).ImportTasksAsync("WS-01", "task.xlsx", source, CancellationToken.None));
        Assert.Equal("/Service/ImportExperimentalTask", Assert.Single(handler.Calls).Uri.AbsolutePath);
    }

    private static string Download(byte[] bytes) => JsonSerializer.Serialize(new { Code = 200, Data = new { FileName = "task.xlsx", FileData = Convert.ToBase64String(bytes) } });
    private static SampleWorkstationDriver Driver(Handler handler, bool control = true) => new(
        new VendorSampleWorkstationHttpClient(new HttpClient(handler) { BaseAddress = new Uri("http://vendor.invalid/Service/") }),
        new SampleWorkstationOptions { DeviceId = "WS-01", EquipmentNo = "EQ-01", Enabled = true, ControlEnabled = control }, TimeProvider.System);
    private sealed class Handler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);
        public Exception? Failure { get; init; }
        public List<(HttpMethod Method, Uri Uri, byte[] Body)> Calls { get; } = [];
        public List<string?> ContentTypes { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Method, request.RequestUri!, request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken)));
            ContentTypes.Add(request.Content?.Headers.ContentType?.MediaType);
            if (Failure is not null) throw Failure;
            return new(HttpStatusCode.OK) { Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json") };
        }
    }
}
