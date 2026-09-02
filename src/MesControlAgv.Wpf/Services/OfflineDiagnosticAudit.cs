using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MesControlAgv.Wpf.Services;

public static class OfflineDiagnosticVersions
{
    public const string ExportSchema = "mes.offline-diagnostics/1.0";
    public const string RuleSchema = "mes.startup-diagnostics/1.0";
    public const string FieldPreflightSchema = "mes.field-preflight/1.0";
    public const string DiffSchema = "mes.offline-diagnostic-diff/1.0";
    public const string QuarantineSchema = "mes.offline-diagnostic-quarantine/1.0";
}

public sealed record OfflineDiagnosticAuditEntry(
    DateTimeOffset OccurredAt,
    string Category,
    string Action,
    string Outcome,
    string Message)
{
    public string OccurredAtText => OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed class OfflineDiagnosticAuditTrail
{
    public const int DefaultCapacity = 500;

    private readonly int _capacity;
    private readonly ObservableCollection<OfflineDiagnosticAuditEntry> _entries = [];

    public OfflineDiagnosticAuditTrail(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        Entries = new ReadOnlyObservableCollection<OfflineDiagnosticAuditEntry>(_entries);
    }

    public ReadOnlyObservableCollection<OfflineDiagnosticAuditEntry> Entries { get; }

    public void Record(
        string category,
        string action,
        string outcome,
        string? message,
        DateTimeOffset? occurredAt = null)
    {
        var entry = new OfflineDiagnosticAuditEntry(
            occurredAt ?? DateTimeOffset.Now,
            DiagnosticRedactor.Redact(category),
            DiagnosticRedactor.Redact(action),
            DiagnosticRedactor.Redact(outcome),
            DiagnosticRedactor.Redact(message));
        _entries.Add(entry);
        while (_entries.Count > _capacity) _entries.RemoveAt(0);
    }

    public IReadOnlyList<OfflineDiagnosticAuditEntry> Snapshot() => _entries.ToArray();

    public void Clear() => _entries.Clear();
}

public static class DiagnosticRedactor
{
    private static readonly Regex SecretPattern = new(
        """\b(password|pwd|token|secret|authorization|api[-_]?key)\b\s*[:=]\s*(?:"[^"]*"|'[^']*'|[^\s,;]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex UrlPattern = new(
        """https?://[^\s"']+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HostPattern = new(
        """\b(host|hostname|address|endpoint)\b\s*[:=]\s*(?:"[^"]*"|'[^']*'|[^\s,;]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex QuotedWindowsPathPattern = new(
        "\"[A-Za-z]:\\\\[^\"]+\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex WindowsPathPattern = new(
        @"\b[A-Za-z]:\\[^\r\n,;]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex UncPathPattern = new(
        @"\\\\[^\r\n,;]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex IpV4Pattern = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ComPortPattern = new(
        @"\bCOM\d+\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Redact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var redacted = SecretPattern.Replace(value, match => $"{match.Groups[1].Value}=[REDACTED]");
        redacted = UrlPattern.Replace(redacted, "[URL]");
        redacted = HostPattern.Replace(redacted, match => $"{match.Groups[1].Value}=[REDACTED]");
        redacted = QuotedWindowsPathPattern.Replace(redacted, "\"[PATH]\"");
        redacted = WindowsPathPattern.Replace(redacted, "[PATH]");
        redacted = UncPathPattern.Replace(redacted, "[PATH]");
        redacted = IpV4Pattern.Replace(redacted, "[IP]");
        redacted = ComPortPattern.Replace(redacted, "[PORT]");
        return redacted;
    }
}

public static class OfflineDiagnosticExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static void Export(
        string filePath,
        StartupConfigurationReport startupConfiguration,
        IReadOnlyList<OfflineDiagnosticAuditEntry> auditEntries,
        DateTimeOffset? exportedAt = null,
        OfflineDiagnosticExportDocument? baseline = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(startupConfiguration);
        ArgumentNullException.ThrowIfNull(auditEntries);

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("诊断导出路径缺少目录。");
        Directory.CreateDirectory(directory);
        var fieldPreflight = FieldPreflightChecklistBuilder.Build(startupConfiguration);
        var configurationDiff = baseline is null
            ? null
            : OfflineDiagnosticDiffBuilder.Build(startupConfiguration, baseline);

        var payload = new
        {
            schemaVersion = OfflineDiagnosticVersions.ExportSchema,
            diagnosticRuleVersion = startupConfiguration.DiagnosticRuleVersion,
            exportedAt = exportedAt ?? DateTimeOffset.Now,
            startup = new
            {
                overallStatus = DiagnosticRedactor.Redact(startupConfiguration.OverallStatus),
                runtimeMode = DiagnosticRedactor.Redact(startupConfiguration.RuntimeMode),
                manageLocalServices = startupConfiguration.ManageLocalServices,
                adapterDriver = DiagnosticRedactor.Redact(startupConfiguration.AdapterDriver),
                adapterRunMode = DiagnosticRedactor.Redact(startupConfiguration.AdapterRunMode),
                realWriteAccess = DiagnosticRedactor.Redact(startupConfiguration.RealWriteAccess),
                checks = startupConfiguration.Items.Select(item => new
                {
                    item.Code,
                    item.Name,
                    value = DiagnosticRedactor.Redact(item.Value),
                    severity = item.SeverityText,
                    detail = DiagnosticRedactor.Redact(item.Detail)
                })
            },
            fieldPreflight = new
            {
                schemaVersion = OfflineDiagnosticVersions.FieldPreflightSchema,
                offlineConfigurationAcceptable = fieldPreflight.OfflineConfigurationAcceptable,
                hasOfflineBlocks = fieldPreflight.HasOfflineBlocks,
                requiresFieldConfirmation = fieldPreflight.RequiresFieldConfirmation,
                canDetermineGo = fieldPreflight.CanDetermineGo,
                decision = DiagnosticRedactor.Redact(fieldPreflight.DecisionText),
                summary = DiagnosticRedactor.Redact(fieldPreflight.SummaryText),
                items = fieldPreflight.Items.Select(item => new
                {
                    item.Code,
                    item.Name,
                    value = DiagnosticRedactor.Redact(item.Value),
                    status = item.Status.ToString(),
                    statusText = item.StatusText,
                    detail = DiagnosticRedactor.Redact(item.Detail)
                })
            },
            configurationDiff = configurationDiff is null
                ? null
                : new
                {
                    schemaVersion = OfflineDiagnosticVersions.DiffSchema,
                    hasChanges = configurationDiff.HasChanges,
                    changeCount = configurationDiff.ChangeCount,
                    canDetermineGo = configurationDiff.CanDetermineGo,
                    decision = DiagnosticRedactor.Redact(configurationDiff.DecisionText),
                    summary = DiagnosticRedactor.Redact(configurationDiff.SummaryText),
                    items = configurationDiff.Items.Select(item => new
                    {
                        item.Code,
                        item.Name,
                        kind = item.Kind.ToString(),
                        kindText = item.KindText,
                        baselineValue = DiagnosticRedactor.Redact(item.BaselineValue),
                        currentValue = DiagnosticRedactor.Redact(item.CurrentValue),
                        baselineStatus = DiagnosticRedactor.Redact(item.BaselineStatus),
                        currentStatus = DiagnosticRedactor.Redact(item.CurrentStatus),
                        detail = DiagnosticRedactor.Redact(item.Detail)
                    })
                },
            audit = auditEntries.Select(entry => new
            {
                entry.OccurredAt,
                entry.Category,
                entry.Action,
                entry.Outcome,
                message = DiagnosticRedactor.Redact(entry.Message)
            })
        };

        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(payload, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static OfflineDiagnosticImportResult Read(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            var root = document.RootElement;
            var schema = root.TryGetProperty("schemaVersion", out var schemaElement)
                ? schemaElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(schema))
            {
                return new OfflineDiagnosticImportResult(
                    true,
                    true,
                    "已读取兼容的旧版诊断文件；请重新导出以升级格式。",
                    null);
            }

            if (!schema.Equals(OfflineDiagnosticVersions.ExportSchema, StringComparison.Ordinal))
            {
                return new OfflineDiagnosticImportResult(
                    false,
                    false,
                    $"不支持的诊断文件版本：{DiagnosticRedactor.Redact(schema)}。",
                    null);
            }

            var ruleVersion = root.TryGetProperty("diagnosticRuleVersion", out var ruleElement)
                ? ruleElement.GetString()
                : null;
            var startup = root.TryGetProperty("startup", out var startupElement)
                ? startupElement.Deserialize<OfflineDiagnosticStartupDocument>(JsonOptions)
                : null;
            var audit = root.TryGetProperty("audit", out var auditElement)
                ? auditElement.Deserialize<IReadOnlyList<OfflineDiagnosticAuditEntry>>(JsonOptions)
                : null;
            var fieldPreflight = root.TryGetProperty("fieldPreflight", out var fieldPreflightElement)
                ? fieldPreflightElement.Deserialize<OfflineDiagnosticFieldPreflightDocument>(JsonOptions)
                : null;
            var configurationDiff = root.TryGetProperty("configurationDiff", out var configurationDiffElement)
                ? configurationDiffElement.Deserialize<OfflineDiagnosticDiffDocument>(JsonOptions)
                : null;
            if (fieldPreflight?.CanDetermineGo == true || configurationDiff?.CanDetermineGo == true)
            {
                return new OfflineDiagnosticImportResult(
                    false,
                    false,
                    "诊断文件包含不允许的现场 GO 判定字段，已拒绝读取。",
                    null);
            }
            var imported = new OfflineDiagnosticExportDocument(
                schema,
                ruleVersion ?? "legacy",
                root.TryGetProperty("exportedAt", out var exportedAtElement) && exportedAtElement.TryGetDateTimeOffset(out var exportedAt)
                    ? exportedAt
                    : DateTimeOffset.MinValue,
                startup,
                audit ?? [])
            {
                FieldPreflight = fieldPreflight,
                ConfigurationDiff = configurationDiff
            };
            var fieldPreflightNeedsUpgrade = fieldPreflight is not null &&
                !string.Equals(fieldPreflight.SchemaVersion, OfflineDiagnosticVersions.FieldPreflightSchema, StringComparison.Ordinal);
            var missingFieldPreflight = fieldPreflight is null;
            var diffNeedsUpgrade = configurationDiff is not null &&
                !string.Equals(configurationDiff.SchemaVersion, OfflineDiagnosticVersions.DiffSchema, StringComparison.Ordinal);
            var needsUpgrade = string.IsNullOrWhiteSpace(ruleVersion) ||
                !ruleVersion.Equals(OfflineDiagnosticVersions.RuleSchema, StringComparison.Ordinal) ||
                fieldPreflightNeedsUpgrade ||
                diffNeedsUpgrade;
            return new OfflineDiagnosticImportResult(
                true,
                needsUpgrade,
                needsUpgrade
                    ? "已读取旧诊断规则或差异字段版本；下次导出将升级为当前格式。"
                    : missingFieldPreflight
                        ? "诊断文件版本受支持，但该快照未包含现场只读预检输入；建议重新导出。"
                        : "诊断文件版本受支持。",
                imported);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new OfflineDiagnosticImportResult(
                false,
                false,
                $"诊断文件无法读取：{DiagnosticRedactor.Redact(exception.Message)}",
                null);
        }
    }
}

public sealed record OfflineDiagnosticImportResult(
    bool IsAccepted,
    bool NeedsUpgrade,
    string Message,
    OfflineDiagnosticExportDocument? Document);

public sealed record OfflineDiagnosticExportDocument(
    string SchemaVersion,
    string DiagnosticRuleVersion,
    DateTimeOffset ExportedAt,
    OfflineDiagnosticStartupDocument? Startup,
    IReadOnlyList<OfflineDiagnosticAuditEntry> Audit)
{
    public OfflineDiagnosticFieldPreflightDocument? FieldPreflight { get; init; }
    public OfflineDiagnosticDiffDocument? ConfigurationDiff { get; init; }
}

public sealed record OfflineDiagnosticFieldPreflightDocument
{
    public string SchemaVersion { get; init; } = string.Empty;
    public bool OfflineConfigurationAcceptable { get; init; }
    public bool HasOfflineBlocks { get; init; }
    public bool RequiresFieldConfirmation { get; init; }
    public bool CanDetermineGo { get; init; }
    public string Decision { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<OfflineDiagnosticFieldPreflightItemDocument> Items { get; init; } = [];
}

public sealed record OfflineDiagnosticFieldPreflightItemDocument
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

public sealed record OfflineDiagnosticDiffDocument
{
    public string SchemaVersion { get; init; } = string.Empty;
    public bool HasChanges { get; init; }
    public int ChangeCount { get; init; }
    public bool CanDetermineGo { get; init; }
    public string Decision { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<OfflineDiagnosticDiffItemDocument> Items { get; init; } = [];
}

public sealed record OfflineDiagnosticDiffItemDocument
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string KindText { get; init; } = string.Empty;
    public string BaselineValue { get; init; } = string.Empty;
    public string CurrentValue { get; init; } = string.Empty;
    public string BaselineStatus { get; init; } = string.Empty;
    public string CurrentStatus { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

public sealed record OfflineDiagnosticStartupDocument
{
    public string OverallStatus { get; init; } = string.Empty;
    public string RuntimeMode { get; init; } = string.Empty;
    public bool ManageLocalServices { get; init; }
    public string AdapterDriver { get; init; } = string.Empty;
    public string AdapterRunMode { get; init; } = string.Empty;
    public string RealWriteAccess { get; init; } = string.Empty;
    public IReadOnlyList<OfflineDiagnosticCheckDocument> Checks { get; init; } = [];
}

public sealed record OfflineDiagnosticCheckDocument
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}
