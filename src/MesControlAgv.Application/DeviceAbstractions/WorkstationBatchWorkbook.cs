using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using MesControlAgv.Contracts;

namespace MesControlAgv.Application;

/// <summary>Operator workbook: one row per transfer, grouped by exact task number.</summary>
public static class WorkstationBatchWorkbook
{
    public static readonly string[] Headers = ["任务编号", "任务名称", "溶剂参数编码", "枪头位模块参数编码", "枪头行X", "枪头行Y",
        "来源孔板模块参数编码", "来源孔板行X", "来源孔板列Y", "目标板模块参数编码", "目标孔板行X", "目标孔板列Y", "移液量(µL)"];
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static void Validate(IReadOnlyList<SampleWorkstationTaskTemplate> tasks)
    {
        if (tasks is null || tasks.Count is < 1 or > 100) throw new ArgumentException("每批需要 1–100 个任务。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var task in tasks)
        {
            if (task is null || string.IsNullOrEmpty(task.TaskNo) || !Regex.IsMatch(task.TaskNo, "^[A-Za-z0-9_-]{1,20}$") || !ids.Add(task.TaskNo))
                throw new ArgumentException("任务编号须为 1–20 位字母、数字、下划线或短横线，任务之间不能重复。");
            if (string.IsNullOrWhiteSpace(task.TaskName) || task.TaskName.Length > 100)
                throw new ArgumentException($"任务 {task.TaskNo}：名称不能为空且最多 100 字。");
            if (task.Transfers is null || task.Transfers.Count == 0 || (count += task.Transfers.Count) > 5000)
                throw new ArgumentException("任务须有分液明细，每批最多 5000 行。");
            // Reuse the verified vendor workbook rules, including the 0/0 default tip pair.
            var vendorBytes = SampleWorkstationTemplateFile.Write(task);
            SampleWorkstationTemplateFile.Read(vendorBytes);
            SampleWorkstationTemplateFile.EncodeUpload(task.TaskNo + ".xlsx", vendorBytes);
        }
    }

    public static IReadOnlyList<SampleWorkstationTaskTemplate> Read(byte[] bytes)
    {
        if (bytes.Length is 0 or > SampleWorkstationTemplateFile.MaximumFileBytes) throw new ArgumentException("Excel 文件需为非空 XLSX，最大 4 MiB。");
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > 200 || zip.Entries.Sum(e => e.Length) > 16 * 1024 * 1024)
            throw new ArgumentException("Excel 解压体积或文件数量过大。");
        XDocument Load(string path)
        {
            using var stream = (zip.GetEntry(path) ?? throw new ArgumentException($"Excel 缺少 {path}")).Open();
            using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(xml);
        }
        var sheets = Load("xl/workbook.xml").Descendants(S + "sheet").ToArray();
        if (sheets.Length != 1 || (string?)sheets[0].Attribute("name") != "批量任务") throw new ArgumentException("请使用导出的样表，保留唯一工作表“批量任务”。");
        var id = (string?)sheets[0].Attribute(R + "id");
        var links = Load("xl/_rels/workbook.xml.rels").Root!.Elements().Where(e => (string?)e.Attribute("Id") == id).ToArray();
        if (links.Length != 1 || (string?)links[0].Attribute("TargetMode") == "External") throw new ArgumentException("无效的工作表链接。");
        var target = (string?)links[0].Attribute("Target") ?? "";
        if (target.Length == 0 || target.Contains("..") || target.Contains(':') || target.Contains('\\')) throw new ArgumentException("无效的工作表路径。");
        var document = Load(target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target);
        if (document.Descendants(S + "f").Any()) throw new ArgumentException("任务表不允许公式，请填写确定数值。");
        var strings = zip.GetEntry("xl/sharedStrings.xml") is null ? [] : Load("xl/sharedStrings.xml").Descendants(S + "si")
            .Select(e => string.Concat(e.Descendants(S + "t").Select(t => t.Value))).ToArray();
        var tasks = new Dictionary<string, (string Name, List<SampleWorkstationTransferRow> Rows)>(StringComparer.Ordinal);
        var rowNos = new HashSet<int>();
        foreach (var row in document.Descendants(S + "row"))
        {
            var number = (int?)row.Attribute("r") ?? 0;
            if (number < 1 || !rowNos.Add(number) || rowNos.Count > 5001) throw new ArgumentException("工作表行号重复或行数过多。");
            var values = new string[Headers.Length]; Array.Fill(values, "");
            var seen = new HashSet<int>(); var taskIdIsText = false;
            foreach (var cell in row.Elements(S + "c"))
            {
                var address = (string?)cell.Attribute("r") ?? "";
                var match = Regex.Match(address, "^([A-Z]+)([1-9][0-9]*)$");
                if (!match.Success || match.Groups[2].Value != number.ToString(CultureInfo.InvariantCulture)) throw new ArgumentException($"第 {number} 行：单元格地址无效。");
                var col = match.Groups[1].Value.Aggregate(0, (n, c) => checked(n * 26 + c - 'A' + 1)) - 1;
                if (!seen.Add(col)) throw new ArgumentException($"第 {number} 行：单元格重复。");
                var type = (string?)cell.Attribute("t");
                var value = cell.Element(S + "v")?.Value ?? "";
                if (type == "s")
                {
                    if (!int.TryParse(value, out var si) || si < 0 || si >= strings.Length) throw new ArgumentException("无效共享字符串。");
                    value = strings[si];
                }
                else if (type == "inlineStr") value = string.Concat(cell.Descendants(S + "t").Select(t => t.Value));
                else if (type is not (null or "n" or "str")) throw new ArgumentException($"第 {number} 行：不支持的单元格类型。");
                if (col == 0) taskIdIsText = type is "s" or "inlineStr" or "str";
                if (col >= values.Length) { if (!string.IsNullOrWhiteSpace(value)) throw new ArgumentException("包含样表以外的列。"); }
                else values[col] = value.Trim();
            }
            if (number == 1) { if (!values.SequenceEqual(Headers)) throw new ArgumentException("表头与样表不一致。"); continue; }
            if (values.All(string.IsNullOrWhiteSpace)) continue;
            if (!rowNos.Contains(1) || !taskIdIsText) throw new ArgumentException($"第 {number} 行：任务编号必须是文本，防止 Excel 截断长编号。");
            int Int(int i) => int.TryParse(values[i], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : throw new ArgumentException($"第 {number} 行，{Headers[i]}：须填写整数。");
            if (!tasks.TryGetValue(values[0], out var entry)) tasks.Add(values[0], entry = (values[1], []));
            if (entry.Name != values[1]) throw new ArgumentException($"第 {number} 行：同一任务编号的名称不一致。");
            entry.Rows.Add(new(values[2], values[3], Int(4), Int(5), values[6], Int(7), Int(8), values[9], Int(10), Int(11), Int(12)));
        }
        var result = tasks.Select(t => new SampleWorkstationTaskTemplate(t.Key, t.Value.Name, t.Value.Rows)).ToArray();
        Validate(result); return result;
    }

    public static byte[] Write(IReadOnlyList<SampleWorkstationTaskTemplate> tasks)
    {
        Validate(tasks);
        // Reuse the verified XLSX package structure; replace only the operator sheet.
        using var memory = new MemoryStream();
        memory.Write(SampleWorkstationTemplateFile.Write(tasks[0])); memory.Position = 0;
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Update, leaveOpen: true))
        {
            XDocument Load(string p) { using var s = zip.GetEntry(p)!.Open(); return XDocument.Load(s); }
            void Put(string p, XDocument d) { zip.GetEntry(p)?.Delete(); using var s = zip.CreateEntry(p).Open(); d.Save(s); }
            var workbook = Load("xl/workbook.xml");
            workbook.Descendants(S + "sheet").Skip(1).Remove(); workbook.Descendants(S + "sheet").First().SetAttributeValue("name", "批量任务");
            Put("xl/workbook.xml", workbook);
            var rels = Load("xl/_rels/workbook.xml.rels"); rels.Root!.Elements().Skip(1).Remove(); Put("xl/_rels/workbook.xml.rels", rels);
            var types = Load("[Content_Types].xml"); types.Root!.Elements().Where(e => (string?)e.Attribute("PartName") == "/xl/worksheets/sheet2.xml").Remove(); Put("[Content_Types].xml", types);
            zip.GetEntry("xl/worksheets/sheet2.xml")?.Delete();
            var rows = new List<string[]> { Headers };
            foreach (var t in tasks) foreach (var r in t.Transfers)
                rows.Add([t.TaskNo, t.TaskName, r.LiquidCode, r.TipModule, r.TipX.ToString(), r.TipY.ToString(), r.SourceModule,
                    r.SourceX.ToString(), r.SourceY.ToString(), r.TargetModule, r.TargetX.ToString(), r.TargetY.ToString(), r.VolumeMicroliters.ToString()]);
            Put("xl/worksheets/sheet1.xml", new(new XElement(S + "worksheet", new XElement(S + "sheetData", rows.Select((r, i) =>
                new XElement(S + "row", new XAttribute("r", i + 1), r.Select((v, j) => new XElement(S + "c", new XAttribute("r", $"{(char)('A' + j)}{i + 1}"),
                    new XAttribute("t", "inlineStr"), new XElement(S + "is", new XElement(S + "t", v))))))))));
        }
        var result = memory.ToArray();
        if (result.Length > SampleWorkstationTemplateFile.MaximumFileBytes) throw new ArgumentException("导出的批量表超过 4 MiB，请拆分批次。");
        return result;
    }
}
