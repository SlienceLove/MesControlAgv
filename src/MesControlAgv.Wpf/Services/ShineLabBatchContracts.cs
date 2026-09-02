using System.Text.Json.Serialization;

namespace MesControlAgv.Wpf.Services;

/// <summary>
/// 批次状态。MES 只在 <see cref="Verified"/> 时显示导入成功。
/// <see cref="Unknown"/> 表示无法判断，禁止自动重试，必须人工处置。
/// </summary>
public enum ShineLabBatchStatus
{
    Ready,
    Importing,
    Submitted,
    Verified,
    Failed,
    Unknown
}

/// <summary>
/// MES 写给控制电脑的批次清单。文件名为 {BatchId}.manifest.json，
/// 先写 .tmp 再原子改名为 .ready，避免代理读到半截文件。
/// </summary>
public sealed record ShineLabBatchManifest
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "1.0";

    [JsonPropertyName("batchId")]
    public string BatchId { get; init; } = string.Empty;

    /// <summary>ShineLab CSV 的 SHA256，与 BatchId 和 TargetSequence 共同构成幂等键。</summary>
    [JsonPropertyName("csvSha256")]
    public string CsvSha256 { get; init; } = string.Empty;

    [JsonPropertyName("csvFileName")]
    public string CsvFileName { get; init; } = string.Empty;

    /// <summary>目标序列标识。由操作人员在 MES 侧填写，用于区分同一 CSV 导入到不同序列。</summary>
    [JsonPropertyName("targetSequence")]
    public string TargetSequence { get; init; } = string.Empty;

    [JsonPropertyName("expectedRows")]
    public int ExpectedRows { get; init; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; init; }

    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; init; } = string.Empty;

    /// <summary>
    /// 是否允许追加导入。ShineLab 导入是追加语义，必须由操作人员显式确认。
    /// </summary>
    [JsonPropertyName("allowAppend")]
    public bool AllowAppend { get; init; }

    /// <summary>
    /// 导入后是否执行只读导出比对。为 false 时批次最多只能到 Submitted，不能判 Verified。
    /// </summary>
    [JsonPropertyName("verifyByExport")]
    public bool VerifyByExport { get; init; } = true;

    /// <summary>
    /// 是否允许代理点击“运行”。当前安全基线要求恒为 false，由控制电脑代理再次强制校验。
    /// </summary>
    [JsonPropertyName("allowRun")]
    public bool AllowRun { get; init; }

    /// <summary>
    /// 幂等键：批次号 + CSV 哈希 + 目标序列。不写入清单文件：
    /// 控制电脑代理按同样规则自行推导，磁盘上多一份副本只会带来分歧风险。
    /// </summary>
    [JsonIgnore]
    public string IdempotencyKey =>
        $"{BatchId}|{CsvSha256}|{TargetSequence}";
}

public sealed record ShineLabBatchEvidence
{
    [JsonPropertyName("importLogPath")]
    public string? ImportLogPath { get; init; }

    /// <summary>实际导入前由控制电脑只读导出的序列快照，用于证明历史前缀未变化。</summary>
    [JsonPropertyName("baselineCsvPath")]
    public string? BaselineCsvPath { get; init; }

    [JsonPropertyName("baselineSnapshotLogPath")]
    public string? BaselineSnapshotLogPath { get; init; }

    [JsonPropertyName("exportedCsvPath")]
    public string? ExportedCsvPath { get; init; }

    [JsonPropertyName("comparisonJsonPath")]
    public string? ComparisonJsonPath { get; init; }

    [JsonPropertyName("screenshotPaths")]
    public IReadOnlyList<string> ScreenshotPaths { get; init; } = [];
}

/// <summary>
/// 控制电脑代理写回的回执。文件名为 {BatchId}.receipt.json，同样先写 .tmp 再原子改名。
/// </summary>
public sealed record ShineLabBatchReceipt
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "1.0";

    [JsonPropertyName("batchId")]
    public string BatchId { get; init; } = string.Empty;

    [JsonPropertyName("csvSha256")]
    public string CsvSha256 { get; init; } = string.Empty;

    [JsonPropertyName("targetSequence")]
    public string TargetSequence { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public ShineLabBatchStatus Status { get; init; } = ShineLabBatchStatus.Unknown;

    [JsonPropertyName("expectedRows")]
    public int ExpectedRows { get; init; }

    /// <summary>导出比对得到的实际行数；无法确定时为 null。</summary>
    [JsonPropertyName("observedRows")]
    public int? ObservedRows { get; init; }

    [JsonPropertyName("comparisonStatus")]
    public string? ComparisonStatus { get; init; }

    [JsonPropertyName("differenceCount")]
    public int? DifferenceCount { get; init; }

    [JsonPropertyName("startedAtUtc")]
    public DateTimeOffset? StartedAtUtc { get; init; }

    [JsonPropertyName("completedAtUtc")]
    public DateTimeOffset? CompletedAtUtc { get; init; }

    [JsonPropertyName("agentVersion")]
    public string? AgentVersion { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("evidence")]
    public ShineLabBatchEvidence? Evidence { get; init; }

    /// <summary>“运行”始终由人工触发；代理必须回报 false。</summary>
    [JsonPropertyName("runTriggered")]
    public bool RunTriggered { get; init; }
}
