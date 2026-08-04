using System.IO;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace MesControlAgv.Wpf.Services;

/// <summary>
/// Parses batch transport tasks from .csv and .xlsx files without a third-party dependency.
/// The first non-empty row is treated as the header row.
/// </summary>
public sealed class BatchTaskImportParser
{
    private static readonly IReadOnlyDictionary<string, string[]> HeaderAliases =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["taskid"] = ["taskid", "id", "\u4EFB\u52A1id", "\u4EFB\u52A1\u7F16\u53F7", "\u4EFB\u52A1\u53F7"],
            ["source"] = ["source", "sourcestation", "start", "startstation", "origin", "from", "\u8D77\u70B9", "\u8D77\u59CB\u70B9", "\u8D77\u70B9\u7AD9", "\u6765\u6E90\u7AD9\u70B9"],
            ["target"] = ["target", "targetstation", "end", "endstation", "destination", "to", "\u7EC8\u70B9", "\u7EC8\u70B9\u7AD9", "\u76EE\u6807\u7AD9\u70B9"],
            ["description"] = ["description", "taskdescription", "remark", "\u4EFB\u52A1\u63CF\u8FF0", "\u63CF\u8FF0", "\u5907\u6CE8"],
            ["priority"] = ["priority", "\u4F18\u5148\u7EA7"],
            ["plannedtime"] = ["plannedtime", "plantime", "scheduledtime", "\u8BA1\u5212\u65F6\u95F4", "\u8BA1\u5212\u6267\u884C\u65F6\u95F4", "\u8BA1\u5212\u5F00\u59CB\u65F6\u95F4"]
        };

    private static readonly string[] RequiredColumns = ["taskid", "source", "target"];

    public BatchTaskImportResult Parse(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var extension = Path.GetExtension(filePath);
        using var stream = File.OpenRead(filePath);
        return Parse(stream, extension);
    }

    public BatchTaskImportResult Parse(Stream source, string fileName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".csv" => ParseCsv(source),
            ".xlsx" => ParseXlsx(source),
            _ => throw new NotSupportedException("Only .csv and .xlsx batch task files are supported.")
        };
    }

    public BatchTaskImportResult ParseCsv(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var reader = new StreamReader(source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var content = reader.ReadToEnd();
        var delimiter = DetectDelimiter(content);
        return ParseRows(ReadCsvRows(content, delimiter));
    }

    public BatchTaskImportResult ParseXlsx(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var sharedStrings = ReadSharedStrings(archive);
        var worksheet = OpenFirstWorksheet(archive);
        var rows = worksheet
            .Descendants()
            .Where(element => element.Name.LocalName == "row")
            .Select(row => ReadWorksheetRow(row, sharedStrings))
            .ToArray();

        return ParseRows(rows);
    }

    private static BatchTaskImportResult ParseRows(IEnumerable<IReadOnlyList<string>> sourceRows)
    {
        var rows = sourceRows.ToList();
        var headerRowIndex = rows.FindIndex(row => row.Any(value => !string.IsNullOrWhiteSpace(value)));
        if (headerRowIndex < 0)
        {
            throw new InvalidDataException("The import file does not contain a header row.");
        }

        var header = rows[headerRowIndex];
        var columnIndexes = ResolveColumns(header);
        var tasks = new List<BatchTaskImportItem>();
        var issues = new List<BatchTaskImportIssue>();
        var taskIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = headerRowIndex + 1; index < rows.Count; index++)
        {
            var row = rows[index];
            if (!row.Any(value => !string.IsNullOrWhiteSpace(value))) continue;

            var sourceRowNumber = index + 1;
            var taskId = GetValue(row, columnIndexes, "taskid");
            var source = GetValue(row, columnIndexes, "source");
            var target = GetValue(row, columnIndexes, "target");
            if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            {
                issues.Add(new BatchTaskImportIssue(sourceRowNumber, "Task ID, source station and target station are required."));
                continue;
            }

            if (!taskIds.Add(taskId))
            {
                issues.Add(new BatchTaskImportIssue(sourceRowNumber, $"Duplicate task ID '{taskId}'."));
                continue;
            }

            var priorityText = GetValue(row, columnIndexes, "priority");
            if (!TryParsePriority(priorityText, out var priority))
            {
                issues.Add(new BatchTaskImportIssue(sourceRowNumber, $"Invalid priority '{priorityText}'."));
                continue;
            }

            var plannedTimeText = GetValue(row, columnIndexes, "plannedtime");
            if (!TryParsePlannedTime(plannedTimeText, out var plannedTime))
            {
                issues.Add(new BatchTaskImportIssue(sourceRowNumber, $"Invalid planned time '{plannedTimeText}'."));
                continue;
            }

            tasks.Add(new BatchTaskImportItem(
                sourceRowNumber,
                taskId,
                source,
                target,
                GetValue(row, columnIndexes, "description"),
                priority,
                plannedTime));
        }

        return new BatchTaskImportResult(BatchTaskImportSorter.Sort(tasks), issues);
    }

    private static Dictionary<string, int> ResolveColumns(IReadOnlyList<string> header)
    {
        var normalizedHeaders = header
            .Select(NormalizeHeader)
            .ToArray();
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var logicalName in HeaderAliases.Keys)
        {
            var aliases = HeaderAliases[logicalName].Select(NormalizeHeader).ToHashSet(StringComparer.Ordinal);
            var column = Array.FindIndex(normalizedHeaders, aliases.Contains);
            if (column >= 0) columns[logicalName] = column;
        }

        var missing = RequiredColumns.Where(column => !columns.ContainsKey(column)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException($"Missing required columns: {string.Join(", ", missing)}.");
        }

        return columns;
    }

    private static string GetValue(IReadOnlyList<string> row, IReadOnlyDictionary<string, int> columns, string logicalName)
    {
        return columns.TryGetValue(logicalName, out var index) && index < row.Count
            ? row[index].Trim()
            : string.Empty;
    }

    private static string NormalizeHeader(string value)
    {
        return new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(character => !char.IsWhiteSpace(character) && character is not '_' and not '-' and not ':' and not '?')
            .ToArray());
    }

    private static bool TryParsePriority(string text, out int priority)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            priority = 0;
            return true;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out priority)) return true;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out priority)) return true;

        priority = text.Trim().ToLowerInvariant() switch
        {
            "high" or "urgent" or "\u9AD8" or "\u7D27\u6025" => 100,
            "medium" or "normal" or "\u4E2D" or "\u666E\u901A" => 50,
            "low" or "\u4F4E" => 0,
            _ => 0
        };

        return text.Trim().ToLowerInvariant() is "high" or "urgent" or "\u9AD8" or "\u7D27\u6025" or "medium" or "normal" or "\u4E2D" or "\u666E\u901A" or "low" or "\u4F4E";
    }

    private static bool TryParsePlannedTime(string text, out DateTime? plannedTime)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            plannedTime = null;
            return true;
        }

        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var localTime) ||
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out localTime))
        {
            plannedTime = localTime;
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 0 and < 2958466)
        {
            try
            {
                plannedTime = DateTime.FromOADate(serial);
                return true;
            }
            catch (ArgumentException)
            {
                // Fall through to the invalid value result.
            }
        }

        plannedTime = null;
        return false;
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
