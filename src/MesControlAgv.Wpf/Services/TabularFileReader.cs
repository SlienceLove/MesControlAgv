using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace MesControlAgv.Wpf.Services;

/// <summary>
/// 在不增加第三方依赖的情况下读取 .csv 和 .xlsx 的原始行数据。
/// 只负责文件读取，不解释任何业务字段，供运输任务导入与离子色谱样品任务导入共用。
/// </summary>
public static class TabularFileReader
{
    public static IReadOnlyList<IReadOnlyList<string>> ReadRows(Stream source, string fileName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".csv" => ReadCsvRows(source),
            ".xlsx" => ReadXlsxRows(source),
            _ => throw new NotSupportedException("Only .csv and .xlsx import files are supported.")
        };
    }

    public static IReadOnlyList<IReadOnlyList<string>> ReadCsvRows(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var reader = new StreamReader(
            source,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);
        var content = reader.ReadToEnd();
        return ReadCsvRows(content, DetectDelimiter(content)).ToArray();
    }

    public static IReadOnlyList<IReadOnlyList<string>> ReadXlsxRows(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var sharedStrings = ReadSharedStrings(archive);
        return OpenFirstWorksheet(archive)
            .Descendants()
            .Where(element => element.Name.LocalName == "row")
            .Select(row => ReadWorksheetRow(row, sharedStrings))
            .ToArray();
    }

    /// <summary>
    /// 归一化表头用于别名匹配：去空白、下划线、连字符、冒号和问号后转小写。
    /// </summary>
    public static string NormalizeHeader(string value)
    {
        return new string((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Where(character => !char.IsWhiteSpace(character) && character is not '_' and not '-' and not ':' and not '?')
            .ToArray());
    }

    private static char DetectDelimiter(string content)
    {
        var firstRow = ReadCsvRows(content, ',').FirstOrDefault() ?? [];
        if (firstRow.Count > 1) return ',';

        var semicolonRow = ReadCsvRows(content, ';').FirstOrDefault() ?? [];
        if (semicolonRow.Count > 1) return ';';

        var tabRow = ReadCsvRows(content, '\t').FirstOrDefault() ?? [];
        return tabRow.Count > 1 ? '\t' : ',';
    }

    private static IEnumerable<IReadOnlyList<string>> ReadCsvRows(string content, char delimiter)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            if (character == '"')
            {
                if (quoted && index + 1 < content.Length && content[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
                continue;
            }

            if (!quoted && character == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
                continue;
            }

            if (!quoted && (character == '\r' || character == '\n'))
            {
                if (character == '\r' && index + 1 < content.Length && content[index + 1] == '\n') index++;
                row.Add(field.ToString());
                field.Clear();
                yield return row.ToArray();
                row.Clear();
                continue;
            }

            field.Append(character);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row.ToArray();
        }
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        return document
            .Descendants()
            .Where(element => element.Name.LocalName == "si")
            .Select(item => string.Concat(item.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value)))
            .ToArray();
    }

    private static XDocument OpenFirstWorksheet(ZipArchive archive)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml");
        if (workbookEntry is not null)
        {
            using var workbookStream = workbookEntry.Open();
            var workbook = XDocument.Load(workbookStream);
            var sheet = workbook.Descendants().FirstOrDefault(element => element.Name.LocalName == "sheet");
            var relationshipId = sheet?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id")?.Value;
            if (!string.IsNullOrWhiteSpace(relationshipId))
            {
                var relationshipsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
                if (relationshipsEntry is not null)
                {
                    using var relationshipsStream = relationshipsEntry.Open();
                    var relationships = XDocument.Load(relationshipsStream);
                    var relationship = relationships.Descendants().FirstOrDefault(element =>
                        element.Name.LocalName == "Relationship" &&
                        string.Equals(element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Id")?.Value, relationshipId, StringComparison.Ordinal));
                    var target = relationship?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Target")?.Value;
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        var entryName = "xl/" + target.TrimStart('/').Replace('\\', '/');
                        var worksheetEntry = archive.GetEntry(entryName);
                        if (worksheetEntry is not null)
                        {
                            using var worksheetStream = worksheetEntry.Open();
                            return XDocument.Load(worksheetStream);
                        }
                    }
                }
            }
        }

        var fallback = archive.Entries
            .Where(entry => entry.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? throw new InvalidDataException("The Excel workbook does not contain a worksheet.");
        using var fallbackStream = fallback.Open();
        return XDocument.Load(fallbackStream);
    }

    private static IReadOnlyList<string> ReadWorksheetRow(XElement row, IReadOnlyList<string> sharedStrings)
    {
        var cells = new Dictionary<int, string>();
        var fallbackColumn = 0;
        foreach (var cell in row.Elements().Where(element => element.Name.LocalName == "c"))
        {
            var reference = cell.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "r")?.Value;
            var column = string.IsNullOrWhiteSpace(reference) ? fallbackColumn : GetColumnIndex(reference);
            cells[column] = ReadCellValue(cell, sharedStrings);
            fallbackColumn = column + 1;
        }

        if (cells.Count == 0) return [];
        var values = new string[cells.Keys.Max() + 1];
        foreach (var pair in cells) values[pair.Key] = pair.Value;
        return values;
    }

    private static int GetColumnIndex(string cellReference)
    {
        var column = 0;
        foreach (var character in cellReference.TakeWhile(char.IsLetter))
        {
            column = (column * 26) + (char.ToUpperInvariant(character) - 'A' + 1);
        }
        return Math.Max(column - 1, 0);
    }

    private static string ReadCellValue(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = cell.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "t")?.Value;
        if (string.Equals(type, "inlineStr", StringComparison.Ordinal))
        {
            return string.Concat(cell.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value));
        }

        var value = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "v")?.Value ?? string.Empty;
        if (string.Equals(type, "s", StringComparison.Ordinal) && int.TryParse(value, out var sharedStringIndex) && sharedStringIndex >= 0 && sharedStringIndex < sharedStrings.Count)
        {
            return sharedStrings[sharedStringIndex];
        }

        return value;
    }
}
