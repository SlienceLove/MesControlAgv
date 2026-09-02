using System.IO;
using System.Text;
using System.Text.Json;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class ShineLabBatchHandoffTests : IDisposable
{
    private readonly string _inbox = Path.Combine(
        Path.GetTempPath(),
        $"shinelab-handoff-{Guid.NewGuid():N}");

    public ShineLabBatchHandoffTests() => Directory.CreateDirectory(_inbox);

    private static readonly DateTimeOffset FixedClock =
        new(2026, 8, 25, 12, 0, 0, TimeSpan.FromHours(8));

    private const string CsvContent =
        "序号,选择,样品名称,样品类型,样品等级,处理方法,清除校正,循环次数,进样体积,进样单位,空白,数据名称,色谱方法,\r\n" +
        "1,,水样-01,未知样品,\"1\",阴离子标准法,否,1,25,μL,否,,阴离子常规,\r\n";

    /// <summary>
    /// 用真实时钟：等待回执的截止时间由时钟推进，注入固定时钟会让超时永远不到。
    /// </summary>
    private ShineLabBatchHandoff CreateHandoff(TimeSpan? receiptTimeout = null) =>
        new(new ShineLabHandoffOptions
        {
            InboxPath = _inbox,
            ReceiptTimeout = receiptTimeout ?? TimeSpan.FromMilliseconds(300),
            PollInterval = TimeSpan.FromMilliseconds(20)
        });

    private static ShineLabBatchManifest CreateManifest(string batchId = "batch-20260825-120000") => new()
    {
        BatchId = batchId,
        CsvFileName = $"{batchId}.csv",
        CsvSha256 = ShineLabCsvWriter.ComputeSha256(CsvContent),
        TargetSequence = "seq-A",
        ExpectedRows = 1,
        CreatedAtUtc = FixedClock.UtcDateTime,
        CreatedBy = "operator",
        AllowAppend = true,
        VerifyByExport = true,
        AllowRun = false
    };

    /// <summary>
    /// 模拟控制电脑代理写回执：与 Start-ShineLabBatchAgent.ps1 的 Write-JsonAtomic 一致，UTF-8 无 BOM。
    /// </summary>
    private void WriteAgentReceipt(string batchId, string json) =>
        File.WriteAllBytes(
            Path.Combine(_inbox, ShineLabBatchHandoff.ReceiptFileName(batchId)),
            new UTF8Encoding(false).GetBytes(json));

    [Fact]
    public async Task Submit_writes_csv_and_manifest_then_flips_the_ready_marker_last()
    {
        var manifest = CreateManifest();
        var handoff = CreateHandoff();

        await handoff.SubmitAsync(manifest, CsvContent, CancellationToken.None);

        var csvPath = Path.Combine(_inbox, manifest.CsvFileName);
        Assert.True(File.Exists(csvPath));
        Assert.True(File.Exists(Path.Combine(_inbox, ShineLabBatchHandoff.ManifestReadyFileName(manifest.BatchId))));
        Assert.Empty(Directory.GetFiles(_inbox, "*.tmp"));

        var bytes = await File.ReadAllBytesAsync(csvPath);
        Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal(CsvContent, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Submitted_csv_hash_matches_the_manifest_the_agent_will_verify()
    {
        var manifest = CreateManifest();
        var handoff = CreateHandoff();

        await handoff.SubmitAsync(manifest, CsvContent, CancellationToken.None);

        var readyPath = Path.Combine(_inbox, ShineLabBatchHandoff.ManifestReadyFileName(manifest.BatchId));
        var written = JsonSerializer.Deserialize<ShineLabBatchManifest>(
            await File.ReadAllTextAsync(readyPath),
            ShineLabBatchHandoff.SerializerOptions)!;

        using var sha = System.Security.Cryptography.SHA256.Create();
        var actual = Convert.ToHexString(
            sha.ComputeHash(await File.ReadAllBytesAsync(Path.Combine(_inbox, manifest.CsvFileName))));

        Assert.Equal(actual, written.CsvSha256, ignoreCase: true);
        Assert.False(written.AllowRun);
    }

    [Fact]
    public async Task Manifest_on_disk_carries_only_the_camel_case_agent_contract()
    {
        var manifest = CreateManifest();
        var handoff = CreateHandoff();

        await handoff.SubmitAsync(manifest, CsvContent, CancellationToken.None);

        var readyPath = Path.Combine(_inbox, ShineLabBatchHandoff.ManifestReadyFileName(manifest.BatchId));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(readyPath));

        var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToList();

        // 幂等键由代理按 batchId|csvSha256|targetSequence 自行推导；
        // 写进清单等于让磁盘上的副本可被篡改，两边一旦分歧就会错判重复批次。
        Assert.DoesNotContain("IdempotencyKey", names);
        Assert.DoesNotContain("idempotencyKey", names);

        // 代理按固定小驼峰键读取清单，出现大驼峰字段说明有属性漏了 JsonPropertyName。
        Assert.All(names, name => Assert.True(
            char.IsLower(name[0]),
            $"清单字段 {name} 不是小驼峰，控制电脑代理读不到。"));
    }

    [Fact]
    public async Task Submit_returns_the_receipt_the_agent_writes_back()
    {
        var manifest = CreateManifest();
        var handoff = CreateHandoff(TimeSpan.FromSeconds(5));

        WriteAgentReceipt(manifest.BatchId, $@"{{
  ""schemaVersion"": ""1.0"",
  ""batchId"": ""{manifest.BatchId}"",
  ""csvSha256"": ""{manifest.CsvSha256}"",
  ""targetSequence"": ""seq-A"",
  ""status"": ""Verified"",
  ""expectedRows"": 1,
  ""observedRows"": 1,
  ""comparisonStatus"": ""Match"",
  ""differenceCount"": 0,
  ""startedAtUtc"": ""2026-08-25T04:00:00Z"",
  ""completedAtUtc"": ""2026-08-25T04:00:20Z"",
  ""agentVersion"": ""batch-agent-2026-08-25.1"",
  ""error"": null,
  ""runTriggered"": false
}}");

        var receipt = await handoff.SubmitAsync(manifest, CsvContent, CancellationToken.None);

        Assert.Equal(ShineLabBatchStatus.Verified, receipt.Status);
        Assert.Equal(1, receipt.ObservedRows);
        Assert.Equal("Match", receipt.ComparisonStatus);
        Assert.False(receipt.RunTriggered);
    }

    /// <summary>
    /// 回执样本取自代理真实输出（演练模式）：null 字段、空 comparisonStatus、嵌套 evidence 都必须能反序列化。
    /// </summary>
    [Fact]
    public async Task Deserializes_a_real_agent_receipt_including_nulls_and_evidence()
    {
        var manifest = CreateManifest();
        var handoff = CreateHandoff(TimeSpan.FromSeconds(5));

        WriteAgentReceipt(manifest.BatchId, $@"{{
    ""schemaVersion"":  ""1.0"",
    ""batchId"":  ""{manifest.BatchId}"",
    ""csvSha256"":  ""{manifest.CsvSha256}"",
    ""targetSequence"":  ""seq-A"",
    ""status"":  ""Ready"",
    ""expectedRows"":  1,
    ""observedRows"":  null,
    ""comparisonStatus"":  """",
    ""differenceCount"":  null,
    ""startedAtUtc"":  ""2026-08-25T00:57:04Z"",
    ""completedAtUtc"":  ""2026-08-25T00:57:05Z"",
    ""agentVersion"":  ""batch-agent-2026-08-25.1"",
    ""error"":  ""演练模式（未指定 -ExecuteImport），未实际提交导入。"",
    ""evidence"":  {{
                     ""importLogPath"":  ""C:\\evidence\\import.log"",
                     ""exportedCsvPath"":  null,
                     ""comparisonJsonPath"":  null,
                     ""screenshotPaths"":  [

                                         ]
                 }},
    ""runTriggered"":  false
}}");

        var receipt = await handoff.SubmitAsync(manifest, CsvContent, CancellationToken.None);

        Assert.Equal(ShineLabBatchStatus.Ready, receipt.Status);
        Assert.Null(receipt.ObservedRows);
        Assert.False(receipt.RunTriggered);
        Assert.Contains("演练模式", receipt.Error);
    }

    [Fact]
    public async Task Submit_reports_unknown_when_the_agent_never_answers()
    {
        var handoff = CreateHandoff(TimeSpan.FromMilliseconds(200));

        var receipt = await handoff.SubmitAsync(CreateManifest(), CsvContent, CancellationToken.None);

        Assert.Equal(ShineLabBatchStatus.Unknown, receipt.Status);
        Assert.False(receipt.RunTriggered);
        Assert.NotNull(receipt.Error);
    }

    [Fact]
    public async Task Submit_refuses_a_manifest_that_requests_a_run()
    {
        var handoff = CreateHandoff();
        var manifest = CreateManifest() with { AllowRun = true };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handoff.SubmitAsync(manifest, CsvContent, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_inbox, "*.manifest.ready"));
    }

    /// <summary>
    /// 追加语义必须在 MES 侧就拦住，不能只依赖控制电脑代理的二次校验。
    /// </summary>
    [Fact]
    public async Task Submit_refuses_a_manifest_without_append_confirmation()
    {
        var handoff = CreateHandoff();
        var manifest = CreateManifest() with { AllowAppend = false };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handoff.SubmitAsync(manifest, CsvContent, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_inbox, "*.manifest.ready"));
    }

    /// <summary>
    /// ShineLab 导入是追加语义：重复下发会让序列翻倍，因此已有回执的批次只能复用原判定。
    /// </summary>
    [Fact]
    public async Task Resubmitting_a_batch_with_a_receipt_reuses_it_instead_of_importing_again()
    {
        var manifest = CreateManifest();
        var handoff = CreateHandoff(TimeSpan.FromSeconds(5));
        WriteAgentReceipt(manifest.BatchId, $@"{{""schemaVersion"":""1.0"",""batchId"":""{manifest.BatchId}"",
""csvSha256"":""{manifest.CsvSha256}"",""targetSequence"":""seq-A"",""status"":""Verified"",
""expectedRows"":1,""observedRows"":1,""comparisonStatus"":""Match"",""runTriggered"":false}}");

        var first = await handoff.SubmitAsync(manifest, CsvContent, CancellationToken.None);
        var second = await handoff.SubmitAsync(manifest, CsvContent, CancellationToken.None);

        Assert.Equal(ShineLabBatchStatus.Verified, first.Status);
        Assert.Equal(ShineLabBatchStatus.Verified, second.Status);
        Assert.Single(Directory.GetFiles(_inbox, "*.receipt.json"));
    }

    /// <summary>
    /// 批次已在队列中但尚无回执时，判 Unknown 而不是再写一份 .ready。
    /// </summary>
    [Fact]
    public async Task Resubmitting_a_queued_batch_reports_unknown_without_requeueing()
    {
        var manifest = CreateManifest();
        var handoff = CreateHandoff(TimeSpan.FromMilliseconds(200));

        await handoff.SubmitAsync(manifest, CsvContent, CancellationToken.None);
        var second = await handoff.SubmitAsync(manifest, CsvContent, CancellationToken.None);

        Assert.Equal(ShineLabBatchStatus.Unknown, second.Status);
        Assert.Contains("已在控制电脑队列中", second.Error);
        Assert.Single(Directory.GetFiles(_inbox, "*.manifest.ready"));
    }

    [Fact]
    public async Task TryReadReceipt_returns_null_before_the_agent_responds()
    {
        var handoff = CreateHandoff();

        Assert.Null(await handoff.TryReadReceiptAsync("batch-does-not-exist", CancellationToken.None));
    }

    [Fact]
    public async Task Submit_honours_cancellation_while_waiting_for_the_receipt()
    {
        var handoff = CreateHandoff(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handoff.SubmitAsync(CreateManifest(), CsvContent, cts.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_inbox)) Directory.Delete(_inbox, recursive: true);
    }
}
