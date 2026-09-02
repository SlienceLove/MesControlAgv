namespace MesControlAgv.Wpf.Services;

public enum OfflineDiagnosticDiffKind
{
    Unchanged,
    Added,
    Removed,
    Changed
}

public enum OfflineDiagnosticDiffFilter
{
    All,
    ChangesOnly,
    Changed,
    Added,
    Removed,
    Unchanged
}

public sealed record OfflineDiagnosticDiffFilterOption(
    string Key,
    string DisplayName,
    OfflineDiagnosticDiffFilter Filter);

public sealed record OfflineDiagnosticDiffItem(
    string Code,
    string Name,
    OfflineDiagnosticDiffKind Kind,
    string BaselineValue,
    string CurrentValue,
    string BaselineStatus,
    string CurrentStatus,
    string Detail)
{
    public string KindText => Kind switch
    {
        OfflineDiagnosticDiffKind.Unchanged => "未变化",
        OfflineDiagnosticDiffKind.Added => "新增",
        OfflineDiagnosticDiffKind.Removed => "移除",
        OfflineDiagnosticDiffKind.Changed => "已变化",
        _ => "未知"
    };

    public bool IsChange => Kind != OfflineDiagnosticDiffKind.Unchanged;
}

public sealed record OfflineDiagnosticDiffReport
{
    public static OfflineDiagnosticDiffReport Empty { get; } = new()
    {
        HasBaseline = false,
        Items = []
    };

    public required IReadOnlyList<OfflineDiagnosticDiffItem> Items { get; init; }
    public bool HasBaseline { get; init; }
    public bool HasChanges => Items.Any(item => item.IsChange);
    public int ChangeCount => Items.Count(item => item.IsChange);
    public bool CanDetermineGo => false;
    public string DecisionText => "不判定 GO（仅审阅离线配置差异）";
    public string SummaryText
    {
        get
        {
            if (!HasBaseline) return "尚未读取基线诊断；读取后可审阅离线配置差异。";
            return HasChanges
                ? $"发现 {ChangeCount} 项离线差异；请人工审阅，仍需新鲜现场证据"
                : "与基线诊断相比未发现离线配置差异；仍需新鲜现场证据";
        }
    }
}

public static class OfflineDiagnosticDiffFilterer
{
    public static IReadOnlyList<OfflineDiagnosticDiffItem> Apply(
        IEnumerable<OfflineDiagnosticDiffItem> items,
        OfflineDiagnosticDiffFilter filter,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(items);
        var normalizedQuery = query?.Trim();
        return items
            .Where(item => MatchesFilter(item, filter))
            .Where(item => string.IsNullOrWhiteSpace(normalizedQuery) || MatchesQuery(item, normalizedQuery!))
            .ToArray();
    }

    private static bool MatchesFilter(OfflineDiagnosticDiffItem item, OfflineDiagnosticDiffFilter filter) => filter switch
    {
        OfflineDiagnosticDiffFilter.All => true,
        OfflineDiagnosticDiffFilter.ChangesOnly => item.IsChange,
        OfflineDiagnosticDiffFilter.Changed => item.Kind == OfflineDiagnosticDiffKind.Changed,
        OfflineDiagnosticDiffFilter.Added => item.Kind == OfflineDiagnosticDiffKind.Added,
        OfflineDiagnosticDiffFilter.Removed => item.Kind == OfflineDiagnosticDiffKind.Removed,
        OfflineDiagnosticDiffFilter.Unchanged => item.Kind == OfflineDiagnosticDiffKind.Unchanged,
        _ => true
    };

