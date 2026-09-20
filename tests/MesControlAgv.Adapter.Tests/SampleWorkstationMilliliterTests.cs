using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MesControlAgv.Adapter.Modules.SampleWorkstation;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using Microsoft.Extensions.Configuration;

namespace MesControlAgv.Adapter.Tests;

public sealed class SampleWorkstationMilliliterTests
{
    private const SampleWorkstationTemplateVolumeUnit Ml = SampleWorkstationTemplateVolumeUnit.Milliliters;
    private static SampleWorkstationTaskTemplate Template(int volume = 50) => new("WS0123456789ABCDEF01", "50 µL test",
        [new("CGRJ-001", "QT-001", 0, 0, "CYC-001-1000", 1, 1, "FYB-001", 1, 1, volume)]);

    [Theory]
    [InlineData(1, "0.001")]
    [InlineData(50, "0.05")]
    [InlineData(1000, "1")]
    [InlineData(int.MaxValue, "2147483.647")]
    public void Milliliter_roundtrip_is_exact_and_culture_independent(int microliters, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var bytes = SampleWorkstationTemplateFile.Write(Template(microliters), Ml);
            Assert.Equal(expected, Cell(bytes));
            Assert.Equal(microliters, SampleWorkstationTemplateFile.Read(bytes, Ml).Transfers.Single().VolumeMicroliters);
            Assert.Equal(bytes, SampleWorkstationTemplateFile.Write(Template(microliters), Ml));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.05")]
    [InlineData("0.0005")]
    [InlineData("0.0010000000000000000000000000001")]
    [InlineData("2147483.648")]
    [InlineData("0,05")]
    [InlineData("1e-3")]
    [InlineData("NaN")]
    public void Invalid_milliliter_volume_is_not_rounded_or_guessed(string value) =>
        Assert.Throws<ArgumentException>(() => SampleWorkstationTemplateFile.Read(ChangeCell(value), Ml));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonpositive_central_volume_cannot_be_written(int value) =>
        Assert.Throws<ArgumentException>(() => SampleWorkstationTemplateFile.Write(Template(value), Ml));

    [Fact]
    public void Legacy_defaults_and_undefined_units_are_explicit()
    {
        var template = Template();
        Assert.Equal("50", Cell(SampleWorkstationTemplateFile.Write(template)));
        var original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "workstation-test-001.xlsx"));
        Assert.Equal(50, SampleWorkstationTemplateFile.Read(original).Transfers.Single().VolumeMicroliters);
        Assert.Throws<ArgumentException>(() => SampleWorkstationTemplateFile.Read(original, (SampleWorkstationTemplateVolumeUnit)9));
        Assert.Throws<ArgumentException>(() => SampleWorkstationTemplateFile.Write(template, (SampleWorkstationTemplateVolumeUnit)9));
        Assert.Throws<InvalidOperationException>(() => SampleWorkstationOptions.BindAndValidate(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Devices:SampleWorkstation:TemplateVolumeUnit"] = "9" }).Build()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Milliliter_upload_preserves_bytes_and_checks_normalized_readback(bool drift)
    {
        var bytes = SampleWorkstationTemplateFile.Write(Template(), Ml);
        using var handler = new Handler("""{"Code":200,"Data":"导入成功"}""", Download(SampleWorkstationTemplateFile.Write(Template(drift ? 51 : 50), Ml)));
        var driver = Driver(handler);
        using var source = new MemoryStream(bytes);
        if (drift)
            await Assert.ThrowsAsync<SampleWorkstationProtocolException>(() => driver.ImportTasksAsync("WS", "task.xlsx", source, CancellationToken.None, Ml));
        else
        {
            var result = await driver.ImportTasksAsync("WS", "task.xlsx", source, CancellationToken.None, Ml);
            Assert.True(result.ReadbackVerified);
            Assert.Equal(Ml, result.FileVolumeUnit);
            Assert.Equal(50, result.Template!.Transfers.Single().VolumeMicroliters);
        }
        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Get }, handler.Calls.Select(c => c.Method));
        var frame = Encoding.ASCII.GetString(handler.Calls[0].Body);
        var length = Convert.FromBase64String(frame[..4])[0];
        Assert.Equal(bytes, Convert.FromBase64String(frame[(4 + length)..]));
    }

    [Fact]
    public async Task Old_upload_without_unit_is_rejected_before_vendor_io()
    {
        using var handler = new Handler();
        using var source = new MemoryStream(SampleWorkstationTemplateFile.Write(Template()));
        await Assert.ThrowsAsync<ArgumentException>(() => Driver(handler).ImportTasksAsync("WS", "old.xlsx", source, CancellationToken.None));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Download_reports_file_unit_and_normalized_microliters()
    {
        using var handler = new Handler(Download(SampleWorkstationTemplateFile.Write(Template(1000), Ml)));
        var result = await Driver(handler).GetTaskTemplateAsync("WS", Template().TaskNo, CancellationToken.None);
        Assert.Equal(Ml, result.FileVolumeUnit);
        Assert.Equal(1000, result.Template.Transfers.Single().VolumeMicroliters);
    }

    private static string Cell(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var stream = zip.GetEntry("xl/worksheets/sheet2.xml")!.Open();
        return XDocument.Load(stream).Descendants().Single(e => e.Name.LocalName == "c" && (string?)e.Attribute("r") == "K2").Value;
    }
    private static byte[] ChangeCell(string value)
    {
        using var output = new MemoryStream();
        output.Write(SampleWorkstationTemplateFile.Write(Template(), Ml));
        using (var zip = new ZipArchive(output, ZipArchiveMode.Update, true))
        {
            var entry = zip.GetEntry("xl/worksheets/sheet2.xml")!;
            XDocument xml;
            using (var input = entry.Open()) xml = XDocument.Load(input);
            xml.Descendants().Single(e => e.Name.LocalName == "c" && (string?)e.Attribute("r") == "K2").Descendants().Single(e => e.Name.LocalName == "t").Value = value;
            entry.Delete();
            using var target = zip.CreateEntry("xl/worksheets/sheet2.xml").Open();
            xml.Save(target);
        }
        return output.ToArray();
    }
    private static string Download(byte[] bytes) => JsonSerializer.Serialize(new { Code = 200, Data = new { FileName = "task.xlsx", FileData = Convert.ToBase64String(bytes) } });
    private static SampleWorkstationDriver Driver(Handler handler) => new(new VendorSampleWorkstationHttpClient(new HttpClient(handler)
        { BaseAddress = new Uri("http://vendor.invalid/Service/") }), new SampleWorkstationOptions
        { DeviceId = "WS", EquipmentNo = "EQ", Enabled = true, ControlEnabled = true, TemplateVolumeUnit = Ml }, TimeProvider.System);
    private sealed class Handler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);
        public List<(HttpMethod Method, byte[] Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.Method, request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct)));
            return new(HttpStatusCode.OK) { Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json") };
        }
    }
}
