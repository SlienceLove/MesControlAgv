using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MesControlAgv.Contracts;

namespace MesControlAgv.Application;

/// <summary>The two-sheet XLSX format captured from the actual workstation.</summary>
public static class SampleWorkstationTemplateFile
{
    public const int MaximumFileBytes = 4 * 1024 * 1024;
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly string[] TaskHeaders = ["任务编号", "任务名称", "布局名称", "工作流名称"];
    private static readonly string[] TransferHeaders = ["溶剂参数编码", "枪头位模块参数编码", "枪头行X", "枪头行Y",
        "来源孔板模块参数编码", "来源孔板行X", "来源孔板列Y", "目标板模块参数编码", "目标孔板行X", "目标孔板列Y", "移液量"];

    public static async Task<byte[]> ReadBytesAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (copy.Length + read > MaximumFileBytes) throw new ArgumentException("Workstation template exceeds 4 MiB.");
            copy.Write(buffer, 0, read);
        }
        return copy.ToArray();
    }

    public static SampleWorkstationTaskTemplate Read(byte[] content)
    {
        try { return ReadCore(content); }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or FormatException or OverflowException)
        { throw new ArgumentException("Invalid workstation XLSX template: " + exception.Message, exception); }
    }

    private static SampleWorkstationTaskTemplate ReadCore(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length is 0 or > MaximumFileBytes) throw new ArgumentException("Template must be a nonempty XLSX up to 4 MiB.");
        using var input = new MemoryStream(content, writable: false);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        if (zip.Entries.Sum(e => e.Length) > 16L * 1024 * 1024) throw new ArgumentException("Expanded template is too large.");
        XDocument Load(string path)
        {
            using var stream = (zip.GetEntry(path) ?? throw new ArgumentException($"Missing XLSX part: {path}")).Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader);
        }
        var workbook = Load("xl/workbook.xml");
        var sheets = workbook.Descendants(S + "sheet").ToArray();
        if (sheets.Length != 2 || (string?)sheets[0].Attribute("name") != "实验任务表" || (string?)sheets[1].Attribute("name") != "移液参数表")
            throw new ArgumentException("Expected 实验任务表 then 移液参数表, exactly two sheets.");
        var relationships = Load("xl/_rels/workbook.xml.rels");
        var strings = zip.GetEntry("xl/sharedStrings.xml") is null ? [] : Load("xl/sharedStrings.xml")
            .Descendants(S + "si").Select(e => string.Concat(e.Descendants(S + "t").Select(t => t.Value))).ToArray();
        List<string[]> ReadSheet(XElement sheet, string[] headers)
        {
            var id = (string?)sheet.Attribute(R + "id");
            var matches = relationships.Root?.Elements().Where(e => (string?)e.Attribute("Id") == id).ToArray() ?? [];
            if (matches.Length != 1) throw new ArgumentException("Missing or duplicate sheet relationship.");
            var relation = matches[0];
            if ((string?)relation.Attribute("TargetMode") == "External") throw new ArgumentException("External worksheet not supported.");
            var target = (string?)relation.Attribute("Target") ?? throw new ArgumentException("Missing sheet target.");
            var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            if (path.Contains("..", StringComparison.Ordinal)) throw new ArgumentException("Invalid worksheet path.");
            var document = Load(path);
            if (document.Descendants(S + "f").Any()) throw new ArgumentException("Formulas cannot define instrument task data.");
            var rows = new List<string[]>();
            var expectedRow = 1;
            foreach (var row in document.Descendants(S + "row").OrderBy(e => (int?)e.Attribute("r") ?? 0))
            {
                var rowNumber = (int?)row.Attribute("r") ?? 0;
                if (rowNumber != expectedRow++) throw new ArgumentException("Template rows must be consecutive, starting at header row 1.");
                var values = new string[headers.Length];
                Array.Fill(values, string.Empty);
                var seen = new HashSet<int>();
                foreach (var cell in row.Elements(S + "c"))
                {
                    var reference = (string?)cell.Attribute("r") ?? throw new ArgumentException("Missing cell address.");
                    var index = 0;
                    foreach (var letter in reference.TakeWhile(char.IsLetter)) index = checked(index * 26 + char.ToUpperInvariant(letter) - 'A' + 1);
                    if (!int.TryParse(string.Concat(reference.SkipWhile(char.IsLetter)), out var cellRow) || cellRow != rowNumber)
                        throw new ArgumentException("Worksheet cell row does not match its containing row.");
                    if (index == 0 || !seen.Add(index)) throw new ArgumentException("Invalid or duplicate cell address.");
                    var type = (string?)cell.Attribute("t");
                    var value = cell.Element(S + "v")?.Value ?? string.Empty;
                    if (type == "s")
                    {
                        if (!int.TryParse(value, out var si) || si < 0 || si >= strings.Length) throw new ArgumentException("Invalid shared string reference.");
                        value = strings[si];
                    }
                    else if (type == "inlineStr") value = string.Concat(cell.Descendants(S + "t").Select(t => t.Value));
                    else if (type is not (null or "n" or "str")) throw new ArgumentException("Unsupported worksheet cell type.");
                    if (index > values.Length)
                    {
                        if (!string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Unexpected template column.");
                    }
                    else values[index - 1] = value;
                }
                if (values.All(v => v.Length == 0)) throw new ArgumentException("Blank task/transfer rows cannot be imported reliably.");
                rows.Add(values);
            }
            if (rows.Count == 0 || !rows[0].SequenceEqual(headers)) throw new ArgumentException("Workstation template headers differ from the captured format.");
            return rows.Skip(1).ToList();
        }
        var tasks = ReadSheet(sheets[0], TaskHeaders);
        if (tasks.Count != 1) throw new ArgumentException("Exactly one task per workstation template is supported.");
        if (tasks[0][2].Length != 0 || tasks[0][3].Length != 0) throw new ArgumentException("Layout/workflow values are not part of the verified template format.");
        var rows = ReadSheet(sheets[1], TransferHeaders).Select(v => new SampleWorkstationTransferRow(
            Required(v[0]), Required(v[1]), Positive(v[2]), Positive(v[3]),
            Required(v[4]), Positive(v[5]), Positive(v[6]), Required(v[7]), Positive(v[8]), Positive(v[9]), Positive(v[10]))).ToArray();
        if (rows.Length == 0) throw new ArgumentException("At least one transfer is required.");
        return new(Required(tasks[0][0]), Required(tasks[0][1]), rows);
    }

    public static byte[] Write(SampleWorkstationTaskTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(template.Transfers);
        var tasks = new[] { TaskHeaders, new[] { Required(template.TaskNo), Required(template.TaskName), "", "" } };
        var transfers = new List<string[]> { TransferHeaders };
        foreach (var row in template.Transfers)
            transfers.Add([Required(row.LiquidCode), Required(row.TipModule), Number(row.TipX), Number(row.TipY),
                Required(row.SourceModule), Number(row.SourceX), Number(row.SourceY), Required(row.TargetModule),
                Number(row.TargetX), Number(row.TargetY), Number(row.VolumeMicroliters)]);
        if (transfers.Count == 1) throw new ArgumentException("At least one transfer is required.");
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, XDocument document)
            {
                var entry = zip.CreateEntry(path);
                // ZIP's default timestamp is wall-clock time. Fixing it makes the
                // generated instrument file hash stable across preparation/import.
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                document.Save(stream);
            }
            XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
            XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
            Add("[Content_Types].xml", new(new XElement(types + "Types",
                new XElement(types + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(types + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new XElement(types + "Override", new XAttribute("PartName", "/xl/workbook.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                Enumerable.Range(1, 2).Select(i => new XElement(types + "Override", new XAttribute("PartName", $"/xl/worksheets/sheet{i}.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))))));
            Add("_rels/.rels", new(new XElement(rel + "Relationships", new XElement(rel + "Relationship",
                new XAttribute("Id", "rId1"), new XAttribute("Type", R.NamespaceName + "/officeDocument"), new XAttribute("Target", "xl/workbook.xml")))));
            Add("xl/workbook.xml", new(new XElement(S + "workbook", new XAttribute(XNamespace.Xmlns + "r", R),
                new XElement(S + "sheets", new[] { "实验任务表", "移液参数表" }.Select((name, i) => new XElement(S + "sheet",
                    new XAttribute("name", name), new XAttribute("sheetId", i + 1), new XAttribute(R + "id", $"rId{i + 1}")))))));
            Add("xl/_rels/workbook.xml.rels", new(new XElement(rel + "Relationships", Enumerable.Range(1, 2).Select(i =>
                new XElement(rel + "Relationship", new XAttribute("Id", $"rId{i}"), new XAttribute("Type", R.NamespaceName + "/worksheet"),
                    new XAttribute("Target", $"worksheets/sheet{i}.xml"))))));
            XDocument Sheet(IEnumerable<string[]> rows) => new(new XElement(S + "worksheet", new XElement(S + "sheetData",
                rows.Select((row, r) => new XElement(S + "row", new XAttribute("r", r + 1), row.Select((value, c) =>
                    new XElement(S + "c", new XAttribute("r", $"{(char)('A' + c)}{r + 1}"), new XAttribute("t", "inlineStr"),
                        new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), value)))))))));
            Add("xl/worksheets/sheet1.xml", Sheet(tasks));
            Add("xl/worksheets/sheet2.xml", Sheet(transfers));
        }
        var bytes = output.ToArray();
        if (bytes.Length > MaximumFileBytes) throw new ArgumentException("Template exceeds 4 MiB.");
        return bytes;
    }

    /// <summary>Vendor Stream framing: base64(byte(nameBase64Length)) + nameBase64 + xlsxBase64.</summary>
    public static byte[] EncodeUpload(string fileName, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName != fileName.Trim() || fileName.Any(c => c < 32) || fileName.IndexOfAny(['/', '\\', ':', '"', '<', '>', '|', '?', '*']) >= 0 ||
            !fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A bare .xlsx filename is required.");
        var stem = fileName.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]))
            throw new ArgumentException("Reserved Windows device filenames cannot be uploaded.");
        var name = Convert.ToBase64String(Encoding.UTF8.GetBytes(fileName));
        if (name.Length > byte.MaxValue) throw new ArgumentException("Vendor filename framing allows at most 255 Base64 characters.");
        return Encoding.ASCII.GetBytes(Convert.ToBase64String([(byte)name.Length]) + name + Convert.ToBase64String(content));
    }

    public static bool SameContent(SampleWorkstationTaskTemplate expected, SampleWorkstationTaskTemplate actual) =>
        expected.TaskNo == actual.TaskNo && expected.TaskName == actual.TaskName && actual.Transfers is not null && expected.Transfers.SequenceEqual(actual.Transfers);
    private static string Required(string value) => string.IsNullOrWhiteSpace(value) || value != value.Trim()
        ? throw new ArgumentException("Required template text must be nonempty without surrounding whitespace.") : value;
    private static string Number(int value) => value > 0 ? value.ToString(CultureInfo.InvariantCulture) : throw new ArgumentException("Coordinates and volume must be positive integers.");
    private static int Positive(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
        ? n : throw new ArgumentException("Coordinates and volume must be positive integers.");
}