    private static bool MatchesQuery(OfflineDiagnosticDiffItem item, string query) =>
        item.Code.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        item.BaselineValue.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        item.CurrentValue.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        item.BaselineStatus.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        item.CurrentStatus.Contains(query, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Compares a current local startup report with an imported offline snapshot.
/// It intentionally has no release or field-authorization decision.
/// </summary>
public static class OfflineDiagnosticDiffBuilder
{
    public static OfflineDiagnosticDiffReport Build(
        StartupConfigurationReport current,
        OfflineDiagnosticExportDocument baseline)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseline);

        var currentEntries = BuildCurrentEntries(current);
        var baselineEntries = BuildBaselineEntries(baseline);
        var codes = currentEntries.Keys
            .Concat(baselineEntries.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();

        var items = new List<OfflineDiagnosticDiffItem>(codes.Length);
        foreach (var code in codes)
        {
            currentEntries.TryGetValue(code, out var currentEntry);
            baselineEntries.TryGetValue(code, out var baselineEntry);
            var kind = currentEntry is null
                ? OfflineDiagnosticDiffKind.Removed
                : baselineEntry is null
                    ? OfflineDiagnosticDiffKind.Added
                    : Equivalent(currentEntry, baselineEntry)
                        ? OfflineDiagnosticDiffKind.Unchanged
                        : OfflineDiagnosticDiffKind.Changed;
            var name = currentEntry?.Name ?? baselineEntry?.Name ?? code;
            items.Add(new OfflineDiagnosticDiffItem(
                code,
                name,
                kind,
                baselineEntry?.Value ?? "未记录",
                currentEntry?.Value ?? "未记录",
                baselineEntry?.Status ?? "未记录",
                currentEntry?.Status ?? "未记录",
                BuildDetail(kind)));
        }

        return new OfflineDiagnosticDiffReport
        {
            HasBaseline = true,
            Items = items
        };
    }

    private static Dictionary<string, SnapshotEntry> BuildCurrentEntries(StartupConfigurationReport report)
    {
        var entries = new Dictionary<string, SnapshotEntry>(StringComparer.Ordinal);
        Add(entries, "RUNTIME_MODE", "运行模式", report.RuntimeMode, "摘要", "运行模式来自当前离线检查。");
        Add(entries, "ADAPTER_DRIVER", "AGV 驱动", report.AdapterDriver, "摘要", "驱动身份来自当前离线检查。");
        Add(entries, "ADAPTER_RUN_MODE", "Adapter 运行模式", report.AdapterRunMode, "摘要", "运行模式来自当前离线检查。");
        Add(entries, "REAL_WRITE_ACCESS", "真实写入入口", report.RealWriteAccess, "摘要", "写入状态仅为离线配置摘要。");
        Add(entries, "WPF_MANAGE_LOCAL_SERVICES", "本地服务托管", report.ManageLocalServices ? "启用" : "禁用", "摘要", "仅记录本地服务托管设置。");

        foreach (var item in report.Items)
        {
            Add(entries, item.Code, item.Name, item.Value, item.SeverityText, item.Detail);
        }

        var fieldPreflight = FieldPreflightChecklistBuilder.Build(report);
        foreach (var item in fieldPreflight.Items)
        {
            Add(entries, $"FIELD_{item.Code}", $"现场·{item.Name}", item.Value, item.StatusText, item.Detail);
        }

        return entries;
    }

    private static Dictionary<string, SnapshotEntry> BuildBaselineEntries(OfflineDiagnosticExportDocument document)
    {
        var entries = new Dictionary<string, SnapshotEntry>(StringComparer.Ordinal);
        if (document.Startup is { } startup)
        {
            Add(entries, "RUNTIME_MODE", "运行模式", startup.RuntimeMode, "摘要", "运行模式来自导入快照。");
            Add(entries, "ADAPTER_DRIVER", "AGV 驱动", startup.AdapterDriver, "摘要", "驱动身份来自导入快照。");
            Add(entries, "ADAPTER_RUN_MODE", "Adapter 运行模式", startup.AdapterRunMode, "摘要", "运行模式来自导入快照。");
            Add(entries, "REAL_WRITE_ACCESS", "真实写入入口", startup.RealWriteAccess, "摘要", "写入状态仅为导入快照摘要。");
            Add(entries, "WPF_MANAGE_LOCAL_SERVICES", "本地服务托管", startup.ManageLocalServices ? "启用" : "禁用", "摘要", "仅记录导入快照的托管设置。");
            foreach (var item in startup.Checks ?? [])
            {
                Add(entries, item.Code, item.Name, item.Value, item.Severity, item.Detail);
            }
        }

        if (document.FieldPreflight is { } fieldPreflight)
        {
            foreach (var item in fieldPreflight.Items ?? [])
            {
                Add(
                    entries,
                    $"FIELD_{item.Code}",
                    $"现场·{item.Name}",
                    item.Value,
                    string.IsNullOrWhiteSpace(item.StatusText) ? item.Status : item.StatusText,
                    item.Detail);
            }
        }

        return entries;
    }

    private static void Add(
        IDictionary<string, SnapshotEntry> entries,
        string code,
        string name,
        string? value,
        string? status,
        string? detail)
    {
        // Redact before retaining or comparing values so a diff cannot become
        // a side channel for a controller address, local path, or credential.
        entries[code] = new SnapshotEntry(
            code,
            DiagnosticRedactor.Redact(name),
            DiagnosticRedactor.Redact(value),
            DiagnosticRedactor.Redact(status),
            DiagnosticRedactor.Redact(detail));
    }

    private static bool Equivalent(SnapshotEntry left, SnapshotEntry right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        string.Equals(left.Value, right.Value, StringComparison.Ordinal) &&
        string.Equals(left.Status, right.Status, StringComparison.Ordinal);

    private static string BuildDetail(OfflineDiagnosticDiffKind kind) => kind switch
    {
        OfflineDiagnosticDiffKind.Unchanged => "值、状态和说明与基线一致。",
        OfflineDiagnosticDiffKind.Added => "当前快照新增该项。",
        OfflineDiagnosticDiffKind.Removed => "当前快照未再提供该项。",
        OfflineDiagnosticDiffKind.Changed => "值、状态或说明与基线不同。",
        _ => "无法比较该项。"
    };

    private sealed record SnapshotEntry(
        string Code,
        string Name,
        string Value,
        string Status,
        string Detail);
}
