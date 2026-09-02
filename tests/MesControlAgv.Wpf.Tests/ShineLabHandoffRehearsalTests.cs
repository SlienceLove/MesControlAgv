using System.IO;
using System.Linq;
using System.Text;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

/// <summary>
/// 端到端演练：用真实操作员 CSV 走完解析 → 规范化写入 → 批次下发，
/// 产物落到 artifacts 演练目录，交给 Start-ShineLabBatchAgent.ps1 消费。
/// 不触碰 ShineLab，只验证 MES 侧交付物与代理的契约一致。
/// </summary>
public sealed class ShineLabHandoffRehearsalTests
{
    private const string BatchId = "batch-20260825-rehearsal";
    private const string TargetSequence = "seq-rehearsal";

    private static string RepoRoot
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            return dir ?? throw new DirectoryNotFoundException("未找到仓库根目录。");
        }
    }

    private static string RehearsalDir =>
        Path.Combine(RepoRoot, "artifacts", "ion-chromatography", "rehearsal-20260825");

    [Fact]
    public async Task Operator_csv_flows_through_parser_writer_and_handoff()
    {
        var operatorCsv = Path.Combine(RehearsalDir, "operator-sample.csv");
        Assert.True(File.Exists(operatorCsv), $"缺少演练输入：{operatorCsv}");

        var parsed = new ShineLabSequenceParser().Parse(operatorCsv);

        Assert.False(parsed.HasIssues);
        Assert.True(parsed.CanGenerateSequence);
        Assert.Equal(3, parsed.Tasks.Count);

        // 前导零必须按文本保留，否则 ShineLab 收到的是 7 和 1。
        var thirdRow = parsed.Tasks[2];
        Assert.Equal("07", thirdRow.SampleLevel);
        Assert.Equal("01", thirdRow.CycleCount);

        var canonicalCsv = ShineLabCsvWriter.Build(parsed.Tasks);
        var inbox = Path.Combine(RehearsalDir, "inbox");
        Directory.CreateDirectory(inbox);

        // 清掉上一轮演练留下的清单与回执：幂等闸门见到既有回执就会复用原判定，
        // 那是生产上要的行为，但会让本测试第二次运行读到代理写的 Ready。
        foreach (var stale in Directory.GetFiles(inbox, $"{BatchId}.manifest*")
            .Concat(Directory.GetFiles(inbox, $"{BatchId}.receipt*")))
        {
            File.Delete(stale);
        }

        var csvFileName = $"{BatchId}.csv";
        ShineLabCsvWriter.Write(Path.Combine(inbox, csvFileName), parsed.Tasks);

        var manifest = new ShineLabBatchManifest
        {
            BatchId = BatchId,
            CsvFileName = csvFileName,
            CsvSha256 = ShineLabCsvWriter.ComputeSha256(canonicalCsv),
            TargetSequence = TargetSequence,
            ExpectedRows = parsed.Tasks.Count,
            CreatedAtUtc = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.FromHours(8)).UtcDateTime,
            CreatedBy = "rehearsal",
            AllowAppend = true,
            VerifyByExport = true,
            AllowRun = false
        };

        // 演练用短超时：控制电脑代理尚未运行，本次只验证下发产物本身。
        var handoff = new ShineLabBatchHandoff(new ShineLabHandoffOptions
        {
            InboxPath = inbox,
            ReceiptTimeout = TimeSpan.FromMilliseconds(300),
            PollInterval = TimeSpan.FromMilliseconds(20)
        });

        var receipt = await handoff.SubmitAsync(manifest, canonicalCsv, CancellationToken.None);

        // 代理未运行，因此必须是 Unknown 而不是 Verified，且绝不能自动重试。
        Assert.Equal(ShineLabBatchStatus.Unknown, receipt.Status);
        Assert.False(receipt.RunTriggered);

        var readyPath = Path.Combine(inbox, ShineLabBatchHandoff.ManifestReadyFileName(BatchId));
        Assert.True(File.Exists(readyPath), "清单未原子改名为 .ready，代理不会消费该批次。");

        var bytes = File.ReadAllBytes(Path.Combine(inbox, csvFileName));
        Assert.False(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "ShineLab 模板不带 BOM，代理按无 BOM 严格读取。");

        var onDisk = new UTF8Encoding(false).GetString(bytes);
        Assert.Equal(canonicalCsv, onDisk);
        Assert.StartsWith(ShineLabCsvWriter.CanonicalHeaderLine + "\r\n", onDisk);
    }

    [Fact]
    public void Rehearsal_batch_id_is_not_the_frozen_poc_batch()
    {
        // batch-20260824-001 已在现场导入过，重复导入会追加第二组任务。
        Assert.NotEqual("batch-20260824-001", BatchId);
    }
}
