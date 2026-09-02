using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MesControlAgv.Wpf.Services;

public sealed record ShineLabHandoffOptions
{
    /// <summary>MES 与控制电脑共享的收件目录；当前现场控制电脑为 192.168.1.108，具体共享名待确认。</summary>
    public required string InboxPath { get; init; }

    /// <summary>等待代理回执的超时。超时后批次判 Unknown，禁止自动重试。</summary>
    public TimeSpan ReceiptTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
}

public interface IShineLabBatchHandoff
{
    /// <summary>
    /// 提交批次：写 CSV 与清单、原子改名为 ready，然后等待控制电脑回执。
    /// 幂等键重复时直接返回既有回执，不再触碰 ShineLab UI。
    /// </summary>
    Task<ShineLabBatchReceipt> SubmitAsync(
        ShineLabBatchManifest manifest,
        string csvContent,
        CancellationToken cancellationToken);

    Task<ShineLabBatchReceipt?> TryReadReceiptAsync(string batchId, CancellationToken cancellationToken);
}

/// <summary>
/// 基于共享目录的批次交接。所有写入都是先写临时文件再原子改名，
/// 保证控制电脑代理永远不会读到半截文件。
/// </summary>
public sealed class ShineLabBatchHandoff : IShineLabBatchHandoff
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ShineLabHandoffOptions _options;
    private readonly Func<DateTimeOffset> _clock;

    public ShineLabBatchHandoff(ShineLabHandoffOptions options, Func<DateTimeOffset>? clock = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.InboxPath);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public static string ManifestFileName(string batchId) => $"{batchId}.manifest.json";
    public static string ManifestReadyFileName(string batchId) => $"{batchId}.manifest.ready";
    public static string ReceiptFileName(string batchId) => $"{batchId}.receipt.json";

    public async Task<ShineLabBatchReceipt> SubmitAsync(
        ShineLabBatchManifest manifest,
        string csvContent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(csvContent);
        ValidateManifest(manifest, csvContent);

        Directory.CreateDirectory(_options.InboxPath);

        // 幂等闸门：任何已存在的回执都直接返回，绝不重复导入。
        var existing = await TryReadReceiptAsync(manifest.BatchId, cancellationToken);
        if (existing is not null)
        {
            return existing with
            {
                Error = existing.Error ?? $"批次 {manifest.BatchId} 已存在回执（{existing.Status}），已拒绝重复导入。"
            };
        }

        if (File.Exists(Path.Combine(_options.InboxPath, ManifestReadyFileName(manifest.BatchId))) ||
            File.Exists(Path.Combine(_options.InboxPath, ManifestFileName(manifest.BatchId))))
        {
            return new ShineLabBatchReceipt
            {
                BatchId = manifest.BatchId,
                CsvSha256 = manifest.CsvSha256,
                TargetSequence = manifest.TargetSequence,
                ExpectedRows = manifest.ExpectedRows,
                Status = ShineLabBatchStatus.Unknown,
                Error = $"批次 {manifest.BatchId} 已在控制电脑队列中，未确认结果前禁止重复下发。"
            };
        }

        WriteAtomic(Path.Combine(_options.InboxPath, manifest.CsvFileName), csvContent);
        WriteAtomic(
            Path.Combine(_options.InboxPath, ManifestFileName(manifest.BatchId)),
            JsonSerializer.Serialize(manifest, SerializerOptions));

        // 原子改名为 .ready 才代表批次可被代理消费。
        File.Move(
            Path.Combine(_options.InboxPath, ManifestFileName(manifest.BatchId)),
            Path.Combine(_options.InboxPath, ManifestReadyFileName(manifest.BatchId)),
            overwrite: false);

        return await WaitForReceiptAsync(manifest, cancellationToken);
    }

    public async Task<ShineLabBatchReceipt?> TryReadReceiptAsync(string batchId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);

        var path = Path.Combine(_options.InboxPath, ReceiptFileName(batchId));
        if (!File.Exists(path)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, new UTF8Encoding(false), cancellationToken);
            return JsonSerializer.Deserialize<ShineLabBatchReceipt>(json, SerializerOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // 代理可能正在写入；调用方会在下一次轮询重试。
            return null;
        }
    }

    private async Task<ShineLabBatchReceipt> WaitForReceiptAsync(
        ShineLabBatchManifest manifest,
        CancellationToken cancellationToken)
    {
        var deadline = _clock() + _options.ReceiptTimeout;
        while (_clock() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = await TryReadReceiptAsync(manifest.BatchId, cancellationToken);
            if (receipt is not null) return receipt;
            await Task.Delay(_options.PollInterval, cancellationToken);
        }

        return new ShineLabBatchReceipt
        {
            BatchId = manifest.BatchId,
            CsvSha256 = manifest.CsvSha256,
            TargetSequence = manifest.TargetSequence,
            ExpectedRows = manifest.ExpectedRows,
            Status = ShineLabBatchStatus.Unknown,
            Error = $"等待控制电脑回执超时（{_options.ReceiptTimeout.TotalMinutes:0.#} 分钟）。批次判为 Unknown，必须人工确认 ShineLab 实际状态后再处置，禁止自动重试。"
        };
    }

    private static void ValidateManifest(ShineLabBatchManifest manifest, string csvContent)
    {
        if (string.IsNullOrWhiteSpace(manifest.BatchId))
        {
            throw new InvalidOperationException("批次号不能为空。");
        }

        if (manifest.BatchId.Any(character => Path.GetInvalidFileNameChars().Contains(character)))
        {
            throw new InvalidOperationException($"批次号 '{manifest.BatchId}' 含有非法文件名字符。");
        }

        if (string.IsNullOrWhiteSpace(manifest.TargetSequence))
        {
            throw new InvalidOperationException("目标序列不能为空；幂等键需要它区分同一 CSV 的不同序列。");
        }

        var actualHash = ShineLabCsvWriter.ComputeSha256(csvContent);
        if (!string.Equals(actualHash, manifest.CsvSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("清单中的 CSV SHA256 与实际内容不一致，已拒绝下发。");
        }

        if (manifest.AllowRun)
        {
            throw new InvalidOperationException("当前安全基线禁止 RPA 触发“运行”；AllowRun 必须为 false。");
        }

        // 与控制电脑代理保持同一条校验：追加语义必须由操作人员显式确认，就地拦住比下发后被拒更早暴露问题。
        if (!manifest.AllowAppend)
        {
            throw new InvalidOperationException("ShineLab 导入为追加语义，必须由操作人员确认 AllowAppend 后才能下发。");
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
