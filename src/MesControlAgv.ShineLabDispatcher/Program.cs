using System.IO;
using MesControlAgv.Wpf.Services;

return await DispatchProgram.RunAsync(args);

/// <summary>
/// 离子色谱批次下发命令行入口。调用与中控 WPF「导入CSV」按钮完全相同的三个服务
/// （ShineLabSequenceParser / ShineLabCsvWriter / ShineLabBatchHandoff），
/// 不复制任何解析或生成逻辑，因此现场下发走的就是 MES 生产路径本身。
///
/// 安全边界（命令行无法放宽）：
///   - allowRun 恒为 false，本程序不含任何触发“运行”的路径。
///   - ShineLab 导入是追加语义，必须显式 --confirm-append 才允许下发。
///   - 解析出任何问题即拒绝下发，绝不把带问题的批次送到现场。
///   - 回执非 Verified 一律视为未成功，需人工确认，绝不自动重试。
/// </summary>
internal static class DispatchProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        DispatchOptions options;
        try
        {
            options = DispatchOptions.Parse(args);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            Console.Error.WriteLine($"参数错误：{ex.Message}");
            PrintUsage();
            return 2;
        }

        if (!options.ConfirmAppend)
        {
            Console.Error.WriteLine("ShineLab 导入是追加语义，必须显式追加 --confirm-append 才允许下发。");
            return 2;
        }

        try
        {
            return await DispatchAsync(options);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                                      or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"下发失败：{ex.Message}");
            return 1;
        }
    }

    private static async Task<int> DispatchAsync(DispatchOptions options)
    {
        Console.WriteLine($"解析操作员文件：{options.SourceFile}");
        var parsed = new ShineLabSequenceParser().Parse(options.SourceFile);

        foreach (var issue in parsed.Issues)
        {
            var prefix = issue.SourceRowNumber > 0 ? $"第 {issue.SourceRowNumber} 行" : "文件";
            Console.Error.WriteLine($"  {prefix}：{issue.Message}");
        }

        if (parsed.HasIssues || !parsed.CanGenerateSequence)
        {
            Console.Error.WriteLine($"解析发现 {parsed.Issues.Count} 条问题，已拒绝下发。请修正操作员文件后重试。");
            return 1;
        }

        Console.WriteLine($"解析通过：{parsed.Tasks.Count} 条样品任务");
        foreach (var task in parsed.Tasks)
        {
            Console.WriteLine(
                $"  {task.SampleName,-12} {task.SampleType,-8} 等级={task.SampleLevel,-4} " +
                $"循环={task.CycleCount,-4} {task.InjectionVolume}{task.InjectionUnit}");
        }

        var csvContent = ShineLabCsvWriter.Build(parsed.Tasks);
        var batchId = options.BatchId ?? $"batch-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        var sha256 = ShineLabCsvWriter.ComputeSha256(csvContent);

        Console.WriteLine($"批次 {batchId}  CSV SHA256={sha256}");

        var manifest = new ShineLabBatchManifest
        {
            BatchId = batchId,
            CsvFileName = $"{batchId}.csv",
            CsvSha256 = sha256,
            TargetSequence = options.TargetSequence,
            ExpectedRows = parsed.Tasks.Count,
            CreatedAtUtc = DateTimeOffset.UtcNow.UtcDateTime,
            CreatedBy = options.OperatorName,
            AllowAppend = true,
            VerifyByExport = options.VerifyByExport,
            AllowRun = false
        };

        var handoff = new ShineLabBatchHandoff(new ShineLabHandoffOptions
        {
            InboxPath = options.InboxPath,
            ReceiptTimeout = TimeSpan.FromSeconds(options.ReceiptTimeoutSeconds)
        });

        Console.WriteLine($"下发到 {options.InboxPath}，等待回执最长 {options.ReceiptTimeoutSeconds} 秒...");
        var receipt = await handoff.SubmitAsync(manifest, csvContent, CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine("回执：");
        Console.WriteLine($"  status       = {receipt.Status}");
        Console.WriteLine($"  runTriggered = {receipt.RunTriggered}");
        Console.WriteLine($"  expectedRows = {receipt.ExpectedRows}");
        Console.WriteLine($"  observedRows = {receipt.ObservedRows?.ToString() ?? "-"}");
        if (!string.IsNullOrWhiteSpace(receipt.Error))
        {
            Console.WriteLine($"  error        = {receipt.Error}");
        }

        if (receipt.RunTriggered)
        {
            Console.Error.WriteLine("回执报告 runTriggered=true，违反安全边界，请立即人工检查控制电脑。");
            return 1;
        }

        if (receipt.Status == ShineLabBatchStatus.Verified)
        {
            Console.WriteLine("批次已验证：导出比对一致。");
            return 0;
        }

        Console.Error.WriteLine(
            $"批次未判定成功（{receipt.Status}）。需人工确认控制电脑与 ShineLab 实际状态，不要自动重试。");
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            离子色谱批次下发（MES 中控侧）

            用法：
              dotnet run --project src/MesControlAgv.ShineLabDispatcher -- [选项]

            必需：
              --source <路径>            操作员样品任务 .csv / .xlsx
              --inbox <路径>             控制电脑交接目录，例如 \\192.168.1.108\MES-RPA-Inbox
              --target-sequence <名称>   ShineLab 目标序列
              --confirm-append           确认 ShineLab 导入为追加语义

            可选：
              --batch-id <标识>          默认按当前时间生成 batch-yyyyMMdd-HHmmss
              --operator <姓名>          默认当前登录用户
              --no-verify-by-export      跳过只读导出比对（则最好判定只能到 Submitted）
              --receipt-timeout <秒>     等待回执超时，默认 300

            退出码：0 已验证；1 失败；2 参数错误或未判定成功（需人工确认）
            """);
    }
}

internal sealed record DispatchOptions
{
    public required string SourceFile { get; init; }
    public required string InboxPath { get; init; }
    public required string TargetSequence { get; init; }
    public string? BatchId { get; init; }
    public string OperatorName { get; init; } = Environment.UserName;
    public bool ConfirmAppend { get; init; }
    public bool VerifyByExport { get; init; } = true;
    public int ReceiptTimeoutSeconds { get; init; } = 300;

    public static DispatchOptions Parse(string[] args)
    {
        string? source = null, inbox = null, target = null, batchId = null, op = null;
        var confirmAppend = false;
        var verifyByExport = true;
        var timeout = 300;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--source": source = Next(args, ref i); break;
                case "--inbox": inbox = Next(args, ref i); break;
                case "--target-sequence": target = Next(args, ref i); break;
                case "--batch-id": batchId = Next(args, ref i); break;
                case "--operator": op = Next(args, ref i); break;
                case "--confirm-append": confirmAppend = true; break;
                case "--no-verify-by-export": verifyByExport = false; break;
                case "--receipt-timeout":
                    var raw = Next(args, ref i);
                    if (!int.TryParse(raw, out timeout) || timeout <= 0)
                    {
                        throw new FormatException($"--receipt-timeout 必须是正整数，收到 ‘{raw}’。");
                    }

                    break;
                default: throw new ArgumentException($"未知参数 ‘{args[i]}’。");
            }
        }

        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("缺少 --source。");
        if (string.IsNullOrWhiteSpace(inbox)) throw new ArgumentException("缺少 --inbox。");
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("缺少 --target-sequence。");
        if (!File.Exists(source)) throw new ArgumentException($"操作员文件不存在：{source}");

        return new DispatchOptions
        {
            SourceFile = Path.GetFullPath(source),
            InboxPath = inbox,
            TargetSequence = target.Trim(),
            BatchId = string.IsNullOrWhiteSpace(batchId) ? null : batchId.Trim(),
            OperatorName = string.IsNullOrWhiteSpace(op) ? Environment.UserName : op.Trim(),
            ConfirmAppend = confirmAppend,
            VerifyByExport = verifyByExport,
            ReceiptTimeoutSeconds = timeout
        };
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"参数 ‘{args[i]}’ 缺少值。");
        return args[++i];
    }
}
